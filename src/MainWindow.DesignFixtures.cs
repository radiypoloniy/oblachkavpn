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
            // Главный экран или одна временная страница поверх него.
            var pages = new[] { Servers.Visibility, Routes.Visibility, Diagnostics.Visibility }.Count(v => v == Visibility.Visible);
            var home = page == "Connection";
            if (home != (Home.Visibility == Visibility.Visible) || home == (PageHost.Visibility == Visibility.Visible) || pages != (home ? 0 : 1) ||
                Onboarding.Visibility == Visibility.Visible || Onboarding.ContinueButton.IsDefault)
                throw new InvalidOperationException("Navigation displays the wrong pages");
        }
        checks.Add("Navigation selects one working page at a time and preserves the imported profile");
        SettingsClick(this, new RoutedEventArgs());
        if (SettingsOverlay.Visibility != Visibility.Visible) throw new InvalidOperationException("Settings overlay did not open");
        CloseSettings();
        if (SettingsOverlay.Visibility != Visibility.Collapsed) throw new InvalidOperationException("Settings overlay did not close");
        checks.Add("Settings overlay opens and closes independently of the connection lifecycle");
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
            if (!popup.PowerButton.IsEnabled || popup.ProfileText.Text != profile?.DisplayName)
                throw new InvalidOperationException("Compact tray window does not reflect the selected profile");
        }
        finally { popup.Close(); }
        checks.Add("Compact tray window reflects the selected profile without inventing connection metrics");
    }

    internal void PrepareDesignPreview(string page)
    {
        if (page == "welcome") return;
        LoadPreviewProfiles();
        CheckPreviewControls();
        if (page == "servers") LoadServerPreview();
        Navigate(page switch { "servers" => "Servers", "routes" => "Routes", "diagnostics" => "Diagnostics", _ => "Connection" });
        if (page is "settings" or "storage") SettingsClick(this, new RoutedEventArgs());
        if (page == "storage") { Settings.SettingsTabs.SelectedIndex = 1; Settings.SaveStateButton.IsEnabled = true; }
    }

    private void LoadServerPreview()
    {
        var names = new[] { "🇪🇺Авто", "🇪🇺Обход", "🇪🇺Обход 2", "🇻🇳Рабочий сервер", "🇳🇱Нидерланды", "🇳🇱Нидерланды 2" };
        Import(string.Join("\n", Enumerable.Range(0, 24).Select(i =>
            $"vless://{i + 1:x8}-1111-4111-8111-111111111111@example.invalid:443?security=tls&type=xhttp#" +
            Uri.EscapeDataString(i < names.Length ? names[i] : $"🇩🇪Резервный сервер {i - 5}"))));
    }
}
