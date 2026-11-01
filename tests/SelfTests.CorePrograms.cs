using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Oblachka;

public static partial class SelfTests
{
    private static async Task CoreProgramChecksAsync(Func<string, Func<Task>, Task> check)
    {
        await check("Compact program counters preserve deltas, short-lived programs, journal expiry and core restarts", () => Sync(() =>
        {
            var now = DateTime.UtcNow; var epoch = Guid.NewGuid().ToString();
            CoreProgramSnapshot Snapshot(long upload, long download, DateTime seen, string? id = null) =>
                new(id ?? epoch, [new(@"C:\Apps\browser.exe", upload, download, 0, 1, 1, seen)]);
            var history = new TrafficHistory(); var traffic = new ProgramTrafficHistory(); var journal = new ProgramJournal();
            var first = Snapshot(100, 200, now);
            traffic.Record(first, history, now); journal.Record(first, now);
            traffic.Record(first, history, now); journal.Record(first, now);
            Assert(history.Report(now, 1).Programs.Single().Total == 300 && journal.Recent(now).Single() is { Samples: 1, Direct: 1, Udp: 1 });
            traffic.Record(Snapshot(120, 240, now.AddSeconds(5)), history, now);
            Assert(history.Report(now, 1).Programs.Single().Total == 360);
            journal.Record(first, now.AddMinutes(11)); Assert(journal.Recent(now.AddMinutes(11)).Count == 0);
            // A new core may already have larger counters: epoch must reset the baseline anyway.
            traffic.Record(Snapshot(200, 400, now, Guid.NewGuid().ToString()), history, now);
            Assert(history.Report(now, 1).Programs.Single().Total == 960);
            traffic.Reset(); traffic.Record(first, history, now);
            Assert(history.Report(now, 1).Programs.Single().Total == 1260);
            history.Reset(); traffic.Rebase(); traffic.Record(first, history, now);
            Assert(history.Report(now, 1).Programs.Length == 0);
            traffic.Record(Snapshot(110, 220, now), history, now);
            Assert(history.Report(now, 1).Programs.Single().Total == 30);
            history.Reset(); traffic.Rebase();
            traffic.Record([new CoreConnection(@"C:\Apps\browser.exe", false, null, null, 443, [], "legacy", 500, 1000)], history, now);
            Assert(history.Report(now, 1).Programs.Length == 0);
        }));
        await check("Program sampler chooses legacy once and never double-counts after a compact API failure", async () =>
        {
            var stats = CoreStatsEntry.Create(23000); int compactCalls = 0, legacyCalls = 0;
            var old = new CoreProgramSampler((_, _) => { legacyCalls++; return Task.FromResult<IReadOnlyList<CoreConnection>?>([]); },
                (_, _) => { compactCalls++; return Task.FromResult<CoreProgramSnapshot?>(null); });
            await old.ReadAsync(stats, CancellationToken.None); await old.ReadAsync(stats, CancellationToken.None);
            Assert(compactCalls == 1 && legacyCalls == 2);
            compactCalls = legacyCalls = 0;
            var current = new CoreProgramSampler((_, _) => { legacyCalls++; return Task.FromResult<IReadOnlyList<CoreConnection>?>([]); },
                (_, _) => Task.FromResult<CoreProgramSnapshot?>(++compactCalls == 2 ? null : new(Guid.NewGuid().ToString(), [])));
            Assert((await current.ReadAsync(stats, CancellationToken.None))?.Summary is not null);
            Assert(await current.ReadAsync(stats, CancellationToken.None) is null);
            Assert((await current.ReadAsync(stats, CancellationToken.None))?.Summary is not null && legacyCalls == 0);
        });
        await check("Compact parser rejects duplicate paths, unsupported versions, negative counters and oversized lists", () => Sync(() =>
        {
            var epoch = Guid.NewGuid().ToString();
            object Row(string path, long upload = 1) => new { path, upload, download = 2, vpn = 1, direct = 0, udp = 0, lastSeen = 1000 };
            CoreProgramSnapshot Parse(object rows, int version = 1) { using var json = JsonDocument.Parse(JsonSerializer.Serialize(new { version, epoch, programs = rows })); return CorePrograms.Parse(json.RootElement); }
            Assert(Parse(new[] { Row("browser") }).Programs.Single().LastSeen == DateTimeOffset.FromUnixTimeMilliseconds(1000).UtcDateTime);
            foreach (var rows in new object[] { new[] { Row("browser"), Row("BROWSER") }, new[] { Row("browser", -1) }, Enumerable.Range(0, 257).Select(i => Row("app" + i)).ToArray() })
            { try { Parse(rows); Assert(false); } catch (JsonException) { } }
            try { Parse(Array.Empty<object>(), 2); Assert(false); } catch (JsonException) { }
            Assert(!JsonSerializer.Serialize(CoreStatsEntry.Create(23000)).Contains("ProgramsPort", StringComparison.Ordinal));
        }));
        await check("Pinned native core exposes compact stats with authentication and tolerates an occupied optional port", async () =>
        {
            foreach (var occupy in new[] { false, true })
            {
                var stats = CoreProgramPorts.Create();
                var occupied = new TcpListener(IPAddress.Loopback, stats.ProgramsPort);
                if (occupy) occupied.Start();
                var config = new JsonObject
                {
                    ["log"] = new JsonObject { ["disabled"] = true },
                    ["outbounds"] = new JsonArray(new JsonObject { ["type"] = "direct", ["tag"] = "direct" })
                };
                CoreProgramsConfig.Apply(config, stats);
                using var process = RunProgramStatsCore(config);
                try
                {
                    using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(2) };
                    var ready = false;
                    for (var i = 0; i < 50 && !ready; i++)
                    {
                        try
                        {
                            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{stats.Port}/version"); request.Headers.Authorization = new("Bearer", stats.Secret);
                            using var response = await http.SendAsync(request); ready = response.IsSuccessStatusCode;
                        }
                        catch (HttpRequestException) { }
                        if (!ready) await Task.Delay(100);
                    }
                    Assert(ready);
                    var summary = await CorePrograms.ReadAsync(stats, CancellationToken.None);
                    if (occupy)
                    {
                        Assert(summary is null && !occupied.Pending()); // Different PID: no secret reaches this listener.
                        var sampler = new CoreProgramSampler(CoreConnections.ReadAsync);
                        Assert((await sampler.ReadAsync(stats, CancellationToken.None))?.Connections is { Count: 0 });
                    }
                    else
                    {
                        Assert(summary is { Programs.Count: 0 });
                        Assert(await CorePrograms.ReadAsync(stats with { Secret = new string('0', 32) }, CancellationToken.None) is null);
                    }
                }
                finally { occupied.Stop(); if (!process.HasExited) { process.Kill(); process.WaitForExit(); } }
            }
        });
    }

    private static Process RunProgramStatsCore(JsonObject config)
    {
        var start = new ProcessStartInfo(TunCore.Program()) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "run", "-c", "stdin" }) start.ArgumentList.Add(argument);
        var process = Process.Start(start)!;
        process.BeginOutputReadLine(); process.BeginErrorReadLine();
        process.StandardInput.Write(config.ToJsonString()); process.StandardInput.Close();
        return process;
    }
}
