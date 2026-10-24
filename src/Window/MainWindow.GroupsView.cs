using System.Windows;
using System.Windows.Controls;

namespace Oblachka;

// Группы на экране: список в «Настройки → Подписка» и вкладки в «Все серверы».
public partial class MainWindow
{
    private void RefreshGroups()
    {
        if (!viewsReady) return;
        var now = DateTimeOffset.UtcNow;
        string Ago(DateTimeOffset? at) => at is not { } time ? "" : (now - time) < TimeSpan.FromMinutes(1) ? " · обновлено только что"
            : (now - time) < TimeSpan.FromHours(20) ? " · обновлено в " + time.ToLocalTime().ToString("HH:mm") : " · обновлено " + time.ToLocalTime().ToString("dd.MM");
        Settings.Subscription.GroupList.ItemsSource = groups.Select(g => new GroupRow
        {
            Group = g, Title = g.Name,
            Sub = (g.IsManual ? "ссылки, файлы и QR · " : "") + ServerCount(ProfilesOf(g.Id).Count) + Ago(g.Updated)
        }).ToArray();
        Settings.Subscription.GroupList.IsEnabled = operation is null;

        // Вкладки: «Все» и группы; выбранная вкладка сохраняется по идентификатору группы.
        var tabs = Servers.GroupTabs;
        var selectedId = tabs.SelectedIndex > 0 && tabs.SelectedIndex <= tabs.Items.Count - 1 && tabs.Tag is string[] ids && tabs.SelectedIndex - 1 < ids.Length ? ids[tabs.SelectedIndex - 1] : null;
        tabs.Tag = groups.Select(g => g.Id).ToArray();
        // Вкладки со счётчиками: «Облачка 12». Выбор по-прежнему по номеру вкладки.
        int Count(ProfileGroup g) => ProfilesOf(g.Id).Count(p => !HiddenServer(p));
        tabs.ItemsSource = new[] { "Все " + groups.Sum(Count) }.Concat(groups.Select(g => g.Name + " " + Count(g))).ToArray();
        var index = selectedId is null ? 0 : groups.FindIndex(g => g.Id == selectedId) + 1;
        tabs.SelectedIndex = Math.Max(0, index);
        Servers.GroupTabsScroll.Visibility = groups.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        RefreshFreePanel();
    }

    private static string ServerCount(int count) =>
        count + " " + (count % 10 == 1 && count % 100 != 11 ? "сервер" : count % 10 is >= 2 and <= 4 && count % 100 is < 12 or > 14 ? "сервера" : "серверов");

    private async void GroupRowClick(object sender, RoutedEventArgs args)
    {
        if (args.OriginalSource is not Button { DataContext: GroupRow row } button || operation is not null) return;
        // Бесплатные: «обновить» — загрузить списки заново, «убрать» — выключить их.
        if (row.Group.Id == FreeServers.GroupId)
        {
            if (Equals(button.Tag, "refresh")) await LoadFreeAsync(force: true);
            else if (Equals(button.Tag, "remove")) { Settings.FreeServers.IsChecked = false; await ToggleFreeAsync(false); }
            return;
        }
        if (Equals(button.Tag, "refresh")) { await LoadSubscriptionAsync(row.Group.Sources, row.Group.PreferredSource); return; }
        if (!Equals(button.Tag, "remove")) return;
        if (!ConfirmRemoveGroup(row.Group)) return;
        RemoveGroup(row.Group.Id);
        DetailText.Text = "«" + row.Title + "» убрана.";
    }
}
