using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

namespace RoutePilot;

public static partial class SelfTests
{
    private static async Task HelperPipeChecksAsync(Func<string, Func<Task>, Task> check, string root)
    {
        await check("Helper frame rejects duplicate, unknown, missing, oversized and invalid command fields", async () =>
        {
            foreach (var text in new[] { "{}", "{\"Version\":1,\"Version\":1}", new string('x', 8193),
                "{\"Version\":1,\"RequestId\":\"00000000-0000-0000-0000-000000000001\",\"Operation\":\"Heartbeat\",\"Lease\":null,\"Run\":\"cmd\"}" })
                ExpectUserError(() => HelperProtocol.Parse<HelperRequest>(Encoding.UTF8.GetBytes(text)));
            ExpectUserError(() => HelperProtocol.Validate(new(2, Guid.NewGuid(), HelperOperation.Heartbeat, null)));
            ExpectUserError(() => HelperProtocol.Validate(new(1, Guid.NewGuid(), HelperOperation.Heartbeat, NetworkAcquire())));
            ExpectUserError(() => HelperPipeIdentity.ValidateName("../pipe"));
            using var shortFrame = new MemoryStream(new byte[] { 0, 32, 0, 0, 1 });
            try { await HelperProtocol.ReadAsync<HelperRequest>(shortFrame, CancellationToken.None); throw new InvalidOperationException(); }
            catch (EndOfStreamException) { }
        });
        await check("Authenticated real pipe acquires, heartbeats, rejects чужой lease, releases and cleans on disconnect", async () =>
        {
            var journal = new MemoryNetworkJournal(); var backend = new FakeNetworkBackend(); var name = HelperPipeIdentity.NewPipeName();
            using var owner = Process.GetCurrentProcess(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var server = new HelperPipeServer(new(journal, backend), name, owner).RunAsync(timeout.Token);
            await using (var client = await HelperPipeClient.ConnectAsync(name, owner, timeout.Token))
            {
                var request = NetworkAcquire();
                Assert((await client.SendAsync(HelperOperation.Acquire, request, timeout.Token)).State == NetworkSessionState.Applied);
                Assert((await client.SendAsync(HelperOperation.Heartbeat, null, timeout.Token)).State == NetworkSessionState.Applied);
                var foreign = new NetworkLeaseRequest(1, Guid.NewGuid(), NetworkLeaseAction.Release, 0, Array.Empty<string>());
                Assert((await client.SendAsync(HelperOperation.Release, foreign, timeout.Token)).Failure == HelperFailure.InvalidRequest);
                Assert(backend.RestoreCalls == 0);
                Assert((await client.SendAsync(HelperOperation.Release, foreign with { LeaseId = request.LeaseId }, timeout.Token)).State == NetworkSessionState.Released);
                await client.SendAsync(HelperOperation.Acquire, NetworkAcquire(), timeout.Token);
            }
            await PipeClosedAsync(server); Assert(backend.Original && journal.Record?.State == NetworkSessionState.Released);
        });
        await check("Missing heartbeat closes real pipe and restores acquired lease", async () =>
        {
            var journal = new MemoryNetworkJournal(); var backend = new FakeNetworkBackend(); var name = HelperPipeIdentity.NewPipeName();
            using var owner = Process.GetCurrentProcess(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var server = new HelperPipeServer(new(journal, backend), name, owner, TimeSpan.FromMilliseconds(400)).RunAsync(timeout.Token);
            await using var client = await HelperPipeClient.ConnectAsync(name, owner, timeout.Token);
            await client.SendAsync(HelperOperation.Acquire, NetworkAcquire(), timeout.Token);
            await PipeClosedAsync(server); Assert(backend.Original && journal.Record?.State == NetworkSessionState.Released);
        });
        await check("Pipe handshake rejects an unexpected owner process before any mutation", async () =>
        {
            var journal = new MemoryNetworkJournal(); var backend = new FakeNetworkBackend(); var name = HelperPipeIdentity.NewPipeName();
            using var child = StartHelperIdle(); using var current = Process.GetCurrentProcess();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            try
            {
                var server = new HelperPipeServer(new(journal, backend), name, child).RunAsync(timeout.Token);
                await using var client = await HelperPipeClient.ConnectAsync(name, current, timeout.Token);
                await ExpectUserErrorAsync(() => server);
                Assert(backend.ApplyCalls == 0 && backend.Original);
            }
            finally { if (!child.HasExited) child.Kill(); await child.WaitForExitAsync(); }
        });
        await check("Client rejects a pipe served by a different process than the launched helper", async () =>
        {
            var journal = new MemoryNetworkJournal(); var backend = new FakeNetworkBackend(); var name = HelperPipeIdentity.NewPipeName();
            using var child = StartHelperIdle(); using var current = Process.GetCurrentProcess();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var server = new HelperPipeServer(new(journal, backend), name, current).RunAsync(timeout.Token);
            try { await ExpectUserErrorAsync(async () => { await using var client = await HelperPipeClient.ConnectAsync(name, child, timeout.Token); }); }
            finally { if (!child.HasExited) child.Kill(); await child.WaitForExitAsync(); await PipeClosedAsync(server); }
            Assert(backend.ApplyCalls == 0);
        });
        await check("Cross-process helper IPC cleans durable journal after client disconnect", async () =>
        {
            var folder = Path.Combine(root, "helper-child"); Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "journal.json"); var name = HelperPipeIdentity.NewPipeName();
            var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "RoutePilot.exe")) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "--helper-fixture", name, Environment.ProcessId.ToString(), path }) start.ArgumentList.Add(arg);
            using var child = Process.Start(start)!; using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                await using (var client = await HelperPipeClient.ConnectAsync(name, child, timeout.Token))
                    Assert((await client.SendAsync(HelperOperation.Acquire, NetworkAcquire(), timeout.Token)).State == NetworkSessionState.Applied);
                await child.WaitForExitAsync(timeout.Token); Assert(child.ExitCode == 0);
                using var journal = new NetworkSessionJournal(path); Assert(journal.Load()?.State == NetworkSessionState.Released);
            }
            finally { if (!child.HasExited) child.Kill(); await child.WaitForExitAsync(); }
        });
    }

    public static async Task<int> HelperFixtureAsync(string name, int ownerId, string path)
    {
        using var owner = Process.GetProcessById(ownerId); using var journal = new NetworkSessionJournal(path);
        var backend = new FakeNetworkBackend();
        await PipeClosedAsync(new HelperPipeServer(new(journal, backend), name, owner).RunAsync(CancellationToken.None));
        return backend.Original ? 0 : 2;
    }

    private static Process StartHelperIdle()
    {
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "RoutePilot.exe")) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--helper-idle-fixture"); return Process.Start(start)!;
    }

    private static async Task PipeClosedAsync(Task task)
    {
        try { await task; } catch (IOException) { } catch (OperationCanceledException) { }
    }
}
