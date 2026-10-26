using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Oblachka;

// Машина — записи одной подписки с одним адресом: тот же сервер с разными протоколами
// («Германия · VLESS», «Германия · XHTTP», «Германия · Hysteria2»). В списке она одной строкой,
// протокол Облачка выбирает сама: тот, что работал в этой сети, иначе лучший по замеру.
// Сервису достаточно отдавать все протоколы машины с одним адресом (доменом или IP).
public static class ServerMachines
{
    private static readonly Regex ProtocolWords = new(
        @"\b(vless|vmess|trojan|shadowsocks|ss|ss2022|xhttp|splithttp|reality|tls|hysteria2?|hy2|tuic|anytls|grpc|ws|websocket|httpupgrade|tcp|raw|udp|quic|wireguard|wg|amneziawg|awg)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Ключ машины: подписка и адрес. Бесплатные чужие узлы не объединяются — каждый сам по себе.
    // Ключ нужен окну каждую секунду (счётчик серверов, быстрый список), поэтому, как и отпечаток
    // (Profiles.Fingerprint), считается один раз на запись: профиль не меняют на месте.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Profile, string> keys = new();
    public static string Key(Profile profile) => keys.GetValue(profile, p =>
        p.IsFree ? "free\u0000" + ProfileSelection.Identity(p) : p.Group + "\u0000" + Host(p));

    // Короткий отпечаток ключа — для памяти по сетям в файле состояния (адресов там не храним).
    public static string Hash(Profile profile) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Key(profile))))[..16];

    private static string Host(Profile profile)
    {
        try { return ProfileServer.Of(profile).Host.Trim().TrimEnd('.').ToLowerInvariant(); }
        catch (Exception ex) when (ex is NullReferenceException or InvalidOperationException or KeyNotFoundException) { return "\u0000" + ProfileSelection.Identity(profile); }
    }

    // Машины в порядке первого появления; записи внутри — в порядке подписки.
    public static IReadOnlyList<IReadOnlyList<Profile>> Group(IEnumerable<Profile> profiles) =>
        profiles.GroupBy(Key).Select(g => (IReadOnlyList<Profile>)g.ToArray()).ToArray();

    // Название машины: общее имя записей без слов-протоколов («Германия VLESS» и «Германия XHTTP» → «Германия»).
    public static string Title(IReadOnlyList<Profile> members)
    {
        var names = members.Select(m => m.DisplayName).ToArray();
        if (names.Distinct().Count() == 1) return names[0];
        var stripped = names.Select(n => Clean(ProtocolWords.Replace(n, " "))).ToArray();
        if (stripped[0].Length > 0 && stripped.All(s => s == stripped[0])) return stripped[0];
        var prefix = Clean(names.Aggregate((a, b) => a[..Enumerable.Range(0, Math.Min(a.Length, b.Length)).TakeWhile(i => a[i] == b[i]).Count()]));
        return prefix.Length >= 2 ? prefix : names[0];
    }

    private static string Clean(string text) => Regex.Replace(text, @"[\s·|/,()\[\]_-]+", " ").Trim();

    // Какой протокол машины брать. remembered — отпечаток записи, что работала в этой сети:
    // она первая, если её свежий замер не провалился. Иначе — лучшая по замеру, затем первая в подписке.
    public static Profile Best(IReadOnlyList<Profile> members, Func<Profile, ServerMeasurement?> latest, string? remembered, DateTimeOffset now)
    {
        if (remembered is not null && members.FirstOrDefault(m => Profiles.Fingerprint(m) == remembered) is { } known)
        {
            var last = latest(known);
            if (last is null || last.Success || !ServerMeasurements.Fresh(last, now)) return known;
        }
        var measured = members.Where(m => latest(m) is { Success: true, Handshake: false } r && ServerMeasurements.Fresh(r, now))
            .OrderBy(m => ServerMeasurements.Score(latest(m)!)).FirstOrDefault();
        return measured ?? members.FirstOrDefault(m => latest(m) is not { Success: false }) ?? members[0];
    }

    // Протоколы машины одной строкой, выбранный первым: «Hysteria2 · XHTTP, REALITY · VLESS».
    public static string Protocols(IReadOnlyList<Profile> members, Profile first) =>
        string.Join(" · ", new[] { first }.Concat(members.Where(m => !ReferenceEquals(m, first))).Select(ServerRow.ProtocolFor).Distinct());
}
