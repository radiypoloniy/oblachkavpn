using System.Windows;
using Microsoft.Win32;
using Oblachka.Views;

namespace Oblachka;

// Правила программ: выбор из списка программ компьютера (AppPicker) или файлом. Правило хранит
// только имя файла — так его исполняет ядро туннеля; имя и значок берутся из каталога для показа.
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
        Picker.Chosen += (apps, action) =>
        {
            foreach (var app in apps) AddProgramRule(app.Executable, action);
            CloseAppPicker();
            DetailText.Text = apps.Count == 1 ? "Программа добавлена в правила." : "Программ добавлено в правила: " + apps.Count + ".";
        };
    }

    private Dictionary<string, SiteAction> AddedPrograms() =>
        programRules.ToDictionary(r => r.Executable, r => r.Action, StringComparer.OrdinalIgnoreCase);

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
        if (AddProgramRule(Path.GetFileName(dialog.FileName), Picker.Action)) { CloseAppPicker(); DetailText.Text = "Программа добавлена в правила."; }
    }

    private bool AddProgramRule(string executable, SiteAction action)
    {
        try
        {
            var rule = ProgramRoutingPlan.Create(executable, action);
            if (programRules.Any(r => string.Equals(r.Executable, rule.Executable, StringComparison.OrdinalIgnoreCase))) return false;
            ProgramRoutingPlan.Validate(programRules.Append(rule).ToArray());
            programRules.Add(rule); RefreshRules(); QueueStateSave();
            return true;
        }
        catch (UserError ex) { DetailText.Text = ex.Message; return false; }
    }

    private RuleRow ProgramRow(ProgramRule rule)
    {
        var app = appCatalog?.FirstOrDefault(a => string.Equals(a.App.Executable, rule.Executable, StringComparison.OrdinalIgnoreCase));
        return app is null
            ? new RuleRow { Rule = rule, Text = rule.Executable, Action = rule.Action }
            : new RuleRow { Rule = rule, Text = app.Name, Sub = rule.Executable, Icon = app.Icon, Action = rule.Action };
    }

    private async Task ValidateProgramDraftAsync(CancellationToken token)
    {
        if (programRules.Count == 0) return;
        var request = new NetworkLeaseRequest(1, Guid.NewGuid(), NetworkLeaseAction.Acquire, 10808, ["192.0.2.1"]);
        var adapter = new CoreAdapter(CoreKind.SingBox, Path.Combine(AppContext.BaseDirectory, "tools", "sing-box.exe"));
        await CoreConfigurationCheck.ValidateAsync(adapter, ProgramRoutingPlan.Compile(request, programRules), token);
    }
}
