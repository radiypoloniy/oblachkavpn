namespace RoutePilot;

// Ключ подписки сервиса: то, что пользователь вводит на первом экране.
// Принимается и короткий ключ, и целая ссылка сервиса — люди вставляют то, что им выдали.
// Чужие хосты в этот путь не попадают: для них есть раздел сторонних подписок.
public static class ServiceKey
{
    // Токены Remnawave — base64url. У проверенной подписки 25 символов; границы взяты
    // с запасом в обе стороны, чтобы смена длины в панели не ломала клиент.
    public const int MinLength = 8;
    public const int MaxLength = 128;

    public static bool LooksLikeKey(string input) =>
        input.Length >= MinLength && input.Length <= MaxLength &&
        input.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    // Возвращает полный адрес подписки сервиса. Бросает UserError с текстом для пользователя.
    public static string ToSubscriptionUrl(string input)
    {
        if (input is null) throw new UserError("Введи ключ подписки.");
        // Пробелы внутри ключа — почти всегда следствие копирования из переписки.
        var text = new string(input.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (text.Length == 0) throw new UserError("Введи ключ подписки.");
        if (text.Length > 8 * 1024) throw new UserError("Это слишком длинно для ключа подписки.");

        if (text.Contains("://", StringComparison.Ordinal)) return FromUrl(text);
        // Пользователь мог вставить адрес без схемы.
        if (text.Contains('/')) return FromUrl("https://" + text);

        if (!LooksLikeKey(text))
            throw new UserError("Ключ состоит из латинских букв, цифр, дефиса и подчёркивания. Проверьте, что скопировали его целиком.");
        return "https://" + ServiceBrand.SubscriptionHosts[0] + ServiceBrand.SubscriptionPath + text;
    }

    private static string FromUrl(string text)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
            throw new UserError("Не удалось прочитать адрес. Введи ключ подписки или полную ссылку сервиса.");
        if (uri.Scheme != Uri.UriSchemeHttps)
            throw new UserError("Ссылка подписки должна начинаться с https://.");
        if (uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
            throw new UserError("В ссылке подписки не должно быть логина, пароля и части после #.");
        if (!ServiceBrand.IsServiceHost(uri.Host))
            throw new UserError($"Это подписка другого сервиса, а не {ServiceBrand.Name}. Добавьте её в «Настройки → Другие подписки».");

        var key = uri.AbsolutePath;
        if (key.StartsWith(ServiceBrand.SubscriptionPath, StringComparison.OrdinalIgnoreCase))
            key = key.Substring(ServiceBrand.SubscriptionPath.Length);
        key = key.Trim('/');
        if (key.Length == 0 || key.Contains('/'))
            throw new UserError($"В ссылке нет ключа подписки. Нужен адрес вида https://{ServiceBrand.SubscriptionHosts[0]}{ServiceBrand.SubscriptionPath}ключ.");
        if (!LooksLikeKey(key))
            throw new UserError("Ключ в ссылке содержит недопустимые символы. Проверьте, что скопировали ссылку целиком.");
        // Нормализуем: запрос и регистр хоста не влияют на выдачу, ключ остаётся как есть.
        return "https://" + ServiceBrand.SubscriptionHosts[0].ToLowerInvariant() + ServiceBrand.SubscriptionPath + key;
    }

    // Обратная операция для показа в интерфейсе: ключ из сохранённого адреса.
    public static string? KeyFromUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !ServiceBrand.IsServiceHost(uri.Host)) return null;
        var key = uri.AbsolutePath;
        if (key.StartsWith(ServiceBrand.SubscriptionPath, StringComparison.OrdinalIgnoreCase))
            key = key.Substring(ServiceBrand.SubscriptionPath.Length);
        key = key.Trim('/');
        return LooksLikeKey(key) ? key : null;
    }
}
