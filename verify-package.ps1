param([Parameter(Mandatory = $true)][string]$ArchivePath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$expected = @(
    'Oblachka.exe', 'Oblachka.dll', 'Oblachka.deps.json', 'Oblachka.runtimeconfig.json',
    'tools/xray.exe', 'tools/LICENSE-Xray', 'tools/sing-box.exe', 'tools/libcronet.dll',
    'tools/LICENSE-sing-box', 'tools/COPYING-GPL-3.0', 'tools/sing-box-provenance.json',
    'README.md', 'THIRD-PARTY.md', 'docs/NEXT-STEPS.md', 'docs/ARCHITECTURE.md',
    'install-package.ps1', 'rollback-package.ps1', 'launch-package.ps1', 'release-crypto.ps1',
    'release-manifest.json', 'docs/DELIVERY-0.8.md', 'vm-kit/START-HERE.md',
    'docs/VALIDATION-0.8.md', 'docs/CLIENT-BLOCKS-0.8.md',
    'docs/SYSTEM-TUN-ARCHITECTURE.md',
    'docs/NETWORK-SESSION-0.6.2.md',
    'docs/HELPER-IPC.md', 'docs/SYSTEM-COORDINATOR.md', 'docs/MILESTONES-0.7.md', 'docs/VALIDATION-0.7.md',
    'vm-kit/README.md', 'vm-kit/test-tun.ps1',
    'docs/REMNAWAVE.md', 'docs/oblachka-response-rule.example.json',
    'docs/OBLACHKA-IMPLEMENTATION.md', 'docs/LAB.md', 'docs/lab-plan.example.json', 'docs/REDESIGN-0.5.md'
)
$archive = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $ArchivePath).Path)
try {
    if ($archive.GetEntry('release-manifest.sig')) { $expected += 'release-manifest.sig' }
    if ($archive.Entries.Count -ne $expected.Count) { throw 'Неверное число файлов комплекта.' }
    $seen = @{}
    foreach ($entry in $archive.Entries) {
        if ($entry.FullName -cnotin $expected -or $seen.ContainsKey($entry.FullName)) { throw 'Посторонний/повторный файл в архиве.' }
        $seen[$entry.FullName] = $true
    }
} finally { $archive.Dispose() }
# Only our verified whitelist is extracted into a new directory inside dist.
$destination = Join-Path $PSScriptRoot ('dist\package-check-v08-' + [guid]::NewGuid().ToString('N'))
[System.IO.Compression.ZipFile]::ExtractToDirectory((Resolve-Path -LiteralPath $ArchivePath).Path, $destination)
$hashes = @{
    'tools\xray.exe' = '0D0FC0EA2B05641ACB78C01FC36AD694E7B029861B2D5EB93DA0E3E9FDA9A98F'
    'tools\sing-box.exe' = '5C19BB3F8C12D4E85B3D47FE63123A7624B314B58DD7A8E32F51004F1C4D2FAF'
    'tools\libcronet.dll' = '3217C6260FBCA5F16072E0B79735742F40109A63BB0FF88FD6B96DD6B54A2928'
}
foreach ($item in $hashes.GetEnumerator()) {
    if ((Get-FileHash -LiteralPath (Join-Path $destination $item.Key) -Algorithm SHA256).Hash -ne $item.Value) { throw 'Хеш ядра в архиве не совпал.' }
}
# Проверки интерфейса выполняются в test.ps1 на отдельной сборке с -p:IncludeTestCode=true.
# В поставляемом комплекте точки входа --ui-self-test нет, поэтому здесь проверяется обратное:
# что тестовый код в комплект не попал.
& (Join-Path $PSScriptRoot 'check-no-test-code.ps1') -Directory $destination
& (Join-Path $destination 'vm-kit\test-tun.ps1') -SocksPort 10808 -PeerAddresses @('192.0.2.1')
Write-Host ('Комплект: ' + $expected.Count + ' файлов; SHA256 ядер совпадают; тестового кода нет.')
Write-Host ('Распаковано для проверки: ' + $destination)



