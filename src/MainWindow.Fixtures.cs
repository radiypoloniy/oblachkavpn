using Microsoft.Win32;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Text.Json;
using Forms = System.Windows.Forms;

namespace Oblachka;

public partial class MainWindow
{
    internal void LoadPreviewProfiles() => Import("vless://11111111-1111-4111-8111-111111111111@example.com:443?security=tls&type=xhttp&path=%2Fexample&mode=stream-up#Тестовый%20XHTTP\nvless://22222222-2222-4222-8222-222222222222@example.net:443?security=tls&type=tcp#Тестовый%20TCP");
    internal void CheckPreviewControls()
    {
        // Предпросмотр загружает сторонние профили: подключиться можно, подбирать маршрут
        // и измерять — нет, это действия только по узлам своей подписки.
        if (ProfilePicker.Items.Count != 2 || !ConnectButton.IsEnabled || StopButton.IsEnabled || ProbeButton.IsEnabled || !trayConnect.Enabled || trayStop.Enabled)
            throw new InvalidOperationException("Imported-profile controls have incorrect state");
        if (FindRouteButton.IsEnabled || Servers.MeasureButton.IsEnabled)
            throw new InvalidOperationException("Route search or measurement is offered for a third-party node");
    }

    // Те же профили, но как узлы сервиса: подбор и измерения должны стать доступны.
    internal void CheckServiceControls(List<string> checks)
    {
        // Те же два профиля приходят как подписка сервиса. Сторонние из предпросмотра
        // при этом никуда не деваются: наборы складываются, а не заменяют друг друга.
        var service = importedProfiles.Select(p => p with { Source = ProfileSource.Service }).ToArray();
        ApplyImport(new ImportResult(service, 0, 0));
        if (ProfilePicker.Items.Count != 4 || importedProfiles.Count(p => p.IsService) != 2 || importedProfiles.Count(p => !p.IsService) != 2)
            throw new InvalidOperationException("Service subscription replaced the third-party list instead of joining it: " + ProfilePicker.Items.Count);
        checks.Add("Service subscription and third-party nodes live in one list without replacing each other");

        // Узлы сервиса идут первыми, поэтому выбран сейчас свой: подбор и измерения доступны.
        if (profile is not { IsService: true } || !FindRouteButton.IsEnabled || !Servers.MeasureButton.IsEnabled)
            throw new InvalidOperationException("Own subscription nodes do not enable route search and measurement");
        ProfilePicker.SelectedIndex = 3;
        if (profile is not { IsService: false } || FindRouteButton.IsEnabled)
            throw new InvalidOperationException("Route search is offered after switching to a third-party node");
        checks.Add("Route search follows the selected node: offered for the service, withheld for third-party");

        RemoveExternalClick(this, new RoutedEventArgs());
        if (ProfilePicker.Items.Count != 2 || importedProfiles.Any(p => !p.IsService) || profile is not { IsService: true })
            throw new InvalidOperationException("Removing third-party subscriptions did not restore the service-only list");
        checks.Add("Third-party subscriptions can be removed while the service list and a valid selection stay");

        // Возвращаем стенд в исходное состояние: дальше проверяется механика обновления
        // и остановки, а она не про источники.
        serviceProfiles = Array.Empty<Profile>();
        externalProfiles = Array.Empty<Profile>();
        profile = null;
        LoadPreviewProfiles();
    }
    internal async Task<IReadOnlyList<string>> RunUiFixtureAsync()
    {
        var checks = new List<string>();
        if (vmTestAllowed || Diagnostics.VmTunButton.IsEnabled) throw new InvalidOperationException("Isolated UI fixture can launch elevated TUN");
        checks.Add("Isolated UI fixture cannot launch the VM TUN host");
        if (Onboarding.Visibility != Visibility.Visible || Sidebar.Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("Empty client does not show subscription onboarding");
        checks.Add("Empty client opens subscription onboarding without fabricated subscription claims");
        CheckSubscriptionInput(checks);
        CheckCountryRows(checks);
        LoadPreviewProfiles(); CheckPreviewControls();
        CheckServiceControls(checks);
        CheckClientBlocks(checks);
        checks.Add("Synthetic subscription updates picker, actions and tray");
        for (var i = 0; i < 2; i++)
        {
            await ConnectCoreAsync();
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            if (engine.State != ConnectionState.Running || engine.ProcessId is null || ConnectButton.IsEnabled || !StopButton.IsEnabled || !ProbeButton.IsEnabled || ImportFileButton.IsEnabled || !trayStop.Enabled)
                throw new InvalidOperationException("Running UI controls have incorrect state");
            if (i == 0)
            {
                var activeId = engine.ProcessId;
                var current = profile!;
                var refreshed = new ImportResult(new[] { (Profile)ProfilePicker.Items[1], current with { Name = "Обновлённое имя" } }, 0, 0);
                var sources = SubscriptionMirrors.ParseSources("https://fixture.invalid/primary\nhttps://backup.invalid/mirror");
                await LoadSubscriptionAsync(sources, 0, (_, _, _) => Task.FromResult(new MirrorResult(refreshed, 1, 2)));
                if (engine.ProcessId != activeId || profile != current || pendingImport is null || !RefreshButton.IsEnabled)
                    throw new InvalidOperationException("Refresh changed the active tunnel");
                checks.Add("Subscription refresh defers list changes and preserves active Xray");
                var before = ProfilePicker.ItemsSource;
                await LoadSubscriptionAsync(sources, 1, (_, _, _) => throw new UserError("Synthetic fetch failure"));
                if (!ReferenceEquals(before, ProfilePicker.ItemsSource) || engine.ProcessId != activeId || pendingImport is null)
                    throw new InvalidOperationException("Failed refresh destroyed profiles or pending update");
                checks.Add("Failed refresh preserves active profiles and pending update");
            }
            StopClick(this, new RoutedEventArgs());
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            CheckPreviewControls();
            if (engine.ProcessId is not null || engine.State != ConnectionState.Idle) throw new InvalidOperationException("UI stop left a running core");
            if (i == 0 && (pendingImport is not null || profile?.Name != "Обновлённое имя" || ProfilePicker.SelectedIndex != 1))
                throw new InvalidOperationException("Deferred refresh lost selected connection");
            checks.Add("UI start/stop cycle " + (i + 1) + " correctly owns its Xray and action states");
        }
        await ConnectCoreAsync();
        // Синтетические адреса стенда ведут на fixture.invalid, то есть это сторонняя
        // подписка: её адреса лежат в externalSources.
        var loading = LoadSubscriptionAsync(externalSources, 0, async (_, _, ct) =>
        { await Task.Delay(Timeout.Infinite, ct); throw new InvalidOperationException(); });
        StopClick(this, new RoutedEventArgs()); await loading;
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        CheckPreviewControls();
        if (engine.ProcessId is not null) throw new InvalidOperationException("Stop during refresh left Xray alive");
        checks.Add("Stop during subscription refresh cancels fetch and terminates active Xray");
        Import("{\"type\":\"vless\",\"server\":\"example.invalid\",\"server_port\":443,\"uuid\":\"11111111-1111-4111-8111-111111111111\",\"tls\":{\"enabled\":true}}");
        await ConnectCoreAsync();
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        if (engine.CoreLabel != "sing-box 1.14.3" || engine.State != ConnectionState.Running || !TransportText.Text.Contains("SingBox"))
            throw new InvalidOperationException("Native import did not select sing-box in the UI");
        StopClick(this, new RoutedEventArgs());
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        if (engine.ProcessId is not null || !ConnectButton.IsEnabled || StopButton.IsEnabled)
            throw new InvalidOperationException("sing-box UI stop left incorrect state");
        checks.Add("Native sing-box import selects the core and completes a UI start/stop cycle");
        await CheckRedesignAsync(checks);
        await CheckCompactServerLayoutAsync(checks);
        CheckSavedSession(checks);
        await CheckTrayOpenAsync(checks);
        await CheckRememberedSubscriptionAsync(checks);
        return checks;
    }
}
