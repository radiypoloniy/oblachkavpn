using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Quic;

namespace Oblachka;

internal static partial class StrictRoutingVmCommand
{
    private const string QuicTraceUrl = "https://www.cloudflare.com/cdn-cgi/trace";
    private const string QuicStreamHost = "speed.cloudflare.com";

    private static Process OpenQuicStream(string exe, string report, ProcessJob job)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "--strict-routing-quic-probe", "stream", report }) start.ArgumentList.Add(arg);
        var process = Process.Start(start)!;
        job.Attach(process);
        return process;
    }

    internal static async Task<int> RunQuicProbeAsync(string mode, string report)
    {
        if (!VmTunTickets.IsVirtualMachine() || mode is not ("trace" or "stream")) return 2;
        AppContext.SetSwitch("System.Net.SocketsHttpHandler.Http3Support", true);
        if (!QuicConnection.IsSupported) { File.WriteAllText(report, "MsQuic unavailable"); return 3; }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(mode == "trace" ? 7 : 25));
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        var url = mode == "trace" ? QuicTraceUrl : $"https://{QuicStreamHost}/__down?bytes=10485760";
        using var request = new HttpRequestMessage(HttpMethod.Get, url) { Version = HttpVersion.Version30, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            response.EnsureSuccessStatusCode();
            if (response.Version.Major != 3) return 4;
            if (mode == "trace")
            {
                var trace = await response.Content.ReadAsStringAsync(deadline.Token);
                var ip = trace.Split('\n').FirstOrDefault(s => s.StartsWith("ip=", StringComparison.Ordinal))?[3..].Trim();
                if (!IPAddress.TryParse(ip, out var address)) return 4;
                File.WriteAllText(report, address.ToString());
                return 0;
            }
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            var buffer = new byte[8192];
            File.WriteAllText(report, "HTTP/3 established");
            while (await stream.ReadAsync(buffer, deadline.Token) > 0) await Task.Delay(150, deadline.Token);
            return 0;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException)
        {
            if (!File.Exists(report)) File.WriteAllText(report, e.GetType().Name);
            return 5;
        }
    }

    private static async Task<string?> QuicTrace(string exe, string outputFolder, CancellationToken token)
    {
        var output = Path.Combine(outputFolder, "quic-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            var result = await Command(exe, ["--strict-routing-quic-probe", "trace", output], token);
            if (result.Code != 0 || !File.Exists(output)) return null;
            return IPAddress.TryParse(await File.ReadAllTextAsync(output, token), out var ip) ? ip.ToString() : null;
        }
        finally { if (File.Exists(output)) File.Delete(output); }
    }
}
