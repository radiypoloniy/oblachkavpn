using System.Security.Cryptography;

namespace Oblachka;

// Идентификатор установки для лимита устройств в панели.
//
// Это случайное значение, созданное один раз на компьютере. Аппаратные серийные номера,
// MAC-адреса и имя машины не читаются: такой идентификатор не должен позволять связать
// пользователя с его железом, а панели достаточно различать установки между собой.
//
// Секретом он не является — уходит в заголовке запроса подписки, — поэтому хранится
// обычным текстом рядом с состоянием клиента. Файл отдельный от client-state.bin:
// удаление подписки не должно занимать новое место в лимите устройств при повторном входе.
public static class DeviceIdentity
{
    public const int Length = 32;
    private static readonly object Gate = new();
    private static string? cached;

    public static string Current
    {
        get
        {
            lock (Gate)
            {
                cached ??= LoadOrCreate();
                return cached;
            }
        }
    }

    // Что видит панель рядом с идентификатором. Версия намеренно огрублена до старшей:
    // точная сборка Windows сужает круг устройств и для лимита не нужна.
    public static string OperatingSystem => "Windows";
    public static string OperatingSystemVersion => Environment.OSVersion.Version.Major >= 10 && Environment.OSVersion.Version.Build >= 22000
        ? "11" : Environment.OSVersion.Version.Major.ToString();
    public static string DeviceModel => ServiceBrand.LatinName + " " + ServiceBrand.Version;

    // Всё, что уходит в заголовках, должно быть ASCII: иначе HttpClient не отправит запрос.
    internal static bool IsHeaderSafe(string value) =>
        value.Length is > 0 and <= 128 && value.All(c => c is >= ' ' and <= '~');

    // Remnawave (с 3.0) берёт x-hwid только по шаблону ^[a-zA-Z0-9=-]{10,64}$, а неподходящий
    // молча игнорирует — и при включённом лимите устройств отвечает 404, список серверов пуст.
    // Ранние идентификаторы были base64url и примерно в 40% установок содержали «_»: такой
    // считается негодным и один раз заменяется (панель его не принимала — места в лимите он не занимал).
    internal static bool IsValid(string? value) =>
        value is { Length: Length } && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    // Для сторонней подписки — свой идентификатор на каждый хост: HMAC от нашего по имени хоста.
    // Панель с лимитом устройств его принимает и узнаёт при повторных запросах, но два разных
    // сервиса не могут понять, что это одно устройство.
    public static string ForHost(string host)
    {
        var mac = HMACSHA256.HashData(System.Text.Encoding.ASCII.GetBytes(Current), System.Text.Encoding.UTF8.GetBytes(host.ToLowerInvariant()));
        return Convert.ToHexStringLower(mac)[..Length];
    }

    private static string LoadOrCreate()
    {
        var path = Path.Combine(ClientDataPaths.Root, "device-id.txt");
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length <= 256)
            {
                var saved = File.ReadAllText(path).Trim();
                if (IsValid(saved)) return saved;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        var created = Generate();
        try
        {
            Directory.CreateDirectory(ClientDataPaths.Root);
            var staged = path + ".pending-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(staged, created);
            File.Move(staged, path, true);
        }
        // Если записать не удалось, идентификатор живёт до перезапуска: лучше так,
        // чем отказ загрузить подписку.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return created;
    }

    private static string Generate() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(Length / 2));
}
