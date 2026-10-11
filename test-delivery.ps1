param([Parameter(Mandatory = $true)][string]$ArchivePath, [string]$TrustedPublicKey)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = Join-Path $PSScriptRoot ('dist\delivery-validation-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$hash = (Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash
$installed = Join-Path $root 'installed'
$trust = @{ ExpectedSha256 = $hash }; if ($TrustedPublicKey) { $trust.TrustedPublicKey = $TrustedPublicKey }
& (Join-Path $PSScriptRoot 'install-package.ps1') -ArchivePath $ArchivePath -InstallRoot $installed @trust
$first = Get-Content -LiteralPath (Join-Path $installed 'current.json') -Raw | ConvertFrom-Json
& (Join-Path $PSScriptRoot 'install-package.ps1') -ArchivePath $ArchivePath -InstallRoot $installed -ExpectedSha256 $hash
$second = Get-Content -LiteralPath (Join-Path $installed 'current.json') -Raw | ConvertFrom-Json
if ($first.Current -eq $second.Current -or $second.Previous -ne $first.Current) { throw 'Update pointer failed.' }
& (Join-Path $PSScriptRoot 'rollback-package.ps1') -InstallRoot $installed
$rolled = Get-Content -LiteralPath (Join-Path $installed 'current.json') -Raw | ConvertFrom-Json
if ($rolled.Current -ne $first.Current -or -not (Test-Path -LiteralPath (Join-Path $installed $second.Current))) { throw 'Rollback lost previous release.' }
function Expect-Rejection([scriptblock]$Action) {
    $rejected = $false
    try { & $Action } catch { $rejected = $true }
    if (-not $rejected) { throw 'Invalid package accepted.' }
}
Expect-Rejection { & (Join-Path $PSScriptRoot 'install-package.ps1') -ArchivePath $ArchivePath -InstallRoot (Join-Path $root 'wrong-hash') -ExpectedSha256 ('0' * 64) }
if (Test-Path -LiteralPath (Join-Path $root 'wrong-hash')) { throw 'Invalid hash wrote files.' }
& (Join-Path $PSScriptRoot 'create-release-key.ps1') -Directory (Join-Path $root 'untrusted-key')
Expect-Rejection { & (Join-Path $PSScriptRoot 'install-package.ps1') -ArchivePath $ArchivePath -InstallRoot (Join-Path $root 'wrong-key') -TrustedPublicKey (Join-Path $root 'untrusted-key\public.json') }
if (Test-Path -LiteralPath (Join-Path $root 'wrong-key')) { throw 'Invalid signature wrote files.' }
# Repack with an unsafe path, preserving original archives and all extracted fixtures.
$unsafe = Join-Path $root 'unsafe.zip'
$inputArchive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
$outputStream = [IO.File]::Open($unsafe, [IO.FileMode]::CreateNew)
$outputArchive = [IO.Compression.ZipArchive]::new($outputStream, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($entry in $inputArchive.Entries) {
        $copy = $outputArchive.CreateEntry($entry.FullName); $input = $entry.Open(); $output = $copy.Open()
        try { $input.CopyTo($output) } finally { $input.Dispose(); $output.Dispose() }
    }
    $entry = $outputArchive.CreateEntry('../outside.txt'); $output = $entry.Open()
    try { $bytes = [Text.Encoding]::UTF8.GetBytes('fixture'); $output.Write($bytes, 0, $bytes.Length) } finally { $output.Dispose() }
} finally { $inputArchive.Dispose(); $outputArchive.Dispose(); $outputStream.Dispose() }
Expect-Rejection { & (Join-Path $PSScriptRoot 'install-package.ps1') -ArchivePath $unsafe -InstallRoot (Join-Path $root 'unsafe-target') -ExpectedSha256 (Get-FileHash -LiteralPath $unsafe -Algorithm SHA256).Hash }
if (Test-Path -LiteralPath (Join-Path $root 'unsafe-target')) { throw 'Unsafe ZIP wrote files.' }
$tampered = Join-Path $root 'tampered.zip'
$inputArchive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
$outputStream = [IO.File]::Open($tampered, [IO.FileMode]::CreateNew)
$outputArchive = [IO.Compression.ZipArchive]::new($outputStream, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($entry in $inputArchive.Entries) {
        $copy = $outputArchive.CreateEntry($entry.FullName); $output = $copy.Open()
        try {
            if ($entry.FullName -eq 'README.md') { $bytes = [Text.Encoding]::UTF8.GetBytes('changed fixture'); $output.Write($bytes, 0, $bytes.Length) }
            else { $input = $entry.Open(); try { $input.CopyTo($output) } finally { $input.Dispose() } }
        } finally { $output.Dispose() }
    }
} finally { $inputArchive.Dispose(); $outputArchive.Dispose(); $outputStream.Dispose() }
$tamperedTrust = @{ ExpectedSha256 = (Get-FileHash -LiteralPath $tampered -Algorithm SHA256).Hash }
if ($TrustedPublicKey) { $tamperedTrust.TrustedPublicKey = $TrustedPublicKey }
Expect-Rejection { & (Join-Path $PSScriptRoot 'install-package.ps1') -ArchivePath $tampered -InstallRoot (Join-Path $root 'tampered-target') @tamperedTrust }
if (Test-Path -LiteralPath (Join-Path $root 'tampered-target')) { throw 'Tampered file wrote installation.' }
[IO.File]::WriteAllText((Join-Path $root 'result.json'), (@{ Passed = $true; Checks = @('Install', 'Update', 'Rollback', 'WrongHash', 'UntrustedKey', 'Traversal', 'TamperedFile'); NetworkChanged = $false } | ConvertTo-Json))
Write-Host ('Delivery checks passed: ' + (Join-Path $root 'result.json'))
