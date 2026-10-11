param([Parameter(Mandatory = $true)][string]$InstallRoot)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
$pointerPath = Join-Path $root 'current.json'
$pointer = Get-Content -LiteralPath $pointerPath -Raw | ConvertFrom-Json
if ($pointer.Previous -notmatch '^release-\d+\.\d+\.\d+-[a-f0-9]{32}$' -or $pointer.Current -notmatch '^release-\d+\.\d+\.\d+-[a-f0-9]{32}$') { throw 'No previous release.' }
$previous = Join-Path $root $pointer.Previous
if (-not (Test-Path -LiteralPath (Join-Path $previous 'RoutePilot.exe'))) { throw 'Previous client missing.' }
$staged = Join-Path $root ('current.pending-' + [guid]::NewGuid().ToString('N'))
[IO.File]::WriteAllText($staged, (@{ Current = $pointer.Previous; Previous = $pointer.Current } | ConvertTo-Json))
[IO.File]::Replace($staged, $pointerPath, (Join-Path $root ('current.backup-' + [guid]::NewGuid().ToString('N') + '.json')))
Write-Host ('Rolled back: ' + $previous + '\RoutePilot.exe. No files removed; close the newer client before launch.')
