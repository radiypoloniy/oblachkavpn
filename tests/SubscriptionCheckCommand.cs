namespace Oblachka;

// Живая проверка пути «ключ подписки → сеть → список узлов».
//   Oblachka.Tests.exe --subscription-check <ключ или ссылка> <файл отчёта>
// Делает один HTTPS-запрос тем же кодом, что и клиент. В отчёт попадают только
// имена узлов, транспорты, срок и остаток трафика. Ключ, адреса узлов и UUID — нет.
internal static class SubscriptionCheckCommand
{
    internal static int Run(string key, string reportPath)
    {
        var report = new System.Text.StringBuilder();
        string url;
        try { url = ServiceKey.ToSubscriptionUrl(key); }
        catch (UserError ex)
        {
            File.WriteAllText(reportPath, "Ключ не принят: " + ex.Message);
            return 1;
        }
        report.AppendLine("Сервис: " + ServiceBrand.Label + ", User-Agent: " + ServiceBrand.UserAgent);
        report.AppendLine("Хост подписки: " + new Uri(url).Host + " (ключ в отчёт не пишется)");
        report.AppendLine();

        ImportResult result;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            result = Subscriptions.FetchAsync(url, timeout.Token).GetAwaiter().GetResult();
        }
        catch (UserError ex)
        {
            report.AppendLine("Загрузка не удалась: " + ex.Message);
            File.WriteAllText(reportPath, report.ToString());
            return 2;
        }
        catch (Exception ex)
        {
            report.AppendLine("Загрузка не удалась: " + ex.GetType().Name);
            File.WriteAllText(reportPath, report.ToString());
            return 2;
        }

        report.AppendLine("Узлов получено: " + result.Profiles.Count);
        report.AppendLine("Отклонено: " + result.Invalid + ", неподдерживаемых: " + result.Unsupported + ", склеено одинаковых: " + result.Duplicates);
        foreach (var reason in result.Reasons) report.AppendLine("  причина отказа: " + reason);
        if (result.Metadata is { } meta)
        {
            var summary = meta.Summary(DateTimeOffset.UtcNow);
            report.AppendLine("Тариф: " + (summary.Length > 0 ? summary : "заголовок есть, но срок и объём не заданы"));
            report.AppendLine("  total=" + (meta.Total?.ToString() ?? "нет") + (meta.Total == 0 ? " (безлимит)" : ""));
            report.AppendLine("  на главном экране: " + meta.ShortSummary(DateTimeOffset.UtcNow));
            report.AppendLine("Название: " + (meta.Title ?? "нет"));
            report.AppendLine("Объявление: " + (meta.Announce ?? "нет"));
            report.AppendLine("Период обновления: " + (meta.UpdateInterval?.ToString() ?? "нет, берём шесть часов"));
        }
        else report.AppendLine("Тариф: заголовка subscription-userinfo нет");
        report.AppendLine();
        foreach (var profile in result.Profiles)
            report.AppendLine(string.Format("  {0,-34} {1,-9} {2,-24} {3}",
                profile.DisplayName, profile.Core, profile.Transport, profile.Country.Code ?? "--"));
        File.WriteAllText(reportPath, report.ToString());
        return 0;
    }
}
