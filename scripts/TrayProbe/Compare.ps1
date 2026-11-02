# Пара старого пути значка (с WPF Application) и нового (без WPF), три последовательных повтора.
param(
    [Parameter(Mandatory)][string]$Before,
    [Parameter(Mandatory)][string]$After,
    [Parameter(Mandatory)][string]$OutputFolder
)
$ErrorActionPreference = 'Stop'
$outputRoot = [IO.Path]::GetFullPath($OutputFolder)
New-Item -ItemType Directory -Force $outputRoot | Out-Null
$reports = @{ before=@(); after=@() }
foreach ($iteration in 1..3) {
    foreach ($version in @(@{ Name='before'; Path=$Before }, @{ Name='after'; Path=$After })) {
        $client = [IO.Path]::GetFullPath($version.Path)
        $report = Join-Path $outputRoot ($version.Name + '-' + $iteration + '.json')
        $arguments = @()
        if ($version.Name -eq 'before') { $arguments += '--wpf-baseline' }
        $arguments += ('"' + $report + '"')
        $process = Start-Process -FilePath $client -ArgumentList $arguments -WorkingDirectory (Split-Path -Parent $client) -WindowStyle Hidden -PassThru -Wait
        $result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
        if ($process.ExitCode -ne 0 -or -not $result.Passed) { throw ('Background tray probe failed: ' + $report) }
        $reports[$version.Name] += $result
    }
}
$summary = foreach ($metric in @('PrivateBytes','PrivateWorkingSet','WorkingSet','ManagedBytes')) {
    $left = @($reports.before | ForEach-Object { $_.Memory.$metric } | Sort-Object)[1]
    $right = @($reports.after | ForEach-Object { $_.Memory.$metric } | Sort-Object)[1]
    [pscustomobject]@{ Metric=$metric; BeforeMiB=[math]::Round($left / 1MB,3); AfterMiB=[math]::Round($right / 1MB,3);
        ChangePercent=[math]::Round(($right / $left - 1) * 100,1) }
}
$summary | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outputRoot 'summary.json') -Encoding utf8
$summary | Format-Table -AutoSize
