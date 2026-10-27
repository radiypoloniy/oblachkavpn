using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Oblachka;

// Меню «⋯» в «Серверах» (облик 0.21): у строки сервера — протокол и скорость, «проверить»,
// «изменить» и «убрать»; у заголовка подписки — обновить, скрыть лишние, вернуть скрытые, убрать.
// В самой строке остаются флаг, название и пинг.
public partial class MainWindow
{
    private void ShowSheetMenu(FrameworkElement anchor, string? caption, params (string Text, bool Danger, Action Act)[] items)
    {
        var list = Servers.RowMenuItems;
        list.Children.Clear();
        if (!string.IsNullOrEmpty(caption))
            list.Children.Add(new TextBlock { Text = caption, FontSize = (double)FindResource("FsSmall"), Foreground = (Brush)FindResource("Fa"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 8, 2, 6) });
        foreach (var (text, danger, act) in items)
        {
            var item = new Button { Content = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center }, Style = (Style)FindResource("MenuRow") };
            if (danger) item.Foreground = (Brush)FindResource("RF");
            item.Click += (_, _) => { Servers.RowMenuPopup.IsOpen = false; act(); };
            list.Children.Add(item);
        }
        Servers.RowMenuPopup.PlacementTarget = anchor;
        Servers.RowMenuPopup.HorizontalOffset = anchor.ActualWidth - 274 + 12;
        Servers.RowMenuPopup.IsOpen = true;
    }

    private void ShowRowMenu(FrameworkElement anchor, ServerRow row)
    {
        var members = row.IsMachine ? row.Members! : [row.Profile!];
        var items = new List<(string, bool, Action)> { (row.IsMachine ? "Проверить все варианты" : "Проверить", false, () => _ = CheckMachineAsync(row)) };
        if (row.IsManual && !row.IsMachine) items.Add(("Изменить настройку", false, () => _ = EditServerAsync(row.Profile!)));
        items.Add(row.IsManual ? ("Удалить", true, () => RemoveServers(members)) : ("Скрыть из списка", false, () => RemoveServers(members)));
        ShowSheetMenu(anchor, row.MenuDetail, items.ToArray());
    }

    // Свой сервер удаляется, сервер подписки скрывается (RemoveServer). У машины — все её протоколы;
    // после удаления ручного узла группа пересобрана, поэтому записи ищутся заново по ключу.
    private void RemoveServers(IReadOnlyList<Profile> members)
    {
        if (members.Count == 1) { RemoveServer(members[0]); return; }
        if (members[0].Group == ProfileGroup.ManualId && !AppDialog.Confirm(this, "Удалить «" + ServerMachines.Title(members) + "»?",
                "Удалим все варианты подключения (" + members.Count + "). Вернуть их можно, только добавив ссылки снова.", "Удалить", danger: true)) return;
        foreach (var key in members.Select(FavoriteKey).ToArray())
            if (ProfilesOf(members[0].Group).FirstOrDefault(p => FavoriteKey(p) == key) is { } current) RemoveServer(current, confirm: false);
    }

    private void ShowGroupMenu(FrameworkElement anchor, SheetHeader header)
    {
        var free = header.GroupId == FreeServers.GroupId;
        var manual = header.GroupId == ProfileGroup.ManualId;
        var items = new List<(string, bool, Action)>();
        if (header.CanRefresh) items.Add(("Обновить подписку", false, () => GroupAction("group-refresh", header)));
        if (header.Junk > 0) items.Add(($"Скрыть неотвечающие и повторы ({header.Junk})", false, () => GroupAction("group-hide-junk", header)));
        if (header.Hidden > 0) items.Add(($"Вернуть скрытые ({header.Hidden})", false, () => GroupAction("group-unhide", header)));
        items.Add((free ? "Выключить бесплатные" : manual ? "Удалить все добавленные вручную" : "Убрать подписку", true, () => GroupAction("group-remove", header)));
        ShowSheetMenu(anchor, header.Updated, items.ToArray());
    }

    // Подпись над пунктами меню подписки: когда обновлялась.
    private static string? UpdatedText(ProfileGroup group)
    {
        if (group.IsManual) return "Ссылки, файлы и QR";
        if (group.Updated is not { } at) return null;
        var local = at.ToLocalTime();
        return "Обновлена " + (local.Date == DateTimeOffset.Now.Date ? "в " + local.ToString("HH:mm") : local.ToString("dd.MM в HH:mm"));
    }
}
