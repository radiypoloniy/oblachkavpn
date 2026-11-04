using System.Windows;

namespace Oblachka;

// Диалог согласия и проекция состояния фонового обхода в представление.
public partial class MainWindow
{
    private bool zapretEnabled => runtime.Zapret.Enabled;
    private bool zapretBusy => runtime.Zapret.Busy;
    private bool zapretTried => runtime.Zapret.Tried;
    private string[] zapretActiveGroups => runtime.Zapret.ActiveGroups;
    private string zapretActiveStrategy => runtime.Zapret.ActiveStrategy;
    private string[] zapretDemoted => runtime.Zapret.Demoted;
    private string[] ZapretCandidates => runtime.Zapret.Candidates;

    private void WireZapret()
    {
        runtime.Zapret.Changed += ZapretChanged;
        Bypass.ZapretToggle.Click += async (_, _) =>
        {
            if (Bypass.ZapretToggle.IsChecked == true) await EnableZapretAsync(); else await DisableZapretAsync();
        };
        Bypass.ZapretPick.Click += async (_, _) =>
        {
            if (zapretBusy) { runtime.Zapret.CancelPick(); return; }
            await runtime.Zapret.PickAsync();
        };
        Bypass.YouTubeBypass.Click += async (_, _) => await runtime.Zapret.SetGroupAsync("youtube", Bypass.YouTubeBypass.IsChecked == true);
        Bypass.DiscordBypass.Click += async (_, _) => await runtime.Zapret.SetGroupAsync("discord", Bypass.DiscordBypass.IsChecked == true);
        RefreshZapret();
    }

    private void ZapretChanged() { if (!closed) RefreshZapret(); }
    private Task DisableZapretAsync() => runtime.Zapret.DisableAsync();

    private async Task EnableZapretAsync()
    {
        if (!NetworkHelperConnection.ServiceRunning)
        {
            Bypass.ZapretToggle.IsChecked = false; Home.BypassToggle.IsChecked = false;
            RefreshZapret();
            Bypass.ZapretText.Text = "Нужна установленная Облачка: обход запускает её сетевая служба.";
            return;
        }
        if (!AppDialog.Confirm(this, "Обход блокировок без VPN",
            "Облачка скачает zapret (набор стратегий Flowseal с GitHub), проверит файлы и подберёт стратегию для вашей сети — это до двух минут. " +
            "YouTube, Discord и другие сайты из его списков пойдут напрямую, не тратя VPN. Антивирус может ругаться на драйвер WinDivert: это известное ложное срабатывание. " +
            "Обход работает, пока запущена Облачка.", "Включить"))
        { Bypass.ZapretToggle.IsChecked = false; Home.BypassToggle.IsChecked = false; return; }
        await runtime.Zapret.EnableAsync();
    }

    private void RefreshZapret()
    {
        Bypass.ZapretToggle.IsChecked = zapretEnabled;
        RefreshSettingsSummary(); RefreshDirectChip();
        Bypass.ZapretText.Text = runtime.Zapret.Note.Length > 0 ? runtime.Zapret.Note : "Через обход блокировок zapret: быстрее и не тратит VPN";
        Bypass.ZapretPick.Visibility = zapretEnabled ? Visibility.Visible : Visibility.Collapsed;
        Bypass.ZapretPick.Content = zapretBusy ? "Остановить подбор" : "Подобрать заново";
        Bypass.ZapretGroups.Visibility = zapretEnabled ? Visibility.Visible : Visibility.Collapsed;
        Bypass.YouTubeBypass.IsChecked = !runtime.Zapret.KeptInVpn.Contains("youtube");
        Bypass.DiscordBypass.IsChecked = !runtime.Zapret.KeptInVpn.Contains("discord");
        Bypass.YouTubeState.Text = ZapretGroupState("youtube");
        Bypass.DiscordState.Text = ZapretGroupState("discord");
    }

    private string ZapretGroupState(string key) =>
        runtime.Zapret.KeptInVpn.Contains(key) ? "Через VPN — так выбрано"
        : zapretBusy ? "Проверяем…"
        : zapretActiveGroups.Contains(key) ? (zapretActiveStrategy == ZapretPlan.NoBypass ? "Напрямую, обход не нужен" : "Через обход")
        : zapretTried ? "Через VPN: обход в этой сети не открыл" : "Ещё не проверяли";

}
