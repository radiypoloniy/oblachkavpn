namespace Oblachka;

public static partial class SelfTests
{
    // Программы «напрямую» под защитой: игра, запущенная до VPN, держит сокеты мимо туннеля, и
    // защита пропускает её файл. Только запущенные, только правило «напрямую», не системные файлы.
    private static async Task DirectAppsChecksAsync(Func<string, Func<Task>, Task> check)
    {
        await check("Protection lets out only running programs whose rule is direct, never Windows files", () => Sync(() =>
        {
            const string steam = @"C:\Program Files (x86)\Steam";
            var rules = new[]
            {
                ProgramRoutingPlan.Create(steam + @"\steam.exe", SiteAction.Direct, allInFolder: true),
                // Точное правило сильнее папки: этот файл Steam остаётся в туннеле.
                ProgramRoutingPlan.Create(steam + @"\steamapps\common\Game\game.exe", SiteAction.Tunnel, allInFolder: false),
                ProgramRoutingPlan.Create("gameprobe.exe", SiteAction.Direct),
                ProgramRoutingPlan.Create("svchost.exe", SiteAction.Direct),
                ProgramRoutingPlan.Create("telegram.exe", SiteAction.Direct) with { Domain = "t.me" },
            };
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var running = new[]
            {
                steam + @"\steam.exe", steam + @"\steamapps\common\dota 2 beta\game\bin\win64\dota2.exe", steam + @"\STEAM.EXE",
                steam + @"\steamapps\common\Game\game.exe", @"C:\lab\game\gameprobe.exe", windows + @"\System32\svchost.exe",
                @"C:\Users\u\AppData\Roaming\Telegram Desktop\Telegram.exe", @"C:\Tools\other.exe", "gameprobe.exe"
            };
            var allowed = WfpDirectApps.Select(running, rules);
            Assert(allowed.Count == 3 && allowed.Contains(steam + @"\steam.exe") && allowed.Contains(@"C:\lab\game\gameprobe.exe") &&
                allowed.Contains(steam + @"\steamapps\common\dota 2 beta\game\bin\win64\dota2.exe"));
            // Без правил «напрямую» — ничего; правило с сайтом решает не за всю программу.
            Assert(WfpDirectApps.Select(running, rules.Where(r => r.Action != SiteAction.Direct || r.Domain is not null).ToArray()).Count == 0);
            var filters = WfpDirectApps.Filters(allowed);
            Assert(filters.Count == 2 && filters.All(f => f.Permit && !f.Packet && f.Weight == 13 && f.Conditions.Length == 3 &&
                f.Conditions.All(c => c.Kind == WfpConditionKind.Application)));
            // Свои номера: за основным диапазоном и разрешениями zapret, снимаются вместе с защитой.
            var keys = Enumerable.Range(WfpDirectApps.First, WfpDirectApps.Slots).Select(WfpProtectionBackend.FilterKey).ToHashSet();
            Assert(filters.All(f => keys.Contains(f.Key)) && WfpDirectApps.First >= WfpReinjection.First + WfpReinjection.Slots);
        }));
        await check("Direct-program permits live only inside the protection sublayer and go away with it", () => Sync(() =>
        {
            var engine = new FakeWfp();
            var programs = new[] { Environment.ProcessPath! };
            WfpDirectApps.Forget();
            Assert(!WfpDirectApps.Sync(() => engine, programs) && engine.Filters.Count == 0);
            engine.Sublayer = true;
            Assert(WfpDirectApps.Sync(() => engine, programs) && engine.Filters.Count == 2);
            // Тот же набор — без лишней транзакции; после восстановления защиты — заново.
            var log = engine.Log.Count;
            Assert(!WfpDirectApps.Sync(() => engine, programs) && engine.Log.Count == log);
            Assert(WfpDirectApps.Sync(() => engine, programs, force: true) && engine.Filters.Count == 2);
            Assert(WfpDirectApps.Sync(() => engine, []) && engine.Filters.Count == 0);
            // Снятие защиты убирает и их: RemoveAll перебирает номера за основным диапазоном.
            WfpDirectApps.Sync(() => engine, programs);
            var backend = new WfpProtectionBackend(() => engine, @"C:\app\tools");
            var change = backend.Prepare(NetworkHelperContracts.Validate(new NetworkLeaseRequest(1, Guid.NewGuid(), NetworkLeaseAction.Acquire, 10808, ["203.0.113.7"])))[0];
            backend.Restore(Guid.NewGuid(), change);
            Assert(engine.Filters.Count == 0 && !engine.Sublayer);
            WfpDirectApps.Forget();
        }));
    }
}
