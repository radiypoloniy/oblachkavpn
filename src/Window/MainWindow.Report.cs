namespace Oblachka;

// «Сообщить о проблеме»: отчёт для поддержки. Кроме состояния подключения в нём настройки,
// серверы с замерами (без имён и адресов), программы в сети с путём их трафика и правилом,
// журнал zapret и последняя ошибка окна. Без него жалобу «игра не работала» или «zapret пишет,
// что не работает» не разобрать. Сайтов и доменов в отчёте нет — это история человека;
// пути программ — без имени пользователя (DiagnosticReport.SafePath).
public partial class MainWindow
{
    private async Task<string> CreateSupportReportAsync() => CreateSupportReport(await ReportProgramsAsync());

    // programs — снимок «Сейчас в сети» (ReportProgramsAsync); null — без него.
    private string CreateSupportReport(object? programs = null)
    {
        var details = new
        {
            Windows = Environment.OSVersion.Version.ToString(),
            Settings = new
            {
                Mode = SystemTunnelWanted ? "SystemTunnel" : "BrowsersAndPrograms", LocalNetwork = Settings.LocalNetwork.IsChecked == true,
                QuicOverTcp = Settings.QuicOverTcp.IsChecked == true, CompatibleStack = Settings.CompatibleStack.IsChecked == true, RussiaDirect = Settings.RussiaDirect.IsChecked == true,
                AutoSelect = autoSelect, AutoRecover = AutoRecover.IsChecked == true, SelectiveVpn = selectiveVpn,
                ProgramRules = programRules.Count, SiteRules = siteRules.Count,
                // «Для продвинутых»: что изменено, без значений — в них бывают адреса рабочей сети.
                Advanced = advanced.Changed()
            },
            Servers = ReportServers(),
            ProgramRules = programRules.Take(60).Select(r => new
            {
                Target = DiagnosticReport.SafePath(r.TargetKey), Action = r.Action.ToString(), r.AllInFolder, WithSite = r.Domain is not null
            }),
            Programs = programs,
            Zapret = new
            {
                Enabled = zapretEnabled, Strategy = zapretActiveStrategy, Direct = zapretActiveGroups, Demoted = zapretDemoted,
                KeptInVpn = zapretVpnGroups.ToArray(), Journal = zapretJournal.Entries.TakeLast(60)
            },
            LastCrash = DiagnosticReport.RecentCrash(CrashLog.PathName, DateTimeOffset.Now)
        };
        return DiagnosticReport.Create(engine.State, importedProfiles.Count,
            groups.Sum(g => g.Sources.Length), events, routeAttempts,
            SystemTunnelActive ? DiagnosticMode.SystemTunnel : systemProxyPort != 0
                ? DiagnosticMode.WindowsProxy : DiagnosticMode.LocalProxy, systemTunnel?.BlocksBypass == true,
            lastDiagnosis?.Kind, fragmentMode.ToString(), (engine as Engine)?.FragmentActive == true, details);
    }

    // Серверы — номером и протоколом (Transport собирается из известных значений, без имени):
    // видно, на ком остановился автовыбор и почему.
    private object ReportServers()
    {
        var now = DateTimeOffset.UtcNow;
        return importedProfiles.Take(60).Select((p, i) =>
        {
            var m = measurements.GetValueOrDefault(Profiles.Fingerprint(p));
            return new
            {
                Index = i, p.Transport, Service = p.IsService, InAutoPool = autoPool.Contains(p), Running = ReferenceEquals(p, runningProfile),
                Measured = m is null ? null : new
                {
                    m.Success, m.Milliseconds, ThroughputKBps = m.Throughput / 1024, MinutesAgo = (int)(now - m.Time).TotalMinutes,
                    Score = ServerMeasurements.Score(m) < double.MaxValue ? (long)ServerMeasurements.Score(m) : (long?)null
                }
            };
        }).ToArray();
    }

    // Программы в сети прямо сейчас: файл, папка, куда идёт трафик и какое правило его взяло.
    private async Task<object> ReportProgramsAsync()
    {
        try
        {
            var own = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory) + "\\";
            bool Own(string path) => path.StartsWith(own, StringComparison.OrdinalIgnoreCase);
            var core = SystemTunnelActive && systemTunnel?.Stats is { } stats ? await CoreConnections.ReadAsync(stats, CancellationToken.None) : null;
            var tunnel = SystemTunnelActive; var proxy = engine.Port;
            var apps = core is not null ? LiveApps.FromCore(core, Own)
                : await Task.Run(() => LiveApps.Group(ConnectionTable.Read(), tunnel, proxy, LivePath, Own));
            if (core is not null) programJournal.Record(core, DateTime.UtcNow);
            var now = DateTime.UtcNow;
            string? RuleOf(string path) => ProgramRoutingPlan.Find(programRules, path)?.Action.ToString();
            string? TargetOf(string path) => ProgramRoutingPlan.Find(programRules, path) is { } rule ? DiagnosticReport.SafePath(rule.TargetKey) : null;
            return new
            {
                Source = core is not null ? "TunnelCore" : "WindowsTables",
                Apps = apps.OrderByDescending(a => a.Total + a.Udp).Take(60).Select(a => new
                {
                    File = Path.GetFileName(a.Path), Folder = DiagnosticReport.SafePath(Path.GetDirectoryName(a.Path) ?? ""),
                    a.Vpn, a.Direct, a.Udp, Rule = RuleOf(a.Path), RuleTarget = TargetOf(a.Path)
                }).ToArray(),
                // За 10 минут, раз в 5 с: и те, что выходят в сеть ненадолго (античит, лаунчер).
                Recent = programJournal.Recent(now).Where(e => !Own(e.Path)).Take(60).Select(e => new
                {
                    File = Path.GetFileName(e.Path), Folder = DiagnosticReport.SafePath(Path.GetDirectoryName(e.Path) ?? ""),
                    SecondsAgo = (int)(now - e.LastSeen).TotalSeconds, e.Samples, e.Vpn, e.Direct, e.Udp, Rule = RuleOf(e.Path), RuleTarget = TargetOf(e.Path)
                }).ToArray(),
                Conflicts = ProgramRuleConflicts.Find(programRules, KnownProgramPaths()).Select(c => new
                {
                    Name = c.Name.Executable, NameAction = c.Name.Action.ToString(), Folder = DiagnosticReport.SafePath(c.Folder.TargetKey), FolderAction = c.Folder.Action.ToString()
                }).ToArray()
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return new { Error = ex.GetType().Name }; }
    }
}
