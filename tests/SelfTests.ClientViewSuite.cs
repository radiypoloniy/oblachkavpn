using System.Text.Json;

namespace Oblachka;

public static partial class SelfTests
{
    public static async Task<int> RunClientViewAsync(string report)
    {
        var checks = new List<Check>();
        var root = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        async Task CheckAsync(string name, Func<Task> action)
        {
            try { await action(); checks.Add(new(name, true, null)); }
            catch (Exception error) { checks.Add(new(name, false, error.ToString())); }
        }
        await ClientViewChecksAsync(CheckAsync, root);
        await ClientConnectionsChecksAsync(CheckAsync);
        await ClientPauseChecksAsync(CheckAsync);
        await ClientImportsChecksAsync(CheckAsync);
        await ClientMeasurementChecksAsync(CheckAsync); await ClientNetworkChecksAsync(CheckAsync);
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { Passed = checks.All(c => c.Passed), Checks = checks }, new JsonSerializerOptions { WriteIndented = true }));
        return checks.All(c => c.Passed) ? 0 : 2;
    }
}
