using System.Windows;
using System.Windows.Controls;

namespace Oblachka;

// Группы на экране: список в «Настройки → Подписка» и вкладки в «Все серверы».
public partial class MainWindow
{
    private void RefreshGroups()
    {
        if (!viewsReady) return;
        RefreshSubscriptionRows();

        // Вкладки: «Все» и группы; выбранная вкладка сохраняется по идентификатору группы.
        var tabs = Servers.GroupTabs;
        var selectedId = SelectedServerGroupId;
        // Включённые бесплатные — вкладка всегда, даже пока список не загрузился или не загрузился
        // вовсе: иначе они пропадали без следа. Пустая вкладка загружает список заново.
        var freePending = freeList is not null && groups.All(g => g.Id != FreeServers.GroupId);
        var tabIds = groups.Select(g => g.Id).Concat(freePending ? [FreeServers.GroupId] : []).ToList();
        tabs.Tag = tabIds.ToArray();
        // Вкладки со счётчиками: «Облачка 12». Выбор по-прежнему по номеру вкладки.
        int Count(ProfileGroup g) => ProfilesOf(g.Id).Count(p => !HiddenServer(p));
        tabs.ItemsSource = new[] { "Все " + groups.Sum(Count) }.Concat(groups.Select(g => g.Name + " " + Count(g))).Concat(freePending ? [FreeServers.GroupName] : []).ToArray();
        var index = selectedId is null ? 0 : tabIds.IndexOf(selectedId) + 1;
        tabs.SelectedIndex = Math.Max(0, index);
        Servers.GroupTabsScroll.Visibility = tabIds.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        RefreshFreePanel();
        RefreshSubscriptionActions();
    }

    private string? SelectedServerGroupId =>
        Servers.GroupTabs.Tag is string[] ids && Servers.GroupTabs.SelectedIndex - 1 is var index && index >= 0 && index < ids.Length
            ? ids[index] : null;

    private IEnumerable<Profile> SubscriptionScopeProfiles() =>
        SelectedServerGroupId is { } id ? importedProfiles.Where(p => p.Group == id) : importedProfiles;

    private string SubscriptionStatus(ProfileGroup group) => runtime.Imports.LoadingId == group.Id ? "Загружаем список…"
        : runtime.Imports.RefreshStatus(group.Id) ?? UpdatedText(group) ?? "Список ещё не обновлялся";

    private ProfileGroup? VisibleSubscription()
    {
        var subscriptions = groups.Where(g => g.IsSubscription);
        return SelectedServerGroupId is { } id ? subscriptions.FirstOrDefault(g => g.Id == id)
            : subscriptions.Take(2).ToArray() is [var single] ? single : null;
    }

    private void RefreshSubscriptionActions()
    {
        if (!viewsReady) return;
        var group = VisibleSubscription();
        var all = SelectedServerGroupId is null && group is null && groups.Any(g => g.IsSubscription);
        Servers.SubscriptionRefreshCard.Visibility = group is not null || all ? Visibility.Visible : Visibility.Collapsed;
        RefreshButton.Visibility = Servers.SubscriptionRefreshCard.Visibility;
        RefreshButton.IsEnabled = (group is not null || all) && operation is null && !runtime.Imports.RefreshingAll;
        Servers.SetRefreshScope(all);
        Servers.RefreshScopeText.Text = group?.Name ?? "Все подписки";
        Servers.RefreshStatusText.Text = group is not null ? SubscriptionStatus(group)
            : runtime.Imports.RefreshingAll
                ? groups.FirstOrDefault(g => g.Id == runtime.Imports.LoadingId) is { } loading ? "Загружаем «" + loading.Name + "»…" : "Обновляем списки…"
                : runtime.Imports.LastBatchStatus ?? "Списки всех провайдеров";
        Settings.Subscription.GroupList.IsEnabled = operation is null;
        Servers.MeasureButton.IsEnabled = tuning is null && (measuring || operation is null && SubscriptionScopeProfiles().Any());
    }

    private void RefreshSubscriptionRows()
    {
        if (!viewsReady) return;
        var now = DateTimeOffset.UtcNow;
        Settings.Subscription.GroupList.ItemsSource = groups.Select(g =>
        {
            var state = g.IsSubscription ? SubscriptionState.For(g, now) : null;
            return new GroupRow
            {
                Group = g, Title = g.Name,
                Sub = ServerCount(ProfilesOf(g.Id).Count) + (g.IsSubscription ? " · " + SubscriptionStatus(g) : ""),
                Plan = state is null ? "" : string.Join(" · ", new[]
                {
                    state.Term == SubscriptionTerm.Unknown ? null : state.TermText,
                    state.Traffic == SubscriptionTraffic.Unknown ? null : state.TrafficText
                }.OfType<string>())
            };
        }).ToArray();
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
        if (Equals(button.Tag, "refresh")) { await runtime.Imports.RefreshAsync(row.Group.Id); return; }
        if (!Equals(button.Tag, "remove")) return;
        if (!ConfirmRemoveGroup(row.Group)) return;
        RemoveGroup(row.Group.Id);
        DetailText.Text = "«" + row.Title + "» убрана.";
    }
}
