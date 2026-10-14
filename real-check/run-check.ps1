# Проверка системного режима Облачки на настоящей машине.
#
# Тот же прогон, что на стенде (Oblachka.Tests.exe --system-tunnel-check), с флагом
# --real-machine: собственный IP машины и резолверы провайдера в отчёт не попадают.
# Аудит соединений Windows (WFP) включается на время прогона и сводится к счётчикам —
# адреса, куда ходили программы, в отчёт не идут.
#
# Сеть меняется временно: туннель, фильтры WFP, одно правило брандмауэра для curl.
# Всё снимается в конце прогона, а фильтры, кроме того, не переживают перезагрузку.
# Без вопросов (для стенда): -Yes -Key <ключ> [-Nodes ...] [-OtherVpn ...].
param([switch]$Elevated, [switch]$Yes, [string]$Key, [string[]]$Nodes, [string]$OtherVpn)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$admin = ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) {
    if ($Elevated) { Write-Host 'Права администратора не получены.' -ForegroundColor Red; return }
    Start-Process powershell.exe -Verb RunAs -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-NoExit', '-File', "`"$PSCommandPath`"", '-Elevated')
    return
}

Write-Host ''
Write-Host 'Проверка системного режима Облачки' -ForegroundColor Cyan
Write-Host 'Около 5 минут на каждый узел. На это время интернет пойдёт через туннель, в отдельные'
Write-Host 'моменты будет недоступен — так проверяется, что при сбоях ничего не уходит мимо.'
Write-Host 'Не запускайте звонки и большие загрузки. В конце всё снимается само.'
Write-Host ''

$tests = Join-Path $here 'Oblachka.Tests.exe'
if (-not (Test-Path -LiteralPath $tests)) { Write-Host 'Не найден Oblachka.Tests.exe рядом со скриптом. Распакуйте архив целиком.' -ForegroundColor Red; return }
$desktop = Get-ChildItem "$env:ProgramFiles\dotnet\shared\Microsoft.WindowsDesktop.App" -ErrorAction SilentlyContinue | Where-Object Name -like '10.*'
if (-not $desktop) {
    Write-Host 'Нужен .NET 10 Desktop Runtime x64: https://dotnet.microsoft.com/download/dotnet/10.0' -ForegroundColor Red
    Write-Host 'Установите его и запустите проверку снова.'
    return
}

if ($Yes -and $Key) {
    $key = $Key; $otherVpn = $OtherVpn
    # Через -File массив приходит одной строкой через запятую: делим сами.
    $nodes = if ($Nodes) { $Nodes -join ',' -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ } } else { @('Германия', 'звонков') }
} else {
    $answer = Read-Host 'Продолжить? Введите ДА'
    if ($answer -ne 'ДА' -and $answer -ne 'да') { Write-Host 'Отменено.'; return }
    $key = Read-Host 'Ключ подписки Облачки'
    if ([string]::IsNullOrWhiteSpace($key)) { Write-Host 'Ключ не введён.'; return }
    $otherVpn = Read-Host 'Включён ли сейчас другой VPN? Напишите его название или нажмите Enter'
    $nodesText = Read-Host 'Узлы через запятую (Enter — «Германия, звонков»)'
    $nodes = if ([string]::IsNullOrWhiteSpace($nodesText)) { @('Германия', 'звонков') } else { $nodesText.Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_ } }
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$work = Join-Path $env:TEMP ('oblachka-check-' + $stamp)
New-Item -ItemType Directory -Force $work | Out-Null
$summary = Join-Path $work 'summary.txt'
function Note([string]$text) { Write-Host $text; Add-Content -LiteralPath $summary -Value $text -Encoding UTF8 }

Note ("Проверка " + $stamp + ", Windows " + [Environment]::OSVersion.Version + ", другой VPN: " + $(if ($otherVpn) { $otherVpn } else { 'нет' }))
$adapters = Get-NetAdapter | Where-Object Status -eq 'Up' | ForEach-Object { $_.InterfaceDescription }
Note ('Активные адаптеры: ' + ($adapters -join '; '))
$v6 = @(Get-NetIPAddress -AddressFamily IPv6 -ErrorAction SilentlyContinue | Where-Object { $_.PrefixOrigin -in 'RouterAdvertisement', 'Dhcp' -and $_.IPAddress -notlike 'fd*' -and $_.IPAddress -notlike 'fe80*' })
Note ('Публичный IPv6 на адаптерах: ' + $(if ($v6.Count -gt 0) { 'есть' } else { 'нет' }))

# Аудит соединений WFP: прежнее состояние восстанавливается после прогона.
$audit = '{0CCE9226-69AE-11D9-BED3-505054503030}'
$auditBefore = (auditpol /get /subcategory:$audit /r | ConvertFrom-Csv).'Inclusion Setting'
auditpol /set /subcategory:$audit /success:enable /failure:enable | Out-Null

function Count-Window($from, $to) {
    if (-not $from -or -not $to) { return 'нет отметок' }
    $events = Get-WinEvent -FilterHashtable @{ LogName = 'Security'; Id = 5156, 5157; StartTime = $from.AddSeconds(-1); EndTime = $to.AddSeconds(1) } -ErrorAction SilentlyContinue |
        Where-Object { $_.TimeCreated -ge $from -and $_.TimeCreated -le $to } | ForEach-Object {
            $x = [xml]$_.ToXml(); $d = @{}; foreach ($n in $x.Event.EventData.Data) { $d[$n.Name] = $n.'#text' }
            if ($d.Direction -ne '%%14593' -or $d.DestAddress -match '^(127\.|::1|ff02|224\.|239\.|255\.|172\.31\.255|fdfe)') { return }
            [pscustomobject]@{ Allow = ($_.Id -eq 5156); App = (Split-Path $d.Application -Leaf) }
        }
    $events = @($events)
    $allowed = @($events | Where-Object Allow)
    'разрешено ' + $allowed.Count + ' (' + (($allowed | Group-Object App | ForEach-Object { $_.Name + '×' + $_.Count }) -join ', ') + '), запрещено ' + @($events | Where-Object { -not $_.Allow }).Count
}

function Step-Time($report, $pattern) {
    $line = Select-String -LiteralPath $report -Pattern ('шаг помощника: (\S+) ' + $pattern) | Select-Object -First 1
    if (-not $line) { return $null }
    $t = [datetime]::ParseExact($line.Matches[0].Groups[1].Value, 'HH:mm:ss.fff', $null)
    $utc = [datetime]::SpecifyKind([datetime]::UtcNow.Date.Add($t.TimeOfDay), 'Utc')
    return $utc.ToLocalTime()
}

$index = 0
foreach ($node in $nodes) {
    $index++
    $report = Join-Path $work ("report-$index.txt")
    Note ''
    Note ("=== Узел «$node» ===")
    & $tests --system-tunnel-check $key $node $report 120 --real-machine | Out-Null
    Note ('код завершения: ' + $LASTEXITCODE)
    if (Test-Path -LiteralPath $report) {
        Get-Content -LiteralPath $report -Encoding UTF8 | Where-Object { $_ -match 'узел:|до туннеля через|SOCKS ядра:|ВЫВОД|IPv6|чужое|смерть|удержание|блокировка трафика|наших фильтров WFP: [0-9]+;|после остановки наших|правила:|ОТКАЗ|подписка не|другой VPN забрал|остались поднятые|выходной IP через' } | ForEach-Object { Note ('  ' + $_) }
        $wfpOn = Step-Time $report 'apply WfpProtection'; $tunUp = Step-Time $report 'apply TunnelInterface'
        $tunDown = Step-Time $report 'restore TunnelInterface'; $wfpOff = Step-Time $report 'restore WfpProtection'
        Note ('  защита стоит, туннеля ещё нет: ' + (Count-Window $wfpOn $tunUp))
        Note ('  туннель снят, защита ещё стоит: ' + (Count-Window $tunDown $wfpOff))
    }
}

if ($auditBefore -match 'No Auditing|Нет аудита') { auditpol /set /subcategory:$audit /success:disable /failure:disable | Out-Null }

# Итог: ничего не должно остаться, сеть должна работать.
Note ''
$filters = Join-Path $work 'wfp-filters.xml'
netsh wfp show filters file="$filters" | Out-Null
$left = @(Select-String -LiteralPath $filters -Pattern '6f1a3c52-0b7e-4d0e-9a1d-0b1a5c7e2f02' -ErrorAction SilentlyContinue).Count
Remove-Item -LiteralPath $filters -ErrorAction SilentlyContinue
# Журнал помощника с UAC — у пользователя, журнал службы — в ProgramData.
$journal = @((Join-Path $env:LOCALAPPDATA 'Oblachka\network-session.json'), (Join-Path $env:ProgramData 'Oblachka\network-session.json')) |
    Where-Object { Test-Path -LiteralPath $_ } | Sort-Object { (Get-Item -LiteralPath $_).LastWriteTime } | Select-Object -Last 1
$state = if ($journal) { (Get-Content -LiteralPath $journal -Raw | ConvertFrom-Json).State } else { 'нет журнала' }
$online = try { (Invoke-WebRequest -UseBasicParsing -TimeoutSec 15 'http://www.msftconnecttest.com/connecttest.txt').StatusCode -eq 200 } catch { $false }
Note ('Итог: фильтров Облачки осталось ' + $left + ', журнал: ' + $state + ', интернет: ' + $(if ($online) { 'работает' } else { 'НЕ РАБОТАЕТ' }))
if ($left -gt 0 -or -not $online) {
    Write-Host 'Сеть могла остаться закрытой. Перезагрузите компьютер: фильтры защиты после перезагрузки не сохраняются.' -ForegroundColor Yellow
}

$zip = Join-Path ([Environment]::GetFolderPath('Desktop')) ("oblachka-check-$stamp.zip")
Compress-Archive -Path (Join-Path $work '*') -DestinationPath $zip -Force
Write-Host ''
Write-Host ('Готово. Отчёт: ' + $zip) -ForegroundColor Green
Write-Host 'Пришлите этот архив. Ключа подписки, собственного IP и адресов сайтов в нём нет.'
