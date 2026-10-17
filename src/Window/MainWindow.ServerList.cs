using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Oblachka;

// Панель «Все серверы»: группы, избранное, порядок. Выбор идёт через ProfilePicker
// (модель вне дерева), клик по строке подключает так же, как на главном экране.
public partial class MainWindow
{
    private readonly HashSet<string> favorites = new(StringComparer.Ordinal);

    // Имя и источник переживают смену ключей панели; значения в файле состояния — хеши.
    private static string FavoriteKey(Profile item) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(item.Source + ":" + item.Name)));

    // «Лучшие сверху»: по той же оценке, что у автовыбора (задержка и скорость); за ними —
    // серверы, у которых есть только отклик входа, по нему; не ответившие и без замера — в конце.
    private double MeasurementOrder(Profile item) => LatestMeasurement(item) is { Success: true } result && ServerMeasurements.Fresh(result, DateTimeOffset.UtcNow)
        ? result.Handshake ? 1e9 + result.Milliseconds : ServerMeasurements.Score(result)
        : double.MaxValue;

    private void WireServerList()
    {
        Servers.SortOrder.SelectionChanged += (_, _) => FilterProfiles();
        Servers.SheetList.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(SheetRowClick));
    }

    private void SheetRowClick(object sender, RoutedEventArgs args)
    {
        if (args.OriginalSource is Button { DataContext: SheetHeader header } headerButton) { SheetHeaderClick(headerButton, header); return; }
        if (args.OriginalSource is not Button { DataContext: ServerRow row } button) return;
        if (Equals(button.Tag, "remove")) { if (row.Profile is not null) RemoveServer(row.Profile); return; }
        if (Equals(button.Tag, "star")) { if (row.Profile is not null) ToggleFavorite(row.Profile); return; }
        if (Equals(button.Tag, "check")) { if (row.Profile is not null) _ = CheckServerAsync(row.Profile); return; }
        PickServer(row.IsAuto ? null : row.Profile);
        Navigate("Connection");
    }

    private void ToggleFavorite(Profile item)
    {
        var key = FavoriteKey(item);
        if (!favorites.Remove(key)) favorites.Add(key);
        RenderSheet();
        RefreshHome();
        QueueStateSave();
    }

    // Группы: автовыбор (без поиска), избранное, узлы сервиса, сторонние без проверок.
    private void RenderSheet()
    {
        if (!viewsReady) return;
        var shown = ProfilePicker.Items.OfType<Profile>().ToArray();
        var items = new List<object>();
        if (Servers.SearchBox.Text.Trim().Length == 0)
            items.Add(new ServerRow
            {
                IsAuto = true, Name = "Автовыбор", Selected = autoSelect, IconOnAccent = true, Icon = TryFindResource("IcoAuto") as Geometry,
                Note = autoSelect && profile is not null ? "сейчас: " + profile.DisplayName : "самый быстрый из пула"
            });
        // Заголовок подписки виден и без строк (всё скрыто, подписка пустая): с него её убирают
        // и возвращают скрытые. Но не при поиске и не на вкладке другой подписки.
        var tab = Servers.GroupTabs.SelectedIndex > 0 && Servers.GroupTabs.Tag is string[] ids && Servers.GroupTabs.SelectedIndex - 1 < ids.Length ? ids[Servers.GroupTabs.SelectedIndex - 1] : null;
        void Group(SheetHeader header, IEnumerable<Profile> members, bool note = false)
        {
            var rows = members.Select(p => Row(p, note)).ToArray();
            if (rows.Length == 0 && (header.GroupId is null || Servers.SearchBox.Text.Trim().Length > 0 || tab is not null && tab != header.GroupId)) return;
            items.Add(header);
            items.AddRange(rows);
        }
        Group(new SheetHeader("ИЗБРАННОЕ"), shown.Where(p => favorites.Contains(FavoriteKey(p))), note: true);
        foreach (var group in groups)
            Group(new SheetHeader(group.Name.ToUpperInvariant(), group.Id, group.IsSubscription, HiddenIn(group.Id)),
                shown.Where(p => p.Group == group.Id && !favorites.Contains(FavoriteKey(p))));
        Servers.SheetList.ItemsSource = items;
        RefreshMeasurement();
    }

    // Подпись строки для отчёта и старых проверок: замер, давность, избранное.
    private void RefreshServerLabels()
    {
        foreach (var item in importedProfiles)
        {
            var label = measurements.TryGetValue(Profiles.Fingerprint(item), out var result)
                ? (result.Success ? $"HTTPS: {result.Milliseconds} мс" : "Нет ответа при проверке") +
                  (result.Time > DateTimeOffset.UtcNow || DateTimeOffset.UtcNow - result.Time >= TimeSpan.FromMinutes(20) ? " · устарело" : "")
                : "Не проверен";
            item.SetListStatus((favorites.Contains(FavoriteKey(item)) ? "★ · " : "") + label);
        }
    }

    private void RefreshServerListControls()
    {
        // Только подписи: панель перестраивается по изменению данных, а не каждую секунду.
        RefreshServerLabels();
    }
}
