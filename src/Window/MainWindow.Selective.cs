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
    private bool selectiveVpn;

    internal static readonly string[] SelectiveDomains =
    [
        "youtube.com", "youtu.be", "googlevideo.com", "ytimg.com", "ggpht.com", "youtube-nocookie.com", "youtubei.googleapis.com",
        "discord.com", "discord.gg", "discordapp.com", "discordapp.net", "discord.media", "discordcdn.com", "discord.dev", "discordstatus.com",
    ];
    // Само приложение Discord (и его бета-версии) — по имени: в VPN, это безопасная сторона.
    internal static readonly string[] SelectivePrograms = ["Discord.exe", "DiscordPTB.exe", "DiscordCanary.exe"];

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
    private SiteRule[] LeaseSites() => !selectiveVpn ? siteRules.ToArray()
        : siteRules.Concat(SelectiveDomains.Where(d => !siteRules.Any(r => r.Domain == d)).Select(d => RoutingPlan.Create(d, SiteAction.Tunnel))).Take(100).ToArray();

    private ProgramRule[] LeasePrograms() => !selectiveVpn ? programRules.ToArray()
        : programRules.Concat(SelectivePrograms.Where(p => !programRules.Any(r => string.Equals(r.Executable, p, StringComparison.OrdinalIgnoreCase)))
            .Select(p => ProgramRoutingPlan.Create(p, SiteAction.Tunnel))).Take(100).ToArray();

    // Для «Браузеры и программы»: список в ядре окна, остальное напрямую.
    private RoutingProfile? SelectiveProfile() => selectiveVpn && Settings.SystemTunnel.IsChecked != true
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
