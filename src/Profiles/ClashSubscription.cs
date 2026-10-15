using System.Text;
using System.Text.RegularExpressions;

namespace Oblachka;

// Подписки Clash / Mihomo (YAML): раздел proxies переводится в ссылки (vless, trojan, hy2 — здесь, остальные — в ClashProxies)
// и проходит через тот же разбор и те же проверки, что и обычная подписка (ShareLinks).
// YAML разбирается ровно настолько, насколько он нужен для proxies: блоки, списки, {…} и […],
// кавычки, комментарии. Якоря, многострочные строки и теги не поддерживаются — такие записи
// просто не попадают в список.
public static class ClashSubscription
{
    private static readonly Regex ProxiesKey = new(@"^proxies:\s*(#.*)?$", RegexOptions.Multiline | RegexOptions.CultureInvariant);

    public static bool Looks(string text) => ProxiesKey.IsMatch(text);

    public static IReadOnlyList<string> ToLinks(string text, out int unsupported)
    {
        unsupported = 0;
        var lines = text.Replace("\r", "").Split('\n');
        var start = Array.FindIndex(lines, l => ProxiesKey.IsMatch(l));
        var block = new List<(int Indent, string Text)>();
        for (var i = start + 1; i < lines.Length; i++)
        {
            var raw = lines[i];
            var content = StripComment(raw).TrimEnd();
            if (content.Trim().Length == 0) continue;
            var indent = content.Length - content.TrimStart().Length;
            if (indent == 0 && !content.StartsWith('-')) break;
            block.Add((indent, content.Trim()));
        }
        if (block.Count == 0) return Array.Empty<string>();
        var index = 0;
        var items = ParseBlock(block, ref index, block[0].Indent) as List<object?> ?? new();
        var links = new List<string>();
        foreach (var item in items)
            if (item is Dictionary<string, object?> proxy && ToLink(proxy) is { } link) links.Add(link); else unsupported++;
        return links;
    }

    private static string? ToLink(Dictionary<string, object?> p)
    {
        string? S(string key) => p.TryGetValue(key, out var v) ? v as string : null;
        Dictionary<string, object?> M(string key) => p.TryGetValue(key, out var v) && v is Dictionary<string, object?> d ? d : new();
        var (type, server, port, name) = (S("type")?.ToLowerInvariant(), S("server"), S("port"), S("name") ?? "");
        if (server is null || port is null || !int.TryParse(port, out _)) return null;
        var host = server.Contains(':') ? "[" + server + "]" : server;
        var q = new List<(string, string)>();
        void Put(string key, string? value) { if (!string.IsNullOrEmpty(value)) q.Add((key, value)); }
        if (S("skip-cert-verify") == "true") Put("insecure", "1");
        if (type is "vless" or "trojan")
        {
            var reality = M("reality-opts");
            var security = reality.Count > 0 ? "reality" : S("tls") == "true" || type == "trojan" ? "tls" : "none";
            var network = S("network") ?? "tcp";
            Put("type", network); Put("security", security);
            Put("sni", S("servername") ?? S("sni")); Put("fp", S("client-fingerprint"));
            Put("pbk", reality.GetValueOrDefault("public-key") as string); Put("sid", reality.GetValueOrDefault("short-id") as string);
            if (p.TryGetValue("alpn", out var alpn) && alpn is List<object?> list) Put("alpn", string.Join(',', list.OfType<string>()));
            var opts = M(network + "-opts");
            switch (network)
            {
                case "ws":
                    Put("path", opts.GetValueOrDefault("path") as string);
                    Put("host", (opts.GetValueOrDefault("headers") as Dictionary<string, object?>)?.GetValueOrDefault("Host") as string);
                    break;
                case "grpc": Put("serviceName", opts.GetValueOrDefault("grpc-service-name") as string); break;
                case "xhttp":
                    Put("path", opts.GetValueOrDefault("path") as string); Put("host", opts.GetValueOrDefault("host") as string); Put("mode", opts.GetValueOrDefault("mode") as string);
                    if (ClashXhttp.Extra(opts) is { Count: > 0 } extra) Put("extra", extra.ToJsonString());
                    break;
                case "tcp": break;
                default: return null;
            }
            if (type == "vless")
            {
                if (S("uuid") is not { } uuid) return null;
                Put("encryption", "none"); Put("flow", S("flow"));
                return "vless://" + Uri.EscapeDataString(uuid) + "@" + host + ":" + port + Query(q) + "#" + Uri.EscapeDataString(name);
            }
            return S("password") is { } password ? "trojan://" + Uri.EscapeDataString(password) + "@" + host + ":" + port + Query(q) + "#" + Uri.EscapeDataString(name) : null;
        }
        if (type is "hysteria2" or "hy2")
        {
            if ((S("password") ?? S("auth")) is not { } password) return null;
            Put("sni", S("sni")); Put("obfs", S("obfs")); Put("obfs-password", S("obfs-password")); Put("mport", S("ports"));
            return "hy2://" + Uri.EscapeDataString(password) + "@" + host + ":" + port + Query(q) + "#" + Uri.EscapeDataString(name);
        }
        return ClashProxies.Link(p, type, host, port, name);
    }

    private static string Query(List<(string Key, string Value)> q) =>
        q.Count == 0 ? "" : "?" + string.Join('&', q.Select(x => x.Key + "=" + Uri.EscapeDataString(x.Value)));

    private static object? ParseBlock(List<(int Indent, string Text)> lines, ref int i, int indent) =>
        i < lines.Count && (lines[i].Text == "-" || lines[i].Text.StartsWith("- ")) ? ParseSequence(lines, ref i, indent) : ParseMapping(lines, ref i, indent);

    private static List<object?> ParseSequence(List<(int Indent, string Text)> lines, ref int i, int indent)
    {
        var list = new List<object?>();
        while (i < lines.Count && lines[i].Indent == indent && (lines[i].Text == "-" || lines[i].Text.StartsWith("- ")))
        {
            var rest = lines[i].Text.Length > 1 ? lines[i].Text[2..].TrimStart() : "";
            if (rest.Length == 0) { i++; list.Add(i < lines.Count && lines[i].Indent > indent ? ParseBlock(lines, ref i, lines[i].Indent) : null); }
            else if (rest.StartsWith('{') || rest.StartsWith('[') || KeyOf(rest) is null) { list.Add(Scalar(rest)); i++; }
            else
            {
                // «- name: x» — отображение, его первая строка с отступом после «- ».
                var inner = indent + (lines[i].Text.Length - rest.Length);
                lines[i] = (inner, rest);
                list.Add(ParseMapping(lines, ref i, inner));
            }
        }
        return list;
    }

    private static Dictionary<string, object?> ParseMapping(List<(int Indent, string Text)> lines, ref int i, int indent)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        while (i < lines.Count && lines[i].Indent == indent && !lines[i].Text.StartsWith("- ") && KeyOf(lines[i].Text) is { } key)
        {
            var value = lines[i].Text[(lines[i].Text.IndexOf(':', key.Length) + 1)..].Trim();
            i++;
            if (value.Length > 0) map[Unquote(key)] = Scalar(value);
            else if (i < lines.Count && (lines[i].Indent > indent || lines[i].Indent == indent && lines[i].Text.StartsWith("- ")))
                map[Unquote(key)] = ParseBlock(lines, ref i, lines[i].Indent);
            else map[Unquote(key)] = null;
        }
        return map;
    }

    // Ключ «key:» или «key: значение»; кавычки в ключе допускаются.
    private static string? KeyOf(string text)
    {
        var quote = text.Length > 0 && text[0] is '"' or '\'' ? text[0] : '\0';
        var end = quote != '\0' ? text.IndexOf(quote, 1) + 1 : 0;
        if (quote != '\0' && end <= 0) return null;
        var colon = text.IndexOf(':', end);
        while (colon >= 0 && colon + 1 < text.Length && text[colon + 1] != ' ') colon = text.IndexOf(':', colon + 1);
        return colon > 0 ? text[..colon].Trim() : null;
    }

    private static object? Scalar(string text)
    {
        text = text.Trim();
        if (text.StartsWith('{') || text.StartsWith('[')) { var position = 0; return Flow(text, ref position); }
        return Unquote(text);
    }

    // Встроенные {…} и […], вложенные любым образом.
    private static object? Flow(string text, ref int p)
    {
        void Skip(ref int pos) { while (pos < text.Length && text[pos] == ' ') pos++; }
        Skip(ref p);
        if (p >= text.Length) return null;
        if (text[p] == '{' || text[p] == '[')
        {
            var open = text[p++]; var close = open == '{' ? '}' : ']';
            var map = new Dictionary<string, object?>(StringComparer.Ordinal); var list = new List<object?>();
            while (true)
            {
                Skip(ref p);
                if (p >= text.Length) break;
                if (text[p] == close) { p++; break; }
                if (text[p] == ',') { p++; continue; }
                if (open == '{')
                {
                    var key = Token(text, ref p, ':');
                    if (p < text.Length && text[p] == ':') p++;
                    map[Unquote(key)] = Flow(text, ref p);
                }
                else list.Add(Flow(text, ref p));
            }
            return open == '{' ? map : list;
        }
        return Unquote(Token(text, ref p, ',', '}', ']'));
    }

    private static string Token(string text, ref int p, params char[] stops)
    {
        var start = p;
        if (p < text.Length && text[p] is '"' or '\'')
        {
            var quote = text[p++];
            while (p < text.Length && !(text[p] == quote && (quote != '\'' || p + 1 >= text.Length || text[p + 1] != '\''))) p += text[p] == quote ? 2 : 1;
            p = Math.Min(p + 1, text.Length);
            return text[start..p];
        }
        while (p < text.Length && !stops.Contains(text[p])) p++;
        return text[start..p].Trim();
    }

    private static string Unquote(string text)
    {
        text = text.Trim();
        if (text.Length >= 2 && text[0] == '\'' && text[^1] == '\'') return text[1..^1].Replace("''", "'");
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
            return Regex.Unescape(text[1..^1]);
        return text;
    }

    private static string StripComment(string line)
    {
        var quote = '\0';
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (c is '"' or '\'') quote = c;
            else if (c == '#' && (i == 0 || line[i - 1] == ' ')) return line[..i];
        }
        return line;
    }
}
