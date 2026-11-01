using System.Runtime.CompilerServices;
using System.Windows.Threading;

namespace Oblachka;

public partial class MainWindow
{
    private async Task CheckWindowReleaseAsync(List<string> checks)
    {
        var core = new ViewLifetimeCore();
        using var owner = new ClientRuntime(core);
        var profile = Profiles.Parse("vless://11111111-1111-4111-8111-111111111111@example.invalid:443?security=tls&type=tcp#Lifetime");
        owner.Profiles.Imported = [profile]; owner.Profiles.Selected = profile;
        owner.Profiles.Picker.ItemsSource = owner.Profiles.Imported;
        owner.Profiles.Picker.SelectedItem = profile;
        owner.Preferences.Notices = false;
        owner.Preferences.QuicOverTcp = true;
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
            $"visible={retained.IsVisible}, update={retained.updateTimer.IsEnabled}, presentation={retained.presentationTimer.IsEnabled}");
        if (!ReferenceEquals(owner.Profiles.Selected, profile) || owner.Profiles.Imported.Count != 1)
            throw new InvalidOperationException("Closing a view cleared application profile state");
        if (owner.Preferences.Notices || owner.Preferences.QuicOverTcp)
            throw new InvalidOperationException("Preferences did not survive control changes and view release");
        if (core.Disposed || core.State != ConnectionState.Running)
            throw new InvalidOperationException("Closing a view stopped the application-owned core");
        var completed = new TaskCompletionSource<HealthObservation>();
        owner.Monitor.Completed += result => completed.TrySetResult(result);
        owner.Monitor.NextCheck = DateTimeOffset.MinValue;
        var observation = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (!observation.Round.Verified) throw new InvalidOperationException("Application monitor stopped with the closed view");
        checks.Add("Closed WPF window is collectible while application runtime, monitor, simulated core and selected profile remain alive");
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
}
