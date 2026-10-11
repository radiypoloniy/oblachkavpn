$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
$projectRoot = $PSScriptRoot
$packageDirectory = Join-Path $projectRoot 'dist'
[System.IO.Directory]::CreateDirectory($packageDirectory) | Out-Null
$archivePath = Join-Path $packageDirectory ('Oblachka-0.8.0-win-x64-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8) + '.zip')
$files = [ordered]@{
    'RoutePilot.exe' = 'app-v08\RoutePilot.exe'
    'RoutePilot.dll' = 'app-v08\RoutePilot.dll'
    'RoutePilot.deps.json' = 'app-v08\RoutePilot.deps.json'
    'RoutePilot.runtimeconfig.json' = 'app-v08\RoutePilot.runtimeconfig.json'
    'tools/xray.exe' = 'app-v08\tools\xray.exe'
    'tools/LICENSE-Xray' = 'app-v08\tools\LICENSE-Xray'
    'tools/sing-box.exe' = 'app-v08\tools\sing-box.exe'
    'tools/libcronet.dll' = 'app-v08\tools\libcronet.dll'
    'tools/LICENSE-sing-box' = 'app-v08\tools\LICENSE-sing-box'
    'tools/COPYING-GPL-3.0' = 'app-v08\tools\COPYING-GPL-3.0'
    'tools/sing-box-provenance.json' = 'vendor\sing-box-1.14.3\provenance.json'
    'README.md' = 'README.md'
    'THIRD-PARTY.md' = 'THIRD-PARTY.md'
    'docs/NEXT-STEPS.md' = 'docs\NEXT-STEPS.md'
    'docs/ARCHITECTURE.md' = 'docs\ARCHITECTURE.md'
    'docs/SYSTEM-TUN-ARCHITECTURE.md' = 'docs\SYSTEM-TUN-ARCHITECTURE.md'
    'docs/NETWORK-SESSION-0.6.2.md' = 'docs\NETWORK-SESSION-0.6.2.md'
    'docs/HELPER-IPC.md' = 'docs\HELPER-IPC.md'
    'docs/SYSTEM-COORDINATOR.md' = 'docs\SYSTEM-COORDINATOR.md'
    'docs/MILESTONES-0.7.md' = 'docs\MILESTONES-0.7.md'
    'docs/VALIDATION-0.7.md' = 'docs\VALIDATION-0.7.md'
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
$stream = [System.IO.File]::Open($archivePath, [System.IO.FileMode]::CreateNew)
try {
    $archive = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($item in $files.GetEnumerator()) {
            $entry = $archive.CreateEntry($item.Key, [System.IO.Compression.CompressionLevel]::Optimal)
            $input = [System.IO.File]::OpenRead((Join-Path $projectRoot $item.Value))
            try { $output = $entry.Open(); try { $input.CopyTo($output) } finally { $output.Dispose() } } finally { $input.Dispose() }
        }
    } finally { $archive.Dispose() }
} finally { $stream.Dispose() }
Write-Host $archivePath



