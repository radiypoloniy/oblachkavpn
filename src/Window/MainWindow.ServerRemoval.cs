using System.Windows;
using System.Windows.Controls;

namespace Oblachka;

// Убрать сервер или подписку прямо в «Серверах», чтобы список не превращался в свалку.
// Свой сервер («Вручную») удаляется. Сервер подписки скрывается: при обновлении подписка
// прислала бы его снова. «Вернуть скрытые» у заголовка подписки показывает их обратно.
public partial class MainWindow
{
    private readonly HashSet<string> hiddenServers = new(StringComparer.Ordinal);

    private bool HiddenServer(Profile item) => hiddenServers.Contains(FavoriteKey(item));
    private int HiddenIn(string groupId) => ProfilesOf(groupId).Count(HiddenServer);

    private void RemoveServer(Profile item, bool confirm = true)
    {
        if (operation is not null) return;
        if (runningProfile is not null && FavoriteKey(runningProfile) == FavoriteKey(item))
        {
            DetailText.Text = "Через этот сервер вы сейчас подключены. Отключитесь или выберите другой, потом уберите.";
            return;
        }
        var key = FavoriteKey(item);
        if (item.Group == ProfileGroup.ManualId)
        {
            if (confirm && !AppDialog.Confirm(this, "Удалить «" + item.DisplayName + "»?", "Сервер добавлен вручную: вернуть его можно, только добавив ссылку снова.", "Удалить", danger: true)) return;
            var rest = ProfilesOf(ProfileGroup.ManualId).Where(p => !ReferenceEquals(p, item)).ToArray();
            favorites.Remove(key);
            if (rest.Length == 0) { RemoveGroup(ProfileGroup.ManualId); DetailText.Text = "Сервер удалён."; return; }
            SetGroup(groups.First(g => g.IsManual), rest);
            DetailText.Text = "Сервер удалён.";
        }
        else
        {
            hiddenServers.Add(key);
            DetailText.Text = "Сервер скрыт. Вернуть — «Вернуть скрытые» у заголовка подписки.";
        }
        AfterServerListChange(profile is not null && FavoriteKey(profile) == key);
    }

    private void UnhideServers(string groupId)
    {
        foreach (var item in ProfilesOf(groupId)) hiddenServers.Remove(FavoriteKey(item));
        AfterServerListChange(false);
    }

    private void AfterServerListChange(bool removedSelected)
    {
        if (removedSelected) profile = null;
        ApplyProfiles(!removedSelected);
        RefreshGroups();
        UpdateControls();
        QueueStateSave(); FlushStateSave();
    }

    private bool ConfirmRemoveGroup(ProfileGroup group)
    {
        var title = group.IsManual ? "Убрать все серверы, добавленные вручную?" : "Убрать «" + group.Name + "»?";
        return AppDialog.Confirm(this, title, "Уберём и её " + ServerCount(ProfilesOf(group.Id).Count) + ". Вернуть можно, добавив подписку снова.", "Убрать", danger: true);
    }

    // Кнопки у заголовка подписки в «Серверах»: обновить, убрать, вернуть скрытые.
    private async void SheetHeaderClick(Button button, SheetHeader header)
    {
        if (operation is not null || groups.FirstOrDefault(g => g.Id == header.GroupId) is not { } group) return;
        switch (button.Tag)
        {
            case "group-refresh": await LoadSubscriptionAsync(group.Sources, group.PreferredSource); break;
            case "group-unhide": UnhideServers(group.Id); break;
            case "group-remove":
                if (!ConfirmRemoveGroup(group)) return;
                RemoveGroup(group.Id);
                DetailText.Text = "«" + group.Name + "» убрана.";
                break;
        }
    }
}
