using System.Text.Json.Nodes;

namespace Oblachka;

// Разбирает сохранённое тело подписки и печатает, что из него получил клиент.
// Нужна для сверки с реальной панелью: Oblachka.Tests.exe --import-check <файл>.
// Печатает только имена узлов, протоколы, транспорты и причины отказа.
// Адреса серверов, UUID, пароли и ключи не выводятся.
internal static class ImportCheckCommand
{
    // Клиент собран как WinExe и консоли не имеет, поэтому отчёт пишется в файл.
    internal static int Run(string path, string reportPath, bool validate = false)
    {
        var report = new System.Text.StringBuilder();
        if (!File.Exists(path)) { File.WriteAllText(reportPath, "Нет файла: " + path); return 1; }
        var body = File.ReadAllText(path);
        ImportResult result;
        try { result = Subscriptions.Parse(body); }
        catch (UserError ex)
        {
            File.WriteAllText(reportPath, "Подписка отклонена целиком: " + ex.Message);
            return 2;
        }

        report.AppendLine("Импортировано: " + result.Profiles.Count);
        report.AppendLine("Отклонено: " + result.Invalid + ", неподдерживаемых outbound: " + result.Unsupported);
        if (result.Reasons.Count > 0)
        {
            report.AppendLine("Причины отказа:");
            foreach (var reason in result.Reasons) report.AppendLine("  - " + reason);
        }
        if (result.Duplicates > 0) report.AppendLine("Склеено полностью одинаковых записей: " + result.Duplicates);
        report.AppendLine();
        report.AppendLine("Узлы:");
        var names = new Dictionary<string, int>(StringComparer.Ordinal);
        var rejected = 0;
        foreach (var profile in result.Profiles)
        {
            names[profile.Name] = 1 + names.GetValueOrDefault(profile.Name);
            var verdict = "";
            if (validate)
            {
                verdict = Validate(profile);
                if (verdict != "ядро приняло") rejected++;
            }
            report.AppendLine(string.Format("  {0,-34} {1,-9} {2,-24} {3,-8} {4}",
                profile.DisplayName, profile.Core, profile.Transport,
                profile.Country.Code ?? "--", verdict));
        }
        if (validate)
        {
            report.AppendLine();
            report.AppendLine("Ядра приняли конфигураций: " + (result.Profiles.Count - rejected) + " из " + result.Profiles.Count);
        }
        var duplicates = names.Where(p => p.Value > 1).ToList();
        if (duplicates.Count > 0)
        {
            report.AppendLine();
            report.AppendLine("Повторяющиеся имена (выбор узла по имени не восстановится однозначно):");
            foreach (var pair in duplicates) report.AppendLine("  " + pair.Key + " x" + pair.Value);
        }
        File.WriteAllText(reportPath, report.ToString());
        return 0;
    }

    // Запускает закреплённое ядро с ключом проверки конфигурации. Соединений не открывает.
    private static string Validate(Profile profile)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "tools", profile.Core == CoreKind.Xray ? "xray.exe" : "sing-box.exe");
        var adapter = new CoreAdapter(profile.Core, executable);
        try
        {
            CoreConfigurationCheck.ValidateAsync(adapter, adapter.RuntimeConfig(profile.Outbound, 10808), CancellationToken.None)
                .GetAwaiter().GetResult();
            return "ядро приняло";
        }
        catch (UserError ex) { return "ОТКАЗ: " + ex.Message; }
        catch (Exception ex) { return "ОШИБКА: " + ex.GetType().Name; }
    }
}
