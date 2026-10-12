param([string]$SigningKeyPath)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-crypto.ps1')
Add-Type -AssemblyName System.IO.Compression
$projectRoot = $PSScriptRoot
$packageDirectory = Join-Path $projectRoot 'dist'
[System.IO.Directory]::CreateDirectory($packageDirectory) | Out-Null
$archivePath = Join-Path $packageDirectory ('Oblachka-0.8.2-win-x64-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8) + '.zip')
$files = [ordered]@{
    'RoutePilot.exe' = 'app-v09\RoutePilot.exe'
    'RoutePilot.dll' = 'app-v09\RoutePilot.dll'
    'RoutePilot.deps.json' = 'app-v09\RoutePilot.deps.json'
    'RoutePilot.runtimeconfig.json' = 'app-v09\RoutePilot.runtimeconfig.json'
    'tools/xray.exe' = 'app-v09\tools\xray.exe'
    'tools/LICENSE-Xray' = 'app-v09\tools\LICENSE-Xray'
    'tools/sing-box.exe' = 'app-v09\tools\sing-box.exe'
    'tools/libcronet.dll' = 'app-v09\tools\libcronet.dll'
    'tools/LICENSE-sing-box' = 'app-v09\tools\LICENSE-sing-box'
    'tools/COPYING-GPL-3.0' = 'app-v09\tools\COPYING-GPL-3.0'
    'tools/sing-box-provenance.json' = 'vendor\sing-box-1.14.3\provenance.json'
    'docs/VM-TUN-0.8.1.md' = 'docs\VM-TUN-0.8.1.md'
    'README.md' = 'README.md'
    'install-package.ps1' = 'install-package.ps1'
    'rollback-package.ps1' = 'rollback-package.ps1'
    'launch-package.ps1' = 'launch-package.ps1'
    'release-crypto.ps1' = 'release-crypto.ps1'
    'THIRD-PARTY.md' = 'THIRD-PARTY.md'
    'docs/NEXT-STEPS.md' = 'docs\NEXT-STEPS.md'
    'docs/ARCHITECTURE.md' = 'docs\ARCHITECTURE.md'
    'docs/SYSTEM-TUN-ARCHITECTURE.md' = 'docs\SYSTEM-TUN-ARCHITECTURE.md'
    'docs/NETWORK-SESSION-0.6.2.md' = 'docs\NETWORK-SESSION-0.6.2.md'
    'docs/HELPER-IPC.md' = 'docs\HELPER-IPC.md'
    'docs/SYSTEM-COORDINATOR.md' = 'docs\SYSTEM-COORDINATOR.md'
    'docs/MILESTONES-0.7.md' = 'docs\MILESTONES-0.7.md'
    'docs/VALIDATION-0.7.md' = 'docs\VALIDATION-0.7.md'
    'docs/DELIVERY-0.8.md' = 'docs\DELIVERY-0.8.md'
    'docs/VALIDATION-0.8.md' = 'docs\VALIDATION-0.8.md'
    'docs/CLIENT-BLOCKS-0.8.md' = 'docs\CLIENT-BLOCKS-0.8.md'
    'vm-kit/START-HERE.md' = 'vm-kit\START-HERE.md'
    'vm-kit/README.md' = 'vm-kit\README.md'
    'vm-kit/test-tun.ps1' = 'vm-kit\test-tun.ps1'
    'docs/REMNAWAVE.md' = 'docs\REMNAWAVE.md'
    'docs/routepilot-response-rule.example.json' = 'docs\routepilot-response-rule.example.json'
    'docs/OBLACHKA-IMPLEMENTATION.md' = 'docs\OBLACHKA-IMPLEMENTATION.md'
    'docs/LAB.md' = 'docs\LAB.md'
    'docs/lab-plan.example.json' = 'docs\lab-plan.example.json'
    'docs/REDESIGN-0.5.md' = 'docs\REDESIGN-0.5.md'
}
foreach ($relativePath in $files.Values) {
    if (-not (Test-Path -LiteralPath (Join-Path $projectRoot $relativePath) -PathType Leaf)) { throw ('Нет файла: ' + $relativePath) }
}
$manifest = [ordered]@{ Version = 1; Release = '0.8.2'; Files = @() }
foreach ($item in $files.GetEnumerator()) {
    $source = Join-Path $projectRoot $item.Value
    $manifest.Files += @{ Path = $item.Key; Size = (Get-Item -LiteralPath $source).Length; Sha256 = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash }
}
$manifestBytes = [Text.Encoding]::UTF8.GetBytes(($manifest | ConvertTo-Json -Depth 4))
$signature = $null
if ($SigningKeyPath) {
    $rsa = Read-ReleaseRsa $SigningKeyPath -Private
    try { $signature = [Text.Encoding]::UTF8.GetBytes([Convert]::ToBase64String($rsa.SignData($manifestBytes, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1))) } finally { $rsa.Dispose() }
}
$stream = [System.IO.File]::Open($archivePath, [System.IO.FileMode]::CreateNew)
try {
    $archive = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($item in $files.GetEnumerator()) {
            $entry = $archive.CreateEntry($item.Key, [System.IO.Compression.CompressionLevel]::Optimal)
            $input = [System.IO.File]::OpenRead((Join-Path $projectRoot $item.Value))
            try { $output = $entry.Open(); try { $input.CopyTo($output) } finally { $output.Dispose() } } finally { $input.Dispose() }
        }
        foreach ($extra in @{ 'release-manifest.json' = $manifestBytes; 'release-manifest.sig' = $signature }.GetEnumerator()) {
            if ($null -eq $extra.Value) { continue }
            $entry = $archive.CreateEntry($extra.Key); $output = $entry.Open()
            try { $output.Write($extra.Value, 0, $extra.Value.Length) } finally { $output.Dispose() }
        }
    } finally { $archive.Dispose() }
} finally { $stream.Dispose() }
Write-Host $archivePath
Write-Host ('SHA256: ' + (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash)



