namespace Oblachka;

public static partial class SelfTests
{
    private static async Task ClientUpdatesChecksAsync(Func<string, Func<Task>, Task> check)
    {
        AppRelease Release() => new(new Version(99, 0, 0), "v99.0.0", new Uri("https://example.invalid/setup.exe"), 1, "", "", new Uri("https://example.invalid"))
            { Signature = new Uri("https://example.invalid/setup.exe.sig") };
        await check("Application update timer checks and announces once without WPF", async () =>
        {
            using var owner = new ClientRuntime(new RecoveryCore()); owner.Preferences.UpdateAuto = true;
            using var updates = new ClientUpdates(owner, (_, _) => Task.FromResult<AppRelease?>(Release()), _ => true, () => { });
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var notices = 0;
            updates.AvailableInBackground += _ => notices++;
            updates.Changed += () => { if (updates.Checked && !updates.Busy) finished.TrySetResult(); };
            updates.Start(new SynchronizationContext(), TimeSpan.FromMilliseconds(10));
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await updates.CheckAsync(false);
            Assert(updates.Available is { Signature: not null } && notices == 1 && updates.Error is null);
        });
        await check("Changing update channel discards an in-flight response", async () =>
        {
            using var owner = new ClientRuntime(new RecoveryCore()); owner.Preferences.UpdatePrerelease = false;
            var response = new TaskCompletionSource<AppRelease?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var channels = new List<bool>();
            using var updates = new ClientUpdates(owner, (pre, _) => { channels.Add(pre); return response.Task; }, _ => true, () => { });
            var pending = updates.CheckAsync(true);
            owner.Preferences.UpdatePrerelease = true; response.SetResult(Release()); await pending;
            Assert(updates.Available is null && !updates.Checked && !updates.Busy);
            await updates.CheckAsync(true);
            Assert(channels.SequenceEqual(new[] { false, true }) && updates.Checked && updates.Available is not null);
        });
        await check("Disposing update checker cancels its request and suppresses later view callbacks", async () =>
        {
            using var owner = new ClientRuntime(new RecoveryCore());
            var cancelled = false; var changes = 0;
            using var updates = new ClientUpdates(owner, async (_, token) =>
            {
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { cancelled = true; throw; }
                return null;
            }, _ => true, () => { });
            updates.Changed += () => changes++;
            var pending = updates.CheckAsync(true); updates.Dispose();
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(cancelled && !updates.Busy && !updates.Checked && changes == 1);
        });
        await check("Update errors stay quiet in background and installation prevents duplicate checks", async () =>
        {
            using var owner = new ClientRuntime(new RecoveryCore()); var calls = 0;
            using var updates = new ClientUpdates(owner, (_, _) => { calls++; throw new UserError("fixture update error"); }, _ => true, () => { });
            updates.Installing = true; await updates.CheckAsync(true); Assert(calls == 0);
            updates.Installing = false; await updates.CheckAsync(false); Assert(updates.Error is null && !updates.Busy);
            await updates.CheckAsync(true); Assert(updates.Error == "fixture update error" && calls == 2);
        });
    }
}
