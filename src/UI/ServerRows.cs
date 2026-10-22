using System.Windows;
using System.Windows.Media;

namespace Oblachka;

// Строка списка серверов нового главного экрана и панели «Все серверы».
//
// Число справа — свежий замер через сервер (ServerMeasurements). Отклик входа (ServerPing,
// TCP-рукопожатие) числом не показывается: у каскадов он единицы мс при настоящих сотнях.
// Цвет точки (токены LatFast/LatMid/LatSlow/LatFail): < 160 мс, < 250 мс, дольше; нет ответа;
// результат старше 20 минут — «давно» с бледной точкой, подробности в подсказке.
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
    public string? LatencyHint { get; init; }
    // Скорость через сервер под задержкой («6,2 МБ/с»); только в «Всех серверах».
    public string Speed { get; init; } = "";
    public bool Selected { get; init; }
    public bool Favorite { get; init; }
    public bool CanFavorite => !IsAuto;

    public Visibility FlagVisibility => Flag is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility IconVisibility => Flag is null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoteVisibility => string.IsNullOrEmpty(Note) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility LatencyVisibility => string.IsNullOrEmpty(Latency) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility SpeedVisibility => string.IsNullOrEmpty(Speed) ? Visibility.Collapsed : Visibility.Visible;
    public FontWeight NameWeight => Selected ? FontWeights.SemiBold : FontWeights.Normal;


    // Отметка справа: текст, цвет точки и подсказка. checking — идёт проверка этого сервера.
    public static (string Text, Brush? Dot, string? Hint) Status(ServerMeasurement? measured, DateTimeOffset now, bool checking = false)
    {
        if (checking) return ("проверяем…", Faint, "Запрос через этот сервер отдельным ядром");
        if (measured is null) return ("", null, null);
        var at = measured.Time.ToLocalTime();
        if (!ServerMeasurements.Fresh(measured, now))
            return ("давно", Faint, (measured is { Success: true, Handshake: false } ? $"Последний замер — {measured.Milliseconds} мс" : measured.Success ? "Отвечал" : "В последний раз не ответил") +
                ", " + at.ToString(at.Date == now.ToLocalTime().Date ? "HH:mm" : "dd.MM HH:mm") + ". Проверьте снова.");
        if (measured is { Success: true, Handshake: true })
            return ("", null, $"Сервер отвечает ({at:HH:mm}). Задержку через него покажет «Обновить пинг».");
        return measured.Success
            ? (measured.Milliseconds + " мс", DotFor(measured.Milliseconds), $"Ответил за {measured.Milliseconds} мс в {at:HH:mm}" +
                (measured.Throughput > 0 ? $", скорость {measured.Throughput / 1_000_000.0:0.#} МБ/с" : ""))
            : ("нет ответа", Failed, $"Не ответил при проверке в {at:HH:mm}");
    }

    // Скорость свежего замера: «6,2 МБ/с», «850 КБ/с»; пусто — не мерили или замер старый.
    public static string SpeedText(ServerMeasurement? measured, DateTimeOffset now)
    {
        if (measured is not { Success: true, Handshake: false, Throughput: > 0 } || !ServerMeasurements.Fresh(measured, now)) return "";
        var ru = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
        return measured.Throughput >= 1_000_000
            ? (measured.Throughput / 1_000_000.0).ToString(measured.Throughput >= 10_000_000 ? "0" : "0.#", ru) + " МБ/с"
            : (measured.Throughput / 1000).ToString(ru) + " КБ/с";
    }

    private static Brush? Faint => Application.Current?.TryFindResource("Fa") as Brush;
    private static Brush? Failed => Tokens.Brush("LatFail");

    public static Brush? DotFor(long? milliseconds) => milliseconds switch
    {
        null => Application.Current?.TryFindResource("Fa") as Brush,
        < 160 => Tokens.Brush("LatFast"),
        < 250 => Tokens.Brush("LatMid"),
        _ => Tokens.Brush("LatSlow")
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

// Строка «Настройки → Подписки и ключ»: подписка или «Вручную».
public sealed class GroupRow
{
    public required ProfileGroup Group { get; init; }
    public required string Title { get; init; }
    public required string Sub { get; init; }
    public Visibility RefreshVisibility => Group.IsSubscription ? Visibility.Visible : Visibility.Collapsed;
    public string RefreshName => "Обновить «" + Title + "»";
    public string RemoveName => "Убрать «" + Title + "»";
}

// Заголовок группы в панели «Все серверы»: «Избранное», затем подписки по именам.
public sealed record SheetHeader(string Title, string? GroupId = null, bool CanRefresh = false, int Hidden = 0, int Junk = 0)
{
    public Visibility ActionsVisibility => GroupId is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility RefreshVisibility => CanRefresh ? Visibility.Visible : Visibility.Collapsed;
    public Visibility UnhideVisibility => Hidden > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string UnhideText => $"Вернуть скрытые ({Hidden})";
    // Серверы без ответа на свежей проверке и повторы (MainWindow.Junk).
    public Visibility JunkVisibility => Junk > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string JunkText => $"Скрыть лишние ({Junk})";
    public string RemoveName => "Убрать «" + Title + "»";
}

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
