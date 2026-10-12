param(
    [Parameter(Mandatory = $true)][string]$ArchivePath,
    [Parameter(Mandatory = $true)][string]$InstallRoot,
    [string]$ExpectedSha256,
    [string]$TrustedPublicKey,
    [switch]$CreateShortcut
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-crypto.ps1')
Add-Type -AssemblyName System.IO.Compression.FileSystem
if (-not $ExpectedSha256 -and -not $TrustedPublicKey) { throw 'A trusted archive SHA256 or public key is required.' }
if ($ExpectedSha256 -and ($ExpectedSha256 -notmatch '^[A-Fa-f0-9]{64}$' -or (Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash -ne $ExpectedSha256)) { throw 'Archive SHA256 mismatch.' }
$root = [IO.Path]::GetFullPath($InstallRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
if ($root -eq [IO.Path]::GetPathRoot($root).TrimEnd('\')) { throw 'Choose a dedicated installation directory.' }
$archive = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $ArchivePath).Path)
try {
    if ($archive.Entries.Count -gt 128) { throw 'Too many archive files.' }
    $entries = @{}; $total = 0L
    foreach ($entry in $archive.Entries) {
        $name = $entry.FullName
        $badSegment = @($name.Split('/') | Where-Object { $_ -in @('.', '..', '') -or $_.EndsWith('.') -or $_ -match '^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)' }).Count -gt 0
        if ($name -notmatch '^[a-zA-Z0-9][a-zA-Z0-9._/-]*$' -or $badSegment -or $entries.ContainsKey($name)) { throw 'Unsafe archive path.' }
        $total += $entry.Length
        if ($entry.Length -gt 200MB -or $total -gt 350MB) { throw 'Archive exceeds size limit.' }
        $entries[$name] = $entry
    }
    $manifestEntry = $entries['release-manifest.json']
    if (-not $manifestEntry -or $manifestEntry.Length -gt 65536) { throw 'Missing release manifest.' }
    $memory = [IO.MemoryStream]::new(); $stream = $manifestEntry.Open()
    try { $stream.CopyTo($memory); $manifestBytes = $memory.ToArray() } finally { $stream.Dispose(); $memory.Dispose() }
    if ($TrustedPublicKey) {
        $signatureEntry = $entries['release-manifest.sig']
        if (-not $signatureEntry -or $signatureEntry.Length -gt 4096) { throw 'Missing signature.' }
        $reader = [IO.StreamReader]::new($signatureEntry.Open())
        try { $signature = [Convert]::FromBase64String($reader.ReadToEnd()) } finally { $reader.Dispose() }
        $rsa = Read-ReleaseRsa $TrustedPublicKey
        try { if (-not $rsa.VerifyData($manifestBytes, $signature, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)) { throw 'Invalid release signature.' } } finally { $rsa.Dispose() }
    }
    $manifest = [Text.Encoding]::UTF8.GetString($manifestBytes) | ConvertFrom-Json
    if ($manifest.Version -ne 1 -or $manifest.Release -notmatch '^\d+\.\d+\.\d+$' -or $manifest.Files.Count -lt 10 -or $manifest.Files.Count -gt 126) { throw 'Invalid manifest.' }
    $expectedCount = $manifest.Files.Count + 1 + [int]$entries.ContainsKey('release-manifest.sig')
    if ($entries.Count -ne $expectedCount) { throw 'Unexpected package entries.' }
    $verified = @{}
    foreach ($file in $manifest.Files) {
        if ($verified.ContainsKey($file.Path) -or -not $entries.ContainsKey($file.Path) -or $file.Sha256 -notmatch '^[A-Fa-f0-9]{64}$' -or $entries[$file.Path].Length -ne $file.Size) { throw 'Invalid file manifest.' }
        $algorithm = [Security.Cryptography.SHA256]::Create(); $stream = $entries[$file.Path].Open()
        try { $hash = ([BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-', '') } finally { $stream.Dispose(); $algorithm.Dispose() }
        if ($hash -ne $file.Sha256) { throw 'Package file changed.' }; $verified[$file.Path] = $true
    }
    if (-not $verified.ContainsKey('Oblachka.exe') -or -not $verified.ContainsKey('Oblachka.dll')) { throw 'Incomplete client package.' }
    $revision = 'release-' + $manifest.Release + '-' + [guid]::NewGuid().ToString('N')
    $destination = Join-Path $root $revision
    [IO.Directory]::CreateDirectory($destination) | Out-Null
    foreach ($entry in $archive.Entries) {
        $target = [IO.Path]::GetFullPath((Join-Path $destination $entry.FullName))
        if (-not $target.StartsWith($destination + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Path escaped installation.' }
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
        $input = $entry.Open(); $output = [IO.File]::Open($target, [IO.FileMode]::CreateNew)
        try { $input.CopyTo($output) } finally { $input.Dispose(); $output.Dispose() }
    }
} finally { $archive.Dispose() }
$pointerPath = Join-Path $root 'current.json'
$previous = $null
if (Test-Path -LiteralPath $pointerPath) { $previous = (Get-Content -LiteralPath $pointerPath -Raw | ConvertFrom-Json).Current }
$staged = Join-Path $root ('current.pending-' + [guid]::NewGuid().ToString('N'))
[IO.File]::WriteAllText($staged, (@{ Current = $revision; Previous = $previous } | ConvertTo-Json))
# File.Replace atomically updates the pointer and keeps a backup, without touching any release.
if (Test-Path -LiteralPath $pointerPath) { [IO.File]::Replace($staged, $pointerPath, (Join-Path $root ('current.backup-' + [guid]::NewGuid().ToString('N') + '.json'))) } else { [IO.File]::Move($staged, $pointerPath) }
if ($CreateShortcut) {
    $shortcutPath = Join-Path $root ('Oblachka-' + [guid]::NewGuid().ToString('N') + '.lnk')
    $shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcutPath)
    $shortcut.TargetPath = Join-Path $PSHOME 'powershell.exe'
    if (-not (Test-Path -LiteralPath $shortcut.TargetPath)) { $shortcut.TargetPath = Join-Path $PSHOME 'pwsh.exe' }
    $shortcut.Arguments = '-NoProfile -File "' + (Join-Path $destination 'launch-package.ps1') + '" -InstallRoot "' + $root + '"'
    $shortcut.WorkingDirectory = $root; $shortcut.WindowStyle = 7; $shortcut.Save()
}
Write-Host ('Installed: ' + $destination)
Write-Host 'Earlier releases and encrypted profile storage are preserved. Close the earlier client before starting this version.'
