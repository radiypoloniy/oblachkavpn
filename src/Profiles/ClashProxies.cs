using System.Text;
using System.Text.Json.Nodes;

namespace Oblachka;

// Записи Clash / Mihomo, кроме vless, trojan и hysteria2 (они в ClashSubscription): ss, vmess,
// tuic, anytls, wireguard. Каждая переводится в ссылку и проходит тот же разбор и те же
// проверки (ShareLinks → SingBoxProfiles). null — запись не поддерживается.
internal static class ClashProxies
{
    public static string? Link(Dictionary<string, object?> p, string? type, string host, string port, string name)
    {
        string? S(string key) => p.TryGetValue(key, out var v) ? v as string : null;
        Dictionary<string, object?> M(string key) => p.TryGetValue(key, out var v) && v is Dictionary<string, object?> d ? d : new();
        string? List(string key) => p.TryGetValue(key, out var v) ? v is List<object?> list ? string.Join(',', list.OfType<string>()) : v as string : null;
        var insecure = S("skip-cert-verify") == "true";
        var tag = "#" + Uri.EscapeDataString(name);
        switch (type)
        {
            case "ss":
                // Плагины (obfs, v2ray-plugin, shadow-tls) не поддерживаются.
                if (S("plugin") is { Length: > 0 } || S("cipher") is not { } cipher || S("password") is not { } password) return null;
                return "ss://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(cipher + ":" + password)).TrimEnd('=').Replace('+', '-').Replace('/', '_') +
                    "@" + host + ":" + port + tag;
            case "vmess":
                if (S("uuid") is not { } uuid) return null;
                var network = S("network") ?? "tcp";
                var v = new JsonObject
                {
                    ["v"] = "2", ["ps"] = name, ["add"] = host.Trim('[', ']'), ["port"] = port, ["id"] = uuid, ["aid"] = S("alterId") ?? "0",
                    ["scy"] = S("cipher") ?? "auto", ["net"] = network, ["tls"] = S("tls") == "true" ? "tls" : "",
                    ["sni"] = S("servername") ?? "", ["alpn"] = List("alpn") ?? "", ["fp"] = S("client-fingerprint") ?? "", ["allowInsecure"] = insecure ? "1" : "0"
                };
                var opts = M(network + "-opts");
                switch (network)
                {
                    case "ws":
                        v["path"] = opts.GetValueOrDefault("path") as string ?? "/";
                        v["host"] = (opts.GetValueOrDefault("headers") as Dictionary<string, object?>)?.GetValueOrDefault("Host") as string ?? "";
                        break;
                    case "grpc": v["path"] = opts.GetValueOrDefault("grpc-service-name") as string ?? ""; break;
                    case "h2":
                        v["path"] = opts.GetValueOrDefault("path") as string ?? "/";
                        v["host"] = opts.GetValueOrDefault("host") is List<object?> hosts ? string.Join(',', hosts.OfType<string>()) : "";
                        break;
                    case "tcp": if (M("http-opts").Count > 0) return null; break;
                    default: return null;
                }
                return "vmess://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(v.ToJsonString()));
            case "tuic":
                // TUIC v4 (token) устарел — только v5 с uuid и паролем.
                if (S("token") is not null || S("uuid") is not { } id || S("password") is not { } secret) return null;
                var q = new List<string>();
                void Put(string key, string? value) { if (!string.IsNullOrEmpty(value)) q.Add(key + "=" + Uri.EscapeDataString(value)); }
                Put("sni", S("sni")); Put("alpn", List("alpn")); Put("congestion_control", S("congestion-controller"));
                Put("udp_relay_mode", S("udp-relay-mode")); if (S("disable-sni") == "true") Put("disable_sni", "1"); if (insecure) Put("insecure", "1");
                return "tuic://" + Uri.EscapeDataString(id) + ":" + Uri.EscapeDataString(secret) + "@" + host + ":" + port + (q.Count > 0 ? "?" + string.Join('&', q) : "") + tag;
            case "anytls":
                if (S("password") is not { } anyPassword) return null;
                var a = new List<string>();
                if (S("sni") is { Length: > 0 } sni) a.Add("sni=" + Uri.EscapeDataString(sni));
                if (S("client-fingerprint") is { Length: > 0 } fp) a.Add("fp=" + Uri.EscapeDataString(fp));
                if (List("alpn") is { Length: > 0 } alpn) a.Add("alpn=" + Uri.EscapeDataString(alpn));
                if (insecure) a.Add("insecure=1");
                return "anytls://" + Uri.EscapeDataString(anyPassword) + "@" + host + ":" + port + (a.Count > 0 ? "?" + string.Join('&', a) : "") + tag;
            case "wireguard":
                // Несколько peers и параметры AmneziaWG не поддерживаются.
                if (p.ContainsKey("peers") || p.ContainsKey("amnezia-wg-option") || S("private-key") is not { } key || S("public-key") is not { } peerKey) return null;
                var addresses = new[] { S("ip") is { Length: > 0 } ip4 ? (ip4.Contains('/') ? ip4 : ip4 + "/32") : null,
                    S("ipv6") is { Length: > 0 } ip6 ? (ip6.Contains('/') ? ip6 : ip6 + "/128") : null }.OfType<string>().ToArray();
                if (addresses.Length == 0) return null;
                var w = new List<string> { "publickey=" + Uri.EscapeDataString(peerKey), "address=" + Uri.EscapeDataString(string.Join(',', addresses)) };
                if (S("pre-shared-key") is { Length: > 0 } psk) w.Add("presharedkey=" + Uri.EscapeDataString(psk));
                if (S("mtu") is { Length: > 0 } mtu) w.Add("mtu=" + mtu);
                if (List("reserved") is { Length: > 0 } reserved) w.Add("reserved=" + Uri.EscapeDataString(reserved));
                return "wireguard://" + Uri.EscapeDataString(key) + "@" + host + ":" + port + "?" + string.Join('&', w) + tag;
            default:
                return null;
        }
    }
}
