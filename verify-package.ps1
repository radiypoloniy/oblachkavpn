param([Parameter(Mandatory = $true)][string]$ArchivePath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

# Состав комплекта сверяется с release-manifest.json внутри архива, а не со вторым
# списком файлов. Прежде список был продублирован здесь и разошёлся с package.ps1:
# проверка падала на числе файлов, хотя комплект был корректным.
#
# Манифест сам по себе доверия не даёт: подпись проверяет install-package.ps1 закреплённым
# ключом при установке. Здесь проверяется целостность комплекта и то, что в него не попало
# лишнее — тестовый код и ядра не той версии.

$archivePath = (Resolve-Path -LiteralPath $ArchivePath).Path
$archive = [System.IO.Compression.ZipFile]::OpenRead($archivePath)
try {
    $manifestEntry = $archive.GetEntry('release-manifest.json')
    if (-not $manifestEntry) { throw 'В архиве нет release-manifest.json.' }
    $reader = [IO.StreamReader]::new($manifestEntry.Open(), [Text.UTF8Encoding]::new($false))
    try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    if ($manifest.Version -ne 1) { throw 'Неподдерживаемая версия манифеста.' }
    if (-not $manifest.Release) { throw 'В манифесте нет версии выпуска.' }

    $expected = @($manifest.Files.Path) + 'release-manifest.json'
    if ($archive.GetEntry('release-manifest.sig')) { $expected += 'release-manifest.sig' }
    if ($archive.Entries.Count -ne $expected.Count) {
        $extra = @($archive.Entries.FullName | Where-Object { $_ -cnotin $expected })
        $missing = @($expected | Where-Object { $_ -cnotin $archive.Entries.FullName })
        throw ('Состав архива не совпал с манифестом. Лишние: ' + (($extra -join ', ') -replace '^$', 'нет') +
               '; отсутствуют: ' + (($missing -join ', ') -replace '^$', 'нет'))
    }
    $seen = @{}
    foreach ($entry in $archive.Entries) {
        if ($entry.FullName -cnotin $expected -or $seen.ContainsKey($entry.FullName)) { throw ('Посторонний или повторный файл в архиве: ' + $entry.FullName) }
        $seen[$entry.FullName] = $true
    }
} finally { $archive.Dispose() }

$destination = Join-Path $PSScriptRoot ('dist\package-check-' + $manifest.Release + '-' + [guid]::NewGuid().ToString('N'))
[System.IO.Compression.ZipFile]::ExtractToDirectory($archivePath, $destination)

# Каждый файл сверяется с манифестом: размер и SHA256.
foreach ($file in $manifest.Files) {
    $path = Join-Path $destination ($file.Path -replace '/', '\')
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw ('Файл манифеста отсутствует в архиве: ' + $file.Path) }
    $item = Get-Item -LiteralPath $path
    if ($item.Length -ne $file.Size) { throw ('Размер не совпал: ' + $file.Path) }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.Sha256) { throw ('SHA256 не совпал: ' + $file.Path) }
}

# Ядра дополнительно сверяются с закреплёнными значениями: манифест собирается из того же
# каталога сборки, поэтому подменённое ядро он бы описал как «правильное».
$pinned = @{
    'tools\xray.exe' = '0D0FC0EA2B05641ACB78C01FC36AD694E7B029861B2D5EB93DA0E3E9FDA9A98F'
    'tools\sing-box.exe' = '5C19BB3F8C12D4E85B3D47FE63123A7624B314B58DD7A8E32F51004F1C4D2FAF'
    'tools\libcronet.dll' = '3217C6260FBCA5F16072E0B79735742F40109A63BB0FF88FD6B96DD6B54A2928'
}
foreach ($item in $pinned.GetEnumerator()) {
    if ((Get-FileHash -LiteralPath (Join-Path $destination $item.Key) -Algorithm SHA256).Hash -ne $item.Value) { throw ('Ядро не соответствует закреплённой версии: ' + $item.Key) }
}

# Проверки интерфейса выполняются в test.ps1 на отдельной сборке с -p:IncludeTestCode=true.
# В поставляемом комплекте точки входа --ui-self-test нет, поэтому здесь проверяется обратное:
# что тестовый код в комплект не попал.
& (Join-Path $PSScriptRoot 'check-no-test-code.ps1') -Directory $destination
& (Join-Path $destination 'vm-kit\test-tun.ps1') -SocksPort 10808 -PeerAddresses @('192.0.2.1')

$signed = if (Test-Path -LiteralPath (Join-Path $destination 'release-manifest.sig')) { 'подписан' } else { 'без подписи' }
Write-Host ('Комплект ' + $manifest.Release + ': ' + $manifest.Files.Count + ' файлов, ' + $signed + '; размеры и SHA256 совпали с манифестом; ядра закреплённых версий; тестового кода нет.')
Write-Host ('Распаковано для проверки: ' + $destination)
