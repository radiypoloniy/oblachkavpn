using System.Text.Json;

namespace Oblachka;

public static partial class SelfTests
{
    public static async Task<int> RunSubscriptionsAsync(string report)
    {
        var checks = new List<Check>();
        async Task CheckAsync(string name, Func<Task> action)
        {
            try { await action(); checks.Add(new(name, true, null)); }
            catch (Exception ex) { checks.Add(new(name, false, ex.ToString())); }
        }
        await SubscriptionChecksAsync(CheckAsync);
        var passed = checks.All(c => c.Passed);
        File.WriteAllText(report, JsonSerializer.Serialize(new { Passed = passed, Checks = checks }, new JsonSerializerOptions { WriteIndented = true }));
        return passed ? 0 : 1;
    }

    private static async Task SubscriptionStateChecksAsync(Func<string, Func<Task>, Task> check)
    {
        var now = DateTimeOffset.Parse("2026-11-01T12:00:00Z");
        ProfileGroup Group(string id, SubscriptionMetadata? meta) => new(id, id, ProfileSource.External, ["https://fixture.invalid/" + id]) { Metadata = meta };
        await check("Expired service never masks external unlimited subscription; running group wins and manual has no fallback", () => Sync(() =>
        {
            var service = Group(ProfileGroup.ServiceId, new(0, 0, 0, now.AddDays(-1))) with { Source = ProfileSource.Service };
            var external = Group("external", new(null, null, 0, null));
            var selected = Profiles.Parse(link) with { Group = external.Id };
            var running = selected with { Group = service.Id };
            Assert(ProfileGroups.Active([service, external], selected, running, false) == external);
            Assert(ProfileGroups.Active([service, external], selected, running, true) == service);
            Assert(ProfileGroups.Active([service, external], selected with { Group = ProfileGroup.ManualId }, running, false) is null);
            Assert(ProfileGroups.Active([service, external], null, running, false) is null);
            Assert(ProfileGroups.Active([service, external], selected, null, true) is null);
            var state = SubscriptionState.For(external, now);
            Assert(state.Term == SubscriptionTerm.Unknown && state.Traffic == SubscriptionTraffic.Unlimited && !state.HasWarning);
        }));
        await check("Unlimited traffic retains expiration; unknown metadata has explicit unknown states", () => Sync(() =>
        {
            var group = Group("monthly", new(null, null, 0, now.AddDays(5)));
            var state = SubscriptionState.For(group, now);
            Assert(state.Term == SubscriptionTerm.Expiring && state.Traffic == SubscriptionTraffic.Unlimited && state.HasWarning);
            Assert(SubscriptionState.For(group, now.AddDays(5)).Critical);
            var unknown = SubscriptionState.For(Group("unknown", null), now);
            Assert(unknown.TermText == "Срок не сообщён" && unknown.Traffic == SubscriptionTraffic.Unknown && !unknown.HasWarning);
            Assert(SubscriptionState.For(Group("partial", new(null, 10, 100, null)), now).Traffic == SubscriptionTraffic.Unknown);
        }));
        await check("Warnings respect exact thresholds, exhaustion and overflow; renew and support belong to the target group", () => Sync(() =>
        {
            var group = Group("target", new(0, 0, 5L * 1073741824, now.AddDays(14))
            { WebPageUrl = "https://target.invalid/account", SupportUrl = "https://target.invalid/support" });
            var state = SubscriptionState.For(group, now);
            Assert(!state.HasWarning && state.RenewUrl == "https://target.invalid/account" && state.SupportUrl == "https://target.invalid/support");
            var later = SubscriptionState.For(group, now.AddSeconds(1));
            Assert(later.HasWarning && later.WarningKey != SubscriptionState.For(group, now.AddDays(14)).WarningKey);
            var provider = group with { Metadata = group.Metadata! with { Provider = new() { RenewUrl = "https://target.invalid/renew" } } };
            Assert(SubscriptionState.For(provider, now).RenewUrl == "https://target.invalid/renew");
            Assert(SubscriptionState.For(Group("other", null), now).RenewUrl is null);
            var exhausted = SubscriptionState.For(Group("spent", new(long.MaxValue, long.MaxValue, long.MaxValue, null)), now);
            Assert(exhausted.Traffic == SubscriptionTraffic.Exhausted && exhausted.RemainingBytes == 0 && exhausted.Used == 1 && exhausted.Critical);
            var low = SubscriptionState.For(group with { Metadata = group.Metadata! with { Download = 1 } }, now);
            Assert(low.Traffic == SubscriptionTraffic.Low && low.HasWarning);
        }));
    }
}
