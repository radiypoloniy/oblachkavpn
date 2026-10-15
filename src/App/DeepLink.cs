using System.Text;
using System.Web;

namespace Oblachka;

// Ссылки «Добавить в Облачка» с сайта или из бота продавца подписки. Схема oblachka:// регистрируется
// установщиком. Формы (как у Happ, v2rayN, Hiddify — продавцы уже умеют их выдавать):
//   oblachka://add/<адрес подписки>            oblachka://add?url=<адрес, закодированный>
//   oblachka://import/<адрес в base64url>      oblachka://install-config?url=<адрес>
//   oblachka://key/<ключ сервиса>
// Результат — то же, что вставка в поле «Ключ или ссылка»: дальше путь один. Принимаются только
// https-адреса и ключи; подключение — после подтверждения в окне, не молча.
public static class DeepLink
{
    public const string Scheme = "oblachka";
    public const int MaxLength = 8 * 1024;

    public static bool IsLink(string? text) =>
        text is not null && text.StartsWith(Scheme + "://", StringComparison.OrdinalIgnoreCase);

    // Возвращает адрес подписки или ключ; null — ссылка не наша или не разобрана.
    public static string? Parse(string? link)
    {
        if (!IsLink(link) || link!.Length > MaxLength) return null;
        var rest = link[(Scheme.Length + 3)..];
        // «add/<адрес>»: всё после «add/» — адрес, вместе с его собственным «?…». Параметры ссылки
        // (?url=) разбираются, только когда адреса в пути нет.
        var slash = rest.IndexOfAny(new[] { '/', '?' });
        var action = (slash >= 0 ? rest[..slash] : rest).ToLowerInvariant();
        var value = slash >= 0 && rest[slash] == '/' ? rest[(slash + 1)..] : "";
        var parameters = value.Length == 0 && slash >= 0 ? HttpUtility.ParseQueryString(rest[(slash + 1)..].TrimStart('/', '?')) : null;
        string? candidate = action switch
        {
            "add" or "install-config" or "import-remote-profile" => parameters?["url"] ?? Unescape(value),
            "import" => parameters?["url"] ?? FromBase64(value) ?? Unescape(value),
            "key" => Unescape(value),
            _ => null
        };
        candidate = candidate?.Trim();
        if (string.IsNullOrEmpty(candidate)) return null;
        if (action == "key") return ServiceKey.LooksLikeKey(candidate) ? candidate : null;
        // Некоторые боты кодируют адрес в base64 и в форме add.
        if (!candidate.Contains("://", StringComparison.Ordinal) && FromBase64(candidate) is { } decoded) candidate = decoded;
        return Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0
            ? uri.AbsoluteUri.Length <= 2048 ? candidate : null : null;
    }

    private static string Unescape(string value)
    {
        try { return Uri.UnescapeDataString(value); } catch (UriFormatException) { return value; }
    }

    private static string? FromBase64(string value)
    {
        if (value.Length is 0 or > 4096) return null;
        var text = value.Replace('-', '+').Replace('_', '/');
        text = text.PadRight(text.Length + (4 - text.Length % 4) % 4, '=');
        try
        {
            var decoded = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(text));
            return decoded.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? decoded : null;
        }
        catch (Exception ex) when (ex is FormatException or DecoderFallbackException) { return null; }
    }

    // Второй экземпляр передаёт ссылку первому через файл в папке пользователя и будит его.
    public static string PendingFile => Path.Combine(ClientDataPaths.Root, "pending-link.txt");

    public static void Hand(string link)
    {
        try
        {
            Directory.CreateDirectory(ClientDataPaths.Root);
            var staged = PendingFile + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(staged, link);
            File.Move(staged, PendingFile, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static string? TakePending()
    {
        try
        {
            if (!File.Exists(PendingFile)) return null;
            var link = File.ReadAllText(PendingFile);
            File.Delete(PendingFile);
            return link.Length <= MaxLength ? link : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
