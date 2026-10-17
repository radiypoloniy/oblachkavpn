using System.Text.RegularExpressions;

namespace Oblachka;

// Что именно берёт правило, когда человек выбирает программу: её папку целиком — клиент вместе со
// службами и помощниками, игру со всеми её файлами. Папка слишком общая (программа лежит прямо в
// «Загрузках» или в корне Program Files) — только сам файл, по полному пути.
public static class AppRuleScope
{
    public static ProgramRule[] For(InstalledApp app, SiteAction action)
    {
        var rules = new List<ProgramRule>();
        foreach (var folder in app.Folders ?? [Root(app.Path)])
            try { rules.Add(ProgramRoutingPlan.Create(Path.Combine(folder, app.Executable), action, allInFolder: true)); }
            catch (UserError) { }
        if (rules.Count == 0) rules.Add(ProgramRoutingPlan.Create(app.Path, action, allInFolder: false));
        return rules.ToArray();
    }

    // Папка, выбранная вручную. Главная программа в ней нужна для имени и значка в списке правил.
    public static ProgramRule ForFolder(string folder, SiteAction action)
    {
        var exe = GameLibraries.MainExe(folder) ?? throw new UserError("В этой папке не нашлось программ (.exe).");
        return ProgramRoutingPlan.Create(Path.Combine(Path.TrimEndingDirectorySeparator(folder), Path.GetFileName(exe)), action, allInFolder: true);
    }

    // Discord, Slack и другие на Squirrel: …\Discord\app-1.0.9\Discord.exe. Папка с версией меняется
    // при каждом обновлении, поэтому правило берёт папку выше.
    private static readonly Regex Versioned = new(@"^app-\d+(\.\d+)*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    internal static string Root(string exe)
    {
        var folder = Path.GetDirectoryName(exe)!;
        return Versioned.IsMatch(Path.GetFileName(folder)) ? Path.GetDirectoryName(folder)! : folder;
    }
}
