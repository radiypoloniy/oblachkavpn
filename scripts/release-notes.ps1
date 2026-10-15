# Описание релиза: раздел версии из CHANGELOG.md, установка и SHA256 установщика.
# Вызывается сборкой на GitHub (.github/workflows/release.yml); можно запустить и локально.
param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [Parameter(Mandatory = $true)][string]$Installer,
    [Parameter(Mandatory = $true)][string]$Output
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$version = $Tag.TrimStart('v')
$lines = [IO.File]::ReadAllLines((Join-Path $root 'CHANGELOG.md'), [Text.UTF8Encoding]::new($false))
$section = @()
$inside = $false
foreach ($line in $lines) {
    if ($line -match '^## ') {
        if ($inside) { break }
        $inside = $line -match ('^## ' + [regex]::Escape($version) + '(\s|$)')
        continue
    }
    if ($inside) { $section += $line }
}
if (-not ($section -join '').Trim()) { throw ('В CHANGELOG.md нет раздела «## ' + $version + '».') }

$name = Split-Path -Leaf $Installer
$sha = (Get-FileHash -LiteralPath $Installer -Algorithm SHA256).Hash
$notes = @(
    ($section -join "`n").Trim(), '',
    '## Установка', '',
    ('Скачайте `' + $name + '` ниже и запустите. Если Облачка уже стоит, она предложит обновиться сама: «Настройки → Помощь → Обновления».'), '',
    'Программа пока не подписана, поэтому Windows может показать «Windows защитила ваш компьютер». Нажмите **«Подробнее» → «Выполнить в любом случае»**.', '',
    ('SHA256: `' + $sha + '`')
) -join "`n"
[IO.File]::WriteAllText($Output, $notes, [Text.UTF8Encoding]::new($false))
Write-Host ('Описание релиза: ' + $Output)
