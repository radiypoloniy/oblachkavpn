using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Oblachka;

// DNS по пути трафика. Имена сайтов, которые идут мимо VPN («Российские сайты напрямую»,
// сайты zapret, правила «напрямую»), разрешает DNS физической сети, остальные — DoH внутри
// туннеля. Раньше всё разрешалось через туннель, то есть из-за границы: Google отдавал
// узлы YouTube рядом с сервером VPN, и прямое соединение из России шло к заграничному
// узлу, который провайдер режет. Проверка zapret видела то же самое и врала, что YouTube
// напрямую не работает, хотя без VPN он открывался.
//
// Список доменов окно передаёт частями (сообщение службы — до 32 КБ), служба проверяет
// каждый и пишет файл правил, который ядро туннеля перечитывает само: менять список можно
// без переподключения. Файл лежит в папке службы, писать туда может только она.
public sealed record DirectDnsRequest(int Part, bool Last, string[] Domains)
{
    public const int MaxParts = 64, MaxPerPart = 900;
    private static readonly Regex Name = new(@"^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)*[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$", RegexOptions.CultureInvariant);

    public static bool Valid(string domain) => Name.IsMatch(domain);

    public void Validate()
    {
        if (Part is < 0 or >= MaxParts || Domains is null || Domains.Length > MaxPerPart || Domains.Any(d => d is null || !Valid(d)))
            throw new UserError("Неверный список прямых сайтов.");
    }

    // Части по размеру сообщения.
    public static IEnumerable<DirectDnsRequest> Split(IReadOnlyList<string> domains)
    {
        var parts = domains.Chunk(MaxPerPart).Take(MaxParts).ToArray();
        if (parts.Length == 0) { yield return new(0, true, []); yield break; }
        for (var i = 0; i < parts.Length; i++) yield return new(i, i == parts.Length - 1, parts[i]);
    }
}

public static class DirectDns
{
    public const string ServerTag = "local-dns", RuleSetTag = "direct-dns";
    private static readonly object Gate = new();
    private static readonly List<string> pending = new();

    public static string RuleSetPath => NetworkHelperHost.DataPath("direct-dns.json");

    // Части собираются по порядку; последняя записывает файл. Чужой порядок — начать заново.
    public static void Accept(DirectDnsRequest request)
    {
        request.Validate();
        lock (Gate)
        {
            if (request.Part == 0) pending.Clear();
            pending.AddRange(request.Domains);
            if (request.Last) { Write(pending.Distinct(StringComparer.Ordinal).ToArray()); pending.Clear(); }
        }
    }

    // Файл есть всегда: без него ядро туннеля не запустится. Пустой список — заглушка.
    public static void EnsureFile() { lock (Gate) if (!File.Exists(RuleSetPath)) Write([]); }

    private static void Write(string[] domains)
    {
        var rules = new JsonArray(new JsonObject { ["domain_suffix"] = new JsonArray((domains.Length > 0 ? domains : ["oblachka.invalid"]).Select(d => (JsonNode?)JsonValue.Create(d)).ToArray()) });
        var json = new JsonObject { ["version"] = 2, ["rules"] = rules }.ToJsonString();
        Directory.CreateDirectory(Path.GetDirectoryName(RuleSetPath)!);
        var temporary = RuleSetPath + ".tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, RuleSetPath, overwrite: true);
    }

    // DNS физической сети: IPv4-резолвер адаптера со шлюзом, не туннеля. null — не нашли,
    // тогда всё разрешается через туннель, как раньше.
    public static IPAddress? PhysicalResolver()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up &&
                n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)))
            {
                if (nic.Name.StartsWith(WindowsTunnelBackend.InterfacePrefix, StringComparison.OrdinalIgnoreCase) || ForeignTunnel.IsForeign(nic.Name, (int)nic.NetworkInterfaceType, nic.Description)) continue;
                var properties = nic.GetIPProperties();
                if (!properties.GatewayAddresses.Any(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))) continue;
                if (properties.DnsAddresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(a) && !a.Equals(IPAddress.Any)) is { } dns) return dns;
            }
        }
        catch (NetworkInformationException) { }
        return null;
    }
}
