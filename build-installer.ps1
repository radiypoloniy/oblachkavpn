# Установщик Облачки: самодостаточная сборка (.NET внутри, ставить runtime не нужно),
# ядра из сборки out\app и сценарий installer\oblachka.iss.
# Нужны: .\build.ps1 (ядра), .NET SDK 10, доступ к nuget.org (пакеты runtime), Inno Setup 6.
# Установщик соответствует ревизии, поэтому незакоммиченные изменения в src запрещены.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
. (Join-Path $root 'scripts\build-info.ps1')
$verified = Join-Path $root 'out\app'
foreach ($core in 'xray.exe', 'sing-box.exe', 'oblachka-core.exe') {
    if (-not (Test-Path -LiteralPath (Join-Path $verified ('tools\' + $core)))) { throw 'Сначала запустите .\build.ps1: ядра берутся из сборки out\app.' }
}
if (git -C $root status --porcelain -- src installer) { throw 'Есть незакоммиченные изменения в src или installer: установщик должен соответствовать ревизии.' }
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Нужен Inno Setup 6: winget install JRSoftware.InnoSetup --scope user' }

$revision = Get-ClientRevision
$short = $revision.Substring(0, 7)
$version = ([xml](Get-Content -LiteralPath (Join-Path $root 'src\Oblachka.csproj') -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$stage = Join-Path $root ('dist\installer-stage-' + $short)
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }

& (Join-Path $root 'scripts\check-source-size.ps1')
& dotnet publish (Join-Path $root 'src\Oblachka.csproj') -c Release -r win-x64 --self-contained true `
    -p:UseSharedCompilation=false -p:DebugType=none "-p:SourceRevisionId=$revision" --source 'https://api.nuget.org/v3/index.json' -o $stage
if ($LASTEXITCODE -ne 0) { throw 'Публикация не прошла.' }
New-Item -ItemType Directory -Force (Join-Path $stage 'tools') | Out-Null
foreach ($file in 'xray.exe', 'sing-box.exe', 'libcronet.dll', 'oblachka-core.exe', 'LICENSE-Xray', 'LICENSE-sing-box', 'COPYING-GPL-3.0') {
    Copy-Item -LiteralPath (Join-Path $verified ('tools\' + $file)) -Destination (Join-Path $stage 'tools')
}
Copy-Item -LiteralPath (Join-Path $root 'THIRD-PARTY.md') -Destination $stage
Write-ClientBuildInfo $stage $revision

& $iscc /Q "/DVersion=$version" "/DVersionNumber=$(($version -split '-')[0])" "/DRevision=$short" "/DSourceDir=$stage" "/DOutputDir=$(Join-Path $root 'dist')" (Join-Path $root 'installer\oblachka.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup не собрал установщик.' }
Remove-Item -LiteralPath $stage -Recurse -Force
$setup = Join-Path $root ('dist\Oblachka-setup-' + $version + '-' + $short + '.exe')
Write-Host $setup
Write-Host ('SHA256: ' + (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash)
