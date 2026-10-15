namespace Oblachka;

public static partial class SelfTests
{
    private static async Task NetworkSessionChecksAsync(Func<string, Func<Task>, Task> check, string root)
    {
        await check("Network intent precedes changes, release reverses dependencies and repeats safely", () => Sync(() =>
        {
            var journal = new MemoryNetworkJournal(); var backend = new FakeNetworkBackend();
            backend.BeforeApply = () => Assert(journal.Record is not null);
            var session = new NetworkSession(journal, backend); var request = NetworkAcquire();
            Assert(session.Acquire(request).State == NetworkSessionState.Applied);
            Assert(session.Release(request.LeaseId).State == NetworkSessionState.Released && backend.Original);
            Assert(backend.Operations.TakeLast(4).SequenceEqual(new[] { "restore:dns", "restore:route", "restore:tun", "restore:peer" }));
            var calls = backend.RestoreCalls;
            session.Release(request.LeaseId); session.Recover(); Assert(calls == backend.RestoreCalls);
        }));
        await check("Crash before and after each apply recovers from durable intent", () => Sync(() =>
        {
            for (var point = 1; point <= 4; point++) foreach (var after in new[] { false, true })
            {
                var journal = new MemoryNetworkJournal(); var backend = new FakeNetworkBackend();
                backend.ApplyHook = (number, written) => { if (number == point && written == after) throw new SimulatedNetworkCrash(); };
                ExpectNetworkCrash(() => new NetworkSession(journal, backend).Acquire(NetworkAcquire()));
                backend.ApplyHook = null;
                Assert(new NetworkSession(journal, backend).Recover()?.State == NetworkSessionState.Released && backend.Original);
            }
        }));
        await check("Crash before and after every acquire journal commit leaves recoverable state", () => Sync(() =>
        {
            for (var point = 1; point <= 6; point++) foreach (var after in new[] { false, true })
            {
                var journal = new MemoryNetworkJournal(); var backend = new FakeNetworkBackend();
                journal.WriteHook = (number, written) => { if (number == point && written == after) throw new SimulatedNetworkCrash(); };
                ExpectNetworkCrash(() => new NetworkSession(journal, backend).Acquire(NetworkAcquire()));
                journal.WriteHook = null;
                var recovered = new NetworkSession(journal, backend).Recover();
                Assert((recovered is null || recovered.State == NetworkSessionState.Released) && backend.Original);
            }
        }));
        await check("Crash before and after each restore supports repeated cleanup", () => Sync(() =>
        {
            for (var point = 1; point <= 4; point++) foreach (var after in new[] { false, true })
            {
                var journal = new MemoryNetworkJournal(); var backend = new FakeNetworkBackend(); var request = NetworkAcquire();
                var session = new NetworkSession(journal, backend); session.Acquire(request);
                backend.RestoreHook = (number, written) => { if (number == point && written == after) throw new SimulatedNetworkCrash(); };
                ExpectNetworkCrash(() => session.Release(request.LeaseId));
                backend.RestoreHook = null;
                Assert(new NetworkSession(journal, backend).Recover()?.State == NetworkSessionState.Released && backend.Original);
            }
        }));
        await check("Crash before and after every restore journal commit is idempotent", () => Sync(() =>
        {
            for (var point = 7; point <= 12; point++) foreach (var after in new[] { false, true })
            {
                var journal = new MemoryNetworkJournal(); var backend = new FakeNetworkBackend(); var request = NetworkAcquire();
                var session = new NetworkSession(journal, backend); session.Acquire(request);
                journal.WriteHook = (number, written) => { if (number == point && written == after) throw new SimulatedNetworkCrash(); };
                ExpectNetworkCrash(() => session.Release(request.LeaseId)); journal.WriteHook = null;
                Assert(new NetworkSession(journal, backend).Recover()?.State == NetworkSessionState.Released && backend.Original);
            }
        }));
        await check("Apply errors and cancelled acquisition roll back without a cancelled cleanup token", () => Sync(() =>
        {
            var journal = new MemoryNetworkJournal(); var backend = new FakeNetworkBackend();
            backend.ApplyHook = (number, after) => { if (number == 2 && after) throw new IOException("fixture"); };
            try { new NetworkSession(journal, backend).Acquire(NetworkAcquire()); throw new InvalidOperationException(); }
            catch (IOException) { }
            Assert(backend.Original && journal.Record?.State == NetworkSessionState.Released);
            using var cancellation = new CancellationTokenSource();
            backend.ApplyHook = (_, after) => { if (after) cancellation.Cancel(); };
            try { new NetworkSession(journal, backend).Acquire(NetworkAcquire(), cancellation.Token); throw new InvalidOperationException(); }
            catch (OperationCanceledException) { }
            Assert(backend.Original && journal.Record?.State == NetworkSessionState.Released);
        }));
        await check("Foreign changes are preserved and dependent cleanup stops until resolved", () => Sync(() =>
        {
            var journal = new MemoryNetworkJournal(); var backend = new FakeNetworkBackend(); var request = NetworkAcquire();
            var session = new NetworkSession(journal, backend); session.Acquire(request);
            backend.Values["dns"] = "foreign-dns"; backend.Owners["dns"] = Guid.NewGuid();
            Assert(session.Release(request.LeaseId).State == NetworkSessionState.RecoveryRequired);
            Assert(backend.Values["dns"] == "foreign-dns" && backend.RestoreCalls == 1 && backend.Values["tun"] == "own-interface");
            ExpectUserError(() => session.Acquire(NetworkAcquire()));
            Assert(session.Recover()?.State == NetworkSessionState.RecoveryRequired && backend.Values["dns"] == "foreign-dns");
            backend.Values["dns"] = "original-dns"; backend.Owners.Remove("dns");
            Assert(session.Recover()?.State == NetworkSessionState.Released && backend.Original);
        }));
        await check("Restore failure retains journal and retry completes remaining work", () => Sync(() =>
        {
            var journal = new MemoryNetworkJournal(); var backend = new FakeNetworkBackend(); var request = NetworkAcquire();
            var session = new NetworkSession(journal, backend); session.Acquire(request);
            backend.RestoreHook = (number, after) => { if (number == 2 && after) throw new IOException("fixture"); };
            Assert(session.Release(request.LeaseId).State == NetworkSessionState.RecoveryRequired);
            backend.RestoreHook = null;
            Assert(new NetworkSession(journal, backend).Recover()?.State == NetworkSessionState.Released && backend.Original);
        }));
        await check("Journal failure blocks mutations; a later retry uses surviving intent", () => Sync(() =>
        {
            var journal = new MemoryNetworkJournal(); var backend = new FakeNetworkBackend(); var request = NetworkAcquire();
            var session = new NetworkSession(journal, backend); session.Acquire(request);
            journal.WriteHook = (_, _) => throw new IOException("fixture storage unavailable");
            try { session.Release(request.LeaseId); throw new InvalidOperationException(); } catch (IOException) { }
            Assert(backend.RestoreCalls == 0 && journal.Record?.State == NetworkSessionState.Applied);
            journal.WriteHook = null; Assert(session.Recover()?.State == NetworkSessionState.Released && backend.Original);
        }));
        await check("Lease mismatch and duplicate acquire do not affect active resources", () => Sync(() =>
        {
            var journal = new MemoryNetworkJournal(); var backend = new FakeNetworkBackend(); var request = NetworkAcquire();
            var session = new NetworkSession(journal, backend); session.Acquire(request);
            ExpectUserError(() => session.Release(Guid.NewGuid())); ExpectUserError(() => session.Acquire(request));
            Assert(backend.ApplyCalls == 4 && backend.RestoreCalls == 0);
            session.Release(request.LeaseId);
            var next = NetworkAcquire(); session.Acquire(next); session.Release(next.LeaseId); Assert(backend.Original);
        }));
        await NetworkJournalChecksAsync(check, root);
        await check("Controllers sharing one journal serialize competing transactions", async () =>
        {
            var journal = new MemoryNetworkJournal(); var backend = new FakeNetworkBackend();
            using var entered = new ManualResetEventSlim(); using var resume = new ManualResetEventSlim();
            using var competing = new ManualResetEventSlim();
            backend.BeforeApply = () =>
            {
                entered.Set();
                if (!resume.Wait(TimeSpan.FromSeconds(5))) throw new IOException("Fixture synchronization timed out");
            };
            var first = Task.Run(() => new NetworkSession(journal, backend).Acquire(NetworkAcquire()));
            Task? second = null;
            try
            {
                Assert(entered.Wait(TimeSpan.FromSeconds(5)));
                second = Task.Run(() =>
                {
                    competing.Set();
                    ExpectUserError(() => new NetworkSession(journal, backend).Acquire(NetworkAcquire()));
                });
                Assert(competing.Wait(TimeSpan.FromSeconds(5)));
                await Task.Delay(100); Assert(!second.IsCompleted);
            }
            finally
            {
                resume.Set(); await first;
                if (second is not null) await second;
            }
            Assert(journal.Record?.State == NetworkSessionState.Applied && backend.ApplyCalls == 4);
            new NetworkSession(journal, backend).Recover(); Assert(backend.Original);
        });
    }
}
