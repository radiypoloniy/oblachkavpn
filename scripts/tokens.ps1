# Пересобирает оформление из единственного источника — theme/tokens.json:
#   src/Themes/Light.xaml, src/Themes/Dark.xaml — цвета тем (ключи одинаковые, берутся через DynamicResource);
#   src/Themes/Tokens.xaml — шрифты, размеры текста и скругления (общие для тем);
#   theme/tokens.css — те же токены для HTML-макетов и документов.
# -OutDir — писать в другую папку (так check-tokens.ps1 проверяет, что сгенерированное не устарело).
param([string]$OutDir)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$tokens = Get-Content -LiteralPath (Join-Path $root 'theme\tokens.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$inv = [Globalization.CultureInfo]::InvariantCulture
$header = '  <!-- Сгенерировано scripts/tokens.ps1 из theme/tokens.json. Руками не править: изменения пропадут. -->'
$xmlns = '<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"'
function Escape([string]$text) { [Security.SecurityElement]::Escape($text) }

function Theme([string]$which) {
    $lines = @(($xmlns + '>'), $header)
    foreach ($p in $tokens.color.PSObject.Properties) {
        if ($p.Value.shared) { continue }
        $value = $p.Value.$which
        if ($value -notmatch '^#([0-9A-F]{6}|[0-9A-F]{8})$') { throw "Цвет $($p.Name) ($which): ожидается #RRGGBB или #AARRGGBB, получено $value" }
        $comment = if ($p.Value.role) { '  <!-- ' + (Escape $p.Value.role) + ' -->' } else { '' }
        if ($p.Value.kind -eq 'color') { $lines += "  <Color x:Key=`"$($p.Name)`">$value</Color>$comment" }
        else { $lines += "  <SolidColorBrush x:Key=`"$($p.Name)`" Color=`"$value`"/>$comment" }
    }
    $lines += '</ResourceDictionary>'
    $lines -join "`r`n"
}

function Shared {
    $lines = @(($xmlns + ' xmlns:sys="clr-namespace:System;assembly=System.Runtime">'), $header)
    # Общие для тем цвета: нужны там, где ссылка на тему невозможна (эффекты внутри стилей).
    foreach ($p in $tokens.color.PSObject.Properties) { if ($p.Value.shared) { $lines += "  <Color x:Key=`"$($p.Name)`">$($p.Value.light)</Color>  <!-- $(Escape $p.Value.role) -->" } }
    foreach ($p in $tokens.font.PSObject.Properties) { $lines += "  <FontFamily x:Key=`"$($p.Name)`">$(Escape $p.Value.value)</FontFamily>  <!-- $(Escape $p.Value.role) -->" }
    foreach ($p in $tokens.size.PSObject.Properties) { $lines += "  <sys:Double x:Key=`"$($p.Name)`">$(([double]$p.Value.value).ToString($inv))</sys:Double>  <!-- $(Escape $p.Value.role) -->" }
    foreach ($p in $tokens.radius.PSObject.Properties) { $lines += "  <CornerRadius x:Key=`"$($p.Name)`">$(([double]$p.Value.value).ToString($inv))</CornerRadius>  <!-- $(Escape $p.Value.role) -->" }
    $lines += '</ResourceDictionary>'
    $lines -join "`r`n"
}

function Css {
    function Name([string]$key) { '--' + ([regex]::Replace($key, '(?<=[a-z0-9])([A-Z])', '-$1')).ToLowerInvariant() }
    function Css([string]$hex) { if ($hex.Length -eq 9) { $a = [Convert]::ToInt32($hex.Substring(1, 2), 16) / 255; "rgba($([Convert]::ToInt32($hex.Substring(3,2),16)), $([Convert]::ToInt32($hex.Substring(5,2),16)), $([Convert]::ToInt32($hex.Substring(7,2),16)), $($a.ToString('0.###', $inv)))" } else { $hex } }
    $light = @(); $dark = @()
    foreach ($p in $tokens.color.PSObject.Properties) { $light += "  $(Name $p.Name): $(Css $p.Value.light);"; $dark += "  $(Name $p.Name): $(Css $p.Value.dark);" }
    $shared = @()
    foreach ($p in $tokens.font.PSObject.Properties) { $shared += "  $(Name $p.Name): $(($p.Value.value -split ',\s*' | ForEach-Object { '"' + $_ + '"' }) -join ', '), system-ui, sans-serif;" }
    foreach ($p in $tokens.size.PSObject.Properties) { $shared += "  $(Name $p.Name): $(([double]$p.Value.value).ToString($inv))px;" }
    foreach ($p in $tokens.radius.PSObject.Properties) { $shared += "  $(Name $p.Name): $(([double]$p.Value.value).ToString($inv))px;" }
    $d = $dark -join "`r`n"
    @('/* Сгенерировано scripts/tokens.ps1 из theme/tokens.json. Руками не править. */',
      ':root {', ($light + $shared -join "`r`n"), '}',
      '@media (prefers-color-scheme: dark) { :root:not([data-theme="light"]) {', $d, '  color-scheme: dark; } }',
      ':root[data-theme="dark"] {', $d, '  color-scheme: dark; }') -join "`r`n"
}

$targets = [ordered]@{
    'src\Themes\Light.xaml' = Theme 'light'
    'src\Themes\Dark.xaml' = Theme 'dark'
    'src\Themes\Tokens.xaml' = Shared
    'theme\tokens.css' = Css
}
foreach ($entry in $targets.GetEnumerator()) {
    $path = Join-Path ($(if ($OutDir) { $OutDir } else { $root })) $entry.Key
    New-Item -ItemType Directory -Force (Split-Path -Parent $path) | Out-Null
    [IO.File]::WriteAllText($path, $entry.Value + "`r`n", (New-Object Text.UTF8Encoding $true))
}
if (-not $OutDir) { Write-Host ('Токены: ' + $tokens.color.PSObject.Properties.Name.Count + ' цветов, ' + $tokens.size.PSObject.Properties.Name.Count + ' размеров текста, ' + $tokens.radius.PSObject.Properties.Name.Count + ' скруглений.') }
