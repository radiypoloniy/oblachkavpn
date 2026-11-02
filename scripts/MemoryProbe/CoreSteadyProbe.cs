using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;

internal static class CoreSteadyProbe
{
    internal static async Task RunAsync(Process core, HttpClient http, int api, List<TcpClient> sockets,
        int seconds, Func<string, Task> sample, List<object> batches)
    {
        var root = $"http://127.0.0.1:{api}";
        async Task Measure(string phase, Func<long> transferred)
        {
            using var before = JsonDocument.Parse(await http.GetStringAsync(root + "/runtime"));
            core.Refresh(); var cpu = core.TotalProcessorTime; var clock = Stopwatch.StartNew(); var polls = 0;
            var bytes = transferred();
            while (clock.Elapsed < TimeSpan.FromSeconds(seconds))
            {
                using var programs = JsonDocument.Parse(await http.GetStringAsync(root + "/programs"));
                if (programs.RootElement.GetProperty("programs").GetArrayLength() == 0) throw new InvalidOperationException("Lost traffic attribution");
                polls++;
                await sample(phase);
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(5, Math.Max(0, seconds - clock.Elapsed.TotalSeconds))));
            }
            core.Refresh(); var used = (core.TotalProcessorTime - cpu).TotalMilliseconds;
            using var after = JsonDocument.Parse(await http.GetStringAsync(root + "/runtime"));
            batches.Add(new { Name = phase, Seconds = clock.Elapsed.TotalSeconds, ProgramPolls = polls, CoreCpuMs = used,
                CoreAllocatedBytes = after.RootElement.GetProperty("totalAlloc").GetUInt64() - before.RootElement.GetProperty("totalAlloc").GetUInt64(),
                TransferredBytes = transferred() - bytes });
        }
        await Measure("500-open-idle", () => 0);
        using var stop = new CancellationTokenSource(); long traffic = 0;
        async Task Load(TcpClient socket)
        {
            var stream = socket.GetStream(); var buffer = new byte[16 * 1024]; var reply = new byte[buffer.Length];
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    await stream.WriteAsync(buffer, stop.Token);
                    await stream.ReadExactlyAsync(reply, stop.Token);
                    Interlocked.Add(ref traffic, buffer.Length);
                    await Task.Delay(20, stop.Token);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
        var loading = Task.WhenAll(sockets.Take(4).Select(Load));
        try { await Measure("loopback-load", () => Interlocked.Read(ref traffic)); }
        finally { stop.Cancel(); await loading; }
    }
}
