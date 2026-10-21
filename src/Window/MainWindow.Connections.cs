using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Oblachka.Views;

namespace Oblachka;

// «Сейчас в сети»: программы с соединениями прямо сейчас и правило для каждой в один клик.
// Список — из таблиц соединений Windows (ConnectionTable), раз в 2 секунды, пока экран открыт.
// Правило берёт программу целиком (AppRuleScope) и под «Весь компьютер» действует сразу
// (MainWindow.LiveRules) — для новых соединений: уже открытые доживают по старому пути.
public partial class MainWindow
{
    private DispatcherTimer? liveTimer;
    private readonly Dictionary<string, ConnectionRow> liveRows = new(StringComparer.OrdinalIgnoreCase);
    private bool liveBusy;

    private void WireConnections()
    {
        Routes.LiveButton.Click += (_, _) => ShowLiveSection();
        Home.LiveNav.Click += (_, _) => ShowLiveSection();
        // «Назад» есть только в узком окне: там раздел закрывается, и видна боковая панель.
        Live.Closed += () => { CloseConnections(); narrowOpen = false; ApplyLayout(); Home.LiveNav.Focus(); };
        Live.ActionChosen += ApplyLiveRule;
        Live.FileActionChosen += ApplyFileRule;
        Live.CloseChosen += ids => _ = CloseLiveAsync(ids);
        // Окно в фоне — список реже: каждое обновление перерисовывает строки, а смотрят на него редко.
        Activated += (_, _) => LivePace();
        Deactivated += (_, _) => LivePace();
    }

    // После правила или «Закрыть» — дважды в секунду, пока соединения переезжают (StartLiveBurst).
    private void LivePace()
    {
        if (liveTimer is null) return;
        var next = TimeSpan.FromSeconds(DateTime.UtcNow < liveBurstUntil ? 0.5 : IsActive ? 2 : 6);
        if (liveTimer.Interval != next) liveTimer.Interval = next;
    }

    // «Сейчас в сети» — раздел острова: настройки и «Все серверы» уступают ему место.
    private void ShowLiveSection()
    {
        Settings.Visibility = Visibility.Collapsed; Picker.Visibility = Visibility.Collapsed; Servers.Visibility = Visibility.Collapsed; RulesPage.Visibility = Visibility.Collapsed;
        Doctor.Visibility = Visibility.Collapsed; SiteCheck.Visibility = Visibility.Collapsed;
        if (currentPage is "Routes" or "Diagnostics" or "Servers") currentPage = "Connection";
        OpenConnections(focus: true);
        OpenSection();
    }

    private string LiveNote => SystemTunnelActive ? "Правило действует сразу — для новых соединений программы."
        : engine.State is not ConnectionState.Idle ? "Сейчас режим «Браузеры и программы»: через VPN идут только программы, которые слушают системный прокси. Правила программ работают в режиме «Весь компьютер»."
        : "VPN выключен — всё идёт напрямую. Правила программ заработают при подключении в режиме «Весь компьютер».";

    // Путь процесса по PID — один раз: OpenProcess на каждый PID раз в 2 секунды заметен.
    // Кэш сбрасывается раз в минуту, чтобы номер, доставшийся новой программе, не врал долго.
    private readonly Dictionary<int, string?> livePaths = new();
    private DateTime livePathsReset = DateTime.UtcNow;
    private string? LivePath(int pid)
    {
        lock (livePaths)
        {
            if (DateTime.UtcNow - livePathsReset > TimeSpan.FromMinutes(1)) { livePaths.Clear(); livePathsReset = DateTime.UtcNow; }
            if (!livePaths.TryGetValue(pid, out var path)) livePaths[pid] = path = ConnectionTable.ProcessPath(pid);
            return path;
        }
    }

    private void OpenConnections(bool focus)
    {
        Live.Show(focus, LiveNote);
        LoadAppCatalog();
        liveTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, async (_, _) => await RefreshConnectionsAsync(), Dispatcher);
        LivePace();
        liveTimer.Start();
        _ = RefreshConnectionsAsync();
    }

    private void CloseConnections()
    {
        liveTimer?.Stop();
        Live.Visibility = Visibility.Collapsed;
        liveFiles.Clear(); liveSites.Clear();
    }

    private async Task RefreshConnectionsAsync()
    {
        // Окно свёрнуто или спрятано в трей — таблицы соединений никто не видит, читать их незачем.
        if (liveBusy || Live.Visibility != Visibility.Visible || closed || !IsVisible || WindowState == WindowState.Minimized) return;
        liveBusy = true;
        try
        {
            var own = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory) + "\\";
            var tunnel = SystemTunnelActive; var proxy = engine.Port;
            bool Own(string path) => path.StartsWith(own, StringComparison.OrdinalIgnoreCase);
            // «Весь компьютер»: куда ушло соединение, знает только ядро туннеля — по таблицам Windows
            // любое соединение открыто с адреса туннеля, даже по правилу «напрямую».
            var core = tunnel && systemTunnel?.Stats is { } stats ? await CoreConnections.ReadAsync(stats, CancellationToken.None) : null;
            var apps = core is not null ? LiveApps.FromCore(core, Own, liveSpeed.Measure(core, DateTime.UtcNow))
                : await Task.Run(() => LiveApps.Group(ConnectionTable.Read(), tunnel, proxy, LivePath, Own));
            if (closed) return;
            Live.SetNote(LiveNote);
            var rows = LiveTree(apps);
            Live.Load(rows);
            var online = rows.Count(r => r.Vpn + r.Direct > 0);
            Home.LiveCount.Text = online > 0 ? online.ToString() : "";
        }
        // Список — справочный: сбой чтения таблиц или одной строки не должен ронять окно.
        catch (Exception ex) when (ex is not OutOfMemoryException) { Live.Load([]); }
        finally { liveBusy = false; LivePace(); }
    }

    private void ShowLiveRule(ConnectionRow row)
    {
        var rule = AppRule(row);
        row.Rule = rule?.Action;
        row.RuleText = rule is null ? "" : RuleRow.ActionLabel(rule.Action);
    }

    private string LiveName(string path)
    {
        if (appCatalog?.FirstOrDefault(a => string.Equals(a.App.Path, path, StringComparison.OrdinalIgnoreCase)) is { } app) return app.Name;
        try { if (FileVersionInfo.GetVersionInfo(path).FileDescription is { Length: > 0 } description) return description.Trim(); }
        catch (FileNotFoundException) { }
        return Path.GetFileNameWithoutExtension(path);
    }

    private static bool SystemFile(string path) =>
        path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows) + "\\", StringComparison.OrdinalIgnoreCase);

    // Программа из каталога, в чью папку входит этот файл (игра Steam, сам Steam со всеми библиотеками),
    // иначе — папка самого файла.
    private ProgramRule[] LiveScope(string path, SiteAction action) => AppRuleScope.For(LiveOwner(path), action);

    private InstalledApp LiveOwner(string path) =>
        appCatalog?.Select(a => a.App).Where(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase) ||
                a.Folders?.Any(f => path.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase)) == true)
            .OrderByDescending(a => a.Folders?.Where(f => path.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase)).Max(f => f.Length) ?? 0).FirstOrDefault()
        ?? new InstalledApp(LiveName(path), Path.GetFileName(path), path, true);

    // Охват правила для строки списка — раз на программу и каталог: разбор пути с проверкой
    // папок на диске для каждой строки раз в 2 секунды заметно грузил процессор.
    private readonly Dictionary<string, (object? Catalog, (ProgramRule[] Rules, string Owner)? Scope)> liveScopes = new(StringComparer.OrdinalIgnoreCase);
    private (ProgramRule[] Rules, string Owner)? TryScope(string path)
    {
        if (liveScopes.TryGetValue(path, out var known) && ReferenceEquals(known.Catalog, appCatalog)) return known.Scope;
        (ProgramRule[], string)? scope;
        try { var owner = LiveOwner(path); scope = (AppRuleScope.For(owner, SiteAction.Tunnel), owner.Name); }
        catch (UserError) { scope = null; }
        liveScopes[path] = (appCatalog, scope);
        return scope;
    }

    // Одной строкой, без списка путей: папки — по ссылке «Показать папки» под ней.
    private static string ScopeText(ProgramRule[] rules, string owner) =>
        rules.Length > 1 ? $"Правило возьмёт {owner} целиком · {Folders(rules.Length)} — со всеми службами и помощниками."
        : rules[0].AllInFolder ? "Правило возьмёт папку программы целиком — вместе со службами и помощниками."
        : "Правило возьмёт только этот файл: его папка слишком общая.";

    private static string Folders(int n) => n + (n % 10 == 1 && n % 100 != 11 ? " папка" : n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14 ? " папки" : " папок");

    private void ApplyLiveRule(ConnectionRow row, SiteAction? action)
    {
        if (!row.Configurable) return;
        if (action is null)
        {
            // Правило программы — иногда несколько папок (Steam и его библиотеки): убираются все разом.
            var keys = TryScope(row.Path)?.Rules.Select(r => r.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var scoped = programRules.Where(r => keys?.Contains(r.Key) == true).ToArray();
            if (scoped.Length > 0) { programRules.RemoveAll(scoped.Contains); RefreshRules(); QueueStateSave(); ProgramRulesChanged(); }
            else if (AppRule(row) is { } rule) RemoveRule(rule);
        }
        else
        {
            try { if (!AddProgramRules(LiveScope(row.Path, action.Value))) return; }
            catch (UserError ex) { DetailText.Text = ex.Message; return; }
            // Плашка сразу показывает новый путь: «→ через VPN…», пока соединения переезжают.
            if (SystemTunnelActive) row.SwitchTo(action.Value);
        }
        ShowLiveRule(row);
        row.Expanded = false;
        StartLiveBurst();
        _ = RefreshConnectionsAsync();
        if (!SystemTunnelActive) DetailText.Text = "Правило сохранено. Заработает в режиме «Весь компьютер».";
    }
}
