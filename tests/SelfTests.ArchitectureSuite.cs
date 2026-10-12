using System.Text.Json;

namespace Oblachka;

public static partial class SelfTests
{
    public static async Task<int> RunArchitectureAsync(string report)
    {
        var checks = new List<Check>();
        var root = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        async Task CheckAsync(string name, Func<Task> action)
        {
            try { await action(); checks.Add(new(name, true, null)); }
            catch (Exception ex) { checks.Add(new(name, false, ex.ToString())); }
        }
        await ArchitectureChecksAsync(CheckAsync);
        await NetworkSessionChecksAsync(CheckAsync, root);
        await HelperPipeChecksAsync(CheckAsync, root);
        await TunConfigurationChecksAsync(CheckAsync);
        await SystemSessionChecksAsync(CheckAsync);
        await VmKitChecksAsync(CheckAsync, root);
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
        {
            Passed = checks.All(c => c.Passed), Network = "Synthetic backend, local pipes, sing-box check only; no TUN startup", Checks = checks
        }, new JsonSerializerOptions { WriteIndented = true }));
        return checks.All(c => c.Passed) ? 0 : 2;
    }
}
