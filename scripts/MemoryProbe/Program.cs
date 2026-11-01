using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;

// Сравнивает один и тот же синтетический ответ /connections в двух сборках клиента.
// Каждая сборка измеряется отдельным процессом; сборка мусора принудительно не запускается.
if (args.Length != 2) return 1;
var assembly = Assembly.LoadFrom(Path.GetFullPath(args[0]));
var method = assembly.GetType("Oblachka.CoreConnections", throwOnError: true)!
    .GetMethod("Parse", BindingFlags.Static | BindingFlags.NonPublic, [typeof(string)])!;
const int connections = 2000, iterations = 30;
var json = JsonSerializer.Serialize(new
{
    connections = Enumerable.Range(0, connections).Select(i => new
    {
        id = "connection-" + i, upload = i * 100L, download = i * 200L,
        metadata = new { network = "tcp", processPath = @"C:\Apps\fixture.exe", host = "site" + i + ".invalid", destinationIP = "192.0.2.1", destinationPort = "443" },
        chains = new[] { "local-core" }
    })
});
object?[] input = [json];
for (var i = 0; i < 5; i++) method.Invoke(null, input);
var allocated = GC.GetAllocatedBytesForCurrentThread();
var watch = Stopwatch.StartNew();
for (var i = 0; i < iterations; i++)
{
    if (method.Invoke(null, input) is not System.Collections.ICollection { Count: connections }) return 2;
}
watch.Stop();
var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
File.WriteAllText(args[1], JsonSerializer.Serialize(new
{
    Assembly = Path.GetFullPath(args[0]), Connections = connections, Iterations = iterations,
    AllocatedBytes = bytes, ElapsedMs = watch.Elapsed.TotalMilliseconds, ForcedGc = false
}, new JsonSerializerOptions { WriteIndented = true }));
return 0;
