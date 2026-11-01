namespace Oblachka;

// «Через VPN — только YouTube и Discord»: запасной путь, когда обход блокировок (zapret) в этой
// сети не справляется. Всё остальное идёт напрямую — загрузки и игры не тратят VPN и не
// переключаются туда-сюда руками. Свои правила «через VPN» тоже действуют.
//
// «Весь компьютер»: выбор делает туннель в службе (DirectByDefault): там видны и имена сайтов, и
// программы — голос Discord идёт по UDP на адреса без имени, его ловит правило программы Discord.
// «Браузеры и программы»: тот же список исполняет ядро окна (остальное у него напрямую).
public partial class MainWindow
{
    private bool selectiveVpn { get => runtime.TunnelPolicy.Selective; set => runtime.TunnelPolicy.Selective = value; }

    internal static readonly string[] SelectiveDomains = ClientTunnelPolicy.SelectiveDomains;
    internal static readonly string[] SelectivePrograms = ClientTunnelPolicy.SelectivePrograms;

    private void WireSelective()
    {
        Bypass.SelectiveToggle.Click += async (_, _) =>
        {
            selectiveVpn = Bypass.SelectiveToggle.IsChecked == true;
            QueueStateSave(); RefreshDirectChip(); RefreshHome();
            await SelectiveChangedAsync();
        };
    }

    // Правила, с которыми ставится сессия «Весь компьютер»: свои и, в выборочном режиме, списки
    // YouTube и Discord. Своё правило на тот же сайт или программу сильнее списка.
    // Сессия через бесплатный чужой узел: домены сервиса закрыты правилами сайтов впереди своих (ForeignGuard).
    private bool foreignSession { get => runtime.Tunnels.ForeignSession; set => runtime.Tunnels.ForeignSession = value; }
    // В «Браузеры и программы» подписка грузится напрямую, мимо узла; в «Весь компьютер» — через туннель.
    private bool ForeignConnected => SystemTunnelActive && foreignSession;
    private SiteRule[] LeaseSites() => runtime.TunnelPolicy.LeaseSites(foreignSession);
    private SiteRule[] OwnLeaseSites() => runtime.TunnelPolicy.LeaseSites(false);
    private ProgramRule[] LeasePrograms() => runtime.TunnelPolicy.LeasePrograms();

    // Программы, которые сервис просит пустить мимо VPN (per-app-proxy-mode-windows: bypass).
    // Своё правило на ту же программу сильнее: сервис не переспорит человека.
    private string[] ServiceDirectApps => ServiceGroup?.Metadata?.Provider?.DirectApps ?? [];
    private ProgramRule[] WithServiceApps(ProgramRule[] rules) => rules.Concat(ServiceDirectApps
        .Where(a => !rules.Any(r => string.Equals(r.Executable, a, StringComparison.OrdinalIgnoreCase)))
        .Select(a => ProgramRoutingPlan.Create(a, SiteAction.Direct))).Take(100).ToArray();

    // Для «Браузеры и программы»: список в ядре окна, остальное напрямую.
    private RoutingProfile? SelectiveProfile() => selectiveVpn && !preferences.SystemTunnel
        ? new RoutingProfile("Через VPN только YouTube и Discord", [], SelectiveDomains.Select(d => "domain:" + d).ToArray(), [], [], [], []) { GlobalProxy = false }
        : null;

    // Режим меняет саму сессию — подключение перезапускается целиком.
    private async Task SelectiveChangedAsync()
    {
        if (closed || !ConnectionActive) return;
        if (operation is not null || systemStopping) { DetailText.Text = "Изменение применится со следующего подключения."; return; }
        recoveryWait?.Cancel();
        manualStop = true;
        if (systemTunnel is not null) await StopSystemTunnelAsync(); else engine.Stop();
        UpdateControls();
        if (!closed && ConnectButton.IsEnabled)
        {
            await ConnectCoreAsync();
            DetailText.Text = selectiveVpn ? "Через VPN — только YouTube и Discord; остальное напрямую." : "Через VPN снова идёт всё, кроме ваших правил «напрямую».";
        }
    }
}
