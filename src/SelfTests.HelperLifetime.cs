using System.Diagnostics;

namespace RoutePilot;

public static partial class SelfTests
{
    private static async Task HelperLifetimeChecksAsync(Func<string, Func<Task>, Task> check, string root)
    {
        await check("Helper process lifetime connects pinned PID, releases and exits without network changes", async () =>
        {
            var path = Path.Combine(root, "lifetime-journal.json");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await using (var lifetime = await HelperProcessLifetime.ConnectAsync((pipe, owner) =>
            {
                var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "RoutePilot.exe")) { UseShellExecute = false, CreateNoWindow = true };
                foreach (var arg in new[] { "--helper-fixture", pipe, owner.ToString(), path }) start.ArgumentList.Add(arg);
                return Process.Start(start)!;
            }, timeout.Token))
            {
                await lifetime.Client.AcquireAsync(NetworkAcquire(), timeout.Token);
                Assert(await lifetime.Client.ReleaseAsync(timeout.Token) == NetworkSessionState.Released);
            }
            using var journal = new NetworkSessionJournal(path); Assert(journal.Load()?.State == NetworkSessionState.Released);
        });
    }
}
