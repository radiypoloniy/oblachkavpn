using System.Windows;
using System.Windows.Controls;
using Oblachka.Views;

namespace Oblachka;

// Боковая панель 0.16: облачко на небе по состоянию подключения, карточка сервера, карточка
// обхода блокировок, счётчики разделов. Разделы открываются на острове (MainWindow.Layout).
public partial class MainWindow
{
    private void WireSidebar()
    {
        Home.ServerCard.Click += (_, _) => Navigate("Servers");
        Home.RulesNav.Click += (_, _) => ShowRulesSection();
        Home.BypassOpen.Click += (_, _) => OpenSettings(Views.SettingsPanel.BypassPage);
        RulesPage.BackButton.Click += (_, _) => CloseRulesSection();
        Settings.Bypass.OpenRules.Click += (_, _) => ShowRulesSection();
        Settings.General.SkyToggle.Click += (_, _) => { Home.Sky.Animate = Settings.General.SkyToggle.IsChecked == true; QueueStateSave(); };
        Home.BypassToggle.Click += async (_, _) =>
        {
            if (Home.BypassToggle.IsChecked == true) await EnableZapretAsync(); else await DisableZapretAsync();
            if (!closed) RefreshZapret();
        };
        WireLayout();
    }

    // «Правила» — раздел острова: свои правила сайтов и программ.
    private void ShowRulesSection()
    {
        ShowModeMenu(false);
        CloseConnections();
        Settings.Visibility = Visibility.Collapsed; Servers.Visibility = Visibility.Collapsed; Picker.Visibility = Visibility.Collapsed;
        Doctor.Visibility = Visibility.Collapsed; SiteCheck.Visibility = Visibility.Collapsed;
        if (currentPage is "Servers" or "Diagnostics") currentPage = "Connection";
        RulesPage.Visibility = Visibility.Visible;
        RefreshRules();
        OpenSection();
    }

    private void CloseRulesSection()
    {
        RulesPage.Visibility = Visibility.Collapsed;
        if (currentPage == "Routes") currentPage = "Connection";
        ShowDefaultSection();
        Home.RulesNav.Focus();
    }

    private void RefreshSidebar()
    {
        Home.Sky.Mood = Home.Power.State switch { PowerState.On => SkyMood.On, PowerState.Connecting => SkyMood.Connecting, _ => SkyMood.Off };
        var shown = runningProfile ?? profile;
        if (shown is null)
        {
            Home.ServerName.Text = "Сервер не выбран"; Home.ServerNote.Text = "Выберите в «Серверах»";
            Home.ServerFlag.Source = null; Home.ServerPing.Text = "";
        }
        else
        {
            var row = Row(shown, note: false);
            Home.ServerName.Text = row.Name;
            Home.ServerNote.Text = autoSelect ? "Автовыбор · самый быстрый" : GroupNote(shown) ?? "выбран вручную";
            // Картинку — только если сменилась: переназначение каждую секунду перерисовывало карточку.
            if (!ReferenceEquals(Home.ServerFlag.Source, row.Flag)) Home.ServerFlag.Source = row.Flag;
            Home.ServerFlag.Visibility = row.Flag is null ? Visibility.Collapsed : Visibility.Visible;
            Home.ServerPing.Text = row.Latency;
        }
        var rules = siteRules.Count + programRules.Count;
        Home.RulesCount.Text = rules > 0 ? rules.ToString() : "";
        Home.AllServersText.Text = importedProfiles.Count > 0 ? importedProfiles.Count.ToString() : "";
    }

    // Остальные группы в VPN: «Discord работает · YouTube через VPN».
    private string ZapretInVpn() =>
        ZapretPlan.Groups.Select(g => g.Key).Except(zapretActiveGroups).ToArray() is { Length: > 0 } rest ? " · " + string.Join(" и ", rest.Select(ZapretTitle)) + " через VPN" : "";

    // Карточка обхода: работает ли обход и для чего. Жёлтым — обход сломался и сайт временно в VPN.
    private void RefreshBypassCard()
    {
        Home.BypassToggle.IsChecked = zapretEnabled;
        var (text, color) =
            !zapretEnabled ? ("YouTube и Discord без VPN", "Mu2")
            : zapretDemoted.Length > 0 ? (string.Join(" и ", zapretDemoted.Select(ZapretTitle)) + " временно через VPN", "WI")
            : zapretBusy ? ("подбираем обход… сайты пока через VPN", "Mu2")
            : zapretActiveGroups.Length > 0 ? (string.Join(" и ", zapretActiveGroups.Select(ZapretTitle)) + (zapretActiveGroups.Length > 1 ? " работают" : " работает") + ZapretInVpn(), "OkF")
            : ZapretCandidates.Length == 0 ? ("YouTube и Discord через VPN — так выбрано", "Mu2")
            : !zapretTried ? ("ещё не проверяли", "Mu2")
            : ("в этой сети не помог — через VPN", "Mu2");
        Home.BypassText.Text = text;
        Home.BypassText.SetResourceReference(TextBlock.ForegroundProperty, color);
    }
}
