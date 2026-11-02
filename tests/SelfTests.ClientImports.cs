namespace Oblachka;

public static partial class SelfTests
{
    private static async Task ClientImportsChecksAsync(Func<string, Func<Task>, Task> check)
    {
        var a = Profiles.Parse(link); var b = Profiles.Parse(link.Replace("example.com", "second.invalid").Replace("#Test", "#Second"));
        string[] sources = ["https://subscription.invalid/a"];
        var group = new ProfileGroup(ProfileGroup.IdFor(sources, ProfileSource.External), "Fixture", ProfileSource.External, sources);
        await check("Runtime import appends manual profiles, preserves subscriptions and releases stale picker/cache data", () =>
        {
            using var owner = new ClientRuntime(new ConnectionCore());
            owner.Imports.Apply(group, new([a], 0, 0)); owner.Imports.ImportText(link.Replace("example.com", "second.invalid").Replace("#Test", "#Second"));
            Assert(owner.Profiles.Groups.Count == 2 && owner.Profiles.Imported.Count == 2 && owner.Profiles.Selected?.Name == b.Name);
            owner.Profiles.MarkMeasured(owner.Profiles.Imported.First(p => p.Group == group.Id), 1);
            owner.Imports.Remove(group.Id);
            Assert(owner.Profiles.Pings.Count == 0 && owner.Profiles.Imported.Count == 1 && owner.Profiles.Picker.Items.Count == 1);
            return Task.CompletedTask;
        });
        await check("Headless refresh renames a running node without restarting or switching its core", async () =>
        {
            var core = new ConnectionCore(); using var owner = new ClientRuntime(core);
            owner.Imports.Apply(group, new([a, b], 0, 0)); var selected = owner.Profiles.Imported[1];
            await owner.Connections.StartAsync(selected);
            await owner.Imports.LoadAsync(sources, 0, (_, _, _) => Task.FromResult(new MirrorResult(new([a, b with { Name = "Renamed" }], 0, 0), 0, 1)));
            Assert(core.Starts.Count == 1 && core.Stops == 0 && owner.Profiles.Selected?.Name == "Renamed" && owner.Imports.Pending is null);
            Assert(owner.Profiles.Running == selected && owner.Profiles.Picker.SelectedItem == owner.Profiles.Selected);
        });
        await check("Headless refresh defers a list missing the active node and applies it only after Stop", async () =>
        {
            var core = new ConnectionCore(); using var owner = new ClientRuntime(core);
            owner.Imports.Apply(group, new([a], 0, 0)); var selected = owner.Profiles.Selected!;
            await owner.Connections.StartAsync(selected);
            await owner.Imports.LoadAsync(sources, 0, (_, _, _) => Task.FromResult(new MirrorResult(new([b], 0, 0), 0, 1)));
            Assert(owner.Imports.Pending is not null && owner.Profiles.Selected == selected && core.Stops == 0);
            await owner.Connections.StopAsync();
            Assert(owner.Imports.Pending is null && owner.Profiles.Selected?.Name == b.Name && owner.Profiles.Picker.Items.Count == 1);
        });
        await check("Removing a subscription invalidates even a loader that ignores cancellation", async () =>
        {
            using var owner = new ClientRuntime(new ConnectionCore()); owner.Imports.Apply(group, new([a], 0, 0));
            var reply = new TaskCompletionSource<MirrorResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var loading = owner.Imports.LoadAsync(sources, 0, (_, _, _) => reply.Task);
            owner.Imports.Remove(group.Id); reply.SetResult(new(new([b], 0, 0), 0, 1)); await loading;
            Assert(owner.Profiles.Groups.Count == 0 && owner.Profiles.Imported.Count == 0 && owner.Profiles.Picker.Items.Count == 0);
            Assert(owner.Workspace.RemovedSubscriptions.Contains(group.Id) && owner.Activity.Operation is null);
        });
        await check("Failed refresh keeps both the active profiles and an earlier pending update", async () =>
        {
            using var owner = new ClientRuntime(new ConnectionCore()); owner.Imports.Apply(group, new([a], 0, 0));
            await owner.Connections.StartAsync(owner.Profiles.Selected!);
            await owner.Imports.LoadAsync(sources, 0, (_, _, _) => Task.FromResult(new MirrorResult(new([b], 0, 0), 0, 1)));
            var pending = owner.Imports.Pending;
            await owner.Imports.LoadAsync(sources, 0, (_, _, _) => throw new UserError("fixture failure"));
            Assert(owner.Imports.Pending == pending && owner.Profiles.Selected?.Name == a.Name && owner.Imports.Message == "fixture failure");
        });
        await check("Background subscriptions obey their own schedule, disabled preference and access-denied hold", async () =>
        {
            var now = DateTimeOffset.UtcNow; var calls = 0;
            using var owner = new ClientRuntime(new ConnectionCore(), new TunnelHostFixture(), importNow: () => now,
                importFetch: (_, _, _) => { calls++; return Task.FromResult(new MirrorResult(new([a], 0, 0), 0, 1)); });
            owner.Imports.Apply(group, new([a], 0, 0));
            owner.Preferences.AutoRefresh = false; await owner.Imports.PollAsync(); Assert(calls == 0);
            owner.Preferences.AutoRefresh = true; await owner.Imports.PollAsync(); await owner.Imports.PollAsync(); Assert(calls == 1);
            now = now.AddHours(7); await owner.Imports.PollAsync(); Assert(calls == 2);
            await owner.Imports.LoadAsync(sources, 0, (_, _, _) => throw new SubscriptionAccessDenied());
            now = now.AddDays(1); await owner.Imports.PollAsync(); Assert(calls == 2);
            await owner.Imports.LoadAsync(sources, 0); Assert(calls == 3);
        });
        await check("Subscription failure backs off, does not touch the old list and retries when due", async () =>
        {
            var now = DateTimeOffset.UtcNow; var calls = 0;
            using var owner = new ClientRuntime(new ConnectionCore(), new TunnelHostFixture(), importNow: () => now,
                importFetch: (_, _, _) => { calls++; throw new UserError("fixture"); });
            owner.Imports.Apply(group, new([a], 0, 0)); var before = owner.Profiles.Imported;
            await owner.Imports.PollAsync(); now = now.AddMinutes(4); await owner.Imports.PollAsync(); Assert(calls == 1);
            now = now.AddMinutes(2); await owner.Imports.PollAsync(); Assert(calls == 2 && ReferenceEquals(before, owner.Profiles.Imported));
        });
        await check("Runtime rejects unsafe subscription sources before invoking a loader", async () =>
        {
            var calls = 0; using var owner = new ClientRuntime(new ConnectionCore());
            await ExpectUserErrorAsync(() => owner.Imports.LoadAsync(["http://subscription.invalid/key"], 0,
                (_, _, _) => { calls++; return Task.FromResult(new MirrorResult(new([a], 0, 0), 0, 1)); }));
            Assert(calls == 0 && owner.Profiles.Groups.Count == 0 && owner.Activity.Operation is null);
        });
        await check("Disposal cancels an outstanding subscription load and rejects its late result", async () =>
        {
            var owner = new ClientRuntime(new ConnectionCore());
            var reply = new TaskCompletionSource<MirrorResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var loading = owner.Imports.LoadAsync(sources, 0, (_, _, _) => reply.Task);
            owner.Dispose(); reply.SetResult(new(new([a], 0, 0), 0, 1)); await loading;
            Assert(owner.Profiles.Groups.Count == 0 && owner.Activity.Operation is null && !owner.Imports.IsActive);
        });
        await check("Service subscriptions never load through a free protected TUN", async () =>
        {
            var free = a with { Source = ProfileSource.Free, Group = FreeServers.GroupId }; var calls = 0;
            var core = new RecoveryCore { Working = free };
            using var owner = new ClientRuntime(core, new TunnelHostFixture(), importFetch: (_, _, _) =>
            { calls++; return Task.FromResult(new MirrorResult(new([a], 0, 0), 0, 1)); });
            owner.Profiles.Imported = [free]; owner.Preferences.SystemTunnel = true; owner.Workspace.FreeConsent = FreeServers.ConsentVersion;
            await owner.Connections.StartAsync(free);
            string[] service = ["https://" + ServiceBrand.SubscriptionHosts[0] + ServiceBrand.SubscriptionPath + "fixture"];
            Assert(ServiceKey.SourceOf(service) == ProfileSource.Service && owner.Tunnels.ForeignSession);
            await owner.Imports.LoadAsync(service, 0); Assert(calls == 0 && owner.Profiles.Groups.Count == 0);
            await owner.Connections.StopAsync();
        });
    }
}
