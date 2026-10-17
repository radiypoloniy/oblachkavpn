using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Oblachka.Views;

// «Соединения»: программы в сети прямо сейчас. Нажатие (или правая кнопка) раскрывает строку
// с действиями — через VPN, напрямую, блокировать, убрать правило. Что делать, решает окно
// (событие ActionChosen); список обновляет оно же, раз в пару секунд, пока экран открыт.
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
        AppList.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(Clicked));
        AppList.AddHandler(MouseRightButtonUpEvent, new MouseButtonEventHandler((_, args) =>
        {
            if ((args.OriginalSource as FrameworkElement)?.DataContext is ConnectionRow row) { Toggle(row); args.Handled = true; }
        }), true);
    }

    public void Show(string note)
    {
        Note.Text = note;
        Visibility = Visibility.Visible;
        SearchBox.Focus();
    }

    // Строки переиспользуются: раскрытая строка остаётся раскрытой между обновлениями.
    public void Load(IReadOnlyList<ConnectionRow> next)
    {
        rows = next;
        Filter();
    }

    private void Filter()
    {
        var query = SearchBox.Text.Trim();
        var shown = query.Length == 0 ? rows : rows.Where(r => r.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            r.Path.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        AppList.ItemsSource = shown;
        Empty.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Empty.Text = rows.Count == 0 ? "Смотрим, какие программы в сети…" : "Ничего не нашлось.";
    }

    private void Clicked(object sender, RoutedEventArgs args)
    {
        if ((args.OriginalSource as FrameworkElement)?.DataContext is not ConnectionRow row) return;
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

// Строка «Соединений»: программа, куда идёт её трафик сейчас и какое правило на неё действует.
public sealed class ConnectionRow(string path) : INotifyPropertyChanged
{
    private bool expanded;
    private string name = "", summary = "", remotes = "", scope = "", ruleText = "";
    private SiteAction? rule;
    private ImageSource? icon;
    public string Path { get; } = path;
    public string Name { get => name; set => Set(ref name, value, nameof(Name), nameof(Initial)); }
    public string Summary { get => summary; set => Set(ref summary, value, nameof(Summary)); }
    public string Remotes { get => remotes; set => Set(ref remotes, value, nameof(Remotes)); }
    // Что возьмёт правило: «вся папка …» или «только этот файл».
    public string Scope { get => scope; set => Set(ref scope, value, nameof(Scope)); }
    public SiteAction? Rule { get => rule; set { rule = value; Changed(nameof(Rule)); Changed(nameof(RuleVisibility)); Changed(nameof(RemoveVisibility)); } }
    public string RuleText { get => ruleText; set => Set(ref ruleText, value, nameof(RuleText)); }
    public bool Configurable { get; set; } = true;
    public ImageSource? Icon { get => icon; set { icon = value; Changed(nameof(Icon)); Changed(nameof(PlaceholderVisibility)); } }
    public bool Expanded { get => expanded; set { if (expanded == value) return; expanded = value; Changed(nameof(Expanded)); Changed(nameof(ActionsVisibility)); } }
    public Visibility ActionsVisibility => expanded ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RuleVisibility => rule is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility RemoveVisibility => rule is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ButtonsVisibility => Configurable ? Visibility.Visible : Visibility.Collapsed;
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
