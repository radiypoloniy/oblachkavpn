using System.Diagnostics;
using System.Net;

namespace Oblachka;

internal static partial class StrictRoutingVmCommand
{
    private static IDisposable BlockPeerPackets(IWfpEngine engine, IReadOnlyList<string> peers)
    {
        var provider = Guid.NewGuid();
        var sublayer = Guid.NewGuid();
        var filters = peers.Select(peer =>
        {
            var address = IPAddress.Parse(peer);
            return new WfpFilter(Guid.NewGuid(), "Strict routing VM fault", address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6,
                15, false, [new WfpCondition(WfpConditionKind.RemoteAddress, address)], Packet: true);
        }).ToArray();
        engine.Begin();
        try
        {
            engine.AddProvider(provider, "Strict routing VM fault");
            engine.AddSublayer(sublayer, provider, "Strict routing VM fault", ushort.MaxValue - 8);
            foreach (var filter in filters) engine.AddFilter(filter, provider, sublayer);
            engine.Commit();
        }
        catch { engine.Abort(); throw; }
        return new PeerPacketFault(engine, provider, sublayer, filters);
    }

    private sealed class PeerPacketFault(IWfpEngine engine, Guid provider, Guid sublayer, WfpFilter[] filters) : IDisposable
    {
        public void Dispose()
        {
            engine.Begin();
            try
            {
                foreach (var filter in filters) engine.DeleteFilter(filter.Key);
                engine.DeleteSublayer(sublayer);
                engine.DeleteProvider(provider);
                engine.Commit();
            }
            catch { engine.Abort(); throw; }
        }
    }

    internal static async Task<int> CheckAfterRebootAsync(string report)
    {
        if (!VmTunTickets.IsVirtualMachine() || !NetworkHelperHost.IsElevated() || !NetworkHelperConnection.ServiceRunning)
            return 2;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = timeout.Token;
        var probe = Path.Combine(Path.GetDirectoryName(report)!, "strict-probe.exe");
        void Note(string line) => File.AppendAllText(report, $"{DateTimeOffset.UtcNow:O} {line}{Environment.NewLine}");
        using var wfp = WfpEngine.Open();
        var intact = WfpLockdown.StrictIntact(wfp, WfpLockdown.StrictPrograms(AppContext.BaseDirectory));
        var blocked = await Probe(probe, TraceUrl, token) is null;
        Note($"uptime-ms={Environment.TickCount64} strict-filters={intact} protected-probe-blocked={blocked}");
        await using var recovery = await NetworkHelperConnection.ConnectAsync(token);
        await recovery.Client.RecoverAsync(token);
        await recovery.Client.SetStrictGuardAsync(arm: false, token);
        var recovered = await Probe(probe, TraceUrl, token) is not null;
        Note($"explicit recovery restored Internet={recovered}");
        return intact && blocked && recovered ? 0 : 5;
    }

    private static async Task PauseWithOpenFlows(SystemTunnelSession session, string ordinary, string pinned,
        (string Ordinary, string Pinned, string Folder)? quic, Action<string> note, Action<string, bool> check, CancellationToken token)
    {
        const string host = "speed.cloudflare.com";
        using var job = new ProcessJob();
        using var normalStream = Stream(ordinary);
        using var protectedStream = Stream(pinned);
        using var quicStreams = new ProcessJob();
        var ordinaryMarker = quic is { } q ? Path.Combine(q.Folder, "ordinary-http3.txt") : null;
        var protectedMarker = quic is { } p ? Path.Combine(p.Folder, "protected-http3.txt") : null;
        if (ordinaryMarker is not null && File.Exists(ordinaryMarker)) File.Delete(ordinaryMarker);
        if (protectedMarker is not null && File.Exists(protectedMarker)) File.Delete(protectedMarker);
        using var normalQuic = quic is { } normal ? OpenQuicStream(normal.Ordinary, ordinaryMarker!, quicStreams) : null;
        using var pinnedQuic = quic is { } protect ? OpenQuicStream(protect.Pinned, protectedMarker!, quicStreams) : null;
        string? normalId = null, pinnedId = null;
        string? normalQuicId = null, pinnedQuicId = null;
        var stats = session.Stats!;
        for (var i = 0; i < 12; i++)
        {
            await Task.Delay(500, token);
            var active = await CoreConnections.ReadAsync(stats, token) ?? [];
            normalId = active.FirstOrDefault(c => !c.Udp && c.Host == host && IsOrdinary(c))?.Id;
            pinnedId = active.FirstOrDefault(c => !c.Udp && c.Host == host && IsPinned(c))?.Id;
            if (quic is { } probe)
            {
                if (File.Exists(ordinaryMarker) && (await File.ReadAllTextAsync(ordinaryMarker!, token)) == "HTTP/3 established")
                    normalQuicId = active.FirstOrDefault(c => c.Udp && IsOrdinary(c) && PathMatches(c, probe.Ordinary))?.Id;
                if (File.Exists(protectedMarker) && (await File.ReadAllTextAsync(protectedMarker!, token)) == "HTTP/3 established")
                    pinnedQuicId = active.FirstOrDefault(c => c.Udp && IsPinned(c) && PathMatches(c, probe.Pinned))?.Id;
            }
            if (normalId is not null && pinnedId is not null && (quic is null || normalQuicId is not null && pinnedQuicId is not null)) break;
        }
        await session.SetMainPausedAsync(true, lockdown: false);
        var after = await CoreConnections.ReadAsync(stats, token);
        if (normalId is null || pinnedId is null)
            note("SKIP existing TCP: both download streams could not be identified.");
        else check("pause closes the old ordinary TCP stream and retains the protected stream", Changed(normalId, pinnedId));
        if (quic is not null)
        {
            if (normalQuicId is null || pinnedQuicId is null) note("SKIP existing QUIC: both HTTP/3 downloads could not be identified.");
            else check("pause closes the old ordinary QUIC stream and retains the protected stream", Changed(normalQuicId, pinnedQuicId));
        }
        return;

        static bool PathMatches(CoreConnection connection, string exe) => string.Equals(connection.ProcessPath, exe, StringComparison.OrdinalIgnoreCase);
        static bool IsOrdinary(CoreConnection connection) => connection.Chains.Contains(SingBoxTunConfig.ServerSelector);
        static bool IsPinned(CoreConnection connection) => connection.Chains.Contains("direct-1") && !IsOrdinary(connection);
        bool Changed(string beforeOrdinary, string beforePinned) =>
            after is not null && after.All(c => c.Id != beforeOrdinary) && after.Any(c => c.Id == beforePinned);

        Process Stream(string exe)
        {
            var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "--noproxy", "*", "-4", "-fsS", "--max-time", "30", "--limit-rate", "64k",
                         "-o", "NUL", $"https://{host}/__down?bytes=10485760" })
                start.ArgumentList.Add(arg);
            var stream = Process.Start(start)!;
            job.Attach(stream);
            return stream;
        }
    }

    private static async Task<string?> Probe(string exe, string url, CancellationToken token, string? local = null)
    {
        var args = new List<string> { "--noproxy", "*", "-4", "-fSs", "--connect-timeout", "5", "--max-time", "9" };
        if (local is not null) args.AddRange(["--interface", local]);
        args.Add(url);
        var result = await Command(exe, args, token);
        if (result.Code != 0) return null;
        var ip = url == TraceUrl ? result.Output.Split('\n').FirstOrDefault(s => s.StartsWith("ip=", StringComparison.Ordinal))?[3..].Trim()
            : result.Output.Trim();
        return IPAddress.TryParse(ip, out var address) ? address.ToString() : null;
    }

    private static async Task<(int Code, string Output)> Command(string exe, IEnumerable<string> arguments, CancellationToken token)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        using var job = new ProcessJob();
        job.Attach(process);
        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        await error;
        return (process.ExitCode, await output);
    }
}
