# Проверяет, что в поставляемой сборке нет тестового кода.
# Имена типов и методов лежат в метаданных сборки как UTF-8, поэтому достаточно
# поискать их байты в файле: присутствие символа означает, что код скомпилирован внутрь.
param([Parameter(Mandatory = $true)][string]$Directory)
$ErrorActionPreference = 'Stop'

$forbiddenSymbols = @(
    'RunUiFixtureAsync',   # части MainWindow из *Fixtures.cs
    'LoadPreviewProfiles',
    'PrepareDesignPreview',
    'SelfTests',           # отдельный проект проверок
    'LabRunner',           # лабораторный стенд
    'DesignPreview',       # захват превью интерфейса
    'FixtureTrace'         # сырой поток ядра для синтетических проверок
)
$forbiddenFiles = @('Oblachka.Tests.exe', 'Oblachka.Tests.dll')

foreach ($name in $forbiddenFiles) {
    if (Test-Path -LiteralPath (Join-Path $Directory $name)) { throw ('В поставляемой сборке лежит файл проверок: ' + $name) }
}

$assembly = Join-Path $Directory 'Oblachka.dll'
if (-not (Test-Path -LiteralPath $assembly)) { throw ('Не найдено ' + $assembly) }
$bytes = [IO.File]::ReadAllBytes($assembly)
$found = @()
foreach ($symbol in $forbiddenSymbols) {
    $needle = [Text.Encoding]::UTF8.GetBytes($symbol)
    $limit = $bytes.Length - $needle.Length
    for ($i = 0; $i -le $limit; $i++) {
        if ($bytes[$i] -ne $needle[0]) { continue }
        $match = $true
        for ($j = 1; $j -lt $needle.Length; $j++) {
            if ($bytes[$i + $j] -ne $needle[$j]) { $match = $false; break }
        }
        if ($match) { $found += $symbol; break }
    }
}
if ($found.Count -gt 0) { throw ('В поставляемой сборке найден тестовый код: ' + ($found -join ', ')) }
Write-Host ('Поставляемая сборка без тестового кода: проверено ' + $forbiddenSymbols.Count + ' символов в Oblachka.dll.')
