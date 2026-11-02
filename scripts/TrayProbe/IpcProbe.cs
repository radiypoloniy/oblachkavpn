using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Oblachka;

// Фоновый процесс не ссылается на типы окна. Все WPF-компоненты живут в дочернем процессе.
internal static class IpcProbe
{
    internal static async Task RunAsync(ClientRuntime owner, SynchronizationContext context, string viewExecutable, string report)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        owner.Profiles.AutoSelect = true;
        await owner.Pause.PauseAsync(5);
        // Только диагностический стенд сокращает пять минут до 100 мс.
        typeof(ClientPause).GetMethod("SetDeadline", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(owner.Pause, new object[] { DateTimeOffset.UtcNow.AddMilliseconds(100) });
        while (owner.Pause.Until is not null || owner.Connections.IsActive) await Task.Delay(25, timeout.Token);
        if (owner.Engine.State != ConnectionState.Running) throw new InvalidOperationException("Headless pause did not resume");
        Func<IReadOnlyList<string>, int, CancellationToken, Task<MirrorResult>> fetch = (_, _, _) => Task.FromResult(
            new MirrorResult(new ImportResult(new[] { owner.Profiles.Selected! }, 0, 0), 0, 1));
        var load = typeof(ClientImports).GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Single(m => m.Name == "LoadAsync" && m.GetParameters().Length == 3);
        await (Task)load.Invoke(owner.Imports, new object[] { new[] { "https://fixture.invalid/sub" }, 0, fetch })!;
        if (!owner.Profiles.Groups.Any(g => g.IsSubscription)) throw new InvalidOperationException("Headless import did not apply");
        owner.Workspace.Favorites.Add(AutoPool.Key(owner.Profiles.Selected!)); owner.Persistence.Queue();
        await Task.Delay(1200, timeout.Token);
        var samples = new List<object> { Sample("before-views", owner) };
        foreach (var mode in new[] { "connect", "close", "crash", "close", "stop" })
        {
            var viewReport = report + ".view-" + samples.Count + ".json";
            using var endpoint = new ClientViewServer(owner, context);
            var start = new ProcessStartInfo(viewExecutable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(viewExecutable)! };
            foreach (var arg in new[] { "--view-channel-fixture", endpoint.PipeName, Environment.ProcessId.ToString(), viewReport, mode }) start.ArgumentList.Add(arg);
            using var child = Process.Start(start) ?? throw new InvalidOperationException("View did not start");
            var serving = endpoint.ServeAsync(child, timeout.Token);
            try
            {
                await child.WaitForExitAsync(timeout.Token); await serving;
                using var json = JsonDocument.Parse(await File.ReadAllTextAsync(viewReport, timeout.Token));
                if (child.ExitCode != (mode == "crash" ? 23 : 0) || !json.RootElement.GetProperty("Passed").GetBoolean() ||
                    json.RootElement.GetProperty("InstanceId").GetGuid() != owner.InstanceId)
                    throw new InvalidOperationException("View exchange failed: " + await File.ReadAllTextAsync(viewReport));
                if (owner.Engine.State != (mode == "stop" ? ConnectionState.Idle : ConnectionState.Running))
                    throw new InvalidOperationException("View lifetime changed connection state");
            }
            finally
            {
                endpoint.Dispose();
                if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); }
                try { await serving; } catch (OperationCanceledException) { }
            }
            samples.Add(Sample("after-" + mode + "-" + samples.Count, owner));
        }
        if (WpfAssemblies().Length != 0 || owner.Persistence.Store!.Load()?.Favorites.Length != 1 || owner.Proxy.Port != 0)
            throw new InvalidOperationException("Background owner lost persistence/proxy state or loaded WPF");
        File.WriteAllText(report, JsonSerializer.Serialize(new { Passed = true, ForcedGc = false,
            Network = "Synthetic core and proxy store; real local pipes and WPF child windows; no real VPN or external requests",
            StateSaved = true, ExplicitStopRestoredProxy = true, Samples = samples }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static string?[] WpfAssemblies() => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name)
        .Where(n => n is "PresentationFramework" or "PresentationCore" or "WindowsBase").ToArray();
    private static object Sample(string phase, ClientRuntime owner) => new { Phase = phase, OwnerInstance = owner.InstanceId,
        Connection = owner.Engine.State.ToString(), WpfAssemblies = WpfAssemblies(), Memory = ProcessMemory.Read() };
}
