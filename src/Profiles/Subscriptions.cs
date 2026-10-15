using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Oblachka;

public sealed record ImportResult(IReadOnlyList<Profile> Profiles, int Unsupported, int Invalid)
{
    public SubscriptionMetadata? Metadata { get; init; }
    // Distinct validation messages for skipped entries; messages never contain profile values.
    public IReadOnlyList<string> Reasons { get; init; } = Array.Empty<string>();
    // Entries dropped as fully identical: same name and same configuration.
    public int Duplicates { get; init; }

    // Разбор не знает, откуда пришло тело подписки. Источник проставляет вызывающий:
    // ключ сервиса — ProfileSource.Service, вставленная ссылка или файл — External.
    public ImportResult WithSource(ProfileSource source) =>
        this with { Profiles = Profiles.Select(p => p with { Source = source }).ToArray() };
}

public static class Subscriptions
{
    public const int MaxProfiles = 200;
    public static ImportResult Parse(string input)
    {
        if (Encoding.UTF8.GetByteCount(input) > Profiles.MaxBytes) throw new UserError("Подписка больше 1 МБ. Используйте отдельную группу подключений.");
        input = input.Trim();
        if (input.StartsWith('<')) throw new UserError("Получена веб-страница. Нужна ссылка на подписку, а не личный кабинет.");
        if (input.Length == 0) throw new UserError("Подписка пуста.");
        // Clash / Mihomo: раздел proxies переводится в ссылки и разбирается как обычная подписка.
        var clashUnsupported = 0;
        if (ClashSubscription.Looks(input))
        {
            var links = ClashSubscription.ToLinks(input, out clashUnsupported);
            if (links.Count == 0) throw new UserError("В подписке Clash нет поддерживаемых подключений (VLESS, Trojan, Hysteria2).");
            input = string.Join('\n', links);
        }
        else if (!input.Contains("://") && !input.StartsWith('{') && !input.StartsWith('['))
        {
            try
            {
                var base64 = new string(input.Where(c => !char.IsWhiteSpace(c)).ToArray()).Replace('-', '+').Replace('_', '/');
                if (base64.Length % 4 != 0) base64 = base64.PadRight(base64.Length + 4 - base64.Length % 4, '=');
                input = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(base64)).Trim();
            }
            catch (Exception ex) when (ex is FormatException or DecoderFallbackException)
            { throw new UserError("Не удалось прочитать подписку. Поддерживаются ссылки VLESS/Trojan/Hysteria2, Base64 и клиентский JSON Xray / sing-box."); }
        }
        var profiles = new List<Profile>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var invalid = 0; var unsupported = 0; var duplicates = 0; var reasons = new List<string>();
        void Add(string text)
        {
            try
            {
                var profile = Profiles.Parse(text);
                // Склеиваем только полностью одинаковые записи: имя и конфигурация.
                // Сервис может намеренно отдать один узел под двумя именами — например
                // «Авто — оптимальное подключение» и «Германия» — и обе записи нужно сохранить.
                // Сам Fingerprint остаётся независимым от имени, чтобы переименование узла
                // в панели не теряло выбор пользователя.
                if (seen.Add(profile.Name + "\u0000" + Profiles.Fingerprint(profile))) profiles.Add(profile);
                else duplicates++;
            }
            catch (UserError ex) { invalid++; if (reasons.Count < 3 && !reasons.Contains(ex.Message)) reasons.Add(ex.Message); }
            if (profiles.Count > MaxProfiles) throw new UserError("В подписке больше 200 подключений. Используйте отдельную группу.");
        }
        if (input.StartsWith('{') || input.StartsWith('['))
        {
            try
            {
                var root = JsonNode.Parse(input, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                var items = root is JsonArray array ? array.ToList() : new List<JsonNode?> { root };
                if (items.Count > MaxProfiles) throw new UserError("В подписке больше 200 конфигураций.");
                foreach (var item in items)
                {
                    if (item is JsonObject obj && obj["outbounds"] is JsonArray outbounds)
                    {
                        foreach (var outbound in outbounds.OfType<JsonObject>())
                        {
                            if (!Profiles.IsXrayProtocol(outbound) && !SingBoxProfiles.IsSupported(outbound)) { unsupported++; continue; }
                            var named = outbound.DeepClone();
                            var wrapper = named!.ToJsonString();
                            var originalCount = profiles.Count;
                            Add(wrapper);
                            if (profiles.Count > originalCount)
                            {
                                // Подписка sing-box — одна конфигурация со многими выходами: имя узла — его tag.
                                var name = obj["remarks"]?.GetValue<string>() ?? obj["name"]?.GetValue<string>()
                                    ?? (outbound["tag"] is JsonValue tag && tag.TryGetValue<string>(out var t) ? t : "");
                                if (!string.IsNullOrWhiteSpace(name)) profiles[^1] = profiles[^1] with { Name = CleanLabel(name) };
                            }
                        }
                    }
                    else if (item is not null) Add(item.ToJsonString());
                }
            }
            catch (UserError) { throw; }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            { throw new UserError("JSON подписки имеет неподдерживаемый формат. Выберите формат Xray/VLESS."); }
        }
        else
        {
            var lines = input.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length > MaxProfiles * 4) throw new UserError("Подписка содержит слишком много строк.");
            foreach (var line in lines)
            {
                if (line.StartsWith('#')) continue;
                if (ShareLinks.IsLink(line)) Add(line);
                else unsupported++;
            }
        }
        if (profiles.Count == 0) throw new UserError("В подписке нет поддерживаемых профилей (VLESS, Trojan, Hysteria2 с TLS/REALITY)." + (reasons.Count > 0 ? " Причина: " + reasons[0] : " Проверьте формат подписки."));
        return new(profiles, unsupported + clashUnsupported, invalid) { Reasons = reasons, Duplicates = duplicates };
    }
    private static string CleanLabel(string name) => new(name.Where(c => !char.IsControl(c)).Take(80).ToArray());

    // Заголовки панели: срок и трафик, название подписки, объявление, период обновления,
    // маршрутизация и ссылки на поддержку и личный кабинет.
    // Текстовые значения приходят от сервера и очищаются перед показом.
    private static SubscriptionMetadata? ReadMetadata(HttpResponseMessage response)
    {
        string? Header(string name) =>
            response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
        var usage = response.Headers.TryGetValues("subscription-userinfo", out var info)
            ? SubscriptionMetadata.Parse(info) : null;
        var title = SubscriptionMetadata.DecodeText(Header("profile-title"));
        var announce = SubscriptionMetadata.DecodeText(Header("announce"));
        var interval = SubscriptionMetadata.ParseUpdateInterval(Header("profile-update-interval"));
        var routing = RoutingProfile.ParseHapp(Header("routing"));
        var support = SubscriptionMetadata.ParseLink(Header("support-url"));
        var page = SubscriptionMetadata.ParseLink(Header("profile-web-page-url"));
        if (usage is null && title is null && announce is null && interval is null && routing is null && support is null && page is null) return null;
        return (usage ?? new SubscriptionMetadata(null, null, null, null))
            with { Title = title, Announce = announce, UpdateInterval = interval, Routing = routing, SupportUrl = support, WebPageUrl = page };
    }

    public static async Task<ImportResult> FetchAsync(string url, CancellationToken token, int? socksPort = null)
    {
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false, UseProxy = socksPort is not null,
            Proxy = socksPort is null ? null : new WebProxy($"socks5://127.0.0.1:{socksPort}", false),
            ConnectTimeout = TimeSpan.FromSeconds(10)
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
        return await FetchWithClientAsync(client, url, token);
    }
    internal static async Task<ImportResult> FetchWithClientAsync(HttpClient client, string url, CancellationToken token)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
            throw new UserError("Нужна HTTPS-ссылка на подписку без логина и пароля в адресе.");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd(ServiceBrand.UserAgent);
            request.Headers.Accept.ParseAdd("text/plain, application/json");
            // Идентификатор установки для лимита устройств. Уходит только на хосты сервиса:
            // сторонней подписке знать про наше устройство незачем.
            if (ServiceBrand.IsServiceHost(uri.Host))
            {
                request.Headers.Add("x-hwid", DeviceIdentity.Current);
                request.Headers.Add("x-device-os", DeviceIdentity.OperatingSystem);
                request.Headers.Add("x-ver-os", DeviceIdentity.OperatingSystemVersion);
                request.Headers.Add("x-device-model", DeviceIdentity.DeviceModel);
            }
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is >= 300 and < 400) throw new UserError("Подписка перенаправляет запрос. Вставьте конечную HTTPS-ссылку из панели.");
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new SubscriptionAccessDenied();
            if (!response.IsSuccessStatusCode) throw new UserError($"Не удалось обновить подписку (HTTP {(int)response.StatusCode}). Попробуйте позже. Сохранённые подключения остаются в списке.");
            if (response.Content.Headers.ContentLength > Profiles.MaxBytes) throw new UserError("Подписка больше 1 МБ.");
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var memory = new MemoryStream(); var buffer = new byte[8192];
            while (true)
            {
                var n = await stream.ReadAsync(buffer, token);
                if (n == 0) break;
                if (memory.Length + n > Profiles.MaxBytes) throw new UserError("Подписка больше 1 МБ.");
                memory.Write(buffer, 0, n);
            }
            var imported = Parse(new UTF8Encoding(false, true).GetString(memory.ToArray()));
            return imported with { Metadata = ReadMetadata(response) };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or DecoderFallbackException or IOException)
        { throw new UserError("Не удалось загрузить подписку. Проверьте сеть и ссылку; можно импортировать её содержимое из файла."); }
    }
}
