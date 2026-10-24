using System.Windows;

namespace Oblachka;

// Подписи настроек (автовыбор) и строка обхода блокировок на главной.
// Обновляются при открытии настроек и после изменений, которые их касаются.
public partial class MainWindow
{
    private void WireSettingsHome()
    {
        Servers.OpenAutoPool.Click += (_, _) => { RenderAutoPool(); OpenSettings(Views.SettingsPanel.AutoPoolPage); };
        Settings.AutoPool.AutoPoolMeasure.Click += async (_, _) =>
        {
            if (measuring) { measureCancel?.Cancel(); return; }
            await MeasureServersAsync(autoPool.Members(importedProfiles.ToArray()), quiet: false);
        };
    }

    // Подписи страниц настроек: сколько серверов в автовыборе.
    private void RefreshSettingsSummary()
    {
        if (!viewsReady) return;
        var all = importedProfiles.Count;
        Servers.AutoPoolSummary.Text = all == 0 ? "какие" : "какие: " + importedProfiles.Count(autoPool.Contains) + " из " + all;
    }

    private bool ZapretWorksWithoutVpn => zapretEnabled && !zapretBusy && zapretActiveGroups.Length > 0;
    private static string ZapretTitle(string key) => key == "youtube" ? "YouTube" : "Discord";

    // Строка под статусом. При подключении — что идёт мимо VPN («Напрямую: рунет, YouTube»), в
    // выборочном режиме — что идёт через VPN. Без подключения — работает ли обход без VPN, а если
    // он выключен — приглашение его включить: многим нужны только YouTube и Discord.
    // Жёлтая — обход сломался и сайт временно в VPN. Нажатие открывает «Без VPN».
    private void RefreshDirectChip()
    {
        if (!viewsReady) return;
        RefreshBypassCard();
        var chip = Home.DirectChip;
        void Show(string text, string background, string foreground)
        {
            Home.DirectChipText.Text = text;
            chip.SetResourceReference(BackgroundProperty, background);
            Home.DirectChipText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, foreground);
            chip.Visibility = Visibility.Visible;
        }
        if (zapretEnabled && zapretDemoted.Length > 0) { Show(string.Join(" и ", zapretDemoted.Select(ZapretTitle)) + " временно через VPN", "WB", "WI"); return; }
        if (!ConnectionActive)
        {
            if (ZapretWorksWithoutVpn) Show("Обход блокировок включён", "OkB", "OkF");
            else if (zapretEnabled && zapretBusy) Show("Подбираем обход для YouTube и Discord…", "Field", "Mu2");
            else if (!zapretEnabled) Show("YouTube и Discord без VPN — включить", "Field", "Mu2");
            else chip.Visibility = Visibility.Collapsed;
            return;
        }
        if (selectiveVpn) { Show("Через VPN только: YouTube, Discord" + (programRules.Concat<object>(siteRules).Any() ? " и ваши правила" : ""), "Aci", "Ac"); return; }
        var parts = new List<string>();
        if (Settings.RussiaDirect.IsChecked == true) parts.Add("рунет");
        if (zapretEnabled && !zapretBusy) parts.AddRange(zapretActiveGroups.Select(ZapretTitle));
        if (parts.Count == 0) { chip.Visibility = Visibility.Collapsed; return; }
        Show("Напрямую: " + string.Join(", ", parts), "OkB", "OkF");
    }
}
