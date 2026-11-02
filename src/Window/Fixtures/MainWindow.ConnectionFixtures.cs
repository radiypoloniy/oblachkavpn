using System.Runtime.CompilerServices;
using System.Windows.Threading;

namespace Oblachka;

public partial class MainWindow
{
    private async Task CheckOwnedConnectionAsync(List<string> checks)
    {
        var selected = Profiles.Parse("vless://11111111-1111-4111-8111-111111111111@example.invalid:443?security=tls&type=tcp#Window-start") with { Group = ProfileGroup.ManualId };
        using (var owner = ConnectionOwner(new WindowStartCore(), selected))
        {
            var core = (WindowStartCore)owner.Engine;
            var child = new MainWindow(owner, persistenceEnabled: false) { ShowActivated = false };
            child.Show();
            var connecting = child.ConnectCoreAsync();
            await core.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            if (!owner.Connections.IsActive || owner.Activity.Operation is null) throw new InvalidOperationException("Window did not delegate startup to runtime");
            child.exitRequested = true; child.Close();
            core.Release.SetResult(); await connecting;
            if (core.State != ConnectionState.Running || owner.Profiles.Running != selected || !owner.Workspace.WasConnected)
                throw new InvalidOperationException("Closing window cancelled its application-owned start");
            await owner.Connections.StopAsync();
        }
        using (var owner = ConnectionOwner(new WindowStartCore(), selected))
        {
            var core = (WindowStartCore)owner.Engine;
            var connecting = owner.Connections.StartAsync(selected);
            await core.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var reference = ClosedStartupView(owner);
            for (var i = 0; i < 5; i++)
            {
                await Task.Delay(150);
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            }
            if (reference.IsAlive || core.Release.Task.IsCompleted || !owner.Connections.IsActive)
                throw new InvalidOperationException("Application startup retains a closed WPF view or was cancelled with it");
            core.Release.SetResult(); await connecting;
            if (core.State != ConnectionState.Running) throw new InvalidOperationException("Headless startup did not complete");
            await owner.Connections.StopAsync();
        }
        checks.Add("Main WPF window delegates connection startup and closing it does not cancel runtime-owned work");
        checks.Add("Closed WPF view is collectible while application-owned connection startup remains pending");
    }
    private static ClientRuntime ConnectionOwner(WindowStartCore core, Profile selected)
    {
        var owner = new ClientRuntime(core); owner.Persistence.Configure(null, isolated: true);
        owner.Profiles.Imported = [selected]; owner.Profiles.Selected = selected;
        owner.Profiles.Picker.ItemsSource = owner.Profiles.Imported; owner.Profiles.Picker.SelectedItem = selected;
        owner.Profiles.Groups.Add(ProfileGroup.Manual); owner.Profiles.GroupProfiles[ProfileGroup.ManualId] = [selected];
        return owner;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ClosedStartupView(ClientRuntime owner)
    {
        var child = new MainWindow(owner, persistenceEnabled: false) { ShowActivated = false };
        child.Show(); child.exitRequested = true; child.Close();
        return new(child);
    }
    private sealed class WindowStartCore : ITunnelController
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Port => 10808;
        public int? ProcessId => State == ConnectionState.Running ? 42 : null;
        public ConnectionState State { get; private set; }
        public string CoreLabel => "window-start-fixture";
        public event Action<ConnectionState, string>? Changed;
        public async Task StartAsync(Profile profile, CancellationToken token)
        {
            State = ConnectionState.Starting; Changed?.Invoke(State, "fixture"); Entered.TrySetResult();
            await Release.Task.WaitAsync(token);
            State = ConnectionState.Running; Changed?.Invoke(State, "fixture");
        }
        public Task<ProbeResult> ProbeAsync(Uri target, CancellationToken token) => Task.FromResult(new ProbeResult(true, 1, "fixture"));
        public void Stop() { State = ConnectionState.Idle; Changed?.Invoke(State, "fixture"); }
        public void Dispose() => Stop();
    }
}
