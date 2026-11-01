using System.Diagnostics;
using System.Text.Json;
using System.Windows;

namespace Oblachka;

// Изолированный замер окна: без реальных подписок, сетевых ядер и принудительной сборки мусора.
internal static class MemoryPreview
{
    internal static int Run(string report)
    {
        FlagImages.AllowNetwork = false;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            MainWindow? window = null;
            try
            {
                window = new MainWindow(persistenceEnabled: false);
                var samples = await window.MeasureMemoryAsync();
                File.WriteAllText(report, JsonSerializer.Serialize(new { Passed = true, ForcedGc = false, Samples = samples }, new JsonSerializerOptions { WriteIndented = true }));
                app.Shutdown(0);
            }
            catch (Exception ex)
            {
                File.WriteAllText(report, JsonSerializer.Serialize(new { Passed = false, Error = ex.ToString() }));
                app.Shutdown(2);
            }
            finally { window?.Close(); }
        };
        return app.Run();
    }
}

public partial class MainWindow
{
    internal async Task<object[]> MeasureMemoryAsync()
    {
        var samples = new List<object>();
        async Task Sample(string name)
        {
            Measure(new Size(1040, 700)); Arrange(new Rect(0, 0, 1040, 700)); UpdateLayout();
            await Task.Delay(1500);
            using var process = Process.GetCurrentProcess();
            samples.Add(new { Scenario = name, PrivateBytes = process.PrivateMemorySize64, WorkingSet = process.WorkingSet64,
                ManagedBytes = GC.GetTotalMemory(false), AllocatedBytes = GC.GetTotalAllocatedBytes() });
        }
        await Sample("startup");
        LoadPreviewProfiles(); Navigate("Connection");
        await Sample("home");
        var allocated = GC.GetTotalAllocatedBytes();
        for (var i = 0; i < 100; i++) RefreshHome();
        samples.Add(new { Scenario = "home-100-refreshes", AllocatedBytes = GC.GetTotalAllocatedBytes() - allocated });
        OpenAnalytics(); await Sample("analytics");
        CloseAnalytics();
        groups.Clear(); groupProfiles.Clear();
        SetGroup(ProfileGroup.Manual, Enumerable.Range(0, ProfileGroup.MaxTotalProfiles).Select(i => Profiles.Parse(
            $"vless://{i + 100:x8}-1111-4111-8111-111111111111@node{i}.invalid:443?security=tls&type=tcp#FI%20Server%20{i}")).ToArray());
        ApplyProfiles(false); Navigate("Connection");
        await Sample("home-600-profiles");
        allocated = GC.GetTotalAllocatedBytes();
        for (var i = 0; i < 100; i++) { RefreshHome(); RefreshServerListControls(); }
        samples.Add(new { Scenario = "600-profiles-100-refreshes", AllocatedBytes = GC.GetTotalAllocatedBytes() - allocated });
        return samples.ToArray();
    }
}
