using System.Text;
using System.Text.Json.Nodes;

namespace RoutePilot;

public static partial class SelfTests
{
    private static async Task VmKitChecksAsync(Func<string, Func<Task>, Task> check, string root)
    {
        await check("VM kit accepts Windows PowerShell BOM and creates only validated config without overwriting", async () =>
        {
            var input = Path.Combine(root, "vm-peers.json"); var output = Path.Combine(root, "vm-tun.json");
            await File.WriteAllTextAsync(input, "[\"192.0.2.1\"]", new UTF8Encoding(true));
            Assert(await VmTunCommand.PrepareAsync(10808, input, output) == 0);
            var before = await File.ReadAllBytesAsync(output); var config = JsonNode.Parse(before)!;
            Assert(config["outbounds"]![0]!["server_port"]!.GetValue<int>() == 10808);
            Assert(!Encoding.UTF8.GetString(before).Contains("uuid") && !Encoding.UTF8.GetString(before).Contains("password"));
            try { await VmTunCommand.PrepareAsync(10808, input, output); throw new InvalidOperationException(); }
            catch (IOException) { }
            // Читаем до сравнения: в .NET 10 SequenceEqual разрешается в span-перегрузку,
            // а span не может пережить await.
            var after = await File.ReadAllBytesAsync(output);
            Assert(before.SequenceEqual(after));
        });
        await check("VM preparation rejects overlong input and broad routes without creating a config", async () =>
        {
            var input = Path.Combine(root, "vm-bad-peers.json"); var output = Path.Combine(root, "vm-bad-tun.json");
            await File.WriteAllTextAsync(input, new string('x', 4097));
            await ExpectUserErrorAsync(() => VmTunCommand.PrepareAsync(10808, input, output)); Assert(!File.Exists(output));
            await File.WriteAllTextAsync(input, "[\"0.0.0.0/0\"]");
            await ExpectUserErrorAsync(() => VmTunCommand.PrepareAsync(10808, input, output)); Assert(!File.Exists(output));
        });
    }
}
