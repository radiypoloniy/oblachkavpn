# Комплект проверки на настоящем компьютере: тестовая сборка, ядра проверенной сборки
# app-v09 и скрипт real-check. Запуск — Проверить.cmd в распакованной папке.
# Тестовая сборка app-v09-test создаётся .\test.ps1; без неё и без свежей app-v09 комплект
# не собирается. Это комплект проверки, а не поставка клиента: в нём тестовый код.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$test = Join-Path $root 'app-v09-test'
$verified = Join-Path $root 'app-v09'
foreach ($required in @((Join-Path $test 'Oblachka.Tests.exe'), (Join-Path $verified 'tools\xray.exe'), (Join-Path $verified 'tools\sing-box.exe'))) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Нет $required. Сначала .\build.ps1 и .\test.ps1." }
}
$revision = (git -C $root rev-parse --short HEAD).Trim()
if (git -C $root status --porcelain -- src tests real-check) { throw 'Есть незакоммиченные изменения в src, tests или real-check: комплект должен соответствовать ревизии.' }

$version = ([xml](Get-Content -LiteralPath (Join-Path $root 'src\Oblachka.csproj') -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$name = 'Oblachka-check-' + $version + '-' + $revision
$stage = Join-Path $root ('dist\' + $name)
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $stage 'tools') | Out-Null

$binaries = 'Oblachka.exe', 'Oblachka.dll', 'Oblachka.deps.json', 'Oblachka.runtimeconfig.json',
    'Oblachka.Tests.exe', 'Oblachka.Tests.dll', 'Oblachka.Tests.deps.json', 'Oblachka.Tests.runtimeconfig.json'
foreach ($file in $binaries) { Copy-Item -LiteralPath (Join-Path $test $file) -Destination $stage }
foreach ($file in 'xray.exe', 'sing-box.exe', 'libcronet.dll', 'LICENSE-Xray', 'LICENSE-sing-box', 'COPYING-GPL-3.0') {
    Copy-Item -LiteralPath (Join-Path $verified ('tools\' + $file)) -Destination (Join-Path $stage 'tools')
}
Copy-Item -LiteralPath (Join-Path $root 'THIRD-PARTY.md') -Destination $stage
Get-ChildItem -LiteralPath (Join-Path $root 'real-check') -File | Copy-Item -Destination $stage
Set-Content -LiteralPath (Join-Path $stage 'revision.txt') -Value $revision -Encoding ASCII

$zip = Join-Path $root ('dist\' + $name + '.zip')
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
Remove-Item -LiteralPath $stage -Recurse -Force
Write-Host $zip
Write-Host ('SHA256: ' + (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash)
