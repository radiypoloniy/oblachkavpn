namespace Oblachka;

public static partial class SelfTests
{
    private static async Task PanelLinkChecksAsync(Func<string, Func<Task>, Task> check)
    {
        await check("Panel links: support-url and profile-web-page-url accept only HTTPS and survive saving only unchanged", () => Sync(() =>
        {
            Assert(SubscriptionMetadata.ParseLink(" https://t.me/oblachka_support ") == "https://t.me/oblachka_support");
            Assert(SubscriptionMetadata.ParseLink("https://panel.example.org/sub/abc?x=1") == "https://panel.example.org/sub/abc?x=1");
            foreach (var bad in new[] { null, "", "http://panel.example.org", "javascript:alert(1)", "file:///C:/Windows", "https://user:pw@panel.example.org",
                         "tg://resolve?domain=x", "https://panel.example.org/\nx", "https://" + new string('a', 600) + ".org" })
                Assert(SubscriptionMetadata.ParseLink(bad) is null);
            var good = new SubscriptionMetadata(1, 2, 3, null) { SupportUrl = "https://t.me/support", WebPageUrl = "https://panel.example.org/cabinet" };
            SubscriptionMetadata.Validate(good);
            ExpectUserError(() => SubscriptionMetadata.Validate(good with { WebPageUrl = "http://panel.example.org/cabinet" }));
            ExpectUserError(() => SubscriptionMetadata.Validate(good with { SupportUrl = "file:///C:/x" }));
            ExpectUserError(() => SubscriptionMetadata.Validate(good with { Upload = -1 }));
        }));
        await check("Server marks: fresh result shows ms or «нет ответа», old one says «давно», a running check says so", () => Sync(() =>
        {
            var now = DateTimeOffset.UtcNow;
            Assert(ServerRow.Status(null, now).Text == "");
            var fast = ServerRow.Status(new("a", now.AddMinutes(-2), true, 84), now);
            Assert(fast.Text == "84 мс" && fast.Hint!.Contains("84 мс") && fast.Dot == ServerRow.DotFor(84));
            Assert(ServerRow.Status(new("a", now.AddMinutes(-2), false, 0), now).Text == "нет ответа");
            var old = ServerRow.Status(new("a", now.AddHours(-3), true, 84), now);
            Assert(old.Text == "давно" && old.Hint!.StartsWith("Последний ответ — 84 мс", StringComparison.Ordinal));
            Assert(ServerRow.Status(new("a", now.AddHours(-3), false, 0), now).Hint!.StartsWith("В последний раз не ответил", StringComparison.Ordinal));
            Assert(ServerRow.Status(new("a", now, true, 84), now, checking: true).Text == "проверяем…");
        }));
    }
}
