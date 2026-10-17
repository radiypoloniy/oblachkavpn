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
        // Окно в фоне — список реже: каждое обновление перерисовывает строки, а смотрят на него редко.
        Activated += (_, _) => LivePace();
        Deactivated += (_, _) => LivePace();
    }

    private void LivePace() { if (liveTimer is not null) liveTimer.Interval = TimeSpan.FromSeconds(IsActive ? 2 : 6); }

    // «Сейчас в сети» — раздел острова: настройки и «Все серверы» уступают ему место.
    private void ShowLiveSection()
    {
        Settings.Visibility = Visibility.Collapsed; Picker.Visibility = Visibility.Collapsed; Servers.Visibility = Visibility.Collapsed;
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
            var apps = await Task.Run(() => LiveApps.Group(ConnectionTable.Read(), tunnel, proxy, LivePath,
                path => path.StartsWith(own, StringComparison.OrdinalIgnoreCase)));
            if (closed) return;
            Live.SetNote(LiveNote);
            Live.Load(apps.Select(LiveRow).ToArray());
            Home.LiveCount.Text = apps.Count > 0 ? apps.Count.ToString() : "";
        }
        // Список — справочный: сбой чтения таблиц или одной строки не должен ронять окно.
        catch (Exception ex) when (ex is not OutOfMemoryException) { Live.Load([]); }
        finally { liveBusy = false; }
    }

    private ConnectionRow LiveRow(LiveApp app)
    {
        if (!liveRows.TryGetValue(app.Path, out var row))
        {
            // Правило можно поставить, только если путь программы годится для правила: необычное
            // имя файла (claude.exe.old.123, символы вне допустимых) — без кнопок, с пояснением.
            row = new ConnectionRow(app.Path) { Name = LiveName(app.Path), Icon = AppIcons.For(app.Path), Configurable = !SystemFile(app.Path) && TryScope(app.Path) is not null };
            liveRows[app.Path] = row;
        }
        row.Summary = (app.Vpn > 0 ? "через VPN " + app.Vpn : "") + (app.Vpn > 0 && app.Direct > 0 ? " · " : "") + (app.Direct > 0 ? "напрямую " + app.Direct : "");
        row.Remotes = "→ " + string.Join(", ", app.Remotes);
        ShowLiveRule(row);
        // Правило меняет только новые соединения: уже открытые идут прежним путём, пока не закроются.
        if (SystemTunnelActive && row.Rule is { } action && (action == SiteAction.Direct ? app.Vpn : action == SiteAction.Tunnel ? app.Direct : app.Total) > 0)
            row.Summary += " · открыты до правила";
        row.Scope = SystemFile(app.Path) ? "Системные программы Windows правилами не настраиваются: правило задело бы службы самой системы."
            : TryScope(app.Path) is { } scope ? ScopeText(scope)
            : "Для этой программы правило не поставить: у её файла необычное имя.";
        return row;
    }

    private void ShowLiveRule(ConnectionRow row)
    {
        var rule = ProgramRoutingPlan.Find(programRules, row.Path);
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
    private ProgramRule[] LiveScope(string path, SiteAction action)
    {
        var owner = appCatalog?.Select(a => a.App).Where(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase) ||
                a.Folders?.Any(f => path.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase)) == true)
            .OrderByDescending(a => a.Folders?.Where(f => path.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase)).Max(f => f.Length) ?? 0).FirstOrDefault();
        return AppRuleScope.For(owner ?? new InstalledApp(LiveName(path), Path.GetFileName(path), path, true), action);
    }

    // Охват правила для строки списка — раз на программу и каталог: разбор пути с проверкой
    // папок на диске для каждой строки раз в 2 секунды заметно грузил процессор.
    private readonly Dictionary<string, (object? Catalog, ProgramRule[]? Scope)> liveScopes = new(StringComparer.OrdinalIgnoreCase);
    private ProgramRule[]? TryScope(string path)
    {
        if (liveScopes.TryGetValue(path, out var known) && ReferenceEquals(known.Catalog, appCatalog)) return known.Scope;
        ProgramRule[]? scope;
        try { scope = LiveScope(path, SiteAction.Tunnel); }
        catch (UserError) { scope = null; }
        liveScopes[path] = (appCatalog, scope);
        return scope;
    }

    private static string ScopeText(ProgramRule[] rules) => rules.Length > 1 ? "Правило возьмёт папки: " + string.Join(", ", rules.Select(r => ShortPath(r.Folder!)))
        : rules[0].AllInFolder ? "Правило возьмёт всю папку " + ShortPath(rules[0].Folder!) + " — вместе со службами и помощниками программы."
        : "Правило возьмёт только этот файл: его папка слишком общая.";

    private void ApplyLiveRule(ConnectionRow row, SiteAction? action)
    {
        if (!row.Configurable) return;
        if (action is null)
        {
            if (ProgramRoutingPlan.Find(programRules, row.Path) is { } rule) RemoveRule(rule);
        }
        else
        {
            try { AddProgramRules(LiveScope(row.Path, action.Value)); }
            catch (UserError ex) { DetailText.Text = ex.Message; return; }
        }
        ShowLiveRule(row);
        row.Expanded = false;
        _ = RefreshConnectionsAsync();
        if (!SystemTunnelActive) DetailText.Text = "Правило сохранено. Заработает в режиме «Весь компьютер».";
    }
}
