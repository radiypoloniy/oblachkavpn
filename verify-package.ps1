param([Parameter(Mandatory = $true)][string]$ArchivePath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$expected = @(
    'RoutePilot.exe', 'RoutePilot.dll', 'RoutePilot.deps.json', 'RoutePilot.runtimeconfig.json',
    'tools/xray.exe', 'tools/LICENSE-Xray', 'tools/sing-box.exe', 'tools/libcronet.dll',
    'tools/LICENSE-sing-box', 'tools/COPYING-GPL-3.0', 'tools/sing-box-provenance.json',
    'README.md', 'THIRD-PARTY.md', 'docs/NEXT-STEPS.md', 'docs/ARCHITECTURE.md',
    'docs/SYSTEM-TUN-ARCHITECTURE.md',
    'docs/NETWORK-SESSION-0.6.2.md',
    'docs/REMNAWAVE.md', 'docs/routepilot-response-rule.example.json',
    'docs/OBLACHKA-IMPLEMENTATION.md', 'docs/LAB.md', 'docs/lab-plan.example.json', 'docs/REDESIGN-0.5.md'
)
$archive = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $ArchivePath).Path)
try {
    if ($archive.Entries.Count -ne $expected.Count) { throw 'Неверное число файлов комплекта.' }
    $seen = @{}
    foreach ($entry in $archive.Entries) {
        if ($entry.FullName -cnotin $expected -or $seen.ContainsKey($entry.FullName)) { throw 'Посторонний/повторный файл в архиве.' }
        $seen[$entry.FullName] = $true
    }
} finally { $archive.Dispose() }
# Only our verified whitelist is extracted into a new directory inside dist.
$destination = Join-Path $PSScriptRoot ('dist\package-check-v07-' + [guid]::NewGuid().ToString('N'))
[System.IO.Compression.ZipFile]::ExtractToDirectory((Resolve-Path -LiteralPath $ArchivePath).Path, $destination)
$hashes = @{
    'tools\xray.exe' = '0D0FC0EA2B05641ACB78C01FC36AD694E7B029861B2D5EB93DA0E3E9FDA9A98F'
    'tools\sing-box.exe' = '5C19BB3F8C12D4E85B3D47FE63123A7624B314B58DD7A8E32F51004F1C4D2FAF'
    'tools\libcronet.dll' = '3217C6260FBCA5F16072E0B79735742F40109A63BB0FF88FD6B96DD6B54A2928'
}
foreach ($item in $hashes.GetEnumerator()) {
    if ((Get-FileHash -LiteralPath (Join-Path $destination $item.Key) -Algorithm SHA256).Hash -ne $item.Value) { throw 'Хеш ядра в архиве не совпал.' }
}
$reportPath = Join-Path $destination 'package-ui-check.json'
$process = Start-Process -FilePath (Join-Path $destination 'RoutePilot.exe') -ArgumentList @('--ui-self-test', ('"' + $reportPath + '"')) -WorkingDirectory $destination -WindowStyle Hidden -PassThru -Wait
if (-not (Test-Path -LiteralPath $reportPath)) { throw 'Проверка распакованного приложения не вернула отчёт.' }
$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
if ($process.ExitCode -ne 0 -or -not $report.Passed -or $report.Checks.Count -ne 22) { throw 'Проверка распакованного приложения не прошла.' }
Write-Host ('Комплект: ' + $expected.Count + ' файлов; SHA256 ядер совпадают; WPF проверок: ' + $report.Checks.Count + '.')
Write-Host ('Отчёт: ' + $reportPath)



