using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Oblachka;

public static partial class SelfTests
{
    public static async Task<int> RunLiveDnsAsync(string report)
    {
        var checks = new List<Check>();
        await LiveDnsChecksAsync(async (name, action) =>
        {
            try { await action(); checks.Add(new(name, true, null)); }
            catch (Exception ex) { checks.Add(new(name, false, ex.ToString())); }
        });
        var passed = checks.All(c => c.Passed);
        File.WriteAllText(report, System.Text.Json.JsonSerializer.Serialize(new { Passed = passed, Checks = checks }));
        return passed ? 0 : 1;
    }
    private static async Task LiveDnsChecksAsync(Func<string, Func<Task>, Task> check)
    {
        await check("Live goldapple DNS exception changes resolver and reverts when removed; cache API requires the session secret", async () =>
        {
            var folder = Path.Combine(AppContext.BaseDirectory, "test-data", "live-dns-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "direct-dns.json");
            void Write(bool direct)
            {
                File.WriteAllText(path + ".tmp", new JsonObject { ["version"] = 2, ["rules"] = new JsonArray(
                    new JsonObject { ["domain_suffix"] = new JsonArray(direct ? "goldapple.ru" : "fixture.invalid") }) }.ToJsonString());
                File.Move(path + ".tmp", path, true);
            }
            Write(false);
            var stats = CoreStatsEntry.Create(Engine.FreePort());
            JsonObject Dns(string tag, string ip) => new() { ["type"] = "hosts", ["tag"] = tag,
                ["predefined"] = new JsonObject { ["goldapple.ru"] = new JsonArray(ip), ["www.goldapple.ru"] = new JsonArray(ip) } };
            var config = new JsonObject
            {
                ["log"] = new JsonObject { ["disabled"] = true },
                ["outbounds"] = new JsonArray(new JsonObject { ["type"] = "direct", ["tag"] = "direct" }),
                ["dns"] = new JsonObject { ["servers"] = new JsonArray(Dns("remote-dns", "198.51.100.20"), Dns("local-dns", "192.0.2.20")),
                    ["final"] = "remote-dns", ["rules"] = new JsonArray(new JsonObject { ["rule_set"] = new JsonArray("direct-dns"), ["server"] = "local-dns" }) },
                ["route"] = new JsonObject { ["final"] = "direct", ["default_domain_resolver"] = "remote-dns", ["rule_set"] = new JsonArray(new JsonObject { ["type"] = "local", ["tag"] = "direct-dns", ["format"] = "source", ["path"] = path }) },
                ["experimental"] = new JsonObject { ["clash_api"] = new JsonObject { ["external_controller"] = "127.0.0.1:" + stats.Port, ["secret"] = stats.Secret } }
            };
            var start = new ProcessStartInfo(TunCore.Program()) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "run", "-c", "stdin" }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
            process.ErrorDataReceived += (_, e) => { if (e.Data is { } line && errors.Count < 8) errors.Enqueue(line); };
            using var job = new ProcessJob(); job.Attach(process);
            try
            {
                process.BeginOutputReadLine(); process.BeginErrorReadLine();
                await process.StandardInput.WriteAsync(config.ToJsonString()); process.StandardInput.Close();
                var ready = false;
                for (var i = 0; i < 30 && !ready && !process.HasExited; i++) { await Task.Delay(100); ready = await CoreDnsCache.FlushAsync(stats, CancellationToken.None); }
                if (!ready) throw new InvalidOperationException("DNS fixture did not start: " + string.Join("; ", errors));
                Assert(!await CoreDnsCache.FlushAsync(CoreStatsEntry.Create(stats.Port), CancellationToken.None));
                using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(2) };
                async Task<bool> Answer(string domain, string ip)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{stats.Port}/dns/query?name={domain}&type=A");
                    request.Headers.Authorization = new("Bearer", stats.Secret);
                    using var response = await http.SendAsync(request);
                    return response.IsSuccessStatusCode && (await response.Content.ReadAsStringAsync()).Contains(ip, StringComparison.Ordinal);
                }
                Assert(await Answer("goldapple.ru", "198.51.100.20"));
                foreach (var direct in new[] { true, false })
                {
                    Write(direct);
                    var changed = false;
                    for (var i = 0; i < 30 && !changed; i++)
                    {
                        await Task.Delay(100);
                        Assert(await CoreDnsCache.FlushAsync(stats, CancellationToken.None));
                        changed = await Answer("goldapple.ru", direct ? "192.0.2.20" : "198.51.100.20");
                    }
                    Assert(changed && await Answer("www.goldapple.ru", direct ? "192.0.2.20" : "198.51.100.20"));
                }
            }
            finally { if (!process.HasExited) process.Kill(true); }
        });
    }
}
