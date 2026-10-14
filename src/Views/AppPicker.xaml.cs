using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Oblachka.Views;

// Панель выбора программ. Сама только показывает и отмечает; что добавить в правила, решает окно
// (событие Chosen), а файлы и значки ищутся в фоне при первом открытии.
public partial class AppPicker : UserControl
{
    private IReadOnlyList<AppRow> rows = Array.Empty<AppRow>();
    public event Action<IReadOnlyList<InstalledApp>, SiteAction>? Chosen;
    public event Action? FileRequested;
    public event Action? Closed;

    public AppPicker()
    {
        InitializeComponent();
        RenderTransform = new TranslateTransform();
        SearchBox.TextChanged += (_, _) => Filter();
        AppList.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(RowClick));
        BackButton.Click += (_, _) => Closed?.Invoke();
        FileButton.Click += (_, _) => FileRequested?.Invoke();
        AddButton.Click += (_, _) =>
        {
            var picked = rows.Where(r => r.Checked).Select(r => r.App).ToArray();
            if (picked.Length > 0) Chosen?.Invoke(picked, Action);
        };
    }

    public SiteAction Action => ActionChoice.SelectedIndex switch { 1 => SiteAction.Direct, 2 => SiteAction.Block, _ => SiteAction.Tunnel };

    // added — уже есть в правилах (имя файла → действие): такие строки видны, но не выбираются.
    public void Open(IReadOnlyDictionary<string, SiteAction> added)
    {
        SearchBox.Clear();
        ActionChoice.SelectedIndex = 0;
        foreach (var row in rows) { row.Checked = false; row.SetAdded(added); }
        Filter();
        Visibility = Visibility.Visible;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ((TranslateTransform)RenderTransform).BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
        SearchBox.Focus();
    }

    public void Load(IReadOnlyList<AppRow> found, IReadOnlyDictionary<string, SiteAction> added)
    {
        rows = found;
        foreach (var row in rows) row.SetAdded(added);
        Filter();
    }

    private void Filter()
    {
        var shown = InstalledApps.Search(rows.Select(r => r.App).ToArray(), SearchBox.Text);
        var byApp = rows.ToDictionary(r => r.App);
        AppList.ItemsSource = shown.Select(a => byApp[a]).ToArray();
        StatusText.Text = rows.Count == 0 ? "Ищем программы на компьютере…"
            : shown.Count == 0 ? "Не нашли такую программу. Нажмите «Выбрать файл…» и укажите её .exe." : "";
        StatusText.Visibility = StatusText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        Count();
    }

    private void RowClick(object sender, RoutedEventArgs args)
    {
        if ((args.OriginalSource as FrameworkElement)?.DataContext is not AppRow row || row.Added) return;
        row.Checked = !row.Checked;
        Count();
    }

    private void Count()
    {
        var count = rows.Count(r => r.Checked);
        AddButton.IsEnabled = count > 0;
        AddButton.Content = count == 0 ? "Добавить" : "Добавить (" + count + ")";
    }
}

// Строка списка программ: значок, имя, подпись (файл, «запущена», «уже в правилах»), отметка.
public sealed class AppRow(InstalledApp app, ImageSource? icon) : INotifyPropertyChanged
{
    private bool isChecked, added;
    private string note = "";
    public InstalledApp App { get; } = app;
    public string Name => App.Name;
    public ImageSource? Icon { get; } = icon;
    public Visibility PlaceholderVisibility => Icon is null ? Visibility.Visible : Visibility.Collapsed;
    public string Initial => Name.Length > 0 ? char.ToUpperInvariant(Name[0]).ToString() : "?";
    public bool Checked { get => isChecked; set { if (isChecked == value) return; isChecked = value; Changed(nameof(Checked)); } }
    public bool Added => added;
    public string Note => note;
    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetAdded(IReadOnlyDictionary<string, SiteAction> rules)
    {
        added = rules.TryGetValue(App.Executable, out var action);
        if (added) isChecked = false;
        note = added ? "уже в правилах · " + RuleRow.ActionLabel(action) : App.Executable + (App.Running ? " · запущена" : "");
        Changed(nameof(Added)); Changed(nameof(Note)); Changed(nameof(Checked));
    }

    private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
}
