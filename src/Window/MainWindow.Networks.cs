using System.Windows;
using System.Windows.Threading;

namespace Oblachka;

// Поведение по сетям (Windscribe Network Options, доверенные сети NordVPN): в сети «Всегда VPN» он
// включается сам, в доверенной — сам не включается, в незнакомой общественной — по желанию.
// Сеть узнаём у Windows (NetworkCost.Network): у каждого Wi-Fi и каждой проводной сети свой идентификатор.
// Сеть меняется — ждём 3 с, пока Windows разберётся с адаптерами, и решаем заново. Выключил человек VPN
// сам — в этой сети больше сами не включаем, пока он не включит его снова.
public partial class MainWindow
{
    private readonly Dictionary<string, KnownNetwork> knownNetworks = new();
    private (string Key, string Name, bool Public)? currentNetwork;
    private string? stoppedOnNetwork;
    private readonly DispatcherTimer networkSettle = new() { Interval = TimeSpan.FromSeconds(3) };

    private void WireNetworks()
    {
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += (_, _) => Dispatcher.BeginInvoke(() => { networkSettle.Stop(); networkSettle.Start(); });
        networkSettle.Tick += async (_, _) =>
        {
            networkSettle.Stop();
            var before = currentNetwork?.Key;
            ReadNetwork();
            // «Выключил сам» не сбрасываем: Wi-Fi моргнул и вернулся в ту же сеть — VPN по-прежнему выключен.
            if (currentNetwork?.Key == before) return;
            await ApplyNetworkAsync();
        };
        AppTab.NetUsual.Click += (_, _) => SetNetworkMode(NetworkMode.Usual);
        AppTab.NetVpn.Click += (_, _) => SetNetworkMode(NetworkMode.Vpn);
        AppTab.NetTrusted.Click += (_, _) => SetNetworkMode(NetworkMode.Trusted);
        AppTab.SecureUnknown.Click += (_, _) => QueueStateSave();
        AppTab.NetForget.Click += (_, _) =>
        {
            foreach (var key in knownNetworks.Keys.Where(k => k != currentNetwork?.Key).ToArray()) knownNetworks.Remove(key);
            QueueStateSave(); RefreshNetworkRow();
        };
    }

    private void RestoreNetworks(ClientSnapshot snapshot)
    {
        knownNetworks.Clear();
        foreach (var (key, network) in NetworkPolicy.Restore(snapshot.KnownNetworks)) knownNetworks[key] = network;
        AppTab.SecureUnknown.IsChecked = snapshot.SecureUnknown;
        RefreshNetworkRow();
    }

    private NetworkMode CurrentNetworkMode => currentNetwork is { } n && knownNetworks.TryGetValue(n.Key, out var known) ? known.Mode : NetworkMode.Usual;

    private void ReadNetwork()
    {
        currentNetwork = NetworkCost.Network() is { } n ? (NetworkPolicy.Key(n.Id), NetworkPolicy.CleanName(n.Name), n.Public) : null;
        RefreshNetworkRow();
    }

    private void SetNetworkMode(NetworkMode mode)
    {
        if (currentNetwork is not { } net) return;
        // «Обычная» — то же, что ничего не решать: сеть из списка уходит.
        if (mode == NetworkMode.Usual) knownNetworks.Remove(net.Key);
        else if (knownNetworks.ContainsKey(net.Key) || knownNetworks.Count < NetworkPolicy.MaxKnown) knownNetworks[net.Key] = new(net.Name, mode);
        else { DetailText.Text = $"Запомнить можно до {NetworkPolicy.MaxKnown} сетей. Забудьте остальные, чтобы добавить эту."; return; }
        QueueStateSave();
        RefreshNetworkRow();
        if (mode == NetworkMode.Vpn) { stoppedOnNetwork = null; _ = ApplyNetworkAsync(); }
    }

    private async Task ApplyNetworkAsync()
    {
        if (currentNetwork is not { } net || closed || vmTestActive || profile is null) return;
        var known = knownNetworks.TryGetValue(net.Key, out var choice);
        if (!NetworkPolicy.ShouldConnect(choice?.Mode ?? NetworkMode.Usual, known, net.Public, AppTab.SecureUnknown.IsChecked == true,
            ConnectionActive || operation is not null, pausedUntil is not null, stoppedOnNetwork == net.Key) || !ConnectButton.IsEnabled) return;
        var why = known ? "в ней всегда VPN" : "это незнакомая общественная сеть";
        DetailText.Text = $"Сеть «{net.Name}»: {why} — включаем.";
        Notify(Notice.NetworkSecured, "VPN включается", $"Сеть «{net.Name}»: {why}.");
        await ConnectFreshAsync();
    }

    private void RefreshNetworkRow()
    {
        if (!viewsReady) return;
        var net = currentNetwork;
        var mode = CurrentNetworkMode;
        AppTab.NetTitle.Text = net is { } n ? "Эта сеть: «" + n.Name + "»" : "Эта сеть";
        AppTab.NetNote.Text = net is null ? "Компьютер сейчас не в сети."
            : mode == NetworkMode.Vpn ? "Здесь VPN включится сам — при запуске и как только компьютер в этой сети"
            : mode == NetworkMode.Trusted ? "Здесь VPN сам не включается — только вашей кнопкой"
            : net.Value.Public ? "Как настроено выше. Windows считает эту сеть общественной"
            : "Как настроено выше";
        foreach (var (button, value) in new[] { (AppTab.NetUsual, NetworkMode.Usual), (AppTab.NetVpn, NetworkMode.Vpn), (AppTab.NetTrusted, NetworkMode.Trusted) })
        {
            button.IsEnabled = net is not null;
            MarkSegment(button, net is not null && mode == value);
        }
        var others = knownNetworks.Keys.Count(k => k != net?.Key);
        AppTab.NetForget.Visibility = others > 0 ? Visibility.Visible : Visibility.Collapsed;
        AppTab.NetForget.Content = "Забыть другие сети (" + others + ")";
    }
}
