namespace RoutePilot;

public static partial class SelfTests
{
    private static async Task ServiceKeyChecksAsync(Func<string, Func<Task>, Task> check)
    {
        var host = ServiceBrand.SubscriptionHosts[0];
        var expected = "https://" + host + ServiceBrand.SubscriptionPath + "hRDbej1hw9YC7rEc-QktKA5Rh";
        await check("Service key accepts a bare code, a full link and a pasted link without scheme", () => Sync(() =>
        {
            Assert(ServiceKey.ToSubscriptionUrl("hRDbej1hw9YC7rEc-QktKA5Rh") == expected);
            Assert(ServiceKey.ToSubscriptionUrl(expected) == expected);
            Assert(ServiceKey.ToSubscriptionUrl(host + ServiceBrand.SubscriptionPath + "hRDbej1hw9YC7rEc-QktKA5Rh") == expected);
            // Копирование из переписки приносит пробелы и перевод строки.
            Assert(ServiceKey.ToSubscriptionUrl("  hRDbej1hw9YC7rEc-QktKA5Rh \r\n") == expected);
            // Регистр хоста выдачу не меняет, ключ регистрозависим и не нормализуется.
            Assert(ServiceKey.ToSubscriptionUrl("https://" + host.ToUpperInvariant() + ServiceBrand.SubscriptionPath + "hRDbej1hw9YC7rEc-QktKA5Rh") == expected);
            Assert(ServiceKey.KeyFromUrl(expected) == "hRDbej1hw9YC7rEc-QktKA5Rh");
        }));
        await check("Service key rejects foreign hosts, plain HTTP, credentials and malformed codes", () => Sync(() =>
        {
            // Чужая подписка — не ошибка пользователя, а другой раздел интерфейса.
            ExpectUserError(() => ServiceKey.ToSubscriptionUrl("https://sub.other-service.invalid/sub/abcdefghij"));
            ExpectUserError(() => ServiceKey.ToSubscriptionUrl("http://" + host + ServiceBrand.SubscriptionPath + "abcdefghij"));
            ExpectUserError(() => ServiceKey.ToSubscriptionUrl("https://user:pass@" + host + ServiceBrand.SubscriptionPath + "abcdefghij"));
            ExpectUserError(() => ServiceKey.ToSubscriptionUrl("https://" + host + ServiceBrand.SubscriptionPath + "abc#fragment"));
            ExpectUserError(() => ServiceKey.ToSubscriptionUrl("https://" + host + "/sub/"));
            ExpectUserError(() => ServiceKey.ToSubscriptionUrl("https://" + host + "/sub/one/two"));
            ExpectUserError(() => ServiceKey.ToSubscriptionUrl("короткий"));
            ExpectUserError(() => ServiceKey.ToSubscriptionUrl("ключ с кириллицей"));
            ExpectUserError(() => ServiceKey.ToSubscriptionUrl("abc"));
            ExpectUserError(() => ServiceKey.ToSubscriptionUrl(""));
            ExpectUserError(() => ServiceKey.ToSubscriptionUrl(new string('a', 200)));
            Assert(ServiceKey.KeyFromUrl("https://sub.other-service.invalid/sub/abcdefghij") is null);
        }));
        await check("Service identity carries no secrets and a usable subscription host", () => Sync(() =>
        {
            Assert(ServiceBrand.SubscriptionHosts.Length is > 0 and <= SubscriptionMirrors.MaxSources);
            Assert(ServiceBrand.SubscriptionHosts.All(h => h.Length > 0 && !h.Contains('/') && !h.Contains(':')));
            Assert(ServiceBrand.SubscriptionPath.StartsWith('/') && ServiceBrand.SubscriptionPath.EndsWith('/'));
            Assert(ServiceBrand.UserAgent.StartsWith("Oblachka/", StringComparison.Ordinal));
            // Адрес подписки, собранный из идентичности сервиса, должен проходить ту же
            // проверку, что и вручную введённые резервные адреса.
            Assert(SubscriptionMirrors.ParseSources(ServiceKey.ToSubscriptionUrl("abcdefghij")).Count == 1);
        }));
    }
}
