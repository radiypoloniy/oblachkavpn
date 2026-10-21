using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Oblachka.Views;

// Общее у строк «Сейчас в сети»: куда идут соединения сейчас (плашка) и смена пути после правила.
// Плашка показывает факт, а соединения переезжают на новый путь за секунду-две: программе надо
// переподключиться. Чтобы нажатие не выглядело пропущенным, сразу видно «→ через VPN…», пока
// соединения не переедут (или 8 секунд — у программы могут быть и сайты с правилом «напрямую»).
public abstract class LiveRouteRow : INotifyPropertyChanged
{
    private string route = "", routeKind = "";
    private SiteAction? switchTo;
    private DateTime switchUntil;
    public string Route { get => route; set => Set(ref route, value, nameof(Route)); }
    // vpn, direct, mix, block и switch — смена пути после правила.
    public string RouteKind { get => routeKind; set => Set(ref routeKind, value, nameof(RouteKind)); }
    public int Vpn { get; set; }
    public int Direct { get; set; }

    public void SwitchTo(SiteAction action)
    {
        switchTo = action; switchUntil = DateTime.UtcNow.AddSeconds(8);
        ShowRoute(Vpn, Direct);
    }

    public void ShowRoute(int vpn, int direct)
    {
        Vpn = vpn; Direct = direct;
        if (switchTo is { } target && (DateTime.UtcNow > switchUntil || Arrived(target, vpn, direct))) switchTo = null;
        (Route, RouteKind) = switchTo is { } next ? ("→ " + Label(next) + "…", "switch")
            : vpn + direct == 0 ? ("заблокирована", "block") : direct == 0 ? ("через VPN", "vpn") : vpn == 0 ? ("напрямую", "direct") : ($"VPN {vpn} · напрямую {direct}", "mix");
    }

    private static bool Arrived(SiteAction target, int vpn, int direct) => target switch
    {
        SiteAction.Tunnel => vpn > 0 && direct == 0,
        SiteAction.Direct => direct > 0 && vpn == 0,
        _ => vpn + direct == 0
    };

    private static string Label(SiteAction action) => action switch { SiteAction.Tunnel => "через VPN", SiteAction.Direct => "напрямую", _ => "блок" };

    // 3,1 МБ/с; меньше килобайта в секунду — пусто: фоновые соединения не отвлекают.
    public static string Speed(long bytes) => bytes < 1024 ? ""
        : bytes < 1024 * 1024 ? (bytes / 1024).ToString(CultureInfo.CurrentCulture) + " КБ/с"
        : (bytes / 1048576.0).ToString(bytes < 10 * 1048576 ? "0.0" : "0", CultureInfo.CurrentCulture) + " МБ/с";

    public static string Count(int n) =>
        n + (n % 10 == 1 && n % 100 != 11 ? " соединение" : n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14 ? " соединения" : " соединений");

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Set(ref string field, string value, params string[] names)
    {
        if (field == value) return;
        field = value;
        foreach (var n in names) Changed(n);
    }
    protected void Changed(string property) => PropertyChanged?.Invoke(this, new(property));
}

// Файл программы внутри строки (steam.exe, steamwebhelper.exe, игра): правило только на этот файл.
public sealed class FileRow(string path) : LiveRouteRow
{
    private string name = "", summary = "";
    private SiteAction? rule;
    public string Path { get; } = path;
    public string Name { get => name; set => Set(ref name, value, nameof(Name)); }
    // «3 соединения · через VPN · 1,2 МБ/с»
    public string Summary { get => summary; set => Set(ref summary, value, nameof(Summary)); }
    public bool Configurable { get; set; } = true;
    public SiteAction? Rule
    {
        get => rule;
        set { if (rule == value) return; rule = value; foreach (var n in new[] { nameof(RuleVpn), nameof(RuleDirect), nameof(RuleBlock), nameof(RemoveVisibility) }) Changed(n); }
    }
    public bool RuleVpn => rule == SiteAction.Tunnel;
    public bool RuleDirect => rule == SiteAction.Direct;
    public bool RuleBlock => rule == SiteAction.Block;
    public Visibility PickVisibility => Configurable ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RemoveVisibility => rule is null ? Visibility.Collapsed : Visibility.Visible;
    public override string ToString() => name;
}

// Сайт или адрес, с которым программа на связи: сколько соединений, куда, скорость; «Закрыть».
public sealed class SiteRow(string name) : LiveRouteRow
{
    private string detail = "";
    private string[] ids = [];
    public string Name { get; } = name;
    public string Detail { get => detail; set => Set(ref detail, value, nameof(Detail)); }
    public string[] Ids { get => ids; set { ids = value; Changed(nameof(CloseVisibility)); } }
    public Visibility CloseVisibility => ids.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public override string ToString() => Name;
}
