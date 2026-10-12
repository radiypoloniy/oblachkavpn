using System.Diagnostics;

namespace Oblachka;

// Process creation is supplied by the trusted host (eventually the installed UAC launcher).
// No arbitrary executable path or command-line text is accepted through helper IPC.
public sealed class HelperProcessLifetime : IAsyncDisposable
{
    private readonly Process process;
    public NetworkHelperClient Client { get; }
    private HelperProcessLifetime(Process process, NetworkHelperClient client) { this.process = process; Client = client; }
    public static async Task<HelperProcessLifetime> ConnectAsync(Func<string, int, Process> launch, CancellationToken token)
    {
        var pipe = HelperPipeIdentity.NewPipeName();
        var process = launch(pipe, Environment.ProcessId);
        try
        {
            _ = process.SafeHandle;
            var client = new NetworkHelperClient(await HelperPipeClient.ConnectAsync(pipe, process, token));
            return new(process, client);
        }
        catch { process.Dispose(); throw; } // Helper's bounded startup/owner timeout owns its recovery.
    }
    public async ValueTask DisposeAsync()
    {
        try
        {
            await Client.DisposeAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { /* Never kill a helper while it may be restoring routes. */ }
        }
        finally { process.Dispose(); }
    }
}
