using System.Windows;

namespace Oblachka;

// Подписи корневой страницы настроек (редизайн 0.14): аккаунт, сводка «Без VPN», автовыбор,
// версия. Обновляются при открытии настроек и после изменений, которые их касаются.
public partial class MainWindow
{
    private void WireSettingsHome()
    {
        Settings.General.AccountRenew.Click += (_, _) => OpenLink(RenewUrl, "страницу продления");
        Settings.General.OpenAutoPool.Click += (_, _) => RenderAutoPool();
        Settings.AutoPool.AutoPoolMeasure.Click += async (_, _) =>
        {
            if (measuring) { measureCancel?.Cancel(); return; }
            await MeasureServersAsync(autoPool.Members(importedProfiles.ToArray()), quiet: false);
        };
    }

    private void RefreshSettingsSummary()
    {
        if (!viewsReady) return;
        var general = Settings.General;
        var now = DateTimeOffset.UtcNow;
        var shortSummary = subscriptionMetadata?.ShortSummary(now);
        general.AccountTitle.Text = string.IsNullOrWhiteSpace(shortSummary) ? "Подписка" : shortSummary;
        var device = DeviceIdentity.Current;
        general.AccountSub.Text = (subscriptionMetadata?.Summary(now) is { Length: > 0 } summary ? summary + " · " : "") +
            "это устройство " + (device.Length > 10 ? device[..4] + "…" + device[^4..] : device);
        general.AccountRenew.Visibility = RenewUrl is not null ? Visibility.Visible : Visibility.Collapsed;

        general.ZapretSummary.Text = ZapretSummary();
        var sites = siteRules.Count; var programs = programRules.Count;
        general.RulesSummary.Text = sites + programs == 0 ? "нет" :
            string.Join(", ", new[] { sites > 0 ? Count(sites, "сайт", "сайта", "сайтов") : null, programs > 0 ? Count(programs, "программа", "программы", "программ") : null }.OfType<string>());
        var all = importedProfiles.Count;
        general.AutoPoolSummary.Text = all == 0 ? "" : importedProfiles.Count(autoPool.Contains) + " из " + all;
        general.VersionTitle.Text = "Версия " + ServiceBrand.Version;
        general.VersionSummary.Text = Diagnostics.UpdateAuto.IsChecked == true ? "обновляется сама" : "проверка выключена";
    }

    // Коротко для корневой страницы и главного экрана: что из YouTube и Discord идёт напрямую.
    private string ZapretSummary()
    {
        if (!zapretEnabled) return "Выключено — идут через VPN";
        if (zapretBusy) return "Проверяем, что открывается без VPN…";
        if (zapretActiveGroups.Length == 0) return "Через VPN: напрямую не открываются";
        return string.Join(" · ", ZapretPlan.Groups.Select(g => (g.Key == "youtube" ? "YouTube" : "Discord") +
            (zapretActiveGroups.Contains(g.Key) ? " напрямую" : " через VPN")));
    }

    private static string Count(int n, string one, string few, string many) =>
        n + " " + (n % 10 == 1 && n % 100 != 11 ? one : n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14 ? few : many);

    // «Напрямую: рунет, YouTube» под статусом. Жёлтая — обход сломался и сайт временно в VPN.
    private void RefreshDirectChip()
    {
        if (!viewsReady) return;
        static string Title(string key) => key == "youtube" ? "YouTube" : "Discord";
        var chip = Home.DirectChip;
        if (zapretEnabled && zapretDemoted.Length > 0)
        {
            Home.DirectChipText.Text = string.Join(" и ", zapretDemoted.Select(Title)) + " временно через VPN";
            chip.SetResourceReference(BackgroundProperty, "WB");
            Home.DirectChipText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "WI");
            chip.Visibility = Visibility.Visible;
            return;
        }
        var parts = new List<string>();
        if (Settings.RussiaDirect.IsChecked == true) parts.Add("рунет");
        if (zapretEnabled && !zapretBusy) parts.AddRange(zapretActiveGroups.Select(Title));
        Home.DirectChipText.Text = "Напрямую: " + string.Join(", ", parts);
        chip.SetResourceReference(BackgroundProperty, "OkB");
        Home.DirectChipText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "OkF");
        chip.Visibility = parts.Count > 0 && ConnectionActive ? Visibility.Visible : Visibility.Collapsed;
    }
}
