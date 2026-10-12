param([Parameter(Mandatory = $true)][string]$InstallRoot)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
$pointer = Get-Content -LiteralPath (Join-Path $root 'current.json') -Raw | ConvertFrom-Json
if ($pointer.Current -notmatch '^release-\d+\.\d+\.\d+-[a-f0-9]{32}$') { throw 'Invalid release pointer.' }
$directory = Join-Path $root $pointer.Current
$manifest = Get-Content -LiteralPath (Join-Path $directory 'release-manifest.json') -Raw | ConvertFrom-Json
foreach ($file in $manifest.Files) {
    $target = [IO.Path]::GetFullPath((Join-Path $directory $file.Path))
    if (-not $target.StartsWith($directory + '\', [StringComparison]::OrdinalIgnoreCase) -or
        (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $file.Sha256) { throw 'Installed package changed. Reinstall a verified archive.' }
}
Start-Process -FilePath (Join-Path $directory 'Oblachka.exe') -WorkingDirectory $directory
