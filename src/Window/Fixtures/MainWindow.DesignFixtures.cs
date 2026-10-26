using System.Windows;
using System.Windows.Input;

namespace Oblachka;

public partial class MainWindow
{
    private async Task CheckRedesignAsync(List<string> checks)
    {
        foreach (var page in new[] { "Connection", "Servers", "Routes", "Diagnostics" })
        {
            Navigate(page);
            // Боковая панель видна всегда; на острове — один раздел: «Сейчас в сети» по умолчанию,
            // «Все серверы», «Правила» или настройки («Помощь» — их раздел).
            var settings = page == "Diagnostics";
            if (Home.Visibility != Visibility.Visible || (page == "Servers") != (Servers.Visibility == Visibility.Visible) ||
                (page == "Connection") != (Live.Visibility == Visibility.Visible) ||
                settings != (Settings.Visibility == Visibility.Visible) ||
                (page == "Routes") != (RulesPage.Visibility == Visibility.Visible) ||
                settings && Diagnostics.Visibility != Visibility.Visible ||
                Onboarding.Visibility == Visibility.Visible || Onboarding.ContinueButton.IsDefault)
                throw new InvalidOperationException("Navigation displays the wrong pages: " + page);
        }
        Navigate("Connection");
        checks.Add("Navigation selects one working page at a time and preserves the imported profile");
        SettingsClick(this, new RoutedEventArgs());
        if (Settings.Visibility != Visibility.Visible) throw new InvalidOperationException("Settings did not open");
        // Каждая вкладка реально отрисовывается: ключ ресурса, совпавший со стилем, ронял окно
        // только при показе вкладки, и проверки без отрисовки этого не видели.
        for (var tab = 0; tab <= Views.SettingsPanel.AutoPoolPage; tab++)
        {
            Settings.ShowTab(tab);
            DesignPreview.Render(this, Path.Combine(AppContext.BaseDirectory, $"preview-settings-{tab}.png"));
        }
        Settings.ShowTab(0);
        CloseSettings();
        if (Settings.Visibility != Visibility.Collapsed) throw new InvalidOperationException("Settings did not close");
        checks.Add("Settings open and close independently of the connection lifecycle");
        // С чистого списка: «Вручную» накапливает добавленное прежними проверками.
        serviceProfiles = Array.Empty<Profile>(); externalProfiles = Array.Empty<Profile>(); profile = null;
        LoadPreviewProfiles();
        var selected = profile;
        Servers.SearchBox.Text = "no-synthetic-match";
        if (ProfilePicker.Items.Count != 0 || profile != selected) throw new InvalidOperationException("Search lost current profile");
        Servers.SearchBox.Clear();
        if (ProfilePicker.Items.Count != 2 || profile != selected) throw new InvalidOperationException("Search reset lost selection");
        checks.Add("Server search filters and restores profiles without discarding the selected connection");
        CheckServerList(checks);
        await ConnectCoreAsync();
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        RefreshPresentation();
        // Локальный прокси — «Подключено», а не «Защищено»: весь компьютер через туннель не идёт.
        if (Home.Power.State != Views.PowerState.On || Home.StatusTitle.Text != "Подключено" ||
            Home.CheckIcon.Visibility == Visibility.Visible || Diagnostics.TunnelState.Text != "Не проверен")
            throw new InvalidOperationException("Running proxy falsely appears verified or power switch is wrong");
        Navigate("Connection");
        DesignPreview.Render(this, Path.Combine(AppContext.BaseDirectory, "preview-running.png"));
        StopClick(this, new RoutedEventArgs());
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        if (Home.Power.State != Views.PowerState.Off || Home.StatusTitle.Text != "Выключено")
            throw new InvalidOperationException("Stopped connection retains running presentation");
        checks.Add("Power switch and statistics track the real process; startup never claims a verified tunnel");
        Settings.StayInTray.IsChecked = true;
        var closing = new System.ComponentModel.CancelEventArgs();
        OnClosing(this, closing);
        if (!closing.Cancel || closed) throw new InvalidOperationException("Stay-in-tray preference terminated the controller");
        Settings.StayInTray.IsChecked = false;
        checks.Add("Optional close-to-tray cancels closing while explicit exit remains available");
        var popup = CreateTrayPreview();
        try
        {
            // Быстрая смена: автовыбор первым, выбранный сервер отмечен.
            var rows = popup.QuickList.Items.OfType<ServerRow>().ToArray();
            if (popup.Power.State == Views.PowerState.Disabled || rows.Length == 0 || !rows[0].IsAuto || !rows.Any(r => r.Selected && r.Profile == profile))
                throw new InvalidOperationException("Compact tray window does not reflect the selected profile");
        }
        finally { popup.Close(); }
        checks.Add("Compact tray window reflects the selected profile without inventing connection metrics");
    }

    // Превью в узком окне: открытый раздел — островом поверх панели, как после щелчка по пункту.
    internal void NarrowPreview(double width)
    {
        Width = width; UpdateLayout(); narrowOpen = true; ApplyLayout(); UpdateLayout();
    }

    internal void PrepareDesignPreview(string page)
    {
        // «Серверы» с итогом «Подобрать лучшее» (servers-tune[-dark]).
        var tuned = page.StartsWith("servers-tune", StringComparison.Ordinal);
        if (tuned) page = "servers";
        if (page == "welcome") return;
        if (page == "welcome-import")
        {
            Onboarding.ForeignList.ItemsSource = new[]
            {
                new ForeignSubscription("Happ", "sub.example.invalid", "https://sub.example.invalid/sub/a1b2c3d4"),
                new ForeignSubscription("Throne", "Рабочая", "https://vpn.example.invalid/s/e5f6g7h8"),
                new ForeignSubscription("v2RayTun", "Резерв", "https://reserve.example.invalid/connection/subs/i9j0"),
                new ForeignSubscription("Throne", "3 отдельных подключения", "") { Profiles = new[] { Profiles.Parse("vless://33333333-3333-4333-8333-333333333333@n.invalid:443?security=tls&sni=example.org#A") } },
            };
            Onboarding.ForeignPanel.Visibility = Visibility.Visible;
            return;
        }
        LoadPreviewProfiles();
        CheckPreviewControls();
        if (page == "servers") { LoadServerPreview(); LoadPingPreview(); }
        if (page is "foreign" or "closed" or "recovery" or "subscription") PrepareStatePreview(page);
        if (page is "groups" or "servers-groups")
        {
            LoadGroupsPreview();
            if (page == "groups") { SettingsClick(this, new RoutedEventArgs()); Settings.ShowTab(2); }
            else { Navigate("Servers"); Servers.GroupTabs.SelectedIndex = 0; }
            return;
        }
        if (page == "doctor")
        {
            findings = NetworkDoctor.Diagnose(new PreviewProbe());
            SettingsClick(this, new RoutedEventArgs()); Settings.ShowTab(Views.SettingsPanel.HelpPage);
            Doctor.Visibility = Visibility.Visible; Doctor.Summary.Text = "Нашли то, что мешает интернету:"; RefreshDoctor();
            return;
        }
        if (page is "routing-offer" or "rules-lists")
        {
            var header = "happ://routing/add/" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
                "{\"Name\":\"Облачка/рутинг\",\"DirectSites\":[\"domain:.ru\",\"geosite:category-ru\"],\"DirectIp\":[\"10.0.0.0/8\"]}"));
            ReceiveRouting(RoutingProfile.ParseHapp(header)!);
            if (page == "rules-lists") { useProviderRouting = true; Settings.RussiaDirect.IsChecked = true; RefreshRouting(); SettingsClick(this, new RoutedEventArgs()); Settings.ShowTab(Views.SettingsPanel.BypassPage); }
            else Navigate("Connection");
            return;
        }
        if (page == "narrow")
        {
            // Узкое окно: только боковая панель, разделы — островом поверх неё.
            LoadGroupsPreview(); Width = 400; Navigate("Connection"); UpdateLayout(); ApplyLayout();
            return;
        }
        if (page is "home-zapret" or "home-invite")
        {
            // VPN выключен: обход работает (YouTube и Discord открываются) или ещё не включён.
            LoadGroupsPreview(); zapretEnabled = page == "home-zapret";
            zapretActiveGroups = zapretEnabled ? ["youtube", "discord"] : [];
            Navigate("Connection"); RefreshHome();
            return;
        }
        if (page is "live" or "live-on") { LivePreview(); if (page == "live-on") ConnectedPreview(); return; }
        if (page is "apps" or "apps-rules")
        {
            // Настоящий каталог этого компьютера, собранный сразу: снимок делается синхронно.
            appCatalog = InstalledApps.Find().Select(a => new Views.AppRow(a, AppIcons.For(a.Path))).ToArray();
            ShowRulesSection(); Routes.ShowSites(false);
            if (page == "apps-rules")
            {
                // Как после выбора в списке: папка программы целиком; чего нет на компьютере — старое правило по имени.
                foreach (var (exe, action) in new[] { ("Telegram.exe", SiteAction.Direct), ("Steam.exe", SiteAction.Direct), ("chrome.exe", SiteAction.Tunnel), ("game.exe", SiteAction.Block) })
                    AddProgramRules(appCatalog.FirstOrDefault(a => string.Equals(a.App.Executable, exe, StringComparison.OrdinalIgnoreCase)) is { } app
                        ? AppRuleScope.For(app.App, action) : [ProgramRoutingPlan.Create(exe, action)]);
                return;
            }
            OpenAppPicker(); Picker.Load(appCatalog, AddedPrograms());
            Picker.SearchBox.Text = "т";
            foreach (var row in Picker.AppList.Items.OfType<Views.AppRow>().Take(2)) row.Checked = true;
            Picker.AddButton.IsEnabled = true; Picker.AddButton.Content = "Добавить (2)";
            return;
        }
        if (page == "power-on") { LoadGroupsPreview(); Navigate("Connection"); ConnectedPreview(); return; }
        // Своя сеть в превью — выдуманная: настоящее имя Wi-Fi в снимки не попадает.
        if (page is "app" or "app-net") { currentNetwork = (NetworkPolicy.Key(Guid.Empty), "Дом", false); if (page == "app-net") { knownNetworks[currentNetwork.Value.Key] = new("Дом", NetworkMode.Vpn); page = "app"; } RefreshNetworkRow(); }
        if (page == "paused") { LoadGroupsPreview(); Navigate("Connection"); pausedUntil = DateTimeOffset.UtcNow.AddMinutes(15); RefreshHome(); ApplyStatusLook(); return; }
        // «Не открывается сайт?» с итогом: напрямую режет DPI, через VPN открывается.
        if (page is "site" or "site-ok") { LoadGroupsPreview(); OpenSiteCheck(); checkedHost = page == "site" ? "rutracker.org" : "www.gosuslugi.ru"; SiteCheck.SiteInput.Text = checkedHost;
            if (page == "site") ShowSiteResult(new(SiteStage.Handshake, 230), new(SiteStage.Ok, 412, 200), SiteRoute.Direct, "напрямую · по спискам", profile);
            else ShowSiteResult(new(SiteStage.Ok, 96, 200), new(SiteStage.Forbidden, 380, 403), SiteRoute.Tunnel, "через VPN", profile);
            SiteCheck.BeginAnimation(OpacityProperty, null); SiteCheck.Opacity = 1; return; }
        Navigate(page switch { "servers" => "Servers", "routes" => "Routes", "diagnostics" => "Diagnostics", _ => "Connection" });
        if (page == "rules") ShowRulesSection();
        if (page is "settings" or "bypass" or "storage" or "app" or "help" or "autopool" or "advanced") SettingsClick(this, new RoutedEventArgs());
        if (page is "bypass" or "storage" or "app" or "help" or "autopool" or "advanced")
            Settings.ShowTab(page switch { "bypass" => Views.SettingsPanel.BypassPage, "storage" => Views.SettingsPanel.SubscriptionPage, "app" => Views.SettingsPanel.AppPage,
                "autopool" => Views.SettingsPanel.AutoPoolPage, "advanced" => Views.SettingsPanel.AdvancedPage, _ => Views.SettingsPanel.HelpPage });
        // Заполненный раздел: видно и значения, и подпись ошибки под полем.
        if (page == "advanced")
        {
            RestoreAdvanced(AdvancedOptions.Default with { Stack = TunnelStack.Mixed, Mtu = 1400, BypassSubnets = ["10.20.0.0/16", "192.168.77.0/24"], TunnelDns = "https://dns.adguard-dns.com/dns-query" });
            Settings.Advanced.Extras.PortInput.Text = "80"; Settings.Advanced.Extras.PortError.Text = "Порт — от 1024 до 65535."; Settings.Advanced.Extras.PortError.Visibility = Visibility.Visible;
        }
        if (page == "autopool") RenderAutoPool();
        if (tuned) ShowTune("Лучшее — Нидерланды", "Было 182 мс · 6,4 МБ/с  →  стало 141 мс · 18,2 МБ/с", "Подстроено: отпечаток Firefox.", undo: true);
    }

    // Три группы: две подписки и «Вручную»; у части узлов есть отклик.
    private void LoadGroupsPreview()
    {
        Profile Node(string name, int n) => Profiles.Parse($"vless://{n:x8}-1111-4111-8111-111111111111@n{n}.invalid:443?security=tls&type=tcp#{Uri.EscapeDataString(name)}");
        var now = DateTimeOffset.UtcNow;
        SetGroup(new ProfileGroup("sub-0123456789ab", "Основная", ProfileSource.External, new[] { "https://main.invalid/s" }) { Updated = now.AddMinutes(-12),
            Metadata = new SubscriptionMetadata(0, 20_000_000_000, 100_000_000_000, now.AddDays(40)) { SupportUrl = "https://t.me/example_support", WebPageUrl = "https://main.invalid/cabinet" } }, new[] { Node("🇳🇱 Нидерланды", 11), Node("🇩🇪 Германия", 12), Node("🇺🇸 США", 13) });
        SetGroup(new ProfileGroup("sub-ba9876543210", "Резерв", ProfileSource.External, new[] { "https://reserve.invalid/t" }) { Updated = now.AddHours(-30) }, new[] { Node("🇫🇮 Финляндия", 21) });
        SetGroup(ProfileGroup.Manual, new[] { Node("Мой сервер", 31) });
        ApplyProfiles(true);
        // Последний узел проверяли давно: видно отметку «давно».
        foreach (var (item, i) in importedProfiles.Select((p, i) => (p, i)))
            pings[Profiles.Fingerprint(item)] = new(Profiles.Fingerprint(item), i == 4 ? now.AddHours(-3) : now, i != 2, new long[] { 84, 61, 142, 97, 38 }[i % 5]);
        FilterProfiles(); RefreshHome();
    }

    private void LoadServerPreview()
    {
        var names = new[] { "🇪🇺Авто", "🇪🇺Обход", "🇪🇺Обход 2", "🇻🇳Рабочий сервер", "🇳🇱Нидерланды", "🇳🇱Нидерланды 2" };
        Import(string.Join("\n", Enumerable.Range(0, 24).Select(i =>
            $"vless://{i + 1:x8}-1111-4111-8111-111111111111@example.invalid:443?security=tls&type=xhttp#" +
            Uri.EscapeDataString(i < names.Length ? names[i] : $"🇩🇪Резервный сервер {i - 5}"))));
    }

    // Отклики как после запуска: у части узлов ответа нет.
    private void LoadPingPreview()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (item, i) in importedProfiles.Select((p, i) => (p, i)))
        {
            var key = Profiles.Fingerprint(item);
            pings[key] = new(key, now, i % 7 != 5, 40 + i * 23 % 260);
            // У части серверов уже есть замер скорости — видна строка «МБ/с».
            if (i % 7 != 5 && i % 3 != 2) measurements[key] = new(key, now, true, 40 + i * 23 % 260) { Throughput = 400_000 + i * 1_370_000L % 9_000_000 };
        }
        FilterProfiles(); RefreshHome();
    }

    private sealed class PreviewProbe : INetworkProbe
    {
        public ProxySettings Proxy() => new(1, "127.0.0.1:2080", null, null);
        public bool Listening(int port) => false;
        public IReadOnlyDictionary<string, string> EnvironmentProxies() => new Dictionary<string, string>();
        public string? WinHttpProxy() => null;
        public IReadOnlyList<AdapterInfo> Adapters() => new[] { new AdapterInfo("{1}", "Беспроводная сеть", true, new[] { System.Net.IPAddress.Parse("127.0.0.1") }) };
        public bool AnswersDns(System.Net.IPAddress server) => false;
        public IReadOnlyList<RouteInfo> DefaultRoutes() => Array.Empty<RouteInfo>();
        public IReadOnlyList<BypassInfo> BypassTools() => new[] { new BypassInfo("zapret (winws)", true) };
        public IReadOnlyList<string> HostsEntries() => new[] { "1.2.3.4 youtube.com" };
        public string? ForeignVpn() => null;
    }
}
