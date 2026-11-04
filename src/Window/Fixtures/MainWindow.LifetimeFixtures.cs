using System.Runtime.CompilerServices;
using System.Windows.Threading;

namespace Oblachka;

public partial class MainWindow
{
    private async Task CheckWindowReleaseAsync(List<string> checks)
    {
        var core = new ViewLifetimeCore();
        using var owner = new ClientRuntime(core);
        var profile = Profiles.Parse("vless://11111111-1111-4111-8111-111111111111@example.invalid:443?security=tls&type=tcp#Lifetime") with { Group = ProfileGroup.ManualId };
        owner.Profiles.Imported = [profile]; owner.Profiles.Selected = profile;
        owner.Profiles.Picker.ItemsSource = owner.Profiles.Imported;
        owner.Profiles.Picker.SelectedItem = profile;
        owner.Profiles.Groups.Add(ProfileGroup.Manual);
        owner.Profiles.GroupProfiles[ProfileGroup.ManualId] = [profile];
        var statePath = Path.Combine(AppContext.BaseDirectory, "test-data", "view-life-" + Guid.NewGuid().ToString("N"), "state.bin");
        owner.Persistence.Configure(new ClientStateStore(statePath), isolated: true);
        var proxyStore = new ViewProxyStore(); owner.Preferences.SystemProxy = true;
        owner.Proxy.Start(new DispatcherSynchronizationContext(Dispatcher), new SystemProxy(proxyStore, statePath + ".proxy.json"), false);
        owner.Preferences.Notices = false;
        owner.Preferences.QuicOverTcp = true;
        owner.TunnelPolicy.Advanced = AdvancedOptions.Default with { Mtu = 1300 };
        var reference = CreateClosedView(owner);
        for (var i = 0; i < 5; i++)
        {
            await Task.Delay(150);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            // Только проверка удержания объектов; в рабочем клиенте принудительного GC нет.
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        }
        if (reference.Target is MainWindow retained) throw new InvalidOperationException(
            $"Closed view remains rooted: closed={retained.closed}, windows={System.Windows.Application.Current.Windows.Count}, " +
            $"visible={retained.IsVisible}, presentation={retained.presentationTimer.IsEnabled}");
        if (!ReferenceEquals(owner.Profiles.Selected, profile) || owner.Profiles.Imported.Count != 1)
            throw new InvalidOperationException("Closing a view cleared application profile state");
        if (owner.Preferences.Notices || owner.Preferences.QuicOverTcp)
            throw new InvalidOperationException("Preferences did not survive control changes and view release");
        if (owner.TunnelPolicy.Advanced.Mtu != 1300) throw new InvalidOperationException("Creating a view reset application TUN tuning");
        if (core.Disposed || core.State != ConnectionState.Running)
            throw new InvalidOperationException("Closing a view stopped the application-owned core");
        if (owner.Tray is null || !owner.Tray.Icon.Visible) throw new InvalidOperationException("Closing a view disposed the application tray");
        if (proxyStore.Current.ProxyServer != "127.0.0.1:10808") throw new InvalidOperationException("Closing a view restored the application proxy");
        proxyStore.Current = new(0, null, null, null); owner.Proxy.Poll();
        if (proxyStore.Current.ProxyServer != "127.0.0.1:10808") throw new InvalidOperationException("Proxy guard stopped with its view");
        var sink = new TrayCommandSink(); owner.Tray.Attach(sink);
        foreach (var command in Enum.GetValues<TrayCommand>()) owner.Tray.Request(command);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        if (!sink.Commands.SequenceEqual(Enum.GetValues<TrayCommand>())) throw new InvalidOperationException("Tray lost command dispatch after closing its view");
        owner.Tray.Detach(sink);
        var requested = false;
        void RequestView() { requested = true; owner.Tray.Attach(sink); }
        owner.Tray.ViewRequested += RequestView; owner.Tray.Request(TrayCommand.Open);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        owner.Tray.ViewRequested -= RequestView;
        if (!requested || sink.Commands[^1] != TrayCommand.Open) throw new InvalidOperationException("Tray did not request a missing view");
        var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.Persistence.Changed += () => { if (!owner.Persistence.Dirty && owner.Persistence.LastError is null) saved.TrySetResult(); };
        owner.Workspace.Favorites.Add(AutoPool.Key(profile)); owner.Persistence.Queue();
        await saved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (owner.Persistence.Store!.Load()?.Favorites.Length != 1) throw new InvalidOperationException("Application state was not saved after view collection");
        var completed = new TaskCompletionSource<HealthObservation>();
        owner.Monitor.Completed += result => completed.TrySetResult(result);
        owner.Monitor.NextCheck = DateTimeOffset.MinValue;
        var observation = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (!observation.Round.Verified) throw new InvalidOperationException("Application monitor stopped with the closed view");
        checks.Add("Closed WPF window is collectible while application runtime, monitor, simulated core and selected profile remain alive");
        checks.Add("Application tray dispatches commands and encrypted autosave continues after its WPF view is collected");
        checks.Add("Application system proxy remains applied and repairs overwrites after its WPF view is collected");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateClosedView(ClientRuntime owner)
    {
        var window = new MainWindow(owner, persistenceEnabled: false);
        if (window.Settings.Notices.IsChecked != false || window.Settings.QuicOverTcp.IsChecked != true)
            throw new InvalidOperationException("New view did not display application preferences");
        window.Settings.QuicOverTcp.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, false);
        window.SubscribeLifecycle(enabled: true); // Только имитация ядра; реальные запросы не выполняются.
        if (owner.Preferences.QuicOverTcp) throw new InvalidOperationException("Toggle did not update application preference");
        owner.Preferences.QuicOverTcp = true;
        if (window.Settings.QuicOverTcp.IsChecked != true) throw new InvalidOperationException("Preference did not update toggle");
        window.Settings.QuicOverTcp.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, false);
        window.exitRequested = true;
        window.ShowActivated = false;
        window.Show();
        window.Close();
        return new WeakReference(window);
    }

    private sealed class ViewLifetimeCore : ITunnelController
    {
        public bool Disposed;
        public int Port => 10808;
        public int? ProcessId => State == ConnectionState.Running ? 42 : null;
        public ConnectionState State { get; private set; } = ConnectionState.Running;
        public string CoreLabel => "fixture";
        public event Action<ConnectionState, string>? Changed { add { } remove { } }
        public Task StartAsync(Profile profile, CancellationToken token) => Task.CompletedTask;
        public Task<ProbeResult> ProbeAsync(Uri target, CancellationToken token) => Task.FromResult(new ProbeResult(true, 1, "fixture"));
        public void Stop() => State = ConnectionState.Idle;
        public void Dispose() { Disposed = true; Stop(); }
    }
    private sealed class TrayCommandSink : IClientTrayView
    {
        public bool Closed => false;
        public bool Foreground => false;
        public List<TrayCommand> Commands { get; } = new();
        public void Handle(TrayCommand command, string? renewUrl) => Commands.Add(command);
    }
    private sealed class ViewProxyStore : IProxySettingsStore
    {
        public ProxySettings Current = new(0, null, null, null);
        public ProxySettings Read() => Current;
        public void Write(ProxySettings settings) => Current = settings;
    }
}
