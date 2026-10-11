$ErrorActionPreference = 'Stop'
$sourceRoot = Join-Path $PSScriptRoot 'src'
$violations = @()
$largest = 0
# Include future subdirectories, excluding compiler-generated trees.
foreach ($file in Get-ChildItem -LiteralPath $sourceRoot -File -Recurse | Where-Object {
    $_.Extension -in @('.cs', '.xaml') -and $_.FullName -notmatch '\\(obj|bin)\\'
}) {
    $lines = [System.IO.File]::ReadAllLines($file.FullName).Length
    $bytes = $file.Length
    $largest = [Math]::Max($largest, $lines)
    if ($lines -gt 250 -or $bytes -gt 24576) { $violations += ($file.Name + ': ' + $lines + ' строк, ' + $bytes + ' байт') }
}
if ($violations.Count -gt 0) { throw ('Раздели исходники по ответственности (лимит 250 строк / 24 КиБ): ' + ($violations -join '; ')) }
Write-Host ('Размеры исходников проверены. Максимум: ' + $largest + ' строк.')
