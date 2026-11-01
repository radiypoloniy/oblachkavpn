# Изолированный замер ядра с запасными Xray-выходами: без TUN, входов и подключений.
param([Parameter(Mandatory)][string]$Executable, [Parameter(Mandatory)][string]$Report)
$ErrorActionPreference = 'Stop'
$samples = @()
foreach ($count in 1, 4, 16) {
    foreach ($iteration in 1..3) {
        $outbounds = @(for ($i = 0; $i -lt $count; $i++) {
            @{ type = 'xray'; tag = "spare-$i"; xray = @{
                protocol = 'vless'; settings = @{ vnext = @(@{ address = '127.0.0.1'; port = 9; users = @(@{ id = '6f1c2e0a-5b7d-4c3e-9a1f-0b2c3d4e5f60'; encryption = 'none' }) }) }
                streamSettings = @{ network = 'xhttp'; security = 'tls'; tlsSettings = @{ serverName = 'fixture.invalid' }; xhttpSettings = @{ path = '/fixture'; mode = 'auto' } }
            } }
        })
        $config = @{ log = @{ disabled = $true }; outbounds = $outbounds } | ConvertTo-Json -Depth 20 -Compress
        $start = [Diagnostics.ProcessStartInfo]::new([IO.Path]::GetFullPath($Executable))
        $start.UseShellExecute = $false; $start.CreateNoWindow = $true
        $start.RedirectStandardInput = $true; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
        foreach ($argument in @('run', '-c', 'stdin')) { $start.ArgumentList.Add($argument) }
        $process = [Diagnostics.Process]::new(); $process.StartInfo = $start
        try {
            [void]$process.Start()
            $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
            $process.StandardInput.Write($config); $process.StandardInput.Close()
            Start-Sleep -Seconds 2
            if ($process.HasExited) { throw ('Core exited: ' + $stderr.GetAwaiter().GetResult()) }
            $process.Refresh()
            $samples += [pscustomobject]@{ Outbounds = $count; Iteration = $iteration; PrivateBytes = $process.PrivateMemorySize64; WorkingSet = $process.WorkingSet64; CpuMs = $process.TotalProcessorTime.TotalMilliseconds }
        }
        finally {
            if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
            $process.Dispose()
        }
    }
}
@{ Executable = [IO.Path]::GetFullPath($Executable); Tun = $false; NetworkTraffic = $false; Samples = $samples } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $Report -Encoding utf8
