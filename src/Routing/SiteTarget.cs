using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Oblachka;

// Цель правила сайта: домен (example.com), подсеть (10.20.0.0/16, адрес — как /32 или /128) или порт
// (port:25, port:27015-27030). Хранится строкой в SiteRule.Domain: правила подсетей и портов живут в
// том же списке, в тех же файлах на ходу (SiteRuleSets) и с тем же порядком «первое совпадение».
public enum SiteTargetKind { Domain, Subnet, Port }

public static partial class SiteTarget
{
    [GeneratedRegex(@"^port:(\d{1,5})(?:-(\d{1,5}))?$")]
    private static partial Regex PortSpec();

    public static SiteTargetKind Kind(string target) =>
        target.StartsWith("port:", StringComparison.Ordinal) ? SiteTargetKind.Port : target.Contains('/') ? SiteTargetKind.Subnet : SiteTargetKind.Domain;

    // Подсеть и порт — к виду, в котором хранятся; домен — null (его разбирает RoutingPlan.Create).
    public static string? TryNormalize(string text)
    {
        text = text.Trim().ToLowerInvariant();
        if (text.StartsWith("port:", StringComparison.Ordinal) || text.StartsWith("порт:", StringComparison.Ordinal))
        {
            var match = PortSpec().Match("port:" + text[5..].Trim());
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out var low) || low is < 1 or > 65535) throw new UserError("Порт — число от 1 до 65535 или диапазон, например port:27015-27030.");
            if (!match.Groups[2].Success) return "port:" + low;
            if (!int.TryParse(match.Groups[2].Value, out var high) || high <= low || high > 65535) throw new UserError("Диапазон портов: меньший — первым, например port:27015-27030.");
            return "port:" + low + "-" + high;
        }
        if (System.Net.IPAddress.TryParse(text, out var single))
            return single + (single.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? "/32" : "/128");
        if (!text.Contains('/')) return null;
        // /0 — весь интернет: это уже не правило, а режим; для этого есть «Через VPN только выбранное».
        if (!AdvancedOptions.TryParseSubnet(text, out _, out var prefix) || prefix == 0) throw new UserError("Подсеть — адрес и длина, например 10.20.0.0/16.");
        return TunnelTuning.Normalize(text);
    }

    // Порядок «первое совпадение»: поддомен раньше домена (больше точек); подсети и порты — после
    // доменов: точки в адресе подсети к вложенности доменов отношения не имеют.
    public static int Order(string target) => Kind(target) == SiteTargetKind.Domain ? target.Count(c => c == '.') : -1;

    // Условие правила sing-box (маршрут и rule-set).
    public static JsonObject SingBox(string target) => Kind(target) switch
    {
        SiteTargetKind.Subnet => new JsonObject { ["ip_cidr"] = new JsonArray(target) },
        SiteTargetKind.Port => Port(target) is (var low, var high) && low == high
            ? new JsonObject { ["port"] = new JsonArray(low) } : new JsonObject { ["port_range"] = new JsonArray(Port(target).Low + ":" + Port(target).High) },
        _ => new JsonObject { ["domain_suffix"] = new JsonArray(target) }
    };

    // Условие правила Xray.
    public static JsonObject Xray(string target) => Kind(target) switch
    {
        SiteTargetKind.Subnet => new JsonObject { ["ip"] = new JsonArray(target) },
        SiteTargetKind.Port => new JsonObject { ["port"] = Port(target) is (var low, var high) && low == high ? low.ToString() : low + "-" + high },
        _ => new JsonObject { ["domain"] = new JsonArray("domain:" + target) }
    };

    public static (int Low, int High) Port(string target)
    {
        var match = PortSpec().Match(target);
        var low = int.Parse(match.Groups[1].Value);
        return (low, match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : low);
    }
}
