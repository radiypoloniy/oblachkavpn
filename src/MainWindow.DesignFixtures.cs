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
            // «Все серверы» — поверх главного экрана; правила и помощь — вкладки настроек.
            var home = page is "Connection" or "Servers";
            var settings = page is "Routes" or "Diagnostics";
            if (home != (Home.Visibility == Visibility.Visible) || (page == "Servers") != (Servers.Visibility == Visibility.Visible) ||
                settings != (Settings.Visibility == Visibility.Visible) ||
                settings && (Routes.Visibility == Visibility.Visible) != (page == "Routes") ||
                settings && (Diagnostics.Visibility == Visibility.Visible) != (page == "Diagnostics") ||
                Onboarding.Visibility == Visibility.Visible || Onboarding.ContinueButton.IsDefault)
                throw new InvalidOperationException("Navigation displays the wrong pages: " + page);
        }
        Navigate("Connection");
        checks.Add("Navigation selects one working page at a time and preserves the imported profile");
        SettingsClick(this, new RoutedEventArgs());
        if (Settings.Visibility != Visibility.Visible) throw new InvalidOperationException("Settings did not open");
        // Каждая вкладка реально отрисовывается: ключ ресурса, совпавший со стилем, ронял окно
        // только при показе вкладки, и проверки без отрисовки этого не видели.
        for (var tab = 0; tab < 4; tab++)
        {
            Settings.ShowTab(tab);
            DesignPreview.Render(this, Path.Combine(AppContext.BaseDirectory, $"preview-settings-{tab}.png"));
        }
        Settings.ShowTab(0);
        CloseSettings();
        if (Settings.Visibility != Visibility.Collapsed) throw new InvalidOperationException("Settings did not close");
        checks.Add("Settings open and close independently of the connection lifecycle");
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
        if (Home.Power.State != Views.PowerState.Off || Home.StatusTitle.Text != "Не подключено")
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
            if (!popup.PowerButton.IsEnabled || rows.Length == 0 || !rows[0].IsAuto || !rows.Any(r => r.Selected && r.Profile == profile))
                throw new InvalidOperationException("Compact tray window does not reflect the selected profile");
        }
        finally { popup.Close(); }
        checks.Add("Compact tray window reflects the selected profile without inventing connection metrics");
    }

    internal void PrepareDesignPreview(string page)
    {
        if (page == "welcome") return;
        if (page == "welcome-import")
        {
            Onboarding.ForeignList.ItemsSource = new[]
            {
                new ForeignSubscription("Happ", "sub.example.invalid", "https://sub.example.invalid/sub/a1b2c3d4"),
                new ForeignSubscription("Throne", "Рабочая", "https://vpn.example.invalid/s/e5f6g7h8"),
                new ForeignSubscription("v2RayTun", "Liberty", "https://liberty.example.invalid/connection/subs/i9j0"),
            };
            Onboarding.ForeignPanel.Visibility = Visibility.Visible;
            return;
        }
        LoadPreviewProfiles();
        CheckPreviewControls();
        if (page == "servers") { LoadServerPreview(); LoadPingPreview(); }
        if (page is "foreign" or "closed" or "recovery" or "subscription") PrepareStatePreview(page);
        if (page is "apps" or "apps-rules")
        {
            // Настоящий каталог этого компьютера, собранный сразу: снимок делается синхронно.
            appCatalog = InstalledApps.Find().Select(a => new Views.AppRow(a, AppIcons.For(a.Path))).ToArray();
            SettingsClick(this, new RoutedEventArgs()); Settings.ShowTab(1); Routes.ShowSites(false);
            if (page == "apps-rules")
            {
                foreach (var (exe, action) in new[] { ("Telegram.exe", SiteAction.Direct), ("Steam.exe", SiteAction.Direct), ("chrome.exe", SiteAction.Tunnel), ("game.exe", SiteAction.Block) })
                    AddProgramRule(exe, action);
                return;
            }
            OpenAppPicker(); Picker.Load(appCatalog, AddedPrograms());
            Picker.SearchBox.Text = "т";
            foreach (var row in Picker.AppList.Items.OfType<Views.AppRow>().Take(2)) row.Checked = true;
            Picker.AddButton.IsEnabled = true; Picker.AddButton.Content = "Добавить (2)";
            return;
        }
        if (page == "power-on") { Navigate("Connection"); RefreshHome(); Home.Power.State = Views.PowerState.On;
            // Снимок делается сразу, анимация ещё не прошла: ставим её итог.
            var power = Home.Power;
            foreach (var layer in new UIElement[] { power.Glow, power.OnLayer }) { layer.BeginAnimation(OpacityProperty, null); layer.Opacity = 1; }
            power.Slide.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null); power.Slide.X = 72;
            return; }
        Navigate(page switch { "servers" => "Servers", "routes" => "Routes", "diagnostics" => "Diagnostics", _ => "Connection" });
        if (page is "settings" or "rules" or "storage" or "help") SettingsClick(this, new RoutedEventArgs());
        if (page is "rules" or "storage" or "help") Settings.ShowTab(page switch { "rules" => 1, "storage" => 2, _ => 3 });
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
        }
        FilterProfiles(); RefreshHome();
    }
}
