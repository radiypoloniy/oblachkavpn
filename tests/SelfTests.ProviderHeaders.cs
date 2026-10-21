using System.Net;
using System.Text.Json.Nodes;

namespace Oblachka;

public static partial class SelfTests
{
    // Управление клиентом заголовками подписки (ProviderOptions): переезд, «Продлить», кнопка
    // объявления, DoH, обход DPI. Имена — как у Happ и INCY.
    private static async Task ProviderHeaderChecksAsync(Func<string, Func<Task>, Task> check, string root)
    {
        var source = new Uri("https://sub.fixture.invalid/sub/abcdefghij");
        static Func<string, string?> Headers(params (string Name, string Value)[] values) =>
            name => values.FirstOrDefault(v => v.Name == name).Value;

        await check("Provider headers: subscription move and fallback address", () => Sync(() =>
        {
            var moved = ProviderOptions.Read(Headers(("new-url", "https://new.fixture.invalid/s/key"), ("fallback-url", "https://backup.fixture.invalid/sub/abcdefghij")), source)!;
            Assert(moved.NewUrl == "https://new.fixture.invalid/s/key" && moved.FallbackUrl == "https://backup.fixture.invalid/sub/abcdefghij");
            // new-domain меняет только хост, ключ в пути остаётся.
            Assert(ProviderOptions.Read(Headers(("new-domain", "Moved.Fixture.Invalid.")), source)!.NewUrl == "https://moved.fixture.invalid/sub/abcdefghij");
            // new-url сильнее new-domain; тот же адрес — не переезд; http и мусор отбрасываются.
            Assert(ProviderOptions.Read(Headers(("new-url", "https://a.fixture.invalid/x"), ("new-domain", "b.fixture.invalid")), source)!.NewUrl == "https://a.fixture.invalid/x");
            Assert(ProviderOptions.Read(Headers(("new-url", source.AbsoluteUri)), source) is null);
            Assert(ProviderOptions.Read(Headers(("new-url", "http://plain.fixture.invalid/sub")), source) is null);
            Assert(ProviderOptions.Read(Headers(("new-domain", "not a host")), source) is null);
            Assert(ProviderOptions.Read(Headers(("new-domain", "localhost")), source) is null);
            Assert(ProviderOptions.Read(Headers(), source) is null);
        }));
        await check("Provider headers: renew link, announce button, DoH and fragmentation", () => Sync(() =>
        {
            var options = ProviderOptions.Read(Headers(("sub-expire-button-link", "https://pay.fixture.invalid/renew"), ("premium-url", "javascript:alert(1)"),
                ("announce-url", "https://news.fixture.invalid/"), ("banner-button-text", "Очень длинная надпись на кнопке баннера"),
                ("server-address-resolve-enable", "1"), ("server-address-resolve-dns-domain", "https://dns.fixture.invalid/dns-query"), ("server-address-resolve-dns-ip", "9.9.9.9"),
                ("fragmentation-enable", "true"), ("fragmentation-length", "100-200"), ("fragmentation-interval", "20-10"), ("fragmentation-packets", "1-3")), source)!;
            Assert(options.RenewUrl == "https://pay.fixture.invalid/renew" && options.AnnounceUrl == "https://news.fixture.invalid/");
            Assert(options.AnnounceButton!.Length == ProviderOptions.MaxButtonLength);
            Assert(options.Resolve == new ServerResolve("https://dns.fixture.invalid/dns-query", "9.9.9.9"));
            // Перевёрнутый диапазон заменяется умолчанием, правильные значения берутся как есть.
            Assert(options.Fragment == new ProviderFragment(true, "1-3", "100-200", "5-10"));
            // DoH без адреса — Cloudflare; адрес DoH в локальной сети не принимается.
            Assert(ProviderOptions.Read(Headers(("server-address-resolve-enable", "1")), source)!.Resolve == DohResolver.Cloudflare);
            Assert(ServerResolve.Create("https://dns.fixture.invalid/dns-query", "192.168.1.1") is null);
            Assert(ProviderOptions.Read(Headers(("server-address-resolve-enable", "0")), source) is null);
            Assert(ProviderOptions.Read(Headers(("fragmentation-enable", "0")), source)!.Fragment is { Enabled: false });
            // Значения вне границ (длина 0, пауза в секунды, пакет 0) заменяются умолчанием (Р-4).
            Assert(ProviderOptions.Read(Headers(("fragmentation-enable", "1"), ("fragmentation-length", "0-10"), ("fragmentation-interval", "500"), ("fragmentation-packets", "0-2")), source)!.Fragment
                == new ProviderFragment(true, "tlshello", "50-100", "5-10"));
        }));
        await check("Fragment shape from the provider reaches the Xray fragment outbound", () => Sync(() =>
        {
            var reality = new CoreAdapter(CoreKind.Xray, "xray.exe").RuntimeConfig(Profiles.Parse("vless://11111111-1111-4111-8111-111111111111@example.com:443?security=reality&pbk=jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0&sni=example.com&fp=chrome&type=tcp&encryption=none#R").Outbound, 34601);
            Assert(TlsFragment.Apply(reality, CoreKind.Xray, new ProviderFragment(true, "1-3", "100-200", "10-20")));
            var fragment = ((JsonArray)reality["outbounds"]!).OfType<JsonObject>().Single(o => (string?)o["tag"] == TlsFragment.XrayTag)["settings"]!["fragment"]!;
            Assert((string?)fragment["packets"] == "1-3" && (string?)fragment["length"] == "100-200" && (string?)fragment["interval"] == "10-20");
        }));
        await check("Subscription move is accepted only after the new address serves a subscription", async () =>
        {
            var profiles = Subscriptions.Parse(link);
            var withMove = profiles with { Metadata = new SubscriptionMetadata(null, null, null, null) { Provider = new() { NewUrl = "https://new.fixture.invalid/sub/k", FallbackUrl = "https://backup.fixture.invalid/sub/k" } } };
            var sources = new[] { "https://old.fixture.invalid/sub/k" };
            var moved = await SubscriptionMirrors.MigrateAsync(sources, new(withMove, 0, 1), (_, _) => Task.FromResult(profiles), CancellationToken.None);
            Assert(moved.Moved && moved.SourceIndex == 0 && moved.Sources!.SequenceEqual(new[] { "https://new.fixture.invalid/sub/k", "https://old.fixture.invalid/sub/k", "https://backup.fixture.invalid/sub/k" }));
            // Новый адрес не отдал подписку — остаёмся на прежнем, запасной всё равно запоминается.
            var failed = await SubscriptionMirrors.MigrateAsync(sources, new(withMove, 0, 1), (_, _) => throw new UserError("нет"), CancellationToken.None);
            Assert(!failed.Moved && failed.Import == withMove && failed.Sources!.SequenceEqual(new[] { "https://old.fixture.invalid/sub/k", "https://backup.fixture.invalid/sub/k" }));
            // Ответ запасного адреса подписку не уводит и адресов не добавляет (Р-1).
            var fromMirror = await SubscriptionMirrors.MigrateAsync(new[] { "https://a.fixture.invalid/sub/k", "https://b.fixture.invalid/sub/k" }, new(withMove, 1, 2), (_, _) => Task.FromResult(profiles), CancellationToken.None);
            Assert(!fromMirror.Moved && fromMirror.Sources is null);
            // Подписка сервиса: новый домен становится доменом сервиса, неудачный — забывается.
            var service = new[] { "https://" + ServiceBrand.SubscriptionHosts[0] + ServiceBrand.SubscriptionPath + "abcdefghij" };
            var serviceMove = profiles with { Metadata = new SubscriptionMetadata(null, null, null, null) { Provider = new() { NewUrl = "https://moved.fixture.invalid/sub/abcdefghij" } } };
            try
            {
                await SubscriptionMirrors.MigrateAsync(service, new(serviceMove, 0, 1), (_, _) => throw new UserError("нет"), CancellationToken.None);
                Assert(!ServiceBrand.IsServiceHost("moved.fixture.invalid"));
                var accepted = await SubscriptionMirrors.MigrateAsync(service, new(serviceMove, 0, 1), (_, _) => Task.FromResult(profiles), CancellationToken.None);
                Assert(accepted.Moved && ServiceBrand.IsServiceHost("moved.fixture.invalid") && ServiceKey.SourceOf(accepted.Sources!) == ProfileSource.Service);
                // Сохраняются только домены, что ещё стоят в адресах подписок.
                Assert(ServiceBrand.LearnedIn(accepted.Sources!).Contains("moved.fixture.invalid") && ServiceBrand.LearnedIn(service).Length == 0);
                // Сторонняя подписка доменов сервиса не добавляет.
                await SubscriptionMirrors.MigrateAsync(sources, new(profiles with { Metadata = new SubscriptionMetadata(null, null, null, null) { Provider = new() { NewUrl = "https://other.fixture.invalid/x" } } }, 0, 1), (_, _) => Task.FromResult(profiles), CancellationToken.None);
                Assert(!ServiceBrand.IsServiceHost("other.fixture.invalid"));
            }
            finally { ServiceBrand.Forget("moved.fixture.invalid"); }
        });
        await check("Subscription response headers become provider options", async () =>
        {
            using var client = new HttpClient(new FixtureHandler(() =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(link) };
                response.Headers.Add("new-domain", "moved.fixture.invalid");
                response.Headers.Add("premium-url", "https://pay.fixture.invalid/");
                response.Headers.Add("banner-text", "base64:0J7QsdC70LDRh9C60LDwn4yk");
                return response;
            }));
            var imported = await Subscriptions.FetchWithClientAsync(client, "https://sub.fixture.invalid/sub/abcdefghij", CancellationToken.None);
            Assert(imported.Metadata!.Provider!.NewUrl == "https://moved.fixture.invalid/sub/abcdefghij" && imported.Metadata.Provider.RenewUrl == "https://pay.fixture.invalid/");
            Assert(imported.Metadata.Announce == "Облачка🌤");
        });
        await check("DoH messages are encoded and parsed per RFC 8484", () => Sync(() =>
        {
            var query = DohResolver.Query("example.com", 1);
            Assert(query.Length == 12 + 13 + 4 && query[5] == 1 && query[12] == 7 && query[^3] == 1);
            ExpectFormat(() => DohResolver.Query(new string('a', 64) + ".com", 1));
            // Ответ: вопрос, затем A через указатель на имя (0xC00C) и CNAME, который пропускается.
            var answer = query.ToList();
            answer[2] = 0x81; answer[3] = 0x80; answer[7] = 2;
            answer.AddRange(new byte[] { 0xC0, 0x0C, 0, 5, 0, 1, 0, 0, 0, 60, 0, 2, 0xC0, 0x0C });
            answer.AddRange(new byte[] { 0xC0, 0x0C, 0, 1, 0, 1, 0, 0, 0, 60, 0, 4, 93, 184, 216, 34 });
            Assert(DohResolver.Answers(answer.ToArray(), 1).Single().Equals(IPAddress.Parse("93.184.216.34")));
            answer[3] = 0x83;
            ExpectFormat(() => DohResolver.Answers(answer.ToArray(), 1));
            ExpectFormat(() => DohResolver.Answers(answer.Take(30).ToArray(), 1));
            Assert(DohResolver.IsPrivate(IPAddress.Parse("100.64.1.1")) && !DohResolver.IsPrivate(IPAddress.Parse("1.1.1.1")));
        }));
        await check("Saved state keeps learned service hosts and rejects junk", async () =>
        {
            var store = new ClientStateStore(Path.Combine(root, "provider-state.bin"));
            var saved = new[] { new SavedProfile("Узел", (JsonObject)Profiles.Parse(link).Outbound.DeepClone()) };
            ClientSnapshot With(string[] hosts, ProviderOptions? provider = null) => new(1, saved, null, Array.Empty<string>(), 0, false, false)
                { ServiceHosts = hosts, Metadata = new SubscriptionMetadata(null, null, null, null) { Provider = provider } };
            store.Save(With(new[] { "moved.fixture.invalid" }, new() { RenewUrl = "https://pay.fixture.invalid/", Fragment = ProviderFragment.Default, Resolve = DohResolver.Google }));
            var restored = store.Load()!;
            Assert(restored.ServiceHosts!.Single() == "moved.fixture.invalid" && restored.Metadata!.Provider!.Resolve == DohResolver.Google);
            ExpectUserError(() => store.Save(With(new[] { "bad host" })));
            ExpectUserError(() => store.Save(With(Enumerable.Range(0, 9).Select(i => $"h{i}.fixture.invalid").ToArray())));
            ExpectUserError(() => store.Save(With(Array.Empty<string>(), new() { RenewUrl = "http://pay.fixture.invalid/" })));
            ExpectUserError(() => store.Save(With(Array.Empty<string>(), new() { Fragment = new(true, "all-of-them", "1-2", "1") })));
            ExpectUserError(() => store.Save(With(Array.Empty<string>(), new() { Resolve = new("https://dns.fixture.invalid/", "10.0.0.1") })));
            await Task.CompletedTask;
        });
    }

    private static void ExpectFormat(Action action)
    {
        try { action(); } catch (FormatException) { return; }
        throw new InvalidOperationException("Expected FormatException");
    }
}
