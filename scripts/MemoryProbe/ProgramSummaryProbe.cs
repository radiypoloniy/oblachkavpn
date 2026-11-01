using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;

internal static class ProgramSummaryProbe
{
    internal static int Run(string assemblyPath, string report)
    {
        var assembly = Assembly.LoadFrom(Path.GetFullPath(assemblyPath));
        var full = assembly.GetType("Oblachka.CoreConnections", true)!.GetMethod("Parse", BindingFlags.Static | BindingFlags.NonPublic, [typeof(string)])!;
        var compactType = assembly.GetType("Oblachka.CorePrograms", true)!;
        var compact = compactType.GetMethod("Parse", BindingFlags.Static | BindingFlags.NonPublic)!;
        var rows = assembly.GetType("Oblachka.CoreProgramSnapshot", true)!.GetProperty("Programs")!;
        const int connections = 2000, programs = 100, iterations = 30;
        var fullJson = JsonSerializer.Serialize(new { connections = Enumerable.Range(0, connections).Select(i => new
        {
            id = "connection-" + i, upload = 100L, download = 1000L, chains = new[] { "user-direct" },
            metadata = new { network = "tcp", processPath = $@"C:\Apps\program-{i % programs}.exe", host = "site.fixture.invalid", destinationIP = "192.0.2.1", destinationPort = "443" }
        }) });
        var compactJson = JsonSerializer.Serialize(new { version = 1, epoch = Guid.NewGuid().ToString(), programs = Enumerable.Range(0, programs).Select(i => new
        {
            path = $@"C:\Apps\program-{i}.exe", upload = 2000L, download = 20000L, vpn = 0, direct = 20, udp = 0, lastSeen = 1000L
        }) });
        object Measure(string name, string json, Func<int> parse, int expected)
        {
            for (var i = 0; i < 5; i++) if (parse() != expected) throw new InvalidOperationException("Wrong parser row count");
            var start = GC.GetAllocatedBytesForCurrentThread(); var clock = Stopwatch.StartNew();
            for (var i = 0; i < iterations; i++) if (parse() != expected) throw new InvalidOperationException("Wrong parser row count");
            clock.Stop();
            return new { Name = name, PayloadBytes = System.Text.Encoding.UTF8.GetByteCount(json), AllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - start, ElapsedMs = clock.Elapsed.TotalMilliseconds };
        }
        var results = new[]
        {
            Measure("FullConnections", fullJson, () => ((ICollection)full.Invoke(null, [fullJson])!).Count, connections),
            Measure("CompactPrograms", compactJson, () => { using var doc = JsonDocument.Parse(compactJson); return ((ICollection)rows.GetValue(compact.Invoke(null, [doc.RootElement]))!).Count; }, programs)
        };
        File.WriteAllText(report, JsonSerializer.Serialize(new { Connections = connections, Programs = programs, Iterations = iterations, ForcedGc = false, Results = results }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}
