<#
    probe-subscription.ps1 — что панель отдаёт клиенту по ссылке подписки.

    Запуск:
        .\probe-subscription.ps1 -Url "https://sub.твой-домен/ABCDEF123456"

    Скрипт запрашивает ссылку с разными User-Agent и печатает отчёт о ФОРМЕ ответа.
    В отчёт не попадают: токен подписки, UUID, пароли, адреса серверов, тело ответа.
    Ссылка нигде не сохраняется. Отчёт можно показывать кому угодно.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Url,

    # Куда сохранить отчёт. По умолчанию только печать в консоль.
    [string] $OutFile
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 -bor [Net.SecurityProtocolType]::Tls11

# User-Agent, которые проверяем. Первый — наш, второй — прежнее имя клиента до 0.9,
# остальные — для сравнения: отличается ли ответ по клиенту.
$agents = [ordered]@{
    'Oblachka'  = 'Oblachka/0.9'
    'Legacy'    = 'RoutePilot/0.8.2'
    'Browser'   = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64)'
    'v2rayNG'   = 'v2rayNG/1.9.0'
    'Happ'      = 'Happ/1.0'
}

# Заголовки Remnawave, которые клиент может использовать: тариф, срок, объявления.
$headersOfInterest = @(
    'content-type', 'content-disposition', 'subscription-userinfo',
    'profile-title', 'profile-update-interval', 'profile-web-page-url',
    'announce', 'support-url', 'routing', 'location'
)

function Describe-Segment {
    param([string] $Segment)
    if ([string]::IsNullOrEmpty($Segment)) { return '' }
    $len = $Segment.Length
    if ($Segment -match '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$') { return "<UUID, $len симв.>" }
    if ($Segment -match '^[0-9a-fA-F]{32}$') { return "<hex, $len симв.>" }
    if ($Segment -match '^[0-9A-Za-z_-]+$') {
        if ($Segment -match '^[0-9A-Za-z]+$') { return "<буквы+цифры, $len симв.>" }
        return "<base64url, $len симв.>"
    }
    return "<прочее, $len симв.>"
}

function Describe-Url {
    param([string] $Raw)
    try { $u = [Uri] $Raw } catch { throw "Это не похоже на ссылку: проверь, что скопировал её целиком." }
    $segments = @($u.AbsolutePath.Trim('/') -split '/' | Where-Object { $_ -ne '' })
    $shape = @()
    foreach ($s in $segments) {
        # Короткие осмысленные сегменты пути (api, sub, json) не секрет — показываем как есть.
        if ($s.Length -le 12 -and $s -match '^[a-z][a-z0-9-]*$') { $shape += $s }
        else { $shape += (Describe-Segment $s) }
    }
    $last = '<нет>'
    if ($segments.Count -gt 0) { $last = Describe-Segment $segments[-1] }
    [pscustomobject] @{
        Scheme       = $u.Scheme
        Host         = $u.Host
        Port         = $u.Port
        PathShape    = '/' + ($shape -join '/')
        LastSegment  = $last
        HasQuery     = ($u.Query.Length -gt 1)
    }
}

function Decode-IfBase64 {
    param([string] $Text)
    $compact = ($Text -replace '\s', '')
    if ($compact.Length -eq 0) { return $null }
    if ($compact -notmatch '^[0-9A-Za-z+/_=-]+$') { return $null }
    $b64 = $compact.Replace('-', '+').Replace('_', '/')
    $pad = $b64.Length % 4
    if ($pad -ne 0) { $b64 = $b64.PadRight($b64.Length + 4 - $pad, '=') }
    try { return [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($b64)) }
    catch { return $null }
}

function Describe-Body {
    param([string] $Body)

    $result = [ordered]@{}
    $trimmed = $Body.Trim()
    $result['Bytes'] = [Text.Encoding]::UTF8.GetByteCount($Body)

    if ($trimmed.StartsWith('<')) {
        $result['Format'] = 'HTML — это личный кабинет, а не подписка'
        return [pscustomobject] $result
    }

    $decoded = $null
    if (-not ($trimmed.StartsWith('{') -or $trimmed.StartsWith('[') -or $trimmed.Contains('://'))) {
        $decoded = Decode-IfBase64 $trimmed
        if ($null -ne $decoded) {
            $result['Format'] = 'Base64 → список ссылок'
            $trimmed = $decoded.Trim()
        }
    }

    if ($trimmed.StartsWith('{') -or $trimmed.StartsWith('[')) {
        if (-not $result.Contains('Format')) { $result['Format'] = 'JSON' }
        try { $json = $trimmed | ConvertFrom-Json } catch { $result['Format'] = 'Похоже на JSON, но не разбирается'; return [pscustomobject] $result }

        $items = @($json)
        $outbounds = @()
        foreach ($item in $items) {
            if ($null -ne $item.PSObject.Properties['outbounds']) { $outbounds += @($item.outbounds) }
        }
        if ($outbounds.Count -eq 0) {
            $result['JsonKind'] = 'без секции outbounds — возможно, формат другого клиента'
            $result['TopLevelKeys'] = (@($items[0].PSObject.Properties.Name) -join ', ')
            return [pscustomobject] $result
        }

        $result['JsonKind'] = 'клиентский конфиг с outbounds'
        $result['Outbounds'] = $outbounds.Count

        $protocols = @{}; $transports = @{}; $securities = @{}
        $xhttpExtra = 0; $xhttpDownstream = 0; $realityFull = 0; $flow = 0; $unknownKeys = @{}
        $known = 'protocol','tag','settings','streamSettings','mux','sendThrough','type','server','server_port','domain_strategy'

        foreach ($ob in $outbounds) {
            $p = $ob.protocol
            if ([string]::IsNullOrEmpty($p)) { $p = $ob.type }   # sing-box
            if ([string]::IsNullOrEmpty($p)) { $p = '<не указан>' }
            if ($p -in 'freedom','blackhole','direct','block','dns') { continue }
            $protocols[$p] = 1 + $protocols[$p]

            foreach ($prop in $ob.PSObject.Properties.Name) {
                if ($prop -notin $known) { $unknownKeys[$prop] = 1 + $unknownKeys[$prop] }
            }

            $ss = $ob.streamSettings
            if ($null -ne $ss) {
                $net = $ss.network
                if ([string]::IsNullOrEmpty($net)) { $net = 'tcp' }
                $transports[$net] = 1 + $transports[$net]
                $sec = $ss.security
                if ([string]::IsNullOrEmpty($sec)) { $sec = 'none' }
                $securities[$sec] = 1 + $securities[$sec]

                if ($null -ne $ss.xhttpSettings) {
                    if ($null -ne $ss.xhttpSettings.extra) { $xhttpExtra++ }
                    if ($null -ne $ss.xhttpSettings.extra -and $null -ne $ss.xhttpSettings.extra.downloadSettings) { $xhttpDownstream++ }
                }
                if ($null -ne $ss.downloadSettings) { $xhttpDownstream++ }
                $r = $ss.realitySettings
                if ($null -ne $r) {
                    if (-not [string]::IsNullOrEmpty($r.publicKey) -and -not [string]::IsNullOrEmpty($r.serverName)) { $realityFull++ }
                }
            }
            $vnext = $ob.settings.vnext
            if ($null -ne $vnext) {
                foreach ($v in @($vnext)) {
                    foreach ($user in @($v.users)) { if (-not [string]::IsNullOrEmpty($user.flow)) { $flow++ } }
                }
            }
        }

        $result['Protocols']        = (($protocols.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Key) x$($_.Value)" }) -join ', ')
        $result['Transports']       = (($transports.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Key) x$($_.Value)" }) -join ', ')
        $result['Security']         = (($securities.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Key) x$($_.Value)" }) -join ', ')
        $result['XHTTP_extra']      = $xhttpExtra
        $result['XHTTP_downstream'] = $xhttpDownstream
        $result['REALITY_full']     = $realityFull
        $result['VLESS_flow']       = $flow
        if ($unknownKeys.Count -gt 0) {
            $result['UnknownOutboundKeys'] = (($unknownKeys.GetEnumerator() | Sort-Object Name | ForEach-Object { $_.Key }) -join ', ')
        }
        return [pscustomobject] $result
    }

    if ($trimmed.Contains('://')) {
        if (-not $result.Contains('Format')) { $result['Format'] = 'список ссылок' }
        $lines = @($trimmed -split "`r?`n" | Where-Object { $_.Trim() -ne '' })
        $schemes = @{}
        $withName = 0; $withParams = 0
        foreach ($line in $lines) {
            $m = [regex]::Match($line.Trim(), '^([a-z0-9+.-]+)://')
            if ($m.Success) {
                $s = $m.Groups[1].Value
                $schemes[$s] = 1 + $schemes[$s]
                if ($line.Contains('#')) { $withName++ }
                if ($line.Contains('?')) { $withParams++ }
            }
        }
        $result['Entries']    = $lines.Count
        $result['Schemes']    = (($schemes.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Key) x$($_.Value)" }) -join ', ')
        $result['WithName']   = $withName
        $result['WithParams'] = $withParams

        # Remnawave кодирует XHTTP extra прямо в параметр ссылки, поэтому проверяем его наличие,
        # а не считаем потерю заранее. downloadSettings в ссылку не помещается.
        $result['LinksWithXHTTP'] = @($lines | Where-Object { $_ -match 'type=xhttp' }).Count
        $result['LinksWithExtra'] = @($lines | Where-Object { $_ -match '[?&]extra=' }).Count
        if ($result['LinksWithXHTTP'] -gt 0 -and $result['LinksWithExtra'] -eq 0) {
            $result['Note'] = 'Есть XHTTP, но ни в одной ссылке нет параметра extra: часть настроек транспорта может теряться. Сверь с выдачей Xray JSON.'
        } elseif ($result['LinksWithExtra'] -gt 0) {
            $result['Note'] = 'XHTTP extra передаётся в ссылках. downloadSettings в ссылку не помещается — если раздельный downstream нужен, нужна выдача Xray JSON.'
        }
        return [pscustomobject] $result
    }

    $result['Format'] = 'не распознан'
    return [pscustomobject] $result
}

function Probe {
    param([string] $Name, [string] $Agent, [string] $Target)

    $row = [ordered]@{ Agent = $Name }
    try {
        $response = Invoke-WebRequest -Uri $Target -UseBasicParsing -Method Get `
            -Headers @{ 'User-Agent' = $Agent; 'Accept' = '*/*' } `
            -MaximumRedirection 5 -TimeoutSec 30
        $row['Status'] = [int] $response.StatusCode
        $picked = [ordered]@{}
        foreach ($h in $headersOfInterest) {
            $key = @($response.Headers.Keys | Where-Object { $_.ToLowerInvariant() -eq $h })
            if ($key.Count -gt 0) { $picked[$h] = ($response.Headers[$key[0]] -join '; ') }
        }
        $row['Headers'] = [pscustomobject] $picked
        $row['Body'] = Describe-Body ([string] $response.Content)
    }
    catch {
        $row['Status'] = 'ошибка'
        $web = $_.Exception
        if ($null -ne $web.Response) { $row['Status'] = [int] $web.Response.StatusCode }
        $row['Error'] = $web.Message
    }
    return [pscustomobject] $row
}

Write-Host ''
Write-Host 'Форма ссылки' -ForegroundColor Cyan
$shape = Describe-Url $Url
$shape | Format-List

Write-Host 'Ответы панели по User-Agent' -ForegroundColor Cyan
$results = @()
foreach ($name in $agents.Keys) {
    $r = Probe -Name $name -Agent $agents[$name] -Target $Url
    $results += $r
    Write-Host ''
    Write-Host ("--- " + $name + " (" + $agents[$name] + ") ---") -ForegroundColor Yellow
    Write-Host ("  HTTP: " + $r.Status)
    if ($null -ne $r.PSObject.Properties['Error']) { Write-Host ("  Ошибка: " + $r.Error) -ForegroundColor Red; continue }
    if ($r.Headers.PSObject.Properties.Count -gt 0) {
        Write-Host '  Заголовки:'
        foreach ($p in $r.Headers.PSObject.Properties) { Write-Host ("    " + $p.Name + ": " + $p.Value) }
    }
    Write-Host '  Тело:'
    foreach ($p in $r.Body.PSObject.Properties) { Write-Host ("    " + $p.Name + ": " + $p.Value) }
}

# Отличается ли ответ по клиенту: если да, в панели уже есть правила по User-Agent.
$formats = @($results | Where-Object { $null -ne $_.Body } | ForEach-Object { [string] $_.Body.Format } | Sort-Object -Unique)
Write-Host ''
Write-Host 'Вывод' -ForegroundColor Cyan
if ($formats.Count -le 1) {
    Write-Host '  Формат ответа одинаков для всех User-Agent: правил по клиенту в панели нет.'
} else {
    Write-Host ('  Формат зависит от User-Agent (' + ($formats -join ' / ') + '): в панели есть правила по клиенту.')
}

if ($OutFile) {
    $report = [pscustomobject] @{
        GeneratedAt = (Get-Date).ToString('o')
        UrlShape    = $shape
        Results     = $results
    }
    $report | ConvertTo-Json -Depth 8 | Out-File -FilePath $OutFile -Encoding utf8
    Write-Host ''
    Write-Host ("  Отчёт сохранён: " + $OutFile)
}
Write-Host ''
