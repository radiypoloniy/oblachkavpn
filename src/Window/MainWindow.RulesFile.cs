using Microsoft.Win32;

namespace Oblachka;

// «Правила» → «Экспорт» / «Импорт» (RulesFile). Импорт добавляет правила к своим: совпадающая цель
// получает действие из файла. Перед этим — сколько правил придёт, без молчаливых замен.
public partial class MainWindow
{
    private void WireRulesFile()
    {
        Routes.ExportRules.Click += (_, _) =>
        {
            if (siteRules.Count + programRules.Count == 0) { DetailText.Text = "Правил пока нет — экспортировать нечего."; return; }
            var dialog = new SaveFileDialog { Filter = "Правила Облачков (*.json)|*.json", FileName = "oblachka-rules.json", Title = "Сохранить правила" };
            if (dialog.ShowDialog(this) != true) return;
            try { File.WriteAllText(dialog.FileName, RulesFile.Export(siteRules, programRules)); DetailText.Text = $"Правила сохранены: сайтов {siteRules.Count}, программ {programRules.Count}."; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DetailText.Text = "Не удалось сохранить файл. Проверьте доступ к папке."; }
        };
        Routes.ImportRules.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { Filter = "Правила Облачков (*.json)|*.json|Все файлы|*.*", Title = "Открыть файл правил" };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                if (new FileInfo(dialog.FileName).Length > RulesFile.MaxBytes) throw new UserError("Файл правил больше 256 КБ.");
                var (sites, programs) = RulesFile.Import(File.ReadAllText(dialog.FileName));
                if (sites.Length + programs.Length == 0) { DetailText.Text = "В файле нет правил."; return; }
                var replaced = sites.Count(s => siteRules.Any(r => r.Domain == s.Domain)) + programs.Count(p => programRules.Any(r => r.Key == p.Key));
                if (!AppDialog.Confirm(this, "Добавить правила из файла?",
                    $"Сайтов, подсетей и портов: {sites.Length}, программ: {programs.Length}." + (replaced > 0 ? $" У {replaced} из них уже есть правило — его действие заменится." : ""), "Добавить")) return;
                var nextSites = siteRules.Where(r => sites.All(s => s.Domain != r.Domain)).Concat(sites).ToList();
                if (nextSites.Count > 100) throw new UserError("Вместе с файлом правил сайтов стало бы больше 100. Уберите лишние и повторите.");
                // Программы — проверить до любых изменений: иначе сайты заменились бы, а программы нет.
                ProgramRoutingPlan.Validate(programRules.Where(r => programs.All(p => !string.Equals(p.Key, r.Key, StringComparison.OrdinalIgnoreCase))).Concat(programs).ToArray());
                siteRules.Clear(); siteRules.AddRange(nextSites);
                AddProgramRules(programs);
                RefreshRules(); QueueStateSave(); ProgramRulesChanged();
                DetailText.Text = $"Правила добавлены: сайтов {sites.Length}, программ {programs.Length}.";
            }
            catch (UserError ex) { DetailText.Text = ex.Message; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DetailText.Text = "Не удалось прочитать файл. Проверьте доступ к нему."; }
        };
    }
}
