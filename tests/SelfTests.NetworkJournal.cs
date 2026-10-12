using System.Text.Json;

namespace Oblachka;

public static partial class SelfTests
{
    private static async Task NetworkJournalChecksAsync(Func<string, Func<Task>, Task> check, string root)
    {
        await check("File journal survives owner restart after a side effect without applied acknowledgement", () => Sync(() =>
        {
            var path = Path.Combine(root, "network-restart", "session.json");
            var backend = new FakeNetworkBackend(); var request = NetworkAcquire();
            using (var journal = new NetworkSessionJournal(path))
            {
                backend.ApplyHook = (number, after) => { if (number == 3 && after) throw new SimulatedNetworkCrash(); };
                ExpectNetworkCrash(() => new NetworkSession(journal, backend).Acquire(request));
                Assert(journal.Load()?.Changes[2].State == NetworkChangeState.Planned);
            }
            backend.ApplyHook = null;
            using var reopened = new NetworkSessionJournal(path);
            Assert(new NetworkSession(reopened, backend).Recover()?.State == NetworkSessionState.Released && backend.Original);
            var content = File.ReadAllText(path);
            Assert(!content.Contains("Outbound") && !content.Contains("Subscription") && !content.Contains("LoopbackPort"));
        }));
        await check("Exclusive journal owner prevents a second process or controller owning the same path", () => Sync(() =>
        {
            var path = Path.Combine(root, "network-lock", "session.json");
            using (var first = new NetworkSessionJournal(path))
            {
                try { using var second = new NetworkSessionJournal(path); throw new InvalidOperationException("Lock missing"); }
                catch (IOException) { }
            }
            using var next = new NetworkSessionJournal(path); Assert(next.Load() is null);
        }));
        await check("Corrupt, duplicate, unknown, missing and overlong journal data cannot trigger cleanup", () => Sync(() =>
        {
            var path = Path.Combine(root, "network-corrupt", "session.json");
            using var journal = new NetworkSessionJournal(path); var backend = new FakeNetworkBackend();
            foreach (var content in new[]
            {
                "{broken", "{}", new string('x', 65537),
                "{\"Version\":1,\"Version\":1}",
                $"{{\"Version\":1,\"LeaseId\":\"{Guid.NewGuid()}\",\"State\":\"Prepare\",\"Changes\":[],\"Command\":\"run\"}}"
            })
            {
                File.WriteAllText(path, content);
                ExpectUserError(() => new NetworkSession(journal, backend).Recover());
                Assert(File.ReadAllText(path) == content && backend.RestoreCalls == 0 && backend.ApplyCalls == 0);
            }
        }));
        await check("Invalid plans cannot overwrite valid journal or mutate backend", () => Sync(() =>
        {
            var path = Path.Combine(root, "network-invalid", "session.json");
            using var journal = new NetworkSessionJournal(path); var backend = new FakeNetworkBackend(); var request = NetworkAcquire();
            var session = new NetworkSession(journal, backend); var valid = session.Acquire(request);
            var before = File.ReadAllBytes(path);
            foreach (var invalid in new[]
            {
                valid with { Version = 2 }, valid with { LeaseId = Guid.Empty },
                valid with { Changes = Array.Empty<NetworkChange>() },
                valid with { Changes = valid.Changes.Reverse().ToArray() },
                valid with { Changes = new[] { valid.Changes[0], valid.Changes[0] } },
                valid with { State = NetworkSessionState.Released },
                valid with { Changes = new[] { valid.Changes[0] with { After = new string('x', 513) } } }
            })
            {
                ExpectUserError(() => journal.Save(invalid)); Assert(before.SequenceEqual(File.ReadAllBytes(path)));
            }
            session.Release(request.LeaseId); Assert(backend.Original);
        }));
    }
}
