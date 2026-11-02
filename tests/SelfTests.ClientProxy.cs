namespace Oblachka;

public static partial class SelfTests
{
    private static async Task ClientProxyChecksAsync(Func<string, Func<Task>, Task> check, string root)
    {
        var original = new ProxySettings(1, "corp.invalid:3128", "*.corp.invalid", "http://pac.invalid/proxy.pac");
        await check("Application proxy survives core failure and restores on stop without a window", async () =>
        {
            var core = new RecoveryCore(); using var owner = new ClientRuntime(core);
            await core.StartAsync(Profiles.Parse(link), CancellationToken.None);
            var store = new FakeProxyStore(original); using var proxy = new ClientProxy(owner);
            owner.Preferences.SystemProxy = true;
            proxy.Start(new SynchronizationContext(), new SystemProxy(store, Path.Combine(root, "client-proxy", "backup.json")), false);
            Assert(store.Current.ProxyServer == "127.0.0.1:10808" && proxy.Port == 10808);
            core.Fail(); proxy.Sync(); Assert(store.Current.ProxyServer == "127.0.0.1:10808" && proxy.Port == 10808);
            core.Stop(); proxy.Sync(); Assert(store.Current == original && proxy.Port == 0);
        });
        await check("Application proxy guard repairs at most three overwrites and preserves a foreign proxy on disposal", async () =>
        {
            var core = new RecoveryCore(); using var owner = new ClientRuntime(core);
            await core.StartAsync(Profiles.Parse(link), CancellationToken.None);
            var clock = DateTimeOffset.UtcNow; var store = new FakeProxyStore(original);
            using var proxy = new ClientProxy(owner, () => clock); owner.Preferences.SystemProxy = true;
            proxy.Start(new SynchronizationContext(), new SystemProxy(store, Path.Combine(root, "client-proxy-guard", "backup.json")), false);
            var foreign = new ProxySettings(1, "foreign.invalid:1234", null, null);
            for (var i = 0; i < 3; i++)
            {
                store.Current = foreign; proxy.Poll(); clock += TimeSpan.FromSeconds(6);
                Assert(store.Current.ProxyServer == "127.0.0.1:10808" && !proxy.GaveUp);
            }
            store.Current = foreign; proxy.Poll(); Assert(proxy.GaveUp && store.Current == foreign);
            proxy.Dispose(); Assert(store.Current == foreign);
        });
        await check("Proxy guard timer operates without WPF and TUN mode restores the previous settings", async () =>
        {
            var core = new RecoveryCore(); using var owner = new ClientRuntime(core);
            await core.StartAsync(Profiles.Parse(link), CancellationToken.None);
            var store = new FakeProxyStore(original); owner.Preferences.SystemProxy = true;
            owner.Proxy.Start(new SynchronizationContext(), new SystemProxy(store, Path.Combine(root, "client-proxy-timer", "backup.json")), false);
            var repaired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            owner.Proxy.Changed += change => { if (change.Kind == ProxyChangeKind.Repaired) repaired.TrySetResult(); };
            store.Current = new(0, null, null, "http://changed.invalid/pac");
            await repaired.Task.WaitAsync(TimeSpan.FromSeconds(9));
            Assert(store.Current.ProxyServer == "127.0.0.1:10808");
            owner.Preferences.SystemTunnel = true; owner.Proxy.Sync();
            Assert(store.Current == original && owner.Proxy.Port == 0);
        });
    }
}
