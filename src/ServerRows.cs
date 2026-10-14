using System.Windows;
using System.Windows.Media;

namespace Oblachka;

// Строка списка серверов нового главного экрана и панели «Все серверы».
//
// Число справа — свежий полный замер через ядро (ServerMeasurements), а без него отклик
// сервера (ServerPing, время TCP-рукопожатия). В интерфейсе оба показаны как «142 мс».
// Цвет точки: < 160 мс зелёный, < 250 мс жёлтый, иначе оранжевый; нет замера — бледный.
public sealed class ServerRow
{
    public Profile? Profile { get; init; }
    public bool IsAuto { get; init; }
    public required string Name { get; init; }
    public string? Note { get; init; }
    public ImageSource? Flag { get; init; }
    public Geometry? Icon { get; init; }
    public bool IconOnAccent { get; init; }
    public bool IconMuted { get; init; }
    public string Latency { get; init; } = "";
    public Brush? LatencyDot { get; init; }
    public bool Selected { get; init; }
    public bool Favorite { get; init; }
    public bool CanFavorite => !IsAuto;

    public Visibility FlagVisibility => Flag is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility IconVisibility => Flag is null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoteVisibility => string.IsNullOrEmpty(Note) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility LatencyVisibility => string.IsNullOrEmpty(Latency) ? Visibility.Collapsed : Visibility.Visible;
    public FontWeight NameWeight => Selected ? FontWeights.SemiBold : FontWeights.Normal;

    private static readonly Brush Fast = Frozen(0x2F, 0xB3, 0x6A), Medium = Frozen(0xD9, 0xA0, 0x1E), Slow = Frozen(0xD9, 0x73, 0x1E);
    private static Brush Frozen(byte r, byte g, byte b) { var brush = new SolidColorBrush(Color.FromRgb(r, g, b)); brush.Freeze(); return brush; }

    public static Brush? DotFor(long? milliseconds) => milliseconds switch
    {
        null => Application.Current?.TryFindResource("Fa") as Brush,
        < 160 => Fast,
        < 250 => Medium,
        _ => Slow
    };

    // Значок вместо флага: игры и звонки, обход белых списков, сторонняя подписка.
    public static Geometry? IconFor(Profile profile)
    {
        var name = profile.DisplayName.ToLowerInvariant();
        var key = !profile.IsService ? "IcoExternal"
            : name.Contains("игр") || name.Contains("звонк") ? "IcoGame"
            : name.Contains("бел") || name.Contains("обход") ? "IcoRoute"
            : null;
        return key is null ? null : Application.Current?.TryFindResource(key) as Geometry;
    }

    public static string? NoteFor(Profile profile) => !profile.IsService ? "сторонняя подписка" : null;
}

// Заголовок группы в панели «Все серверы»: «Избранное», «Облачка», «Сторонние».
public sealed record SheetHeader(string Title);

// Строка правила во вкладке «Правила»: адрес или программа и метка действия.
public sealed class RuleRow
{
    public required object Rule { get; init; }
    public required string Text { get; init; }
    public SiteAction Action { get; init; }
    // У программы из каталога: значок, человеческое имя в Text и имя файла под ним.
    public ImageSource? Icon { get; init; }
    public string? Sub { get; init; }
    public bool Plain => Sub is not null;
    public Visibility IconVisibility => Icon is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility SubVisibility => Sub is null ? Visibility.Collapsed : Visibility.Visible;
    public string ActionText => ActionLabel(Action);
    public static string ActionLabel(SiteAction action) => action switch { SiteAction.Direct => "напрямую", SiteAction.Block => "блок", _ => "через VPN" };
}
