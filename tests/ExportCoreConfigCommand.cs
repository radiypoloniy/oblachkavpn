using System.Text.Json;
using System.Text.Json.Nodes;

namespace Oblachka;

// Готовит конфигурацию ядра для стенда в виртуальной машине, где интерфейса нет.
//   Oblachka.Tests.exe --export-core-config <ключ> <имя узла> <файл конфигурации> <порт>
//
// Собирается тем же кодом, что и в приложении (CoreAdapter.RuntimeConfig), поэтому стенд
// проверяет реальный путь, а не отдельную тестовую сборку конфигурации.
//
// ВНИМАНИЕ: файл содержит учётные данные узла. Он предназначен для одноразовой VM и не
// должен попадать в репозиторий. Рядом пишется сводка без секретов: порт и адреса узлов,
// которые нужно исключить из туннеля.
internal static class ExportCoreConfigCommand
{
    internal static int Run(string key, string nodeName, string configPath, int port)
    {
        var summaryPath = configPath + ".summary.json";
        string url;
        try { url = ServiceKey.ToSubscriptionUrl(key); }
        catch (UserError ex) { File.WriteAllText(summaryPath, "{\"error\":\"ключ не принят\"}"); Console.Error.WriteLine(ex.Message); return 1; }

        ImportResult result;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            result = Subscriptions.FetchAsync(url, timeout.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex) { File.WriteAllText(summaryPath, "{\"error\":\"подписка не загружена\"}"); Console.Error.WriteLine(ex.Message); return 2; }

        var matches = result.Profiles.Where(p => p.DisplayName.Contains(nodeName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 0)
        {
            File.WriteAllText(summaryPath, JsonSerializer.Serialize(new { error = "узел не найден", available = result.Profiles.Select(p => p.DisplayName + " · " + p.Transport).ToArray() }));
            return 3;
        }
        // Тот же путь, которым приложение готовит профиль для системного туннеля:
        // адрес входа закрепляется числовым IP, SNI сохраняется, отдельный XHTTP
        // downstream отклоняется — его нельзя исключить из маршрута.
        TunnelEndpointPlan plan;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            plan = TunnelEndpoints.PrepareAsync(matches[0], timeout.Token).GetAwaiter().GetResult();
        }
        catch (UserError ex)
        {
            File.WriteAllText(summaryPath, JsonSerializer.Serialize(new { error = ex.Message, node = matches[0].DisplayName }));
            return 4;
        }
        var profile = plan.PinnedProfile;
        var peers = plan.Peers;

        var executable = Path.Combine(AppContext.BaseDirectory, "tools", profile.Core == CoreKind.Xray ? "xray.exe" : "sing-box.exe");
        var adapter = new CoreAdapter(profile.Core, executable);
        var config = adapter.RuntimeConfig(profile.Outbound, port);
        File.WriteAllText(configPath, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(summaryPath, JsonSerializer.Serialize(new
        {
            node = profile.DisplayName,
            core = profile.Core.ToString(),
            transport = profile.Transport,
            port,
            peers,
            config = Path.GetFileName(configPath)
        }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}
