# Оформление живёт в одном месте — theme/tokens.json. Проверяем две вещи:
#   1) src/Themes/*.xaml и theme/tokens.css собраны из текущего tokens.json (scripts/tokens.ps1);
#   2) в разметке и коде нет цветов, размеров текста и скруглений в обход токенов.
# Скругления «таблеток» (половина высоты элемента) — геометрия, а не стиль: они перечислены явно.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$problems = @()

$temp = Join-Path ([IO.Path]::GetTempPath()) ('oblachka-tokens-' + [guid]::NewGuid().ToString('N'))
try {
    & (Join-Path $PSScriptRoot 'tokens.ps1') -OutDir $temp
    foreach ($file in 'src\Themes\Light.xaml', 'src\Themes\Dark.xaml', 'src\Themes\Tokens.xaml', 'theme\tokens.css') {
        $actual = Join-Path $root $file
        # Окончания строк не в счёт: их может менять Git при выгрузке.
        if (-not (Test-Path -LiteralPath $actual) -or [IO.File]::ReadAllText($actual).Replace("`r`n", "`n") -ne [IO.File]::ReadAllText((Join-Path $temp $file)).Replace("`r`n", "`n")) {
            $problems += "$file не совпадает с theme/tokens.json — запустите scripts/tokens.ps1"
        }
    }
} finally { if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force } }

$pillRadii = @('0', '19', '36', '44', '45')
$src = Join-Path $root 'src'
$skip = '[\\/](obj|bin|Themes)[\\/]'
foreach ($file in Get-ChildItem -LiteralPath $src -Recurse -File -Filter *.xaml | Where-Object { $_.FullName -notmatch $skip }) {
    $name = $file.FullName.Substring($src.Length + 1)
    $n = 0
    foreach ($line in [IO.File]::ReadAllLines($file.FullName)) {
        $n++
        if ($line -match '="#[0-9A-Fa-f]{3,8}"|>#[0-9A-Fa-f]{6,8}<') { $problems += "${name}:$n цвет в разметке — возьмите токен из theme/tokens.json" }
        if ($line -match '(Fill|Foreground|Background|Stroke|BorderBrush|Color|Value)="(White|Black|Red|Green|Blue|Gray|Grey|Yellow|Orange)"') { $problems += "${name}:$n именованный цвет — возьмите токен" }
        if ($line -match 'FontSize="[0-9.]+"|Property="FontSize" Value="[0-9.]+"') { $problems += "${name}:$n размер текста числом — возьмите Fs-токен" }
        foreach ($m in [regex]::Matches($line, 'CornerRadius="([0-9.]+)"|Property="(?:o:Ui\.)?CornerRadius" Value="([0-9.]+)"')) {
            $value = if ($m.Groups[1].Success) { $m.Groups[1].Value } else { $m.Groups[2].Value }
            if ($pillRadii -notcontains $value) { $problems += "${name}:$n скругление $value числом — возьмите Radius-токен" }
        }
    }
}
# Окно проверки на виртуалке — инструмент стенда, в выпуск не входит.
$codeExempt = @('Network\VmTunWindow.cs')
foreach ($file in Get-ChildItem -LiteralPath $src -Recurse -File -Filter *.cs | Where-Object { $_.FullName -notmatch $skip }) {
    $name = $file.FullName.Substring($src.Length + 1)
    if ($codeExempt -contains $name) { continue }
    $n = 0
    foreach ($line in [IO.File]::ReadAllLines($file.FullName)) {
        $n++
        if ($line.TrimStart().StartsWith('//')) { continue }
        if ($line -match 'Color\.From(A?Rgb|ScRgb)|ColorConverter|"#[0-9A-Fa-f]{6,8}"|Brushes\.(?!Transparent)\w+|Colors\.(?!Transparent)\w+') { $problems += "${name}:$n цвет в коде — Tokens.Brush/Tokens.Color" }
        if ($line -match 'FontSize\s*=\s*[0-9]') { $problems += "${name}:$n размер текста числом — Tokens.Size" }
        if ($line -match 'new CornerRadius\(\s*[1-9]') { $problems += "${name}:$n скругление числом — Tokens.Radius" }
    }
}
if ($problems.Count -gt 0) { throw ("Оформление в обход theme/tokens.json:`n" + ($problems -join "`n")) }
Write-Host 'Токены оформления проверены: всё из theme/tokens.json.'
