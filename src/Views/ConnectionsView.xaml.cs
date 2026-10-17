using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Oblachka.Views;

// «Сейчас в сети»: программы в сети прямо сейчас. Нажатие (или правая кнопка) раскрывает строку:
// «VPN · Напрямую · Блок», что возьмёт правило, с кем программа на связи. Что делать, решает окно
// (событие ActionChosen); список обновляет оно же, раз в пару секунд, пока раздел открыт.
// Фильтры: все, через VPN, напрямую, с правилом.
public partial class ConnectionsView : UserControl
{
    private IReadOnlyList<ConnectionRow> rows = Array.Empty<ConnectionRow>();
    public event Action<ConnectionRow, SiteAction?>? ActionChosen;
    public event Action? Closed;

    public ConnectionsView()
    {
        InitializeComponent();
        BackButton.Click += (_, _) => Closed?.Invoke();
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
        if (Filters.SelectedIndex <= 0 && next.Count == rows.Count && next.Select(r => r.Path).SequenceEqual(rows.Select(r => r.Path), StringComparer.OrdinalIgnoreCase)) return;
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
        if ((args.OriginalSource as FrameworkElement)?.DataContext is not ConnectionRow row) return;
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
    }
}

// Строка «Сейчас в сети»: программа, куда идёт её трафик сейчас и какое правило на неё действует.
public sealed class ConnectionRow(string path) : INotifyPropertyChanged
{
    private bool expanded, foldersShown;
    private string name = "", summary = "", remotes = "", scope = "", ruleText = "", route = "", routeKind = "", pending = "";
    private string[] folders = [];
    private SiteAction? rule;
    private ImageSource? icon;
    public string Path { get; } = path;
    public string Name { get => name; set => Set(ref name, value, nameof(Name), nameof(Initial)); }
    // «23 соединения» — под именем.
    public string Summary { get => summary; set => Set(ref summary, value, nameof(Summary)); }
    public string Remotes { get => remotes; set => Set(ref remotes, value, nameof(Remotes)); }
    // Что возьмёт правило одной строкой: «Правило возьмёт Steam целиком · 3 папки».
    public string Scope { get => scope; set => Set(ref scope, value, nameof(Scope)); }
    // Папки правила — по ссылке «Показать папки», чтобы карточка не была кучной.
    public string[] Folders
    {
        get => folders;
        set
        {
            if (folders.SequenceEqual(value)) return;
            folders = value;
            foreach (var n in new[] { nameof(Folders), nameof(FoldersLink), nameof(FoldersLinkVisibility), nameof(FoldersVisibility) }) Changed(n);
        }
    }
    public bool FoldersShown { get => foldersShown; set { if (foldersShown == value) return; foldersShown = value; Changed(nameof(FoldersLink)); Changed(nameof(FoldersVisibility)); } }
    public string FoldersLink => foldersShown ? "Скрыть" : folders.Length == 1 ? "Показать папку" : $"Показать папки ({folders.Length})";
    // Плашка справа: «через VPN», «напрямую», «VPN 29 · напрямую 2»; вид — vpn, direct, mix.
    public string Route { get => route; set => Set(ref route, value, nameof(Route)); }
    public string RouteKind { get => routeKind; set => Set(ref routeKind, value, nameof(RouteKind)); }
    // Соединения, открытые до правила, доживают прежним путём.
    public string Pending { get => pending; set => Set(ref pending, value, nameof(Pending), nameof(PendingVisibility)); }
    public int Vpn { get; set; }
    public int Direct { get; set; }
    public SiteAction? Rule
    {
        get => rule;
        set
        {
            if (rule == value) return;
            rule = value;
            foreach (var n in new[] { nameof(Rule), nameof(RuleVisibility), nameof(RemoveVisibility), nameof(RuleVpn), nameof(RuleDirect), nameof(RuleBlock) }) Changed(n);
        }
    }
    public string RuleText { get => ruleText; set => Set(ref ruleText, value, nameof(RuleText)); }
    public bool Configurable { get; set; } = true;
    public ImageSource? Icon { get => icon; set { icon = value; Changed(nameof(Icon)); Changed(nameof(PlaceholderVisibility)); } }
    public bool Expanded
    {
        get => expanded;
        set
        {
            if (expanded == value) return;
            expanded = value;
            foreach (var n in new[] { nameof(Expanded), nameof(ActionsVisibility), nameof(PickVisibility), nameof(RouteVisibility) }) Changed(n);
        }
    }
    public bool RuleVpn => rule == SiteAction.Tunnel;
    public bool RuleDirect => rule == SiteAction.Direct;
    public bool RuleBlock => rule == SiteAction.Block;
    public Visibility ActionsVisibility => expanded ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PickVisibility => expanded && Configurable ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RouteVisibility => expanded && Configurable ? Visibility.Collapsed : Visibility.Visible;
    public Visibility RuleVisibility => rule is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility RemoveVisibility => rule is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility FoldersLinkVisibility => folders.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FoldersVisibility => foldersShown && folders.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PendingVisibility => pending.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PlaceholderVisibility => icon is null ? Visibility.Visible : Visibility.Collapsed;
    public string Initial => name.Length > 0 ? char.ToUpperInvariant(name[0]).ToString() : "?";
    public event PropertyChangedEventHandler? PropertyChanged;
    // Имя элемента списка для экранного диктора и UIA (иначе там имя класса).
    public override string ToString() => name;

    private void Set(ref string field, string value, params string[] names)
    {
        if (field == value) return;
        field = value;
        foreach (var n in names) Changed(n);
    }
    private void Changed(string property) => PropertyChanged?.Invoke(this, new(property));
}
