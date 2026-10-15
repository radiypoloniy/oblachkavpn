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

    private long MeasurementOrder(Profile item) => LatestMeasurement(item) is { Success: true } result &&
        ServerMeasurements.Fresh(result, DateTimeOffset.UtcNow) ? result.Milliseconds : long.MaxValue;

    private void WireServerList()
    {
        Servers.SortOrder.SelectionChanged += (_, _) => FilterProfiles();
        Servers.SheetList.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(SheetRowClick));
    }

    private void SheetRowClick(object sender, RoutedEventArgs args)
    {
        if (args.OriginalSource is not Button { DataContext: ServerRow row } button) return;
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
                Note = autoSelect && profile is not null ? "сейчас: " + profile.DisplayName : "самый быстрый отклик"
            });
        void Group(string title, IEnumerable<Profile> members, bool note = false)
        {
            var rows = members.Select(p => Row(p, note)).ToArray();
            if (rows.Length == 0) return;
            items.Add(new SheetHeader(title));
            items.AddRange(rows);
        }
        Group("ИЗБРАННОЕ", shown.Where(p => favorites.Contains(FavoriteKey(p))), note: true);
        foreach (var group in groups)
            Group(group.Name.ToUpperInvariant(), shown.Where(p => p.Group == group.Id && !favorites.Contains(FavoriteKey(p))));
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
