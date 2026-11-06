using System.Windows;
using System.Windows.Controls;
using Oblachka.Views;

namespace Oblachka;

// Боковая колонка (облик 0.20): карточка статуса — погода и облачко по состоянию подключения,
// строка сервера с пингом ступенями; обход блокировок; разделы; карточка «сегодня и подписка».
// Разделы открываются на острове (MainWindow.Layout).
public partial class MainWindow
{
    private void WireSidebar()
    {
        Home.ServerCard.Click += (_, _) => Navigate("Servers");
        Home.HelpNav.Click += (_, _) => OpenSettings(Views.SettingsPanel.HelpPage);
        Home.RulesNav.Click += (_, _) => ShowRulesSection();
        Home.ServicesNav.Click += (_, _) => ShowRulesSection();
        WireServiceRoutes();
        Home.BypassOpen.Click += (_, _) => OpenSettings(Views.SettingsPanel.BypassPage);
        RulesPage.BackButton.Click += (_, _) => CloseRulesSection();
        Settings.Bypass.OpenRules.Click += (_, _) => ShowRulesSection();
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
        Settings.Visibility = Visibility.Collapsed; Servers.Visibility = Visibility.Collapsed; HidePage(picker);
        HidePage(doctor); HidePage(siteCheck); HidePage(analytics);
        if (currentPage is "Servers" or "Diagnostics" or "Analytics") currentPage = "Connection";
        RulesPage.Visibility = Visibility.Visible;
        RefreshRules();
        RefreshServiceRoutes();
        OpenSection();
        Routes.RuleDomain.Focus();
    }

    private void CloseRulesSection()
    {
        RulesPage.Visibility = Visibility.Collapsed;
        CloseRouteEditor();
        if (currentPage == "Routes") currentPage = "Connection";
        ShowDefaultSection();
        Home.LiveNav.Focus();
    }

    private void RefreshSidebar()
    {
        ApplyStatusLook();
        RefreshServiceRoutes();
        var shown = runningProfile ?? profile;
        if (shown is null)
        {
            Home.ServerName.Text = "Сервер не выбран"; Home.ServerNote.Text = "Выберите в «Серверах»";
            Home.ServerFlag.Source = null; Home.ServerPing.Text = ""; Home.ServerPing.ToolTip = null;
        }
        else
        {
            var row = Row(shown, note: false);
            Home.ServerName.Text = row.Name;
            Home.ServerNote.Text = ExitNote(shown) ?? (autoSelect ? "Автовыбор · самый быстрый" : GroupNote(shown) ?? "выбран вручную");
            // Картинку — только если сменилась: переназначение каждую секунду перерисовывало карточку.
            if (!ReferenceEquals(Home.ServerFlag.Source, row.Flag)) Home.ServerFlag.Source = row.Flag;
            Home.ServerFlag.Visibility = row.Flag is null ? Visibility.Collapsed : Visibility.Visible;
            Home.ServerPing.Text = row.Latency;
            Home.ServerPing.ToolTip = row.LatencyHint;
        }
        var rules = siteRules.Count + programRules.Count;
        Home.RulesCount.Text = rules > 0 ? rules.ToString() : "";
        // Серверами, а не записями подписки: протоколы одной машины — один сервер (ServerMachines).
        EnsureMachineIndex();
        Home.AllServersText.Text = importedProfiles.Count > 0 ? machineIndex.Count.ToString() : "";
    }

    // Погода карточки статуса: ночь — выключено, ясно — подключаемся и защищено, закат — связи нет
    // (сеть закрыта, ищем сервер). Цвета текста — под фон, лицо облачка — под состояние.
    private bool statusTrouble;
    private void ApplyStatusLook()
    {
        var trouble = statusTrouble || Home.Power.State == PowerState.Blocked || Home.RecoveryCard.Visibility == Visibility.Visible || Home.ClosedCard.Visibility == Visibility.Visible;
        var off = !trouble && Home.Power.State is PowerState.Off or PowerState.Disabled;
        var (card, title, sub, mood) = trouble ? ("Sunset", "Tx", "SunsetMu", CloudMood.Worried)
            : off ? ("Night", "CloudWhite", "NightMu", CloudMood.Sleep)
            : ("Clear", "Tx", "ClearMu", Home.Power.State == PowerState.On ? CloudMood.Happy : CloudMood.Wait);
        if (Home.Face.Mood == mood && Home.StatusCard.Tag as string == card) return;
        Home.StatusCard.Tag = card;
        Home.Face.Mood = mood;
        Home.StatusCard.SetResourceReference(Border.BackgroundProperty, card);
        Home.StatusTitle.SetResourceReference(TextBlock.ForegroundProperty, title);
        Home.StatusSub.SetResourceReference(TextBlock.ForegroundProperty, sub);
        Home.ServerCard.SetResourceReference(Control.ForegroundProperty, off ? "CloudWhite" : "Tx");
        Home.ServerCard.SetResourceReference(Control.BackgroundProperty, off ? "GlassNight" : "Glass");
        Home.PauseButton.SetResourceReference(Control.ForegroundProperty, off ? "CloudWhite" : "Tx");
        Home.PauseButton.SetResourceReference(Control.BackgroundProperty, off ? "GlassNight" : "Glass");
    }

    // Остальные группы в VPN: «Discord работает · YouTube через VPN».
    private string ZapretInVpn() =>
        ZapretPlan.Groups.Select(g => g.Key).Except(zapretActiveGroups).ToArray() is { Length: > 0 } rest ? " · " + string.Join(" и ", rest.Select(ZapretTitle)) + " через VPN" : "";

    // Карточка обхода: работает ли обход и для чего. Жёлтым — обход сломался и сайт временно в VPN.
    private void RefreshBypassCard()
    {
        Home.BypassToggle.IsChecked = zapretEnabled;
        var (text, color) =
            runtime.Zapret.UpdatingRoutes ? ("Применяем маршруты…", "Mu2")
            : !zapretEnabled ? ("YouTube и Discord без VPN", "Mu2")
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
