using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Oblachka;

// Списки маршрутизации («Российские сайты напрямую», списки сервиса, сайты zapret) в ядре туннеля.
// Раньше их исполняло ядро окна, и «Весь компьютер» со списками шёл через два ядра и SOCKS.
// Теперь окно передаёт службе готовые наборы (CompiledRouting) частями, служба проверяет каждую
// запись и пишет три файла правил; ядро туннеля перечитывает их само — списки меняются на ходу.
// В аренде — только порядок наборов и «остальное через VPN или напрямую» (RoutingListsEntry).
public sealed record RoutingListsRequest(int Part, bool Last, string Set, string Kind, string[] Items)
{
    public const int MaxParts = 512, MaxPartBytes = 24 * 1024;
    public static readonly string[] Sets = { "direct", "proxy", "block" };
    public static readonly string[] Kinds = { "suffix", "full", "keyword", "regex", "cidr" };
    private static readonly Regex Keyword = new("^[a-z0-9.-]{1,63}$", RegexOptions.CultureInvariant);

    public void Validate()
    {
        if (Part is < 0 or >= MaxParts || !Sets.Contains(Set) || !Kinds.Contains(Kind) || Items is null || Items.Length > 4096 || Items.Any(i => !Valid(Kind, i)))
            throw new UserError("Неверная часть списков маршрутизации.");
    }

    public static bool Valid(string kind, string? item) => item is not null && kind switch
    {
        "suffix" or "full" => DirectDnsRequest.Valid(item),
        "keyword" => Keyword.IsMatch(item),
        "regex" => SafeRegex(item),
        _ => Cidr(item)
    };

    // Регулярные выражения исполняет Go (RE2): заглядываний и обратных ссылок там нет, а запись,
    // которую ядро не разберёт, не должна сорвать запуск туннеля. Сомнительные — отбрасываются.
    private static bool SafeRegex(string item)
    {
        if (item.Length is < 1 or > 256 || item.Any(c => c < 0x20 || c > 0x7e) || item.Contains("(?") || Regex.IsMatch(item, @"\\[1-9kpP]")) return false;
        try { _ = new Regex(item, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)); return true; }
        catch (ArgumentException) { return false; }
    }

    private static bool Cidr(string item) => item.Length <= 43 && (item.Contains('/') ? IPNetwork.TryParse(item, out _) : IPAddress.TryParse(item, out _));

    // Части по размеру сообщения службы: наборы и виды записей подряд, в каждой — один вид.
    public static IEnumerable<RoutingListsRequest> Split(CompiledRouting routing)
    {
        var parts = new List<(string Set, string Kind, string[] Items)>();
        foreach (var set in Sets)
        {
            var matchers = set switch { "direct" => routing.Direct, "proxy" => routing.Proxy, _ => routing.Block };
            foreach (var (kind, items) in new[] { ("suffix", matchers.Suffix), ("full", matchers.Full), ("keyword", matchers.Keyword), ("regex", matchers.Regex), ("cidr", matchers.Cidr) })
            {
                var chunk = new List<string>(); var bytes = 0;
                foreach (var item in items.Where(i => Valid(kind, i)).Order(StringComparer.Ordinal))
                {
                    if (chunk.Count > 0 && (bytes + item.Length + 4 > MaxPartBytes || chunk.Count == 4096)) { parts.Add((set, kind, chunk.ToArray())); chunk.Clear(); bytes = 0; }
                    chunk.Add(item); bytes += item.Length + 4;
                }
                if (chunk.Count > 0) parts.Add((set, kind, chunk.ToArray()));
            }
        }
        if (parts.Count == 0) parts.Add(("direct", "suffix", []));
        if (parts.Count > MaxParts) throw new UserError("Списки маршрутизации слишком большие для ядра туннеля.");
        for (var i = 0; i < parts.Count; i++) yield return new(i, i == parts.Count - 1, parts[i].Set, parts[i].Kind, parts[i].Items);
    }
}

// Порядок наборов (как задал сервис: «block-direct-proxy») и что делать с остальным трафиком.
public sealed record RoutingListsEntry(string[] Order, bool GlobalProxy)
{
    public void Validate()
    {
        if (Order is not { Length: >= 1 and <= 3 } || Order.Any(o => !RoutingListsRequest.Sets.Contains(o)) || Order.Distinct().Count() != Order.Length)
            throw new UserError("Неверный порядок списков маршрутизации.");
    }
}

public static class RoutingLists
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, HashSet<string>> pending = new();

    public static string Folder => NetworkHelperHost.DataPath("routing-lists");
    public static string PathOf(string folder, string set) => Path.Combine(folder, "routing-" + set + ".json");
    public static string Tag(string set) => "routing-" + set;

    // Части собираются по порядку; последняя записывает файлы. Часть 0 — начать заново.
    public static void Accept(RoutingListsRequest request)
    {
        request.Validate();
        lock (Gate)
        {
            if (request.Part == 0) pending.Clear();
            var key = request.Set + ":" + request.Kind;
            if (!pending.TryGetValue(key, out var items)) pending[key] = items = new(StringComparer.Ordinal);
            items.UnionWith(request.Items);
            if (!request.Last) return;
            foreach (var set in RoutingListsRequest.Sets) Write(Folder, set, kind => pending.TryGetValue(set + ":" + kind, out var found) ? found.ToArray() : []);
            pending.Clear();
        }
    }

    // Файлы есть всегда: без них ядро туннеля не запустится. Пустой набор — заглушка.
    public static void EnsureFiles(string folder)
    {
        lock (Gate)
            foreach (var set in RoutingListsRequest.Sets)
                if (!File.Exists(PathOf(folder, set))) Write(folder, set, _ => []);
    }

    internal static void Write(string folder, string set, Func<string, string[]> items)
    {
        static JsonArray Array(IEnumerable<string> values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
        var sites = new JsonObject();
        foreach (var (kind, field) in new[] { ("suffix", "domain_suffix"), ("full", "domain"), ("keyword", "domain_keyword"), ("regex", "domain_regex") })
            if (items(kind) is { Length: > 0 } values) sites[field] = Array(values);
        // Домены и адреса — разные правила: в одном правиле sing-box требовал бы совпадения обоих.
        var rules = new JsonArray(sites.Count > 0 ? sites : new JsonObject { ["domain"] = Array(["oblachka.invalid"]) });
        if (items("cidr") is { Length: > 0 } cidr) rules.Add(new JsonObject { ["ip_cidr"] = Array(cidr) });
        Directory.CreateDirectory(folder);
        var path = PathOf(folder, set); var temporary = path + ".tmp";
        File.WriteAllText(temporary, new JsonObject { ["version"] = 2, ["rules"] = rules }.ToJsonString());
        File.Move(temporary, path, overwrite: true);
    }
}
