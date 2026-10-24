namespace Oblachka;

// Правило по имени и правило на папку с разными действиями делят одну программу: файл с этим
// именем идёт по правилу имени (оно в ядре раньше папок), всё остальное из папки — по правилу
// папки. Так у AION2 (24.10.2026) сама игра шла напрямую, а её помощники из той же папки — через
// VPN, и сервер игры видел одного игрока с двух адресов. Окно показывает такой спор у обеих строк.
public static class ProgramRuleConflicts
{
    // knownPaths — пути программ, которые окно видело (каталог, «Сейчас в сети», журнал программ):
    // по ним видно, что файл с этим именем лежит в папке правила. Без них — по главной программе папки.
    public static IReadOnlyList<(ProgramRule Name, ProgramRule Folder)> Find(IReadOnlyList<ProgramRule> rules, IEnumerable<string> knownPaths)
    {
        var placed = rules.Where(r => r.Domain is null && r.Folder is not null).ToArray();
        var folders = placed.Where(r => r.AllInFolder).ToArray();
        if (folders.Length == 0) return [];
        var paths = knownPaths.Concat(folders.Select(f => f.FilePath!)).ToArray();
        var found = new List<(ProgramRule, ProgramRule)>();
        foreach (var name in rules.Where(r => r.Domain is null && r.Folder is null))
            foreach (var path in paths.Where(p => ProgramRoutingPlan.Covers(name, p)))
                // Точное правило на файл сильнее имени — тогда имя этот файл не берёт и спора нет.
                if (ProgramRoutingPlan.Find(placed, path) is { AllInFolder: true } folder && folder.Action != name.Action && !found.Contains((name, folder)))
                    found.Add((name, folder));
        return found;
    }

    // Пояснение к строке правила; null — правило ни с кем не спорит.
    public static string? Note(ProgramRule rule, IReadOnlyList<(ProgramRule Name, ProgramRule Folder)> conflicts)
    {
        if (conflicts.FirstOrDefault(c => c.Name.Key == rule.Key) is { Name: not null } byName)
            return $"Спорит с правилом на папку {Path.GetFileName(byName.Folder.Folder)}: {rule.Executable} пойдёт {Way(rule.Action)}, " +
                $"остальное из папки — {Way(byName.Folder.Action)}. Оставьте одно из правил.";
        if (conflicts.FirstOrDefault(c => c.Folder.Key == rule.Key) is { Folder: not null } byFolder)
            return $"Спорит с правилом по имени: {byFolder.Name.Executable} отсюда пойдёт {Way(byFolder.Name.Action)}, " +
                $"остальное из папки — {Way(rule.Action)}. Оставьте одно из правил.";
        return null;
    }

    private static string Way(SiteAction action) => action switch { SiteAction.Direct => "напрямую", SiteAction.Block => "под блокировку", _ => "через VPN" };
}
