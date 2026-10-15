using System.Net.Http;

namespace Oblachka;

// Живая проверка маршрутизации на настоящих данных:
//   Oblachka.Tests.exe --routing-check <ключ> <файл отчёта>
// Берёт заголовок routing из подписки, скачивает базы во временную папку, разворачивает
// списки и проверяет конфигурации ядер Xray и sing-box и туннеля. Ключ в отчёт не пишется.
internal static class RoutingCheckCommand
{
    internal static int Run(string key, string reportPath)
    {
        var lines = new List<string>();
        var folder = Path.Combine(Path.GetTempPath(), "oblachka-routing-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
            var imported = Subscriptions.FetchAsync(ServiceKey.ToSubscriptionUrl(key), timeout.Token).GetAwaiter().GetResult();
            var provider = imported.Metadata?.Routing;
            lines.Add(provider is null ? "Маршрутизации в подписке нет" : $"Маршрутизация сервиса «{provider.Name}»: {provider.RuleCount} записей, geosite {string.Join(",", provider.GeositeTags)}");
            var geo = new GeoStore(folder);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            geo.DownloadAsync(GeoStore.Kind.Geosite, provider?.GeositeUrl ?? RoutingProfile.DefaultGeosite, timeout.Token).GetAwaiter().GetResult();
            lines.Add($"geosite.dat: {geo.Info(GeoStore.Kind.Geosite)!.Bytes / 1048576.0:0.0} МБ за {clock.Elapsed.TotalSeconds:0} с");
            clock.Restart();
            var profiles = new[] { provider, RoutingProfile.RussiaDirect }.OfType<RoutingProfile>().ToArray();
            var routing = CompiledRouting.Compile(profiles, geo)!;
            lines.Add($"Развёрнуто за {clock.ElapsedMilliseconds} мс: напрямую {routing.Direct.Suffix.Count} доменов, {routing.Direct.Full.Count} точных, {routing.Direct.Cidr.Count} диапазонов; нет категорий: {string.Join(",", routing.Missing)}");
            foreach (var host in new[] { "www.gosuslugi.ru", "online.sberbank.ru", "www.ozon.ru", "vk.com", "music.yandex.ru", "youtube.com", "chatgpt.com" })
                lines.Add("  " + host + " → " + (routing.Decide(host)?.ToString() ?? "через VPN (по умолчанию)"));
            var tools = Path.Combine(AppContext.BaseDirectory, "tools");
            var profile = imported.Profiles.First(p => p.Core == CoreKind.Xray);
            var xray = new CoreAdapter(CoreKind.Xray, Path.Combine(tools, "xray.exe"));
            var config = RoutingPlan.Compile(xray, profile, 10808, [], routing);
            CoreConfigurationCheck.ValidateAsync(xray, config, timeout.Token).GetAwaiter().GetResult();
            lines.Add($"Xray принял конфигурацию ({config.ToJsonString().Length / 1024} КБ)");
            if (imported.Profiles.FirstOrDefault(p => p.Core == CoreKind.SingBox) is { } hy2)
            {
                var singBox = new CoreAdapter(CoreKind.SingBox, Path.Combine(tools, "sing-box.exe"));
                CoreConfigurationCheck.ValidateAsync(singBox, RoutingPlan.Compile(singBox, hy2, 10808, [], routing), timeout.Token).GetAwaiter().GetResult();
                lines.Add("sing-box принял конфигурацию");
            }
            CoreConfigurationCheck.ValidateTunAsync(new(1, Guid.NewGuid(), NetworkLeaseAction.Acquire, 10808, ["192.0.2.1"], CoreDirect: CoreDirectEntry.Create(23456)), timeout.Token).GetAwaiter().GetResult();
            lines.Add("Туннель с входом прямого трафика принят");
            File.WriteAllLines(reportPath, lines);
            return 0;
        }
        catch (Exception ex) when (ex is UserError or HttpRequestException or OperationCanceledException)
        { lines.Add("Ошибка: " + ex.Message); File.WriteAllLines(reportPath, lines); return 1; }
        finally { try { Directory.Delete(folder, true); } catch (IOException) { } }
    }
}
