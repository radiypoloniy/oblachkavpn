using System.Windows;
using System.Windows.Media;

namespace Oblachka.Views;

// Строка «Сейчас в сети»: программа (все её файлы — одним правилом), куда идёт её трафик сейчас,
// какое правило на неё действует. Раскрытая — файлы программы и сайты, с которыми она на связи.
public sealed class ConnectionRow(string path) : LiveRouteRow
{
    private bool expanded, foldersShown;
    private string name = "", summary = "", remotes = "", scope = "", ruleText = "", pending = "", observation = "";
    private string[] folders = [];
    private FileRow[] files = [];
    private const int SitePageSize = 12;
    private SiteRow[] sites = [], visibleSites = [];
    private string[] connectionIds = [];
    private string siteQuery = "";
    private int siteLimit = SitePageSize, siteMatches;
    private SiteAction? rule;
    private ImageSource? icon;
    public string Path { get; } = path;
    public string Name { get => name; set => Set(ref name, value, nameof(Name), nameof(Initial)); }
    // «23 соединения · 3,1 МБ/с» — под именем.
    public string Summary { get => summary; set => Set(ref summary, value, nameof(Summary)); }
    public string Observation { get => observation; set => Set(ref observation, value, nameof(Observation)); }
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
    // Файлы показываются, только когда их больше одного: у Chrome и Telegram это лишняя строка.
    public FileRow[] Files { get => files; set { if (files.SequenceEqual(value)) return; files = value; Changed(nameof(Files)); Changed(nameof(FilesVisibility)); } }
    public SiteRow[] Sites
    {
        get => sites;
        set
        {
            if (sites.SequenceEqual(value)) return;
            sites = value;
            FilterSites();
            Changed(nameof(SitesVisibility));
        }
    }
    public string SiteQuery
    {
        get => siteQuery;
        set
        {
            if (siteQuery == value) return;
            siteQuery = value;
            siteLimit = SitePageSize;
            FilterSites();
        }
    }
    public IReadOnlyList<SiteRow> VisibleSites => visibleSites;
    public string SiteHeading => siteMatches == sites.Length ? $"Сайты и адреса ({sites.Length})"
        : $"Сайты и адреса ({siteMatches} из {sites.Length})";
    public string MoreSitesLabel => $"Показать ещё ({siteMatches - visibleSites.Length})";
    public Visibility MoreSitesVisibility => siteMatches > visibleSites.Length ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoSiteMatchesVisibility => siteMatches == 0 && sites.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public void ShowMoreSites() { siteLimit += SitePageSize; FilterSites(); }

    private void FilterSites()
    {
        var query = siteQuery.Trim();
        var matches = sites.Where(site => site.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        siteMatches = matches.Length;
        var next = matches.Take(siteLimit).ToArray();
        if (!visibleSites.SequenceEqual(next)) { visibleSites = next; Changed(nameof(VisibleSites)); }
        Changed(nameof(SiteHeading)); Changed(nameof(MoreSitesLabel)); Changed(nameof(MoreSitesVisibility)); Changed(nameof(NoSiteMatchesVisibility));
    }
    public bool FoldersShown { get => foldersShown; set { if (foldersShown == value) return; foldersShown = value; Changed(nameof(FoldersLink)); Changed(nameof(FoldersVisibility)); } }
    public string FoldersLink => foldersShown ? "Скрыть" : folders.Length == 1 ? "Показать папку" : $"Показать папки ({folders.Length})";
    // Соединения, открытые до правила, доживают прежним путём.
    public string Pending { get => pending; set => Set(ref pending, value, nameof(Pending), nameof(PendingVisibility)); }
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
    public Visibility FilesVisibility => files.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SitesVisibility => sites.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string[] ConnectionIds
    {
        get => connectionIds;
        set
        {
            var hadConnections = connectionIds.Length > 0;
            connectionIds = value;
            if (hadConnections != (value.Length > 0)) Changed(nameof(CloseAllVisibility));
        }
    }
    public Visibility CloseAllVisibility => connectionIds.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PendingVisibility => pending.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PlaceholderVisibility => icon is null ? Visibility.Visible : Visibility.Collapsed;
    public string Initial => name.Length > 0 ? char.ToUpperInvariant(name[0]).ToString() : "?";
    // Имя элемента списка для экранного диктора и UIA (иначе там имя класса).
    public override string ToString() => name;
}
