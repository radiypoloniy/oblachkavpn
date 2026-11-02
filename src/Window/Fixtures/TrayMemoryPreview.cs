using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace Oblachka;

// Диагностика гипотезы освобождения окна. Принудительный GC — отдельный диагностический образец,
// не поведение клиента и не результат оптимизации. Реальных ядер, подписок и системных изменений нет.
internal static partial class TrayMemoryPreview
{
    internal static int Run(string report)
    {
        if (Environment.GetEnvironmentVariable("OBLACHKA_PROBE_SOFTWARE") == "1")
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        FlagImages.AllowNetwork = false;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            using var owner = new ClientRuntime(new FixtureCore());
            owner.Persistence.Configure(null, isolated: true);
            owner.Preferences.StayInTray = false;
            owner.Preferences.Notices = false;
            WeakReference<MainWindow>? view = null;
            try
            {
                var samples = new List<object>();
                view = Open(owner);
                await Task.Delay(4000);
                var exercise = await Exercise(view);
                samples.Add(Sample("visible", view, owner, false));
                ChangeView(view, release: false);
                await Task.Delay(4000);
                samples.Add(Sample("hidden", view, owner, false));
                ChangeView(view, release: true);
                await Task.Delay(6000);
                await app.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                samples.Add(Sample("closed-natural-gc", view, owner, false));
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                await Task.Delay(1500);
                samples.Add(Sample("closed-diagnostic-forced-gc", view, owner, true));
                if (view.TryGetTarget(out _)) throw new InvalidOperationException("Released window remains rooted");
                File.WriteAllText(report, JsonSerializer.Serialize(new { Passed = true,
                    Network = "Synthetic core; no VPN, system proxy, real subscriptions or external requests",
                    Exercise = exercise, Samples = samples }, new JsonSerializerOptions { WriteIndented = true }));
                app.Shutdown(0);
            }
            catch (Exception error)
            {
                File.WriteAllText(report, JsonSerializer.Serialize(new { Passed = false, Error = error.ToString() }));
                app.Shutdown(2);
            }
            finally { if (view is not null) ChangeView(view, release: true); }
        };
        return app.Run();
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<object> Exercise(WeakReference<MainWindow> view)
    {
        using var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime; var clock = Stopwatch.StartNew();
        for (var i = 0; i < 40; i++)
        {
            if (view.TryGetTarget(out var current)) current.ExerciseMemoryNavigation(i % 2 == 0);
            current = null;
            await Task.Delay(16);
        }
        return new { CpuMilliseconds = (process.TotalProcessorTime - cpu).TotalMilliseconds, ElapsedMilliseconds = clock.Elapsed.TotalMilliseconds };
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<MainWindow> Open(ClientRuntime owner)
    {
        var window = new MainWindow(owner, persistenceEnabled: false) { ShowActivated = false };
        window.LoadPreviewProfiles(); window.PrepareTrayMemoryFixture(); window.Show();
        return new(window);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ChangeView(WeakReference<MainWindow> view, bool release)
    {
        if (!view.TryGetTarget(out var current) || current.IsClosed) return;
        if (release) current.Close(); else current.Hide();
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object Sample(string phase, WeakReference<MainWindow> view, ClientRuntime owner, bool forcedGc)
    {
        using var process = Process.GetCurrentProcess();
        var size = (uint)Marshal.SizeOf<MemoryCounters>();
        if (!GetProcessMemoryInfo(process.Handle, out var counters, size))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var memory = GC.GetGCMemoryInfo();
        return new { Phase = phase, ForcedGc = forcedGc, WindowAlive = view.TryGetTarget(out _),
            Utc = DateTimeOffset.UtcNow, CpuMilliseconds = process.TotalProcessorTime.TotalMilliseconds, AllocatedBytes = GC.GetTotalAllocatedBytes(),
            Windows = Application.Current.Windows.Count, Profiles = owner.Profiles.Imported.Count,
            TrayAlive = owner.Tray?.Icon.Visible == true, PrivateBytes = process.PrivateMemorySize64,
            PrivateWorkingSet = (long)counters.PrivateWorkingSetSize,
            WorkingSet = process.WorkingSet64, ManagedBytes = GC.GetTotalMemory(false),
            GcCommittedBytes = memory.TotalCommittedBytes, GcHeapBytes = memory.HeapSizeBytes,
            Collections = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) } };
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryCounters
    {
        public uint Size, PageFaultCount;
        public nuint PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
            QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage,
            PrivateUsage, PrivateWorkingSetSize;
        public ulong SharedCommitUsage;
    }
    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMemoryInfo(IntPtr process, out MemoryCounters counters, uint size);
    private sealed class FixtureCore : ITunnelController
    {
        public int Port => 0;
        public int? ProcessId => null;
        public ConnectionState State => ConnectionState.Idle;
        public string CoreLabel => "tray-memory-fixture";
        public event Action<ConnectionState, string>? Changed { add { } remove { } }
        public Task StartAsync(Profile profile, CancellationToken token) => throw new InvalidOperationException("Network disabled");
        public Task<ProbeResult> ProbeAsync(Uri target, CancellationToken token) => throw new InvalidOperationException("Network disabled");
        public void Stop() { }
        public void Dispose() { }
    }
}

public partial class MainWindow
{
    internal void PrepareTrayMemoryFixture() { OpenAnalytics(); CloseAnalytics(); Navigate("Connection"); }
    internal void ExerciseMemoryNavigation(bool servers) => Navigate(servers ? "Servers" : "Connection");
}
