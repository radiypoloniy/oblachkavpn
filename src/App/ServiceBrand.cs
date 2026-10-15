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
    public const string Version = "0.12.0";
    public const string UserAgent = LatinName + "/" + Version;
    public static string Label => Name + " " + Version;

    // Хосты, которые считаются подпиской сервиса. Первый — основной: именно на нём
    // разворачивается короткий ключ. Остальные служат резервными адресами того же сервиса.
    public static readonly string[] SubscriptionHosts = { "sub.oblaconnection.com" };

    // Путь до токена в адресе подписки, между хостом и ключом.
    public const string SubscriptionPath = "/sub/";

    // Куда ведёт кнопка поддержки. Пустая строка — кнопка не показывается.
    // Заголовок support-url самой панели сейчас равен значению по умолчанию
    // (https://dummy.docs.rw), поэтому брать адрес оттуда нельзя.
    public const string SupportUrl = "";

    public static bool IsServiceHost(string host) =>
        SubscriptionHosts.Any(h => string.Equals(h, host, StringComparison.OrdinalIgnoreCase));
}
