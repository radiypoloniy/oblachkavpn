using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace RoutePilot;

public static partial class SelfTests
{
    private const string link = "vless://11111111-1111-4111-8111-111111111111@example.com:443?security=tls&type=xhttp&path=%2Fexample&mode=stream-up&encryption=none#Test";
    private static Task Sync(Action action) { action(); return Task.CompletedTask; }
    private sealed record Check(string Name, bool Passed, string? Error);
    public static async Task<int> RunAsync(string report)
    {
        var checks = new List<Check>();
        var root = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
        var executable = Path.Combine(AppContext.BaseDirectory, "tools", "xray.exe");
        Directory.CreateDirectory(root);
        async Task CheckAsync(string name, Func<Task> action)
        {
            try { await action(); checks.Add(new(name, true, null)); }
            catch (Exception ex) { checks.Add(new(name, false, ex.ToString())); }
        }


        await ServiceKeyChecksAsync(CheckAsync);
        await ImportChecksAsync(CheckAsync, executable, root);
        await ShareLinkChecksAsync(CheckAsync, root);
        await SystemProxyChecksAsync(CheckAsync, root);
        await EngineChecksAsync(CheckAsync, executable, root);
        await SingBoxChecksAsync(CheckAsync, executable, root);
        await LabChecksAsync(CheckAsync, root);
        await StorageChecksAsync(CheckAsync, root);
        await ArchitectureChecksAsync(CheckAsync);
        await NetworkSessionChecksAsync(CheckAsync, root);
        await HelperPipeChecksAsync(CheckAsync, root);
        await TunConfigurationChecksAsync(CheckAsync);
        await SystemSessionChecksAsync(CheckAsync);
        await VmKitChecksAsync(CheckAsync, root);
        await VmTunChecksAsync(CheckAsync, root);
        await HealthChecksAsync(CheckAsync);
        await SubscriptionChecksAsync(CheckAsync);
        await ClientBlockChecksAsync(CheckAsync, root);
        await HelperLifetimeChecksAsync(CheckAsync, root);
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { Passed = checks.All(c => c.Passed), Network = "Loopback only; no real profiles, system routes or external requests", Checks = checks }, new JsonSerializerOptions { WriteIndented = true }));
        return checks.All(c => c.Passed) ? 0 : 2;
    }
    public static async Task<int> JobChildAsync(string folder, CoreKind core = CoreKind.Xray)
    {
        using var engine = new Engine(new CoreAdapter(core, Path.Combine(AppContext.BaseDirectory, "tools", core == CoreKind.Xray ? "xray.exe" : "sing-box.exe")));
        await engine.StartAsync(core == CoreKind.Xray ? Direct() : new JsonObject { ["type"] = "direct" }, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(folder, "pid.txt"), engine.ProcessId!.Value.ToString());
        await Task.Delay(Timeout.Infinite); return 0;
    }
    private static JsonObject Direct() => new() { ["protocol"] = "freedom", ["settings"] = new JsonObject() };
    private static bool IsAlive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
    }
    private static void Assert(bool value) { if (!value) throw new InvalidOperationException("Assertion failed"); }
    private static void ExpectUserError(Action action)
    {
        try { action(); } catch (UserError) { return; }
        throw new InvalidOperationException("Expected profile rejection");
    }
    private static async Task ExpectUserErrorAsync(Func<Task> action)
    {
        try { await action(); } catch (UserError) { return; }
        throw new InvalidOperationException("Expected rejection");
    }
    private sealed class FixtureHandler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(response());
    }
}
