using System.Windows;
using Microsoft.Win32;
using Oblachka.Views;

namespace Oblachka;

// Правила программ: выбор из списка программ компьютера (AppPicker), файлом или папкой. Правило
// берёт папку программы целиком (AppRuleScope); имя и значок берутся из каталога для показа.
public partial class MainWindow
{
    private readonly List<ProgramRule> programRules = new();
    private IReadOnlyList<AppRow>? appCatalog;
    private bool appCatalogLoading;

    private void WireProgramRules()
    {
        Routes.ChooseProgramsButton.Click += (_, _) => OpenAppPicker();
        Picker.Closed += CloseAppPicker;
        Picker.FileRequested += ChooseProgramFile;
        Picker.FolderRequested += ChooseProgramFolder;
        Picker.Chosen += (apps, action) =>
        {
            try { AddProgramRules(apps.SelectMany(a => AppRuleScope.For(a, action)).ToArray()); }
            catch (UserError ex) { DetailText.Text = ex.Message; return; }
            CloseAppPicker();
            DetailText.Text = apps.Count == 1 ? "Программа добавлена в правила." : "Программ добавлено в правила: " + apps.Count + ".";
        };
    }

    private IReadOnlyList<ProgramRule> AddedPrograms() => programRules.ToArray();

    private void OpenAppPicker()
    {
        Picker.Open(AddedPrograms());
        LoadAppCatalog();
    }

    private void CloseAppPicker()
    {
        Picker.Visibility = Visibility.Collapsed;
        Routes.ChooseProgramsButton.Focus();
    }

    // Каталог и значки собираются один раз за запуск и в фоне: ярлыков и процессов бывают сотни.
    private async void LoadAppCatalog()
    {
        if (appCatalog is not null || appCatalogLoading) return;
        appCatalogLoading = true;
        try
        {
            var rows = await Task.Run(() => InstalledApps.Find().Select(a => new AppRow(a, AppIcons.For(a.Path))).ToArray());
            appCatalog = rows;
            if (closed) return;
            Picker.Load(rows, AddedPrograms());
            RefreshRules();
        }
        finally { appCatalogLoading = false; }
    }

    private void ChooseProgramFile()
    {
        var dialog = new OpenFileDialog { Filter = "Программа (*.exe)|*.exe", Title = "Выберите программу" };
        if (dialog.ShowDialog(this) != true) return;
        var app = new InstalledApp(Path.GetFileNameWithoutExtension(dialog.FileName), Path.GetFileName(dialog.FileName), dialog.FileName, false);
        try { if (AddProgramRules(AppRuleScope.For(app, Picker.Action))) { CloseAppPicker(); DetailText.Text = "Программа добавлена в правила."; } }
        catch (UserError ex) { DetailText.Text = ex.Message; }
    }

    // Игра, которой нет в списке (лаунчер ставит её в свою папку): правило на всю выбранную папку.
    private void ChooseProgramFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Выберите папку программы или игры" };
        if (dialog.ShowDialog(this) != true) return;
        try { if (AddProgramRules([AppRuleScope.ForFolder(dialog.FolderName, Picker.Action)])) { CloseAppPicker(); DetailText.Text = "Папка добавлена в правила."; } }
        catch (UserError ex) { DetailText.Text = ex.Message; }
    }

    // Уже существующее правило на тот же файл или папку заменяется: человек поменял решение.
    private bool AddProgramRules(IReadOnlyList<ProgramRule> rules)
    {
        try
        {
            var next = programRules.Where(r => !rules.Any(n => string.Equals(n.Key, r.Key, StringComparison.OrdinalIgnoreCase))).Concat(rules).ToArray();
            ProgramRoutingPlan.Validate(next);
            programRules.Clear(); programRules.AddRange(next); RefreshRules(); QueueStateSave();
            ProgramRulesChanged();
            return true;
        }
        catch (UserError ex) { DetailText.Text = ex.Message; return false; }
    }

    // Подпись говорит, что именно берёт правило: всю папку, один файл или любую копию по имени.
    private RuleRow ProgramRow(ProgramRule rule, IReadOnlyList<(ProgramRule Name, ProgramRule Folder)> conflicts)
    {
        var app = appCatalog?.FirstOrDefault(a => a.Covers(rule)) ??
            appCatalog?.FirstOrDefault(a => string.Equals(a.App.Executable, rule.Executable, StringComparison.OrdinalIgnoreCase));
        var sub = (rule.Domain is null ? "" : "только " + rule.Domain + " · ") +
            (rule.Folder is null ? rule.Executable + " · в любой папке" : rule.AllInFolder ? "вся папка · " + ShortPath(rule.Folder) : ShortPath(rule.FilePath!));
        var text = app?.Name ?? (rule.AllInFolder ? Path.GetFileName(rule.Folder!) : rule.Executable);
        return new RuleRow { Rule = rule, Text = text, Sub = sub, Icon = app?.Icon, Action = rule.Action, Warning = ProgramRuleConflicts.Note(rule, conflicts) };
    }

    // Пути программ, которые окно видело: каталог, «Сейчас в сети» и журнал программ за 10 минут.
    private IEnumerable<string> KnownProgramPaths()
    {
        string?[] live; lock (livePaths) live = livePaths.Values.ToArray();
        return (appCatalog?.Select(a => a.App.Path) ?? []).Concat(live.OfType<string>()).Concat(programJournal.Recent(DateTime.UtcNow).Select(e => e.Path));
    }

    // C:\Users\me\AppData\Roaming\Telegram Desktop → C:\…\Roaming\Telegram Desktop: в узкой строке
    // важен конец пути, а не начало.
    internal static string ShortPath(string path)
    {
        var parts = path.Split(Path.DirectorySeparatorChar);
        return parts.Length <= 4 ? path : parts[0] + Path.DirectorySeparatorChar + "…" + Path.DirectorySeparatorChar + string.Join(Path.DirectorySeparatorChar, parts[^2..]);
    }

    private async Task ValidateProgramDraftAsync(CancellationToken token)
    {
        if (programRules.Count == 0) return;
        var request = new NetworkLeaseRequest(1, Guid.NewGuid(), NetworkLeaseAction.Acquire, 10808, ["192.0.2.1"]);
        var adapter = new CoreAdapter(CoreKind.SingBox, TunCore.Program());
        await CoreConfigurationCheck.ValidateAsync(adapter, ProgramRoutingPlan.Compile(request, programRules), token);
    }
}
