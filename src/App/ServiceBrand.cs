namespace Oblachka;

// Единственное место, где описан сервис. Всё, что относится к «Облачкам» как к конкретной
// подписке, настраивается здесь и больше нигде.
//
// Секретов в этом файле нет и быть не должно: базовый хост подписки не секрет, его узнаёт
// любой пользователь из своей ссылки. Ключ подписки вводит сам пользователь и хранится
// под DPAPI. Административные учётные данные панели клиенту не выдаются никогда.
public static class ServiceBrand
{
    public const string Name = "Облачка";
    // Латинское имя для всего, что уходит в сеть и в имена файлов: значения заголовков
    // HTTP передаются в однобайтовой кодировке, кириллица там не проходит.
    public const string LatinName = "Oblachka";
    public const string Version = "0.20.0";
    public const string UserAgent = LatinName + "/" + Version;
    public static string Label => Name + " " + Version;

    // Хосты, которые считаются подпиской сервиса. Первый — основной: именно на нём
    // разворачивается короткий ключ. Остальные служат резервными адресами того же сервиса.
    public static readonly string[] SubscriptionHosts = { "sub.oblaconnection.com" };

    // Домены сервиса целиком: подписка, панель, сайт. Через бесплатный чужой узел они закрыты (ForeignGuard).
    public static readonly string[] ServiceDomains = { "oblaconnection.com" };

    // Путь до токена в адресе подписки, между хостом и ключом.
    public const string SubscriptionPath = "/sub/";

    // Куда ведёт кнопка поддержки. Пустая строка — кнопка не показывается.
    // Заголовок support-url самой панели сейчас равен значению по умолчанию
    // (https://dummy.docs.rw), поэтому брать адрес оттуда нельзя.
    public const string SupportUrl = "";

    public static bool IsServiceHost(string host) =>
        SubscriptionHosts.Concat(learnedHosts).Any(h => string.Equals(h, host, StringComparison.OrdinalIgnoreCase));

    // Домены, на которые подписка сервиса переехала заголовками new-url / fallback-url
    // (SubscriptionMirrors.MigrateAsync). Сохраняются в состоянии клиента.
    public const int MaxLearnedHosts = 8;
    private static string[] learnedHosts = Array.Empty<string>();
    public static IReadOnlyList<string> LearnedHosts => learnedHosts;
    public static bool Learn(string host)
    {
        host = host.ToLowerInvariant();
        if (IsServiceHost(host) || learnedHosts.Length >= MaxLearnedHosts || Uri.CheckHostName(host) != UriHostNameType.Dns) return false;
        learnedHosts = learnedHosts.Append(host).ToArray();
        return true;
    }
    // Выученные домены, которые ещё стоят в адресах подписок. Домен, убранный из всех подписок,
    // не сохраняется: брошенный сервисом и перерегистрированный чужим, он не получит
    // идентификатор устройства (аудит 19.10, Р-1).
    public static string[] LearnedIn(IEnumerable<string> sources)
    {
        var hosts = sources.Select(s => Uri.TryCreate(s, UriKind.Absolute, out var uri) ? uri.Host : null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return learnedHosts.Where(hosts.Contains).ToArray();
    }
    public static void Forget(string host) =>
        learnedHosts = learnedHosts.Where(h => !string.Equals(h, host, StringComparison.OrdinalIgnoreCase)).ToArray();
}
