using System.Diagnostics;
using System.Text.Json;

namespace Oblachka;

public static partial class SelfTests
{
    private static async Task ClientConnectionIpcChecksAsync(Func<string, Func<Task>, Task> check)
    {
        await check("Server catalog is paged and excludes credentials even with 600 long labels", async () =>
        {
            using var owner = new ClientRuntime(new ConnectionCore());
            owner.Profiles.Imported = Enumerable.Range(0, ProfileGroup.MaxTotalProfiles)
                .Select(i => Profiles.Parse(link) with { Name = i + new string('я', 400) }).ToArray();
            owner.Profiles.Selected = owner.Profiles.Imported[0];
            using var current = Process.GetCurrentProcess(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var endpoint = new ClientViewServer(owner, new SynchronizationContext()); var serving = endpoint.ServeAsync(current, timeout.Token);
            await using (var client = await ClientViewClient.ConnectAsync(endpoint.PipeName, current, timeout.Token))
            {
                var first = await client.SendAsync(ClientViewOperation.ListServers, timeout.Token);
                Assert(first.Servers is { Offset: 0, Total: ProfileGroup.MaxTotalProfiles } && first.Servers.Items.Length == ClientServerCatalog.PageSize);
                await using var frame = new MemoryStream(); await HelperProtocol.WriteAsync(frame, first, timeout.Token);
                Assert(frame.Length <= HelperProtocol.MaxBytes + 4);
                var json = JsonSerializer.Serialize(first);
                Assert(!json.Contains("11111111-1111", StringComparison.Ordinal) && !json.Contains("example.com", StringComparison.Ordinal));
                var next = await client.SendAsync(ClientViewOperation.ListServers, timeout.Token, offset: ClientServerCatalog.PageSize);
                Assert(next.Servers?.Offset == ClientServerCatalog.PageSize && next.Servers.Items[0].Id != first.Servers!.Items[0].Id);
            }
            await serving;
        });
        await check("A connect request survives disposal of its requesting IPC view", async () =>
        {
            var core = new ConnectionCore { Release = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            using var owner = new ClientRuntime(core); var profile = Profiles.Parse(link); owner.Profiles.Imported = [profile]; owner.Profiles.Selected = profile;
            using var current = Process.GetCurrentProcess(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var endpoint = new ClientViewServer(owner, new SynchronizationContext()); var serving = endpoint.ServeAsync(current, timeout.Token);
            await using (var client = await ClientViewClient.ConnectAsync(endpoint.PipeName, current, timeout.Token))
            {
                var accepted = await client.SendAsync(ClientViewOperation.Connect, timeout.Token, serverId: ClientServerCatalog.Id(profile));
                Assert(accepted.State?.Busy == true);
            }
            await serving; Assert(owner.Connections.IsActive && core.State == ConnectionState.Starting);
            core.Release.SetResult(); await owner.Connections.Pending!.WaitAsync(timeout.Token);
            Assert(core.State == ConnectionState.Running && owner.Profiles.Running == profile && owner.Workspace.WasConnected);
        });
        await check("Catalog identifiers select exact rows and stale identifiers cannot select a replacement", async () =>
        {
            var core = new ConnectionCore(); using var owner = new ClientRuntime(core);
            var first = Profiles.Parse(link); var second = Profiles.Parse(link.Replace("example.com", "second.invalid"));
            owner.Profiles.Imported = [first, second]; owner.Profiles.Selected = first;
            using var current = Process.GetCurrentProcess(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var endpoint = new ClientViewServer(owner, new SynchronizationContext()); var serving = endpoint.ServeAsync(current, timeout.Token);
            await using (var client = await ClientViewClient.ConnectAsync(endpoint.PipeName, current, timeout.Token))
            {
                var stale = ClientServerCatalog.Id(second)!;
                Assert((await client.SendAsync(ClientViewOperation.SelectServer, timeout.Token, serverId: stale)).State?.SelectedServerId == stale);
                await client.SendAsync(ClientViewOperation.Connect, timeout.Token);
                await owner.Connections.Pending!.WaitAsync(timeout.Token);
                Assert(core.Current == second);
                owner.Profiles.Imported = [first];
                var refused = await client.SendAsync(ClientViewOperation.SelectServer, timeout.Token, serverId: stale);
                Assert(refused.Failure == ClientViewFailure.InvalidRequest && core.Starts.Count == 1 && core.Current == second);
            }
            await serving;
        });
    }
}
