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
        Home.RulesNav.Click += (_, _) => OpenSettings(1);
        Home.BypassOpen.Click += (_, _) => OpenSettings(1);
        Home.BypassToggle.Click += async (_, _) =>
        {
            if (Home.BypassToggle.IsChecked == true) await EnableZapretAsync(); else await DisableZapretAsync();
            if (!closed) RefreshZapret();
        };
        WireLayout();
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

    // Карточка обхода: работает ли обход и для чего. Жёлтым — обход сломался и сайт временно в VPN.
    private void RefreshBypassCard()
    {
        Home.BypassToggle.IsChecked = zapretEnabled;
        var (text, color) =
            !zapretEnabled ? ("YouTube и Discord без VPN", "Mu2")
            : zapretDemoted.Length > 0 ? (string.Join(" и ", zapretDemoted.Select(ZapretTitle)) + " временно через VPN", "WI")
            : zapretBusy ? ("подбираем обход…", "Mu2")
            : zapretActiveGroups.Length > 0 ? (string.Join(" и ", zapretActiveGroups.Select(ZapretTitle)) + (zapretActiveGroups.Length > 1 ? " работают" : " работает"), "OkF")
            : ("в этой сети не помог — через VPN", "Mu2");
        Home.BypassText.Text = text;
        Home.BypassText.SetResourceReference(TextBlock.ForegroundProperty, color);
    }
}
