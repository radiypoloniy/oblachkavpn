namespace Oblachka;

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
    // Что вставили в единственное поле первого запуска: ключ, ссылку своего сервиса, ссылку
    // подписки другого сервиса или сами профили (vless://, trojan://, hy2://, JSON, список).
    public enum InputKind { ServiceKey, ServiceUrl, ExternalUrl, Profiles }

    public static InputKind Classify(string input)
    {
        var text = (input ?? "").Trim();
        var first = text.Split('\n', 2)[0].Trim();
        if (text.StartsWith('{') || text.StartsWith('[') || ShareLinks.IsLink(first)) return InputKind.Profiles;
        if (first.Contains("://", StringComparison.Ordinal) || first.Contains('/'))
        {
            var url = first.Contains("://", StringComparison.Ordinal) ? first : "https://" + first;
            return Uri.TryCreate(url, UriKind.Absolute, out var uri) && !ServiceBrand.IsServiceHost(uri.Host)
                ? InputKind.ExternalUrl : InputKind.ServiceUrl;
        }
        return text.Contains('\n') ? InputKind.Profiles : InputKind.ServiceKey;
    }

    public static string ToSubscriptionUrl(string input)
    {
        if (input is null) throw new UserError("Введите ключ подписки.");
        // Пробелы внутри ключа — почти всегда следствие копирования из переписки.
        var text = new string(input.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (text.Length == 0) throw new UserError("Введите ключ подписки.");
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
            throw new UserError("Не удалось прочитать адрес. Введите ключ подписки или полную ссылку сервиса.");
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

    // Источник списка узлов определяется адресами, с которых он загружен. Смешанный набор
    // считается сторонним: отвечать за подбор и измерения можно только по своим узлам.
    public static ProfileSource SourceOf(IEnumerable<string> urls)
    {
        var list = urls as IReadOnlyList<string> ?? urls.ToArray();
        if (list.Count == 0) return ProfileSource.External;
        foreach (var url in list)
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !ServiceBrand.IsServiceHost(uri.Host))
                return ProfileSource.External;
        return ProfileSource.Service;
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
