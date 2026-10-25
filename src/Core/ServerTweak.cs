using System.Text.Json.Nodes;

namespace Oblachka;

// Поправка к серверу от «Подобрать лучшее» (TuneSearch): только то, что сервер не видит и о чём не
// договаривается, — отпечаток TLS, нарезка рукопожатия, управление скоростью Hysteria2. Сломать
// подключение такой правкой нельзя. Хранится рядом с подпиской, по источнику и имени сервера:
// обновление подписки её не стирает, а удалённый сервер просто не находит своей поправки.
public sealed record ServerTweak(string? Fingerprint = null, bool Fragment = false, bool Bbr = false)
{
    // Отпечатки для подбора: самые распространённые браузеры и случайный.
    public static readonly string[] TriedFingerprints = ["chrome", "firefox", "safari", "random"];

    public bool IsEmpty => Fingerprint is null && !Fragment && !Bbr;
    public bool Valid => Fingerprint is null || Utls.Names.Contains(Fingerprint);

    public static string Key(Profile profile) => profile.Source + ":" + profile.Name;

    // Сохранённые поправки; окно подставляет свой словарь. Без окна (проверки, служба) — никаких.
    public static Func<Profile, ServerTweak?> Lookup { get; set; } = _ => null;

    // Профиль с поправкой: сохранённый не меняется, его хеш и замеры остаются прежними.
    public Profile Apply(Profile profile)
    {
        if (Fingerprint is null && !Bbr) return profile;
        var copy = (JsonObject)profile.Outbound.DeepClone();
        if (Fingerprint is { } fp) SetFingerprint(copy, profile.Core, fp);
        // Hysteria2 с заданной скоростью шлёт с ней (Brutal), даже когда канал уже; без неё — BBR.
        if (Bbr && profile.Core == CoreKind.SingBox && copy["type"]?.GetValue<string>() == "hysteria2") { copy.Remove("up_mbps"); copy.Remove("down_mbps"); }
        return profile with { Outbound = copy };
    }

    // Где у профиля отпечаток TLS; null — его нет (протокол без TLS, QUIC).
    internal static JsonObject? FingerprintHolder(JsonObject outbound, CoreKind core)
    {
        if (core == CoreKind.Xray)
            return outbound["streamSettings"] is JsonObject stream && stream["security"]?.GetValue<string>() is { } security and ("tls" or "reality")
                ? stream[security + "Settings"] as JsonObject : null;
        if (outbound["tls"] is not JsonObject tls || tls["enabled"]?.GetValue<bool>() != true || outbound["type"]?.GetValue<string>() is "hysteria2" or "tuic") return null;
        return tls;
    }

    private static void SetFingerprint(JsonObject outbound, CoreKind core, string fp)
    {
        if (FingerprintHolder(outbound, core) is not { } holder) return;
        if (core == CoreKind.Xray) holder["fingerprint"] = fp;
        else holder["utls"] = new JsonObject { ["enabled"] = true, ["fingerprint"] = fp };
    }

    private static string? CurrentFingerprint(Profile profile) => FingerprintHolder(profile.Outbound, profile.Core) is not { } holder ? null
        : profile.Core == CoreKind.Xray ? holder["fingerprint"]?.GetValue<string>() ?? "chrome" : holder["utls"]?["fingerprint"]?.GetValue<string>() ?? "chrome";

    // Варианты для подбора: как в подписке и по одной поправке за раз.
    public static IReadOnlyList<ServerTweak> Candidates(Profile profile)
    {
        var list = new List<ServerTweak> { new() };
        if (CurrentFingerprint(profile) is { } current)
            list.AddRange(TriedFingerprints.Where(fp => fp != current).Select(fp => new ServerTweak(Fingerprint: fp)));
        if (TlsFragment.Applies(profile)) list.Add(new(Fragment: true));
        if (profile.Core == CoreKind.SingBox && profile.Outbound["type"]?.GetValue<string>() == "hysteria2" && (profile.Outbound["up_mbps"] ?? profile.Outbound["down_mbps"]) is not null)
            list.Add(new(Bbr: true));
        return list;
    }

    // Словами для карточки итога.
    public string Describe()
    {
        var parts = new List<string>();
        if (Fingerprint is { } fp) parts.Add("отпечаток " + (fp == "random" ? "случайный" : char.ToUpperInvariant(fp[0]) + fp[1..]));
        if (Fragment) parts.Add("нарезка рукопожатия");
        if (Bbr) parts.Add("скорость Hysteria2 — по каналу");
        return parts.Count == 0 ? "как в подписке" : string.Join(", ", parts);
    }
}
