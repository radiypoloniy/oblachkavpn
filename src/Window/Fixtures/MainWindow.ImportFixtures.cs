using System.Windows.Threading;

namespace Oblachka;

public partial class MainWindow
{
    private async Task CheckOwnedImportAsync(List<string> checks)
    {
        var selected = Profiles.Parse("vless://11111111-1111-4111-8111-111111111111@example.invalid:443?security=tls&type=tcp#Window-import") with { Group = ProfileGroup.ManualId };
        using (var owner = ConnectionOwner(new WindowStartCore(), selected))
        {
            var reply = new TaskCompletionSource<MirrorResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var child = new MainWindow(owner, persistenceEnabled: false) { ShowActivated = false }; child.Show();
            var loading = child.LoadSubscriptionAsync(["https://fixture.invalid/sub"], 0, (_, _, _) => reply.Task);
            if (!owner.Imports.IsActive) throw new InvalidOperationException("Window did not delegate import ownership");
            child.exitRequested = true; child.Close();
            reply.SetResult(new(new([selected], 0, 0), 0, 1)); await loading;
            if (owner.Profiles.Groups.All(g => !g.IsSubscription) || owner.Activity.Operation is not null)
                throw new InvalidOperationException("Closing the window cancelled subscription loading");
        }
        using (var owner = ConnectionOwner(new WindowStartCore(), selected))
        {
            var reply = new TaskCompletionSource<MirrorResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var loading = owner.Imports.LoadAsync(["https://fixture.invalid/sub"], 0, (_, _, _) => reply.Task);
            var reference = ClosedStartupView(owner);
            for (var i = 0; i < 3; i++)
            {
                await Task.Delay(100); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            }
            if (reference.IsAlive || !owner.Imports.IsActive) throw new InvalidOperationException("Subscription owner retains a closed view");
            reply.SetResult(new(new([selected], 0, 0), 0, 1)); await loading;
        }
        checks.Add("Main window delegates subscription loading and closing it preserves the fetched result");
        checks.Add("Closed WPF view is collectible while a subscription request remains owned by the application");
        using (var owner = ConnectionOwner(new WindowStartCore(), selected))
        {
            ((WindowStartCore)owner.Engine).Release.SetResult();
            string[] sources = ["https://fixture.invalid/deferred"];
            var group = new ProfileGroup(ProfileGroup.IdFor(sources, ProfileSource.External), "Deferred", ProfileSource.External, sources);
            owner.Imports.Apply(group, new([selected], 0, 0));
            await owner.Connections.StartAsync(owner.Profiles.Selected!);
            var other = Profiles.Parse("vless://22222222-2222-4222-8222-222222222222@other.invalid:443?security=tls&type=tcp#Other");
            await owner.Imports.LoadAsync(sources, 0, (_, _, _) => Task.FromResult(new MirrorResult(new([other], 0, 0), 0, 1)));
            ClosedStartupView(owner);
            if (owner.Imports.Pending is null) throw new InvalidOperationException("Closing a view discards its owner's deferred subscription");
            await owner.Connections.StopAsync();
            if (owner.Imports.Pending is not null || owner.Profiles.GroupProfiles[group.Id].Single().Name != "Other") throw new InvalidOperationException("Deferred subscription was lost after view close");
        }
        checks.Add("Closing a WPF view preserves a deferred subscription until the connection stops");
        var fetched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var owner = new ClientRuntime(new WindowStartCore(), new ClientTunnelHost(), importFetch: (_, _, _) =>
        { fetched.TrySetResult(); return Task.FromResult(new MirrorResult(new([selected], 0, 0), 0, 1)); }))
        {
            owner.Persistence.Configure(null, isolated: true);
            string[] sources = ["https://fixture.invalid/scheduled"];
            var group = new ProfileGroup(ProfileGroup.IdFor(sources, ProfileSource.External), "Scheduled", ProfileSource.External, sources);
            owner.Imports.Apply(group, new([selected], 0, 0));
            var reference = ClosedStartupView(owner);
            owner.Preferences.AutoRefresh = true;
            owner.Imports.Start(new DispatcherSynchronizationContext(Dispatcher));
            await Task.Delay(100); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            await fetched.Task.WaitAsync(TimeSpan.FromSeconds(8));
            for (var i = 0; i < 3; i++)
            {
                await Task.Delay(100); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            }
            if (reference.IsAlive) throw new InvalidOperationException("Subscription scheduler retains a closed view");
        }
        checks.Add("Subscription scheduler fetches after the WPF view has closed and been collected");
    }
}
