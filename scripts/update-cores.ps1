# Обновление ядер Xray и sing-box до последних релизов. Без -Apply только сообщает, что вышло,
# что из этого срочно и не пора ли поднять Go и .NET (так его запускает задача
# .github/workflows/cores-update.yml). С -Apply (только ядра; Go и .NET — руками):
#   1. скачивает архивы релизов и сверяет SHA256 с digest GitHub (у Xray — ещё и с .dgst);
#   2. переписывает пины в fetch-cores.ps1, THIRD-PARTY.md и папку лицензии в vendor\;
#   3. поднимает модули в core\go.mod (sing-box по тегу, Xray по коммиту тега), собирает
#      oblachka-core.exe теми же флагами, что build-core.ps1, и закрепляет его SHA256 в TunCore.cs;
#   4. меняет номер версии в строках, где он упомянут рядом с именем ядра (кроме CHANGELOG.md).
# После -Apply: .\build.ps1, .\test.ps1, стенд скорости, раздел в CHANGELOG.md — и только потом выпуск.
param([switch]$Apply)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = Split-Path -Parent $PSScriptRoot
$fetch = Join-Path $root 'scripts\fetch-cores.ps1'
$headers = @{ 'User-Agent' = 'oblachka-cores-update'; 'Accept' = 'application/vnd.github+json' }
if ($env:GH_TOKEN) { $headers.Authorization = 'Bearer ' + $env:GH_TOKEN }
function Api([string]$path) { Invoke-RestMethod -Uri ('https://api.github.com/' + $path) -Headers $headers }

$text = [IO.File]::ReadAllText($fetch)
$current = @{
    Xray = [regex]::Match($text, "Name = 'Xray ([0-9.]+)'").Groups[1].Value
    SingBox = [regex]::Match($text, "Name = 'sing-box ([0-9.]+)'").Groups[1].Value
}
if (-not $current.Xray -or -not $current.SingBox) { throw 'В fetch-cores.ps1 не найдены версии ядер.' }

# sing-box: последний стабильный. Xray помечает все релизы как пробные — берём последний не черновик.
$singRelease = Api 'repos/SagerNet/sing-box/releases/latest'
# Через переменную: в PowerShell 5.1 Invoke-RestMethod отдаёт массив одним объектом.
$xrayList = Api 'repos/XTLS/Xray-core/releases?per_page=10'
$xrayRelease = $xrayList | Where-Object { -not $_.draft } | Select-Object -First 1
$latest = @{ Xray = $xrayRelease.tag_name.TrimStart('v'); SingBox = $singRelease.tag_name.TrimStart('v') }
$updates = @()
foreach ($core in 'Xray', 'SingBox') {
    if ([version]$latest[$core] -gt [version]$current[$core]) { $updates += $core }
}

# Срочно — если автор отозвал наш релиз (так Xray снял 26.10.17) или в новых релизах есть
# исправления безопасности. Тогда обновление выпускается за 1–3 дня, а не в плановый раз в месяц.
$urgent = @(); $notes = @()
$securityWords = '(?i)CVE-\d|GHSA-|security|vulnerab|撤版|紧急|安全更新|安全修复'
function Gone([string]$repo, [string]$tag) {
    try { Api ('repos/' + $repo + '/releases/tags/' + $tag) | Out-Null; return $false }
    catch { if ($_.Exception.Response -and [int]$_.Exception.Response.StatusCode -eq 404) { return $true }; throw }
}
# У Xray в описании — спонсоры и кошельки; смотрим только текст до них.
function Notes([object]$release) { ([string]$release.body -split '## Sponsors')[0] }
if (Gone 'XTLS/Xray-core' ('v' + $current.Xray)) { $urgent += ('Xray ' + $current.Xray + ' отозван автором — релиза больше нет.') }
if (Gone 'SagerNet/sing-box' ('v' + $current.SingBox)) { $urgent += ('sing-box ' + $current.SingBox + ' отозван автором — релиза больше нет.') }
foreach ($release in $xrayList | Where-Object { -not $_.draft -and [version]$_.tag_name.TrimStart('v') -gt [version]$current.Xray }) {
    if ((Notes $release) -match $securityWords) { $urgent += ('Xray ' + $release.tag_name + ': в описании исправление безопасности.') }
}
if ($updates -contains 'SingBox') {
    if ((Notes $singRelease) -match $securityWords) { $urgent += ('sing-box ' + $singRelease.tag_name + ': в описании исправление безопасности.') }
    $old = [version]$current.SingBox; $new = [version]$latest.SingBox
    if ($new.Major -ne $old.Major -or $new.Minor -ne $old.Minor) { $notes += ('sing-box ' + $latest.SingBox + ' — новая основная версия: обновлять отдельной веткой, core\ под новое API.') }
}

# Go: точечные выпуски закрывают уязвимости стандартной библиотеки, из которой собрано ядро туннеля.
$goMod = [IO.File]::ReadAllText((Join-Path $root 'core\go.mod'))
$goCurrent = [regex]::Match($goMod, '(?m)^go ([0-9.]+)').Groups[1].Value
$goLine = ($goCurrent -split '\.')[0..1] -join '.'
$goLatest = (Invoke-RestMethod -Uri 'https://go.dev/dl/?mode=json' -Headers @{ 'User-Agent' = 'oblachka-cores-update' }) |
    ForEach-Object { $_.version.TrimStart('go') } | Where-Object { $_ -eq $goLine -or $_.StartsWith($goLine + '.') } | Select-Object -First 1
if ($goLatest -and [version]$goLatest -gt [version]$goCurrent) { $notes += ('Go ' + $goCurrent + ' -> ' + $goLatest + ': поднять go в core\go.mod и пересобрать ядро туннеля (обычно это исправления безопасности).') }

# .NET входит в установщик (self-contained): его исправления доходят до людей только с нашим выпуском.
$dotnet = Invoke-RestMethod -Uri 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json' -Headers @{ 'User-Agent' = 'oblachka-cores-update' }
$repo = if ($env:GITHUB_REPOSITORY) { $env:GITHUB_REPOSITORY } else { 'radiypoloniy/oblachkavpn' }
$ours = Api ('repos/' + $repo + '/releases?per_page=10')
$lastRelease = $ours | Where-Object { -not $_.draft } | Select-Object -First 1
if ($lastRelease -and [datetime]$dotnet.'latest-release-date' -gt ([datetime]$lastRelease.published_at).Date) {
    $security = if ($dotnet.releases[0].security) { ' с исправлениями безопасности' } else { '' }
    $notes += ('.NET ' + $dotnet.'latest-release' + $security + ' вышел ' + $dotnet.'latest-release-date' + ', после нашего ' + $lastRelease.tag_name + ': пересобрать и выпустить.')
}

$title = @()
if ($updates.Count) { $title += ('Новые ядра: Xray ' + $latest.Xray + ', sing-box ' + $latest.SingBox) }
elseif ($urgent.Count) { $title += 'Ядра: отозван релиз' }
if ($notes.Count -and -not $updates.Count) { $title += ('Обновления: ' + (($notes | ForEach-Object { ($_ -split ':')[0] }) -join ', ')) }
$title = ($title -join '; ')
if ($urgent.Count) { $title = 'СРОЧНО — ' + $title }
$report = [ordered]@{
    Xray = $current.Xray + ' -> ' + $latest.Xray; SingBox = $current.SingBox + ' -> ' + $latest.SingBox
    Updates = $updates; Urgent = $urgent; Notes = $notes; Title = $title
}
if (-not $Apply) { $report | ConvertTo-Json -Compress; return }
if ($updates.Count -eq 0) { Write-Host 'Ядра актуальны.'; return }

$work = Join-Path $root 'vendor\downloads'
New-Item -ItemType Directory -Force $work | Out-Null
function Fetch([object]$release, [string]$name) {
    $asset = $release.assets | Where-Object { $_.name -eq $name } | Select-Object -First 1
    if (-not $asset) { throw ('В релизе ' + $release.tag_name + ' нет ' + $name) }
    $file = Join-Path $work ([IO.Path]::GetFileNameWithoutExtension($name) + '-' + $release.tag_name.TrimStart('v') + '.zip')
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $file -UseBasicParsing
    $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
    if ($asset.digest -and $asset.digest -ne ('sha256:' + $hash.ToLowerInvariant())) { throw ($name + ': SHA256 не совпал с digest GitHub.') }
    return @{ File = $file; Hash = $hash }
}
function Unpack([string]$zip) {
    $dir = Join-Path $work ([IO.Path]::GetFileNameWithoutExtension($zip))
    if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force }
    Expand-Archive -LiteralPath $zip -DestinationPath $dir
    return $dir
}
function FileHash([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
# Строки, где номер стоит рядом с именем ядра: так 1.14.3 sing-box не путается с чужими числами.
function Rename([string]$old, [string]$new, [string]$marker) {
    foreach ($relative in git -C $root ls-files '*.cs' '*.ps1' '*.md' '*.yml' '*.go' '*.json' '*.iss') {
        if ($relative -eq 'CHANGELOG.md' -or $relative -like 'zapret/*') { continue }
        $path = Join-Path $root $relative
        $lines = [IO.File]::ReadAllLines($path)
        $changed = $false
        for ($i = 0; $i -lt $lines.Length; $i++) {
            if ($lines[$i] -match $marker -and $lines[$i].Contains($old)) { $lines[$i] = $lines[$i].Replace($old, $new); $changed = $true }
        }
        if ($changed) { $bytes = [IO.File]::ReadAllBytes($path); [IO.File]::WriteAllLines($path, $lines, [Text.UTF8Encoding]::new($bytes.Length -ge 3 -and $bytes[0] -eq 239 -and $bytes[1] -eq 187)) }
    }
}
function Pin([string]$name, [string]$archiveHash, [hashtable]$files) {
    $script:text = [IO.File]::ReadAllText($fetch)
    $block = [regex]::Match($script:text, "(?s)@\{\s*Name = '" + [regex]::Escape($name) + "'.*?\n    \}")
    if (-not $block.Success) { throw ('В fetch-cores.ps1 нет блока ' + $name) }
    $updated = [regex]::Replace($block.Value, "ArchiveSha256 = '[0-9A-F]{64}'", "ArchiveSha256 = '" + $archiveHash + "'")
    foreach ($file in $files.GetEnumerator()) {
        $updated = [regex]::Replace($updated, "'" + [regex]::Escape($file.Key) + "' = '[0-9A-F]{64}'", "'" + $file.Key + "' = '" + $file.Value + "'")
    }
    [IO.File]::WriteAllText($fetch, $script:text.Replace($block.Value, $updated), [Text.UTF8Encoding]::new($true))
}

$go = Get-Command go -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1
if (-not $go -and (Test-Path -LiteralPath "$env:USERPROFILE\tools\go\bin\go.exe")) { $go = "$env:USERPROFILE\tools\go\bin\go.exe" }
if (-not $go) { throw 'Нужен Go (версия — в core\go.mod).' }

if ($updates -contains 'Xray') {
    $old = $current.Xray; $new = $latest.Xray
    $zip = Fetch $xrayRelease 'Xray-windows-64.zip'
    $dgst = Invoke-WebRequest -Uri (($xrayRelease.assets | Where-Object name -eq 'Xray-windows-64.zip.dgst').browser_download_url) -UseBasicParsing
    if (([regex]::Match([Text.Encoding]::ASCII.GetString($dgst.Content), 'SHA2-256=\s*([0-9a-f]{64})').Groups[1].Value.ToUpperInvariant()) -ne $zip.Hash) { throw 'Xray: архив не совпал с .dgst релиза.' }
    $dir = Unpack $zip.File
    $exe = FileHash (Join-Path $dir 'xray.exe')
    Pin ('Xray ' + $old) $zip.Hash @{ 'xray.exe' = $exe }
    git -C $root mv ('vendor/xray-' + $old) ('vendor/xray-' + $new)
    Copy-Item -LiteralPath (Join-Path $dir 'LICENSE') -Destination (Join-Path $root ('vendor\xray-' + $new + '\LICENSE-Xray')) -Force
    $third = Join-Path $root 'THIRD-PARTY.md'
    $md = [IO.File]::ReadAllText($third)
    $md = [regex]::Replace($md, '(?s)(## Xray-core .*?архива `Xray-windows-64.zip`: `)[0-9A-F]{64}', '${1}' + $zip.Hash)
    $md = [regex]::Replace($md, '(?s)(## Xray-core .*?SHA256 `xray.exe`: `)[0-9A-F]{64}', '${1}' + $exe)
    [IO.File]::WriteAllText($third, $md, [Text.UTF8Encoding]::new($false))
    Rename $old $new '(?i)xray'
    $commit = (Api ('repos/XTLS/Xray-core/commits/' + $xrayRelease.tag_name)).sha
    Push-Location (Join-Path $root 'core'); try { $env:GOFLAGS = ''; & $go get ('github.com/xtls/xray-core@' + $commit); if ($LASTEXITCODE -ne 0) { throw 'go get xray-core не прошёл.' } } finally { Pop-Location }
}
if ($updates -contains 'SingBox') {
    $old = $current.SingBox; $new = $latest.SingBox
    $zip = Fetch $singRelease ('sing-box-' + $new + '-windows-amd64.zip')
    $dir = Join-Path (Unpack $zip.File) ('sing-box-' + $new + '-windows-amd64')
    Pin ('sing-box ' + $old) $zip.Hash @{ 'sing-box.exe' = (FileHash (Join-Path $dir 'sing-box.exe')); 'libcronet.dll' = (FileHash (Join-Path $dir 'libcronet.dll')) }
    $text = [IO.File]::ReadAllText($fetch).Replace("sing-box-$old-windows-amd64", "sing-box-$new-windows-amd64")
    [IO.File]::WriteAllText($fetch, $text, [Text.UTF8Encoding]::new($true))
    git -C $root mv ('vendor/sing-box-' + $old) ('vendor/sing-box-' + $new)
    Copy-Item -LiteralPath (Join-Path $dir 'LICENSE') -Destination (Join-Path $root ('vendor\sing-box-' + $new + '\LICENSE-sing-box')) -Force
    Rename $old $new '(?i)sing-box|singbox|sing_box'
    Push-Location (Join-Path $root 'core'); try { $env:GOFLAGS = ''; & $go get ('github.com/sagernet/sing-box@v' + $new); if ($LASTEXITCODE -ne 0) { throw 'go get sing-box не прошёл.' } } finally { Pop-Location }
    Write-Host 'sing-box: сверьте хеши в THIRD-PARTY.md и vendor\sing-box-*\provenance.json вручную.'
}

# Ядро туннеля: те же флаги, что в build-core.ps1; новый хеш — в TunCore.cs.
Push-Location (Join-Path $root 'core')
try {
    & $go mod tidy; if ($LASTEXITCODE -ne 0) { throw 'go mod tidy не прошёл.' }
    $env:CGO_ENABLED = '0'; $env:GOOS = 'windows'; $env:GOARCH = 'amd64'; $env:GOAMD64 = 'v1'; $env:GOFLAGS = '-mod=readonly'
    $build = [IO.File]::ReadAllText((Join-Path $root 'scripts\build-core.ps1'))
    $tags = [regex]::Match($build, "\`$tags = '([^']+)'").Groups[1].Value
    $ldflags = [regex]::Match($build, '\$ldflags = "([^"]+)"').Groups[1].Value.Replace('`', '')
    $target = Join-Path $root 'vendor\cores\oblachka-core.exe'
    & $go build -trimpath -buildvcs=false -tags $tags -ldflags $ldflags -o $target .
    if ($LASTEXITCODE -ne 0) { throw 'oblachka-core не собрался с новыми ядрами: поправьте core\ под их API.' }
}
finally { Pop-Location }
$tun = Join-Path $root 'src\Network\TunCore.cs'
$code = [regex]::Replace([IO.File]::ReadAllText($tun), 'Sha256 = "[0-9A-F]{64}"', 'Sha256 = "' + (FileHash $target) + '"')
[IO.File]::WriteAllText($tun, $code, [Text.UTF8Encoding]::new($false))
Write-Host ('Обновлено: ' + ($updates -join ', ') + '. Дальше: .\build.ps1; .\test.ps1; стенд; CHANGELOG.md.')
