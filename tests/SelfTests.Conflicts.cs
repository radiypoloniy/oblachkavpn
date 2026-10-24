namespace Oblachka;

public static partial class SelfTests
{
    // Спор правила по имени и правила на папку (AION2, 24.10.2026) и журнал программ для отчёта.
    private static async Task ProgramConflictChecksAsync(Func<string, Func<Task>, Task> check)
    {
        await check("Name rule and folder rule with different actions are reported on both rows", () => Sync(() =>
        {
            var game = @"C:\Program Files (x86)\Steam\steamapps\common\AION2";
            var name = ProgramRoutingPlan.Create("AION2.exe", SiteAction.Direct);
            var folder = ProgramRoutingPlan.Create(game + @"\Launcher.exe", SiteAction.Tunnel, allInFolder: true);
            var steam = ProgramRoutingPlan.Create(@"C:\Program Files (x86)\Steam\steam.exe", SiteAction.Direct, allInFolder: true);
            var rules = new[] { name, folder, steam };
            // Игра глубже главной программы папки: спор виден по пути, который окно видело.
            var conflicts = ProgramRuleConflicts.Find(rules, [game + @"\Aion2\Binaries\Win64\AION2.exe"]);
            Assert(conflicts.Count == 1 && conflicts[0].Name == name && conflicts[0].Folder == folder);
            Assert(ProgramRuleConflicts.Note(name, conflicts)!.Contains("напрямую") && ProgramRuleConflicts.Note(folder, conflicts)!.Contains("через VPN"));
            Assert(ProgramRuleConflicts.Note(steam, conflicts) is null);
            // Без известного пути спора не видно; одинаковые действия — не спор; точный файл сильнее имени.
            Assert(ProgramRuleConflicts.Find(rules, []).Count == 0);
            Assert(ProgramRuleConflicts.Find([name, folder with { Action = SiteAction.Direct }], [game + @"\AION2.exe"]).Count == 0);
            var exact = ProgramRoutingPlan.Create(game + @"\AION2.exe", SiteAction.Tunnel, allInFolder: false);
            Assert(ProgramRuleConflicts.Find([name, folder, exact], [game + @"\AION2.exe"]).Count == 0);
            // Главная программа папки с тем же именем — спор и без путей.
            Assert(ProgramRuleConflicts.Find([name, ProgramRoutingPlan.Create(game + @"\AION2.exe", SiteAction.Tunnel, allInFolder: true)], []).Count == 1);
        }));
        await check("Program journal keeps short-lived programs for ten minutes and their paths", () => Sync(() =>
        {
            var journal = new ProgramJournal();
            var start = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
            CoreConnection Conn(string path, bool direct, bool udp = false) => new(path, udp, null, "203.0.113.5", 443, direct ? [SingBoxTunConfig.UserDirect] : ["tunnel"]);
            journal.Record([Conn(@"C:\Game\game.exe", true, udp: true), Conn(@"C:\Game\game.exe", true), Conn(@"C:\Game\guard.exe", false)], start);
            journal.Record([Conn(@"C:\Game\game.exe", true)], start.AddMinutes(1));
            var recent = journal.Recent(start.AddMinutes(2));
            var game = recent.Single(e => e.Path.EndsWith("game.exe")); var guard = recent.Single(e => e.Path.EndsWith("guard.exe"));
            Assert(game.Samples == 2 && game.Direct == 2 && game.Udp == 1 && game.Vpn == 0 && guard.Vpn == 1 && guard.Direct == 0);
            Assert(recent[0] == game);
            Assert(journal.Recent(start.AddMinutes(10.5)).Select(e => e.Path).SequenceEqual([@"C:\Game\game.exe"]));
            Assert(journal.Recent(start.AddMinutes(12)).Count == 0);
        }));
    }
}
