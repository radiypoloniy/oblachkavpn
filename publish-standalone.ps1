# Self-contained build: the client PC does not need the .NET Desktop Runtime installed.
# .NET 8 support ends on 10 November 2026, so .NET 10 is used when its SDK is installed.
# Requires nuget.org access (runtime packs). Run .\build.ps1 first: cores are copied from its verified output.
param([switch]$AllowNet8)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$verified = Join-Path $projectRoot 'app-v082'
if (-not (Test-Path -LiteralPath (Join-Path $verified 'tools\xray.exe'))) { throw 'Сначала запусти .\build.ps1: ядра берутся из проверенной сборки app-v082.' }
$sdks = (& dotnet --list-sdks) -join "`n"
if ($sdks -match '(?m)^10\.') { $tfm = 'net10.0-windows' }
elseif ($AllowNet8) { $tfm = 'net8.0-windows'; Write-Warning '.NET 8 перестаёт получать обновления 10.11.2026. Установи SDK 10: winget install Microsoft.DotNet.SDK.10' }
else { throw 'Нужен .NET SDK 10: winget install Microsoft.DotNet.SDK.10. Временно на .NET 8: .\publish-standalone.ps1 -AllowNet8' }
$outputDirectory = Join-Path $projectRoot ('app-v082-standalone-' + $tfm.Split('.')[0])
if (Test-Path -LiteralPath $outputDirectory) { throw ('Папка уже есть: ' + $outputDirectory + '. Переименуй её; скрипт ничего не удаляет.') }
& (Join-Path $projectRoot 'check-source-size.ps1')
& dotnet publish (Join-Path $projectRoot 'src\RoutePilot.csproj') -c Release -r win-x64 --self-contained true `
    -p:TargetFramework=$tfm -p:UseSharedCompilation=false --source 'https://api.nuget.org/v3/index.json' -o $outputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Публикация не прошла.' }
Copy-Item -LiteralPath (Join-Path $verified 'tools') -Destination (Join-Path $outputDirectory 'tools') -Recurse
Write-Host ('Готово: ' + $outputDirectory + '\RoutePilot.exe (' + $tfm + ', без установки .NET).')
