using System.Text.Json;

namespace Oblachka;

public static partial class SelfTests
{
    public static async Task<int> RunBackgroundNetworkAsync(string report)
    {
        var checks = new List<Check>();
        async Task CheckAsync(string name, Func<Task> action)
        {
            try { await action(); checks.Add(new(name, true, null)); }
            catch (Exception error) { checks.Add(new(name, false, error.ToString())); }
        }
        await ClientMeasurementChecksAsync(CheckAsync); await ClientNetworkChecksAsync(CheckAsync);
        var wpf = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name)
            .Where(n => n is "PresentationFramework" or "PresentationCore" or "WindowsBase").ToArray();
        checks.Add(new("Background measurement, network and rule operations do not load WPF", wpf.Length == 0, wpf.Length == 0 ? null : string.Join(",", wpf)));
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { Passed = checks.All(c => c.Passed),
            Network = "Synthetic measurement and network backends; loopback stats only; no real VPN", WpfAssemblies = wpf, Checks = checks }, new JsonSerializerOptions { WriteIndented = true }));
        return checks.All(c => c.Passed) ? 0 : 2;
    }
}
