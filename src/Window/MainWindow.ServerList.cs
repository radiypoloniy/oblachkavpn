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
        Servers.SortOrder.SelectionChanged += (_, _) => { Servers.SortText.Text = Servers.SortOrder.SelectedItem as string ?? ""; FilterProfiles(); };
        // Порядок — меню у «Лучшие сверху ▾»; выбранное значение по-прежнему в SortOrder.
        // Меню в облике приложения (SortPopup), а не системный ContextMenu: выбранный — с галочкой.
        Servers.SortButton.Click += (_, _) =>
        {
            Servers.SortItems.Children.Clear();
            for (var i = 0; i < Servers.SortOrder.Items.Count; i++)
            {
                var index = i;
                var check = new System.Windows.Shapes.Path { Data = (Geometry)FindResource("IcoCheck"), Style = (Style)FindResource("Icon"), Width = 14, Height = 14, StrokeThickness = 2.4,
                    Visibility = Servers.SortOrder.SelectedIndex == i ? Visibility.Visible : Visibility.Hidden };
                System.Windows.Controls.DockPanel.SetDock(check, System.Windows.Controls.Dock.Right);
                var row = new System.Windows.Controls.DockPanel { Children = { check, new System.Windows.Controls.TextBlock { Text = (string)Servers.SortOrder.Items[i], VerticalAlignment = VerticalAlignment.Center } } };
                var item = new System.Windows.Controls.Button { Content = row, Style = (Style)FindResource("MenuRow") };
                if (index == Servers.SortOrder.SelectedIndex) item.FontWeight = FontWeights.SemiBold;
                item.Click += (_, _) => { Servers.SortOrder.SelectedIndex = index; Servers.SortPopup.IsOpen = false; };
                Servers.SortItems.Children.Add(item);
            }
            Servers.SortPopup.HorizontalOffset = Servers.SortButton.ActualWidth - 254 + 12;
            Servers.SortPopup.IsOpen = true;
        };
        Servers.AutoPick.Click += (_, _) => { PickServer(null); Navigate("Connection"); };
        Servers.SheetList.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(SheetRowClick));
    }

    private void SheetRowClick(object sender, RoutedEventArgs args)
    {
        if (args.OriginalSource is Button { DataContext: SheetHeader header } headerButton) { SheetHeaderClick(headerButton, header); return; }
        if (args.OriginalSource is not Button { DataContext: ServerRow row } button) return;
        if (Equals(button.Tag, "remove")) { if (row.Profile is not null) RemoveServer(row.Profile); return; }
        if (Equals(button.Tag, "star")) { if (row.Profile is not null) ToggleFavorite(row.Profile); return; }
        if (Equals(button.Tag, "check")) { if (row.Profile is not null) _ = CheckServerAsync(row.Profile); return; }
        if (Equals(button.Tag, "edit")) { if (row.Profile is not null) _ = EditServerAsync(row.Profile); return; }
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
        // Автовыбор — закреплённая карточка над списком (AutoCard), выбранный — чернильной рамкой.
        Servers.AutoCard.BorderThickness = new System.Windows.Thickness(autoSelect ? 2 : 0);
        Servers.AutoNote.Text = autoSelect && profile is not null ? "сейчас: " + profile.DisplayName : "самый быстрый из отмеченных";
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
        Group(new SheetHeader("Избранное"), shown.Where(p => favorites.Contains(FavoriteKey(p))), note: true);
        foreach (var group in groups)
            Group(new SheetHeader(group.Name, group.Id, group.IsSubscription, HiddenIn(group.Id), group.IsSubscription ? Junk(group.Id).Length : 0),
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
