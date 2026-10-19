$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$verified = Join-Path $projectRoot 'out\app'
$testDirectory = Join-Path $projectRoot 'out\test'

# Проверки собираются отдельным проектом и в поставляемый Oblachka.exe не попадают.
# Здесь клиент пересобирается с -p:IncludeTestCode=true: только в этой сборке есть
# части MainWindow из *Fixtures.cs и точка входа --ui-self-test.
if (-not (Test-Path -LiteralPath (Join-Path $verified 'tools\xray.exe'))) { throw 'Сначала запустите .\build.ps1: ядра берутся из сборки out\app.' }
& (Join-Path $projectRoot 'scripts\check-source-size.ps1')
& (Join-Path $projectRoot 'scripts\check-voice.ps1')
& (Join-Path $projectRoot 'scripts\check-tokens.ps1')
& dotnet build (Join-Path $projectRoot 'tests\Oblachka.Tests.csproj') -c Release -o $testDirectory --configfile (Join-Path $projectRoot 'src\NuGet.Config') -p:UseSharedCompilation=false -p:IncludeTestCode=true
if ($LASTEXITCODE -ne 0) { throw 'Сборка проверок не прошла.' }
# Содержимое, а не папка: в существующую tools Copy-Item положил бы вложенную tools\tools,
# и новые файлы ядер до проверок не доходили бы.
New-Item -ItemType Directory -Force (Join-Path $testDirectory 'tools') | Out-Null
Copy-Item -Path (Join-Path $verified 'tools\*') -Destination (Join-Path $testDirectory 'tools') -Recurse -Force

$reportPath = Join-Path $testDirectory ('self-test-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8) + '.json')
$testProcess = Start-Process -FilePath (Join-Path $testDirectory 'Oblachka.Tests.exe') -ArgumentList @('--self-test', ('"' + $reportPath + '"')) -WorkingDirectory $testDirectory -WindowStyle Hidden -PassThru -Wait
if (-not (Test-Path -LiteralPath $reportPath)) { throw 'Контроллер не сформировал отчёт.' }
$testReport = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
Write-Host ('Проверок: ' + $testReport.Checks.Count + '. Успех: ' + $testReport.Passed)
Write-Host ('Отчёт: ' + $reportPath)
if ($testProcess.ExitCode -ne 0 -or -not $testReport.Passed) {
    $testReport.Checks | Where-Object { -not $_.Passed } | Select-Object Name, Error | Format-List
    throw 'Проверки не прошли.'
}
$uiReportPath = Join-Path $testDirectory ('ui-test-' + [guid]::NewGuid().ToString('N') + '.json')
$uiProcess = Start-Process -FilePath (Join-Path $testDirectory 'Oblachka.exe') -ArgumentList @('--ui-self-test', ('"' + $uiReportPath + '"')) -WorkingDirectory $testDirectory -WindowStyle Hidden -PassThru -Wait
if (-not (Test-Path -LiteralPath $uiReportPath)) { throw 'Проверка интерфейса не сформировала отчёт.' }
$uiReport = Get-Content -LiteralPath $uiReportPath -Raw | ConvertFrom-Json
Write-Host ('Проверки интерфейса: ' + $uiReport.Checks.Count + '. Успех: ' + $uiReport.Passed)
Write-Host ('Отчёт: ' + $uiReportPath)
if ($uiProcess.ExitCode -ne 0 -or -not $uiReport.Passed) { throw 'Проверки интерфейса не прошли.' }

# Поставляемая сборка не должна содержать тестовых символов.
& (Join-Path $projectRoot 'scripts\check-no-test-code.ps1') -Directory $verified
