# Три изолированных процесса: видимое, скрытое и закрытое окно в каждом.
# Последний образец с принудительным GC только диагностический; не выдавать его за поведение клиента.
param(
    [Parameter(Mandatory)][string]$Executable,
    [Parameter(Mandatory)][string]$OutputFolder
)
$ErrorActionPreference = 'Stop'
$client = [IO.Path]::GetFullPath($Executable)
$outputRoot = [IO.Path]::GetFullPath($OutputFolder)
New-Item -ItemType Directory -Force $outputRoot | Out-Null
$reports = @()
foreach ($iteration in 1..3) {
    $report = Join-Path $outputRoot ('tray-' + $iteration + '.json')
    $process = Start-Process -FilePath $client -ArgumentList @('--ui-tray-memory-check', ('"' + $report + '"')) -WorkingDirectory (Split-Path -Parent $client) -WindowStyle Hidden -PassThru -Wait
    if ($process.ExitCode -ne 0) { throw ('Tray memory probe failed: ' + $report) }
    $result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
    if (-not $result.Passed) { throw ('Tray memory report failed: ' + $report) }
    $reports += $result
}
$summary = foreach ($sample in $reports[0].Samples) {
    foreach ($metric in @('PrivateBytes', 'PrivateWorkingSet', 'WorkingSet', 'ManagedBytes', 'GcCommittedBytes')) {
        $phase = $sample.Phase
        $values = @($reports | ForEach-Object { ($_.Samples | Where-Object Phase -eq $phase).$metric } | Sort-Object)
        [pscustomobject]@{ Phase=$phase; ForcedGc=$sample.ForcedGc; Metric=$metric; MedianMiB=[math]::Round($values[1] / 1MB, 3) }
    }
}
$summary | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $outputRoot 'summary.json') -Encoding utf8
$summary | Format-Table -AutoSize
