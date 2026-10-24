# Сборка клиента в out\app: исходники, затем ядра Xray и sing-box в out\app\tools.
# Ядра скачиваются с официальных релизов и сверяются по SHA256 (scripts\fetch-cores.ps1).
# Ядро системного туннеля oblachka-core собирается из core\ и тоже сверяется (scripts\build-core.ps1).
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
. (Join-Path $projectRoot 'scripts\build-info.ps1')
$buildRevision = Get-ClientRevision
$outputDirectory = Join-Path $projectRoot 'out\app'
& (Join-Path $projectRoot 'scripts\check-source-size.ps1')
& (Join-Path $projectRoot 'scripts\fetch-cores.ps1')
& (Join-Path $projectRoot 'scripts\build-core.ps1')
& dotnet build (Join-Path $projectRoot 'src\Oblachka.csproj') -c Release -o $outputDirectory --configfile (Join-Path $projectRoot 'src\NuGet.Config') -p:UseSharedCompilation=false "-p:SourceRevisionId=$buildRevision"
if ($LASTEXITCODE -ne 0) { throw 'Сборка не прошла.' }

$tools = Join-Path $outputDirectory 'tools'
New-Item -ItemType Directory -Force $tools | Out-Null
foreach ($core in 'xray.exe', 'sing-box.exe', 'libcronet.dll', 'oblachka-core.exe') {
    Copy-Item -LiteralPath (Join-Path $projectRoot ('vendor\cores\' + $core)) -Destination $tools -Force
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'vendor\xray-26.10.21\LICENSE-Xray') -Destination $tools -Force
foreach ($license in 'LICENSE-sing-box', 'COPYING-GPL-3.0') {
    Copy-Item -LiteralPath (Join-Path $projectRoot ('vendor\sing-box-1.14.4\' + $license)) -Destination $tools -Force
}
Write-ClientBuildInfo $outputDirectory $buildRevision
Write-Host 'Готово: out\app\Oblachka.exe'
