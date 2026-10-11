$ErrorActionPreference = 'Stop'
$appDirectory = Join-Path $PSScriptRoot 'app-v07'
$reportPath = Join-Path $appDirectory ('self-test-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8) + '.json')
$testProcess = Start-Process -FilePath (Join-Path $appDirectory 'RoutePilot.exe') -ArgumentList @('--self-test', ('"' + $reportPath + '"')) -WorkingDirectory $appDirectory -WindowStyle Hidden -PassThru -Wait
if (-not (Test-Path -LiteralPath $reportPath)) { throw 'Контроллер не сформировал отчёт.' }
$testReport = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
Write-Host ('Проверок: ' + $testReport.Checks.Count + '. Успех: ' + $testReport.Passed)
Write-Host ('Отчёт: ' + $reportPath)
if ($testProcess.ExitCode -ne 0 -or -not $testReport.Passed) {
    $testReport.Checks | Where-Object { -not $_.Passed } | Select-Object Name, Error | Format-List
    throw 'Проверки не прошли.'
}
$uiReportPath = Join-Path $appDirectory ('ui-test-' + [guid]::NewGuid().ToString('N') + '.json')
$uiProcess = Start-Process -FilePath (Join-Path $appDirectory 'RoutePilot.exe') -ArgumentList @('--ui-self-test', ('"' + $uiReportPath + '"')) -WorkingDirectory $appDirectory -WindowStyle Hidden -PassThru -Wait
if (-not (Test-Path -LiteralPath $uiReportPath)) { throw 'Проверка интерфейса не сформировала отчёт.' }
$uiReport = Get-Content -LiteralPath $uiReportPath -Raw | ConvertFrom-Json
Write-Host ('Проверки интерфейса: ' + $uiReport.Checks.Count + '. Успех: ' + $uiReport.Passed)
Write-Host ('Отчёт: ' + $uiReportPath)
if ($uiProcess.ExitCode -ne 0 -or -not $uiReport.Passed) { throw 'Проверки интерфейса не прошли.' }


