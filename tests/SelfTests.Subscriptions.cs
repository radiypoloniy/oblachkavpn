using System.Net;

namespace Oblachka;

public static partial class SelfTests
{
    private static async Task SubscriptionChecksAsync(Func<string, Func<Task>, Task> check)
    {
        await check("Subscription metadata uses bounded nonnegative headers and avoids overflow", () => Sync(() =>
        {
            var info = SubscriptionMetadata.Parse(["upload=1073741824; download=1073741824; total=4294967296; expire=1800000000"]);
            Assert(info is not null && info.Summary(DateTimeOffset.UnixEpoch).Contains("2 ГБ"));
            Assert(SubscriptionMetadata.Parse(["upload=-1"]) is null);
            Assert(SubscriptionMetadata.Parse(["expire=9223372036854775807"]) is null);
            Assert(SubscriptionMetadata.Parse(["download=1; download=2"]) is null);
            Assert(!new SubscriptionMetadata(long.MaxValue, long.MaxValue, long.MaxValue, null).Summary(DateTimeOffset.UtcNow).Contains('-'));
        }));
        await check("HTTP subscription header survives import", async () =>
        {
            using var client = new HttpClient(new FixtureHandler(() =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(link) };
                response.Headers.Add("subscription-userinfo", "upload=1; download=2; total=100; expire=1800000000"); return response;
            }));
            Assert((await Subscriptions.FetchWithClientAsync(client, "https://fixture.invalid/sub", CancellationToken.None)).Metadata?.Total == 100);
        });
        await check("Refresh preserves a unique server label after credentials rotate; ambiguous labels do not match", () => Sync(() =>
        {
            var old = Profiles.Parse(link); var rotated = Profiles.Parse(link.Replace("11111111-1111-4111-8111-111111111111", "22222222-2222-4222-8222-222222222222"));
            Assert(ProfileSelection.Find([rotated with { Name = "Other" }, rotated], old) == 1);
            Assert(ProfileSelection.Find([rotated with { Name = "Other" }, rotated, rotated], old) == 0);
            var schedule = new SubscriptionSchedule(); var now = DateTimeOffset.UtcNow;
            schedule.Complete(now, true); Assert(!schedule.IsDue(now.AddHours(5)) && schedule.IsDue(now.AddHours(6)));
            schedule.Complete(now, false); Assert(!schedule.IsDue(now.AddMinutes(4)) && schedule.IsDue(now.AddMinutes(5)));
            schedule.AccessDenied(); Assert(!schedule.IsDue(now.AddYears(1)));
        }));
    }
}
