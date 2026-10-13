# Self-contained build: the client PC does not need the .NET Desktop Runtime installed.
# .NET 8 support ends on 10 November 2026, so .NET 10 is used when its SDK is installed.
# Requires nuget.org access (runtime packs). Run .\build.ps1 first: cores are copied from its verified output.
param([switch]$AllowNet8)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
. (Join-Path $projectRoot 'build-info.ps1')
$buildRevision = Get-ClientRevision
$verified = Join-Path $projectRoot 'app-v09'
if (-not (Test-Path -LiteralPath (Join-Path $verified 'tools\xray.exe'))) { throw 'Сначала запустите .\build.ps1: ядра берутся из проверенной сборки app-v09.' }
$sdks = (& dotnet --list-sdks) -join "`n"
if ($sdks -match '(?m)^10\.') { $tfm = 'net10.0-windows' }
elseif ($AllowNet8) { $tfm = 'net8.0-windows'; Write-Warning '.NET 8 перестаёт получать обновления 10.11.2026. Установи SDK 10: winget install Microsoft.DotNet.SDK.10' }
else { throw 'Нужен .NET SDK 10: winget install Microsoft.DotNet.SDK.10. Временно на .NET 8: .\publish-standalone.ps1 -AllowNet8' }
$outputDirectory = Join-Path $projectRoot ('app-v09-standalone-' + $tfm.Split('.')[0])
if (Test-Path -LiteralPath $outputDirectory) { throw ('Папка уже есть: ' + $outputDirectory + '. Переименуй её; скрипт ничего не удаляет.') }
& (Join-Path $projectRoot 'check-source-size.ps1')
& dotnet publish (Join-Path $projectRoot 'src\Oblachka.csproj') -c Release -r win-x64 --self-contained true `
    -p:TargetFramework=$tfm -p:UseSharedCompilation=false "-p:SourceRevisionId=$buildRevision" --source 'https://api.nuget.org/v3/index.json' -o $outputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Публикация не прошла.' }
Copy-Item -LiteralPath (Join-Path $verified 'tools') -Destination (Join-Path $outputDirectory 'tools') -Recurse
Write-ClientBuildInfo $outputDirectory $buildRevision
Write-Host ('Готово: ' + $outputDirectory + '\Oblachka.exe (' + $tfm + ', без установки .NET).')
