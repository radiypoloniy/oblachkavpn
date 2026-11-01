using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Oblachka;

internal static class RuleSetMemoryCheckCommand
{
    internal static int Run(int domainCount, string reportPath)
    {
        var folder = Path.Combine(Path.GetTempPath(), "oblachka-rules-bench-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            var matchers = new RouteMatchers();
            for (var i = 0; i < domainCount; i++) matchers.Suffix.Add($"site{i}.example{i % 997}.com");
            for (var i = 0; i < 20000; i++) matchers.Cidr.Add($"10.{i >> 8 & 255}.{i & 255}.0/24");
            string[] Items(string kind) => (kind switch
            {
                "suffix" => matchers.Suffix,
                "full" => matchers.Full,
                "keyword" => matchers.Keyword,
                "regex" => matchers.Regex,
                _ => matchers.Cidr
            }).ToArray();
            var sourceJson = RoutingLists.SourceJson(Items);
            var sourcePath = Path.Combine(folder, "rules.json");
            var binaryPath = Path.Combine(folder, "rules.srs");
            File.WriteAllText(sourcePath, sourceJson);
            BinaryRuleSets.Compile(folder, sourceJson, binaryPath);
            var runs = new List<Sample>();
            for (var round = 0; round < 3; round++)
            {
                runs.Add(Measure("source", sourcePath));
                runs.Add(Measure("binary", binaryPath));
            }
            var report = runs.GroupBy(r => r.Format).Select(g => new
            {
                Format = g.Key,
                MedianPrivateBytes = Median(g.Select(r => r.PrivateBytes)),
                MedianWorkingSet = Median(g.Select(r => r.WorkingSet)),
                MedianStartupMs = Median(g.Select(r => r.StartupMs))
            });
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new { Domains = domainCount, Cidrs = 20000, SourceBytes = new FileInfo(sourcePath).Length, BinaryBytes = new FileInfo(binaryPath).Length, Results = report }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    private sealed record Sample(string Format, long PrivateBytes, long WorkingSet, long StartupMs);

    private static Sample Measure(string format, string path)
    {
        var adapter = new CoreAdapter(CoreKind.SingBox, Path.Combine(AppContext.BaseDirectory, "tools", "sing-box.exe"));
        using var engine = new Engine(adapter);
        var clock = Stopwatch.StartNew();
        engine.StartCoreAsync(port =>
        {
            var config = adapter.RuntimeConfig(new JsonObject { ["type"] = "direct" }, port);
            config["route"]!["rule_set"] = new JsonArray(new JsonObject
            {
                ["type"] = "local", ["tag"] = "bench", ["format"] = format, ["path"] = path
            });
            config["route"]!["rules"] = new JsonArray(new JsonObject
            {
                ["rule_set"] = new JsonArray("bench"), ["outbound"] = "tunnel"
            });
            return config;
        }, CancellationToken.None).GetAwaiter().GetResult();
        var startup = clock.ElapsedMilliseconds;
        using var process = Process.GetProcessById(engine.ProcessId!.Value);
        Thread.Sleep(1500);
        process.Refresh();
        var result = new Sample(format, process.PrivateMemorySize64, process.WorkingSet64, startup);
        engine.Stop();
        return result;
    }

    private static long Median(IEnumerable<long> values)
    {
        var sorted = values.Order().ToArray();
        return sorted[sorted.Length / 2];
    }
}
