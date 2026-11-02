using System.Windows.Threading;

namespace Oblachka;

public partial class MainWindow
{
    private async Task CheckOwnedMeasurementsAsync(List<string> checks)
    {
        var selected = Profiles.Parse("vless://11111111-1111-4111-8111-111111111111@example.invalid:443?security=tls&type=tcp#Background-measure") with { Group = ProfileGroup.ManualId };
        var backend = new BackgroundMeasurementFixture(); var network = new BackgroundNetworkFixture();
        using (var owner = ConnectionOwner(new WindowStartCore(), selected, backend, network))
        {
            owner.Networks.Read();
            var measuring = owner.Measurements.MeasureAsync([selected], true);
            var reference = ClosedStartupView(owner);
            for (var i = 0; i < 3; i++)
            {
                await Task.Delay(100); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            }
            if (reference.IsAlive || !owner.Measurements.IsMeasuring) throw new InvalidOperationException("Measurement is owned by the closed view");
            backend.Release.SetResult(); await measuring;
            if (owner.Profiles.Measurements.Count != 1) throw new InvalidOperationException("Measurement lost its result after closing the view");
        }
        using (var owner = ConnectionOwner(new WindowStartCore(), selected, new BackgroundMeasurementFixture(), network))
        {
            ((WindowStartCore)owner.Engine).Release.SetResult(); owner.Activity.VmTestActive = true;
            var reference = ClosedStartupView(owner); owner.Activity.VmTestActive = false;
            owner.Workspace.KnownNetworks[network.Key] = new("Fixture", NetworkMode.Vpn);
            var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var count = 0;
            owner.Networks.Connected += () => { count++; connected.TrySetResult(); };
            owner.Networks.Start(new DispatcherSynchronizationContext(Dispatcher));
            for (var i = 0; i < 100; i++) owner.Networks.RequestChange();
            await connected.Task.WaitAsync(TimeSpan.FromSeconds(6));
            for (var i = 0; i < 3; i++)
            {
                await Task.Delay(100); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            }
            if (reference.IsAlive || count != 1 || owner.Engine.State != ConnectionState.Running)
                throw new InvalidOperationException("Network notifications retained the view or caused duplicate connects");
            await owner.Connections.StopAsync();
        }
        checks.Add("Application-owned measurement survives closing and collecting its WPF view");
        checks.Add("Coalesced network events connect once after the WPF view closes and is collected");
        using (var owner = ConnectionOwner(new WindowStartCore(), selected))
        {
            owner.Workspace.TrafficDay = "2000-01-01"; owner.Workspace.TrafficToday = 500;
            owner.Traffic.Start(new DispatcherSynchronizationContext(Dispatcher));
            var reference = ClosedStartupView(owner);
            for (var i = 0; i < 5; i++)
            {
                await Task.Delay(250); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            }
            if (reference.IsAlive || !owner.Traffic.Started || owner.Workspace.TrafficDay != DateTime.Now.ToString("yyyy-MM-dd") || owner.Workspace.TrafficToday != 0)
                throw new InvalidOperationException("Traffic accounting stopped or retained the view");
        }
        checks.Add("Traffic accounting timer survives closing and collecting the WPF view");
    }
    private sealed class BackgroundNetworkFixture : IClientNetworkEnvironment
    {
        public string Key { get; } = NetworkPolicy.Key(Guid.NewGuid());
        public (string Key, string Name, bool Public)? Read() => (Key, "Fixture", false);
        public bool Moved(SystemTunnelSession session) => false;
    }
    private sealed class BackgroundMeasurementFixture : IClientMeasurementBackend
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? Network() => "Fixture";
        public bool Metered() => true;
        public Task PingAsync(IReadOnlyList<Profile> targets, Action<Profile, long?> result, CancellationToken token) => Task.CompletedTask;
        public async Task MeasureAsync(IReadOnlyList<Profile> targets, bool physical, Action<ServerMeasurement> result,
            Func<Profile, Profile?> route, bool throughput, CancellationToken token, int parallelism = ServerMeasurements.Parallel, Action<Profile>? starting = null)
        {
            await Release.Task.WaitAsync(token);
            foreach (var p in targets) result(new(Profiles.Fingerprint(p), DateTimeOffset.UtcNow, true, 5));
        }
    }
}
