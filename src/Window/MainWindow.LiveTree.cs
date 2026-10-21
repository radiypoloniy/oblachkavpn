using Oblachka.Views;

namespace Oblachka;

// «Сейчас в сети» деревом: программа → её файлы → сайты. Строка — то, что возьмёт одно правило
// программы (AppRuleScope): Steam, steamwebhelper и игра из его библиотеки — одна строка, а в ней
// файлы по отдельности, со своим правилом «только этот файл». Сайты — с кем программа на связи,
// со скоростью и «Закрыть». После правила список несколько секунд обновляется чаще: соединения
// переезжают на новый путь за секунду-две, и это должно быть видно сразу.
public partial class MainWindow
{
    private readonly LiveSpeed liveSpeed = new();
    private readonly Dictionary<string, FileRow> liveFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SiteRow> liveSites = new(StringComparer.OrdinalIgnoreCase);
    private DateTime liveBurstUntil;
    private const int LiveSitesShown = 8;

    private void StartLiveBurst() { liveBurstUntil = DateTime.UtcNow.AddSeconds(8); LivePace(); }

    // Ключ строки — правило, которое возьмёт программа: файлы одной программы — одна строка.
    private string LiveKey(string path) =>
        !SystemFile(path) && TryScope(path) is { } scope ? string.Join('|', scope.Rules.Select(r => r.Key)) : path;

    private ConnectionRow LiveRow(LiveApp app) => LiveTree([app])[0];

    private IReadOnlyList<ConnectionRow> LiveTree(IReadOnlyList<LiveApp> apps)
    {
        var result = new List<ConnectionRow>();
        foreach (var group in apps.GroupBy(a => LiveKey(a.Path), StringComparer.OrdinalIgnoreCase))
        {
            if (!liveRows.TryGetValue(group.Key, out var row))
            {
                // Имя, значок и правило — от программы-владельца (Steam), а не от первого попавшегося файла.
                var lead = LiveOwner(group.First().Path).Path;
                row = new ConnectionRow(lead) { Name = LiveName(lead), Icon = AppIcons.For(lead), Configurable = !SystemFile(lead) && TryScope(lead) is not null };
                liveRows[group.Key] = row;
            }
            UpdateLiveRow(row, group.ToArray());
            result.Add(row);
        }
        // Заблокированная программа соединений не открывает и пропала бы из списка — а снять
        // блок удобнее всего там же, где его поставили. Такие остаются, пока окно открыто.
        foreach (var row in liveRows.Values.Where(r => r.Rule == SiteAction.Block && !result.Contains(r)).ToArray())
        {
            UpdateLiveRow(row, []);
            result.Add(row);
        }
        return result;
    }

    private void UpdateLiveRow(ConnectionRow row, LiveApp[] parts)
    {
        int vpn = parts.Sum(p => p.Vpn), direct = parts.Sum(p => p.Direct), total = vpn + direct;
        row.ShowRoute(vpn, direct);
        row.Summary = total == 0 ? "в сеть не выходит" : Joined(LiveRouteRow.Count(total), LiveRouteRow.Speed(parts.Sum(p => p.Speed)));
        row.Remotes = string.Join(" · ", parts.SelectMany(p => p.Remotes).Distinct().Take(3));
        ShowLiveRule(row);
        // Правило меняет только новые соединения: уже открытые идут прежним путём, пока не закроются.
        row.Pending = SystemTunnelActive && row.RouteKind != "switch" && row.Rule is { } action &&
            (action == SiteAction.Direct ? vpn : action == SiteAction.Tunnel ? direct : total) > 0
            ? "Соединения, открытые до правила, Облачка обрывает — программа переподключится уже по правилу. Голос и видео (UDP) переходят, когда звонок закончится." : "";
        var scope = SystemFile(row.Path) ? null : TryScope(row.Path);
        row.Scope = SystemFile(row.Path) ? "Системные программы Windows правилами не настраиваются: правило задело бы службы самой системы."
            : scope is not null ? ScopeText(scope.Value.Rules, scope.Value.Owner)
            : "Для этой программы правило не поставить: у её файла необычное имя.";
        row.Folders = scope?.Rules is { } rules && rules.All(r => r.AllInFolder) ? rules.Select(r => r.Folder!).ToArray() : [];
        row.Files = parts.Length > 1 ? parts.OrderByDescending(p => p.Total).Select(LiveFile).ToArray() : [];
        row.Sites = parts.SelectMany(p => p.Sites).GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.Key, Vpn: g.Sum(s => s.Vpn), Direct: g.Sum(s => s.Direct), Speed: g.Sum(s => s.Speed), Ids: g.SelectMany(s => s.Ids).ToArray()))
            .OrderByDescending(s => s.Speed).ThenByDescending(s => s.Vpn + s.Direct).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Take(LiveSitesShown).Select(s => LiveSite(row, s.Name, s.Vpn, s.Direct, s.Speed, s.Ids)).ToArray();
        foreach (var site in row.Sites) ShowSiteRules(row, site);
    }

    private FileRow LiveFile(LiveApp part)
    {
        if (!liveFiles.TryGetValue(part.Path, out var file))
        {
            var configurable = !SystemFile(part.Path);
            try { ProgramRoutingPlan.Create(part.Path, SiteAction.Tunnel, allInFolder: false); }
            catch (UserError) { configurable = false; }
            liveFiles[part.Path] = file = new FileRow(part.Path) { Name = Path.GetFileName(part.Path), Configurable = configurable };
        }
        file.ShowRoute(part.Vpn, part.Direct);
        file.Rule = FileRule(part.Path)?.Action;
        file.Summary = Joined(LiveRouteRow.Count(part.Total), file.Route, LiveRouteRow.Speed(part.Speed), file.Rule is not null ? "своё правило" : "");
        return file;
    }

    private SiteRow LiveSite(ConnectionRow row, string name, int vpn, int direct, long speed, string[] ids)
    {
        var key = row.Path + "|" + name;
        if (!liveSites.TryGetValue(key, out var site)) liveSites[key] = site = new SiteRow(name);
        site.ShowRoute(vpn, direct);
        site.Detail = Joined(vpn + direct > 1 ? "×" + (vpn + direct) : "", site.Route, LiveRouteRow.Speed(speed));
        site.Ids = ids;
        return site;
    }

    private static string Joined(params string[] parts) => string.Join(" · ", parts.Where(p => p.Length > 0));

    // Правило всей программы — то, что ставит строка (её охват), иначе правило по имени или папке.
    // Правило на отдельный файл показывается у файла, а не у программы.
    private ProgramRule? AppRule(ConnectionRow row)
    {
        var keys = TryScope(row.Path)?.Rules.Select(r => r.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return programRules.FirstOrDefault(r => keys?.Contains(r.Key) == true)
            ?? (ProgramRoutingPlan.Find(programRules, row.Path) is { } found && (found.Folder is null || found.AllInFolder) ? found : null);
    }

    private ProgramRule? FileRule(string path) => programRules.FirstOrDefault(r =>
        r.Domain is null && r.Folder is not null && !r.AllInFolder && string.Equals(r.FilePath, path, StringComparison.OrdinalIgnoreCase));

    private void ApplyFileRule(FileRow file, SiteAction? action)
    {
        if (!file.Configurable) return;
        if (action is null) { if (FileRule(file.Path) is { } rule) RemoveRule(rule); }
        else
        {
            try { if (!AddProgramRules([ProgramRoutingPlan.Create(file.Path, action.Value, allInFolder: false)])) return; }
            catch (UserError ex) { DetailText.Text = ex.Message; return; }
            if (SystemTunnelActive) file.SwitchTo(action.Value);
        }
        file.Rule = FileRule(file.Path)?.Action;
        StartLiveBurst();
        _ = RefreshConnectionsAsync();
        if (!SystemTunnelActive) DetailText.Text = "Правило сохранено. Заработает в режиме «Весь компьютер».";
    }

    // «Закрыть» у сайта или программы: ядро рвёт соединения, программа переподключается заново.
    private async Task CloseLiveAsync(IReadOnlyCollection<string> ids)
    {
        if (ids.Count == 0 || systemTunnel?.Stats is not { } stats) return;
        await CoreConnections.CloseIdsAsync(stats, ids.ToHashSet(StringComparer.Ordinal), CancellationToken.None);
        StartLiveBurst();
        await RefreshConnectionsAsync();
    }
}
