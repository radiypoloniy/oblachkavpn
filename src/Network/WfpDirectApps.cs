namespace Oblachka;

// Программы с правилом «напрямую» под защитой «Весь компьютер».
//
// Обычно их трафик идёт в туннель и ядро отправляет его прямым выходом. Но сокет, который
// программа подключила до включения VPN или сама привязала к адресу сетевой карты, идёт мимо
// туннеля: ядро его не видит, правило к нему не применяется, а запрет мимо туннеля его режет.
// Игра, запущенная раньше VPN, теряла связь, пока её не перезапустят (Dota 2, стенд 25.10.2026:
// UDP с connect() и TCP после подключения молчали, новые сокеты работали).
//
// Поэтому файлам запущенных программ, которые правило отправляет напрямую, защита разрешает
// выход мимо туннеля — по фильтру на семейство адресов, файлы в одном поле через «или». WFP
// заново проверяет и уже открытые соединения, так что они не рвутся. Разрешение — только
// запущенным: у правила на папку Steam тысячи файлов. Системные файлы Windows не разрешаются:
// через svchost шёл бы DNS всех программ мимо туннеля.
//
// Номера фильтров — за основным диапазоном защиты; WfpProtectionBackend.RemoveAll снимает и их.
public static class WfpDirectApps
{
    public const int Slots = 2;
    public static int First => WfpProtectionBackend.MaxFilters;
    // Больше программ в одном фильтре не нужно: столько «прямых» сразу не запускают.
    public const int MaxPrograms = 64;

    private static readonly object Gate = new();
    private static string[] allowed = [];
    // Защита стоит: подключение «Весь компьютер» прошло. Без неё сторож процессы не перебирает.
    private static bool armed;

    // Файлы, которым нужно разрешение: путь процесса, правило которого — «напрямую».
    public static IReadOnlyList<string> Select(IEnumerable<string> running, IReadOnlyList<ProgramRule> rules)
    {
        if (!rules.Any(r => r.Domain is null && r.Action == SiteAction.Direct)) return [];
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows) + "\\";
        return running.Where(p => !p.StartsWith(windows, StringComparison.OrdinalIgnoreCase) && Path.IsPathFullyQualified(p) &&
                ProgramRoutingPlan.Find(rules, p)?.Action == SiteAction.Direct)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).Take(MaxPrograms).ToArray();
    }

    public static IReadOnlyList<WfpFilter> Filters(IReadOnlyList<string> programs) => programs.Count == 0 ? [] :
        new[] { false, true }.Select(v6 => new WfpFilter(WfpProtectionBackend.FilterKey(First + (v6 ? 1 : 0)),
            ServiceBrand.Name + " — программы «напрямую»" + (v6 ? " (IPv6)" : " (IPv4)"), v6, 13, true,
            programs.Select(p => new WfpCondition(WfpConditionKind.Application, Program: p)).ToArray())).ToArray();

    // Разрешения под нынешний набор. Без защиты (режим «Браузеры», сессия снята) — ничего.
    // force — защиту только что поставили заново: прежних разрешений в ней уже нет.
    public static bool Sync(Func<IWfpEngine> open, IReadOnlyList<string> programs, bool force = false)
    {
        lock (Gate)
        {
            if (!force && programs.SequenceEqual(allowed, StringComparer.OrdinalIgnoreCase)) return false;
            using var engine = open();
            if (!engine.SublayerExists(WfpProtectionBackend.SublayerKey)) { allowed = []; armed = false; return false; }
            armed = true;
            engine.Begin();
            try
            {
                for (var i = First; i < First + Slots; i++) engine.DeleteFilter(WfpProtectionBackend.FilterKey(i));
                // Файл, удалённый между снимком процессов и разрешением, WFP не опознает — без него.
                foreach (var filter in Filters(programs.Where(File.Exists).ToArray()))
                    engine.AddFilter(filter, WfpProtectionBackend.ProviderKey, WfpProtectionBackend.SublayerKey);
                engine.Commit();
            }
            catch { engine.Abort(); throw; }
            allowed = programs.ToArray();
            return true;
        }
    }

    // Сессия снята целиком: фильтры ушли вместе с подслоем.
    public static void Forget() { lock (Gate) { allowed = []; armed = false; } }

    public static IReadOnlyList<string> Allowed { get { lock (Gate) return allowed; } }

    // Пути запущенных процессов. Путь по номеру — один раз: OpenProcess на каждый процесс раз
    // в две секунды заметен. Кэш сбрасывается раз в минуту, чтобы номер, доставшийся новой
    // программе, не врал долго.
    private static readonly Dictionary<int, string?> paths = new();
    private static DateTime pathsReset = DateTime.UtcNow;
    public static IReadOnlyList<string> Running()
    {
        var processes = System.Diagnostics.Process.GetProcesses();
        lock (paths)
        {
            if (DateTime.UtcNow - pathsReset > TimeSpan.FromMinutes(1)) { paths.Clear(); pathsReset = DateTime.UtcNow; }
            var result = new List<string>();
            foreach (var process in processes)
            {
                using (process)
                {
                    if (!paths.TryGetValue(process.Id, out var path)) paths[process.Id] = path = ConnectionTable.ProcessPath(process.Id);
                    if (path is not null) result.Add(path);
                }
            }
            return result;
        }
    }

    // Шаг сторожа службы: разрешения под запущенные сейчас программы и правила в файлах ядра.
    public static void Tick(Func<IWfpEngine> open, bool force = false)
    {
        lock (Gate) if (!armed && !force) return;
        var rules = ProgramRuleSets.Current;
        var programs = rules.Any(r => r.Domain is null && r.Action == SiteAction.Direct) ? Select(Running(), rules) : [];
        Sync(open, programs, force);
    }
}
