using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace Oblachka.Views;

// «Сейчас в сети»: программы в сети прямо сейчас. Нажатие (или правая кнопка) раскрывает строку:
// «VPN · Напрямую · Блок», что возьмёт правило, с кем программа на связи. Что делать, решает окно
// (событие ActionChosen); список обновляет оно же, раз в пару секунд, пока раздел открыт.
// Фильтры: все, через VPN, напрямую, с правилом.
public partial class ConnectionsView : UserControl
{
    private IReadOnlyList<ConnectionRow> rows = Array.Empty<ConnectionRow>();
    public event Action<ConnectionRow, SiteAction?>? ActionChosen;
    public event Action<FileRow, SiteAction?>? FileActionChosen;
    public event Action<IReadOnlyCollection<string>>? CloseChosen;
    // Правило на сайт: app — только в этой программе, иначе для всех программ; null — убрать.
    public event Action<SiteRow, bool, SiteAction?>? SiteActionChosen;
    public event Action? DetailsChanged;
    public event Action? Closed;

    public ConnectionsView()
    {
        InitializeComponent();
        BackButton.Click += (_, _) => Closed?.Invoke();
        SizeChanged += (_, args) => Fit(args.NewSize.Width);
        SearchBox.TextChanged += (_, _) => Filter();
        Filters.ItemsSource = new[] { "Все", "Через VPN", "Напрямую", "С правилом" };
        Filters.SelectedIndex = 0;
        Filters.SelectionChanged += (_, _) => Filter();
        AppList.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(Clicked));
        AppList.AddHandler(MouseRightButtonUpEvent, new MouseButtonEventHandler((_, args) =>
        {
            if ((args.OriginalSource as FrameworkElement)?.DataContext is ConnectionRow row) { Toggle(row); args.Handled = true; }
        }), true);
    }

    // Узкий остров: переключатель режима — строкой под заголовком, поиск — под фильтрами во всю ширину.
    private void Fit(double width)
    {
        var narrow = width < 690;
        AppList.Tag = width < 480 ? "narrow" : null;
        var rulesHost = width < 520 ? NarrowRulesSlot : HeaderRulesSlot;
        if (RulesOpen.Parent is Panel parent && parent != rulesHost) { parent.Children.Remove(RulesOpen); rulesHost.Children.Add(RulesOpen); }
        NarrowRulesSlot.Visibility = rulesHost == NarrowRulesSlot ? Visibility.Visible : Visibility.Collapsed;
        var host = narrow ? NarrowModeSlot : HeaderModeSlot;
        if (ModeTrack.Parent is Panel from && from != host) { from.Children.Remove(ModeTrack); host.Children.Add(ModeTrack); }
        NarrowModeSlot.Visibility = narrow ? Visibility.Visible : Visibility.Collapsed;
        DockPanel.SetDock(SearchBox, narrow ? Dock.Bottom : Dock.Right);
        SearchBox.Width = narrow ? double.NaN : 200;
        SearchBox.Margin = narrow ? new Thickness(0, 8, 0, 0) : new Thickness(12, 0, 0, 0);
    }

    // focus — человек открыл раздел сам; при открытии по умолчанию фокус поиск не отбирает.
    public void Show(bool focus, string note)
    {
        SetNote(note);
        Visibility = Visibility.Visible;
        if (focus) SearchBox.Focus();
    }

    // Строки переиспользуются: раскрытая строка остаётся раскрытой между обновлениями.
    // Список пересобирается, только если изменился состав или порядок программ: строки сами
    // обновляют свои подписи, а пересборка дерева раз в 2 секунды стоила заметного процессора.
    // С фильтром список перепроверяется и при том же составе: программа могла уйти из VPN в «напрямую».
    public void Load(IReadOnlyList<ConnectionRow> next)
    {
        Count.Text = next.Count > 0 ? next.Count.ToString() : "";
        if (Filters.SelectedIndex <= 0 && next.Count == rows.Count && next.SequenceEqual(rows)) return;
        // Пока строка раскрыта, список не переставляется: прежние строки на своих местах, новые — в конце.
        if (rows.Any(r => r.Expanded))
            next = rows.Where(next.Contains).Concat(next.Where(r => !rows.Contains(r))).ToArray();
        rows = next;
        Filter();
    }

    public void SetNote(string note) { if (Note.Text != note) Note.Text = note; }

    private void Filter()
    {
        var query = SearchBox.Text.Trim();
        var filter = Filters.SelectedIndex;
        var shown = rows.Where(r => (query.Length == 0 || r.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                r.Path.Contains(query, StringComparison.OrdinalIgnoreCase)) &&
            filter switch { 1 => r.Vpn > 0, 2 => r.Direct > 0, 3 => r.Rule is not null, _ => true }).ToArray();
        if (AppList.ItemsSource is not IReadOnlyList<ConnectionRow> old || !old.SequenceEqual(shown)) AppList.ItemsSource = shown;
        Empty.Visibility = shown.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Empty.Text = rows.Count == 0 ? "Смотрим, какие программы в сети…" : query.Length > 0 ? "Ничего не нашлось." : "Таких программ сейчас нет.";
    }

    private void Clicked(object sender, RoutedEventArgs args)
    {
        var source = args.OriginalSource as FrameworkElement;
        // Файл программы: правило только на него. Сайт и программа: закрыть их соединения.
        if (source is Button { Tag: string fileTag } && source.DataContext is FileRow file)
        {
            FileActionChosen?.Invoke(file, fileTag switch { "file-vpn" => SiteAction.Tunnel, "file-direct" => SiteAction.Direct, "file-block" => SiteAction.Block, _ => null });
            return;
        }
        if (source is Button { Tag: string siteTag } && source.DataContext is SiteRow site)
        {
            if (siteTag == "site-close") CloseChosen?.Invoke(site.Ids);
            else if (siteTag == "site")
            {
                var open = !site.Expanded;
                foreach (var other in rows.Where(r => r.Expanded).SelectMany(r => r.Sites))
                    if (!ReferenceEquals(other, site)) other.Expanded = false;
                site.Expanded = open;
                if (open)
                {
                    DetailsChanged?.Invoke();
                    if (source.Parent is FrameworkElement editor)
                        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => editor.BringIntoView()));
                }
            }
            else if (siteTag.Split('-') is [var scope and ("app" or "all"), var verb])
                SiteActionChosen?.Invoke(site, scope == "app", verb switch { "vpn" => SiteAction.Tunnel, "direct" => SiteAction.Direct, "block" => SiteAction.Block, _ => null });
            return;
        }
        if (source?.DataContext is not ConnectionRow row) return;
        if (source is Button { Tag: "close-all" }) { CloseChosen?.Invoke(row.ConnectionIds); return; }
        if (source is Button { Tag: "more-sites" }) { row.ShowMoreSites(); return; }
        if (args.OriginalSource is Button { Tag: "folders" }) { row.FoldersShown = !row.FoldersShown; return; }
        if (args.OriginalSource is Button { Tag: string tag } && tag != "row")
        {
            SiteAction? action = tag switch { "vpn" => SiteAction.Tunnel, "direct" => SiteAction.Direct, "block" => SiteAction.Block, _ => null };
            ActionChosen?.Invoke(row, action);
            return;
        }
        Toggle(row);
    }

    private void Toggle(ConnectionRow row)
    {
        var open = !row.Expanded;
        foreach (var other in rows) other.Expanded = false;
        row.Expanded = open;
        if (open) DetailsChanged?.Invoke();
    }
}
