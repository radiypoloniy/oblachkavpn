using System.Text.Json.Nodes;

namespace Oblachka;

// «Обход DPI»: TLS-рукопожатие с сервером VPN уходит несколькими TCP-сегментами, чтобы фильтр,
// который ищет имя сайта (SNI) в первом пакете, его не увидел. Шифрование и проверка
// сертификата не меняются; цена — несколько десятков миллисекунд на каждое рукопожатие.
//   Xray:     sockopt.dialerProxy → выход freedom с fragment (tlshello, куски 50–100 байт)
//   sing-box: tls.fragment — режет рукопожатие по границам имени сайта
// Для протоколов без TLS и поверх UDP (Hysteria2, TUIC, WireGuard) не применяется.
public enum FragmentMode { Auto, Always, Off }

public static class TlsFragment
{
    public const string XrayTag = "dpi-fragment";

    public static bool Applies(Profile profile) => profile.Core == CoreKind.Xray
        ? profile.Outbound["streamSettings"]?["security"]?.GetValue<string>() is "tls" or "reality"
        : !ProfileServer.IsUdp(profile) && profile.Outbound["tls"]?["enabled"]?.GetValue<bool>() == true;

    // Меняет готовую конфигурацию ядра на месте. false — для этого профиля неприменимо.
    public static bool Apply(JsonObject config, CoreKind core)
    {
        var tunnel = Tunnel(config);
        if (tunnel is null) return false;
        if (core == CoreKind.SingBox)
        {
            if (tunnel["tls"] is not JsonObject tls || tls["enabled"]?.GetValue<bool>() != true || tunnel["type"]?.GetValue<string>() is "hysteria2" or "tuic") return false;
            tls["fragment"] = true;
            return true;
        }
        if (tunnel["streamSettings"] is not JsonObject stream || stream["security"]?.GetValue<string>() is not ("tls" or "reality")) return false;
        var sockopt = stream["sockopt"] as JsonObject ?? new JsonObject();
        sockopt["dialerProxy"] = XrayTag;
        stream["sockopt"] = sockopt;
        ((JsonArray)config["outbounds"]!).Add(new JsonObject
        {
            ["tag"] = XrayTag, ["protocol"] = "freedom",
            ["settings"] = new JsonObject { ["fragment"] = new JsonObject { ["packets"] = "tlshello", ["length"] = "50-100", ["interval"] = "5-10" } }
        });
        return true;
    }

    private static JsonObject? Tunnel(JsonObject config) =>
        (config["outbounds"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().FirstOrDefault(o => o["tag"]?.GetValue<string>() == "tunnel");
}
