namespace Oblachka;

public static partial class SelfTests
{
    private static ClientRuntime PersistenceFixture(string path)
    {
        var runtime = new ClientRuntime(new RecoveryCore());
        var profile = Profiles.Parse(link) with { Source = ProfileSource.External, Group = ProfileGroup.ManualId };
        runtime.Profiles.Groups.Add(ProfileGroup.Manual); runtime.Profiles.GroupProfiles[ProfileGroup.ManualId] = [profile];
        runtime.Profiles.Imported = [profile]; runtime.Profiles.Selected = profile;
        runtime.Persistence.Configure(new ClientStateStore(path), isolated: true);
        runtime.Persistence.Restored = true;
        return runtime;
    }
    private static async Task ClientPersistenceChecksAsync(Func<string, Func<Task>, Task> check, string root)
    {
        await check("Application snapshot preserves hidden nodes and saved rule-set data without a window", () => Sync(() =>
        {
            var path = Path.Combine(root, "application-state.bin"); using var runtime = PersistenceFixture(path);
            var first = runtime.Profiles.Selected!; var hidden = first with { Name = "Hidden" };
            runtime.Profiles.GroupProfiles[ProfileGroup.ManualId] = [first, hidden];
            runtime.Workspace.HiddenServers.Add(AutoPool.Key(hidden)); runtime.Workspace.Favorites.Add(AutoPool.Key(first));
            var kept = RoutingPlan.Create("saved.invalid", SiteAction.Block);
            runtime.TunnelPolicy.Sites.Add(kept);
            runtime.Workspace.WasConnected = true; runtime.Workspace.TrafficToday = 1234;
            runtime.Persistence.Queue(); Assert(runtime.Persistence.Flush());
            var restored = runtime.Persistence.Store!.Load()!;
            Assert(restored.Profiles.Length == 2 && restored.HiddenServers.Contains(AutoPool.Key(hidden)) && restored.Favorites.Length == 1);
            Assert(restored.RouteRules.SequenceEqual([kept]) && restored.WasConnected && restored.TrafficBytes == 1234);
            Assert(ClientStateStore.RestoreProfiles(restored).Any(p => p.Name == "Hidden"));
        }));
        await check("Automatic application save runs without a WPF timer and writes the latest edit", async () =>
        {
            var path = Path.Combine(root, "application-auto-state.bin"); using var runtime = PersistenceFixture(path);
            var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            runtime.Persistence.Changed += () => { if (runtime.Persistence.LastError is null && !runtime.Persistence.Dirty) saved.TrySetResult(); };
            runtime.Persistence.Start(new SynchronizationContext());
            runtime.Preferences.Notices = false;
            runtime.TunnelPolicy.Sites.Add(RoutingPlan.Create("latest.invalid", SiteAction.Block)); runtime.Persistence.Queue();
            await saved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var snapshot = runtime.Persistence.Store!.Load()!;
            Assert(!snapshot.Notices && snapshot.RouteRules.Any(r => r.Domain == "latest.invalid"));
        });
        await check("Automatic save waits for connection work and honors opt-out", async () =>
        {
            var path = Path.Combine(root, "application-busy-state.bin"); using var runtime = PersistenceFixture(path);
            using var busy = new CancellationTokenSource(); runtime.Activity.Operation = busy;
            runtime.Persistence.Queue(); Assert(!runtime.Persistence.Flush() && !File.Exists(path));
            runtime.Activity.Operation = null; runtime.Persistence.Remember = false;
            Assert(!runtime.Persistence.Flush() && !File.Exists(path));
            runtime.Persistence.Remember = true;
            Assert(runtime.Persistence.Flush() && File.Exists(path));
            var saved = runtime.Persistence.Store!.Load()!;
            runtime.Preferences.Notices = false; runtime.Persistence.Remember = false;
            Assert(!runtime.Persistence.Flush() && runtime.Persistence.Store.Load()!.Notices == saved.Notices);
            await Task.CompletedTask;
        });
    }
}
