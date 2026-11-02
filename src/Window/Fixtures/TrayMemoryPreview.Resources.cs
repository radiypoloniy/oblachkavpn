using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace Oblachka;

// Minutes of the complete WPF view, not a navigation microbenchmark. No user state or VPN.
internal static partial class TrayMemoryPreview
{
    internal static int RunResources(string report, int seconds, int profiles)
    {
        if (seconds is < 5 or > 1800 || profiles is < 1 or > ProfileGroup.MaxTotalProfiles) return 2;
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        FlagImages.AllowNetwork = false;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            using var owner = new ClientRuntime(new FixtureCore());
            owner.Persistence.Configure(null, isolated: true);
            owner.Preferences.StayInTray = false; owner.Preferences.Notices = false;
            owner.Activity.VmTestActive = true;
            MainWindow? window = null;
            try
            {
                window = new MainWindow(owner, persistenceEnabled: false) { ShowActivated = false };
                window.PrepareResourceProfiles(profiles); window.Show();
                owner.Traffic.Start(new DispatcherSynchronizationContext(app.Dispatcher));
                var phases = new List<object>();
                foreach (var phase in new[] { "home", "servers", "hidden" })
                {
                    window.ResourcePage(phase);
                    await Task.Delay(phase == "home" ? 30000 : 5000);
                    File.WriteAllText(report + ".progress.json", JsonSerializer.Serialize(new { Pid = Environment.ProcessId, Phase = phase, Utc = DateTimeOffset.UtcNow }));
                    using var process = Process.GetCurrentProcess();
                    var cpu = process.TotalProcessorTime; var allocated = GC.GetTotalAllocatedBytes();
                    var collections = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
                    var ticks = window.ResourcePresentationTicks; var clock = Stopwatch.StartNew();
                    var memory = new List<object>();
                    for (var elapsed = 0; elapsed < seconds; elapsed += 5)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(Math.Min(5, seconds - elapsed)));
                        memory.Add(Sample(phase, new(window), owner, false));
                    }
                    phases.Add(new { Phase = phase, Seconds = clock.Elapsed.TotalSeconds,
                        CpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds,
                        AllocatedBytes = GC.GetTotalAllocatedBytes() - allocated,
                        Collections = new[] { GC.CollectionCount(0) - collections[0], GC.CollectionCount(1) - collections[1], GC.CollectionCount(2) - collections[2] },
                        PresentationTicks = window.ResourcePresentationTicks - ticks, Memory = memory });
                    File.WriteAllText(report, JsonSerializer.Serialize(new { Passed = true, Complete = phase == "hidden", Profiles = profiles,
                        Network = "Synthetic idle core, no real VPN, proxy changes or external requests", ForcedGc = false, Phases = phases }, new JsonSerializerOptions { WriteIndented = true }));
                }
                File.WriteAllText(report + ".refresh.json", JsonSerializer.Serialize(window.ResourceRefreshBenchmark(), new JsonSerializerOptions { WriteIndented = true }));
                window.Close(); app.Shutdown(0);
            }
            catch (Exception error)
            { File.WriteAllText(report, JsonSerializer.Serialize(new { Passed = false, Error = error.ToString() })); app.Shutdown(2); }
            finally { window?.Close(); }
        };
        return app.Run();
    }
}

public partial class MainWindow
{
    internal int ResourcePresentationTicks { get; private set; }
    internal void PrepareResourceProfiles(int count)
    {
        groups.Clear(); groupProfiles.Clear();
        SetGroup(ProfileGroup.Manual, Enumerable.Range(0, count).Select(i => Profiles.Parse(
            $"vless://{i + 100:x8}-1111-4111-8111-111111111111@node{i}.invalid:443?security=tls&type=tcp#FI%20Server%20{i}")).ToArray());
        ApplyProfiles(false); Navigate("Connection");
        presentationTimer.Tick += (_, _) => ResourcePresentationTicks++;
    }
    internal void ResourcePage(string page)
    {
        if (page == "hidden") Hide();
        else { Show(); Navigate(page == "servers" ? "Servers" : "Connection"); }
        UpdateLayout();
    }
    internal object ResourceRefreshBenchmark()
    {
        using var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime; var bytes = GC.GetAllocatedBytesForCurrentThread(); var clock = Stopwatch.StartNew();
        for (var i = 0; i < 1000; i++) { RefreshHome(); RefreshServerListControls(); }
        return new { Iterations = 1000, CpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds,
            ElapsedMs = clock.Elapsed.TotalMilliseconds, AllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - bytes };
    }
}
