# Парные запуски UI-стенда: последовательно, без принудительного GC и без сетевых ядер.
param(
    [Parameter(Mandatory)][string]$Before,
    [Parameter(Mandatory)][string]$After,
    [Parameter(Mandatory)][string]$OutputFolder
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $OutputFolder | Out-Null
$outputRoot = [IO.Path]::GetFullPath($OutputFolder)
$reports = @{ before = @(); after = @() }
foreach ($iteration in 1..3) {
    foreach ($version in @(@{ Name = 'before'; Path = $Before }, @{ Name = 'after'; Path = $After })) {
        $executable = [IO.Path]::GetFullPath($version.Path)
        $report = Join-Path $outputRoot ($version.Name + '-' + $iteration + '.json')
        $process = Start-Process -FilePath $executable -ArgumentList @('--ui-memory-check', ('"' + $report + '"')) -WorkingDirectory (Split-Path -Parent $executable) -WindowStyle Hidden -PassThru -Wait
        if ($process.ExitCode -ne 0) { throw ('UI memory probe failed: ' + $report) }
        $result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
        if (-not $result.Passed) { throw ('UI memory report failed: ' + $report) }
        $reports[$version.Name] += $result
    }
}
function Median($values) { $sorted = @($values | Sort-Object); return $sorted[1] }
$summary = foreach ($sample in $reports.before[0].Samples) {
    foreach ($metric in @('PrivateBytes', 'WorkingSet', 'AllocatedBytes')) {
        if ($sample.PSObject.Properties.Name -notcontains $metric) { continue }
        $scenario = $sample.Scenario
        $left = Median @($reports.before | ForEach-Object { ($_.Samples | Where-Object Scenario -eq $scenario).$metric })
        $right = Median @($reports.after | ForEach-Object { ($_.Samples | Where-Object Scenario -eq $scenario).$metric })
        [pscustomobject]@{
            Scenario = $scenario; Metric = $metric
            BeforeMiB = [math]::Round($left / 1MB, 3); AfterMiB = [math]::Round($right / 1MB, 3)
            ChangePercent = [math]::Round(($right / $left - 1) * 100, 1)
        }
    }
}
$summary | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $outputRoot 'summary.json') -Encoding utf8
$summary | Format-Table
