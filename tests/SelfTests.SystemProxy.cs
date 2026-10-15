using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Oblachka;

public static partial class SelfTests
{
    private sealed class FakeProxyStore(ProxySettings initial) : IProxySettingsStore
    {
        public ProxySettings Current { get; set; } = initial;
        public int Writes { get; private set; }
        public ProxySettings Read() => Current;
        public void Write(ProxySettings settings) { Current = settings; Writes++; }
    }

    private static async Task SystemProxyChecksAsync(Func<string, Func<Task>, Task> CheckAsync, string root)
    {
        var original = new ProxySettings(1, "corp-proxy.invalid:3128", "*.corp.invalid", "http://pac.invalid/proxy.pac");
        await CheckAsync("System proxy applies loopback port and restores exact original values", () => Sync(() =>
        {
            var store = new FakeProxyStore(original);
            var proxy = new SystemProxy(store, Path.Combine(root, "proxy-a", "backup.json"));
            proxy.Apply(23456);
            Assert(store.Current == new ProxySettings(1, "127.0.0.1:23456", SystemProxy.Bypass, null) && proxy.IsApplied);
            Assert(proxy.Restore() && store.Current == original && !proxy.IsApplied);
            Assert(!proxy.Restore());
        }));
        await CheckAsync("System proxy keeps the first original across reconnects and port changes", () => Sync(() =>
        {
            var store = new FakeProxyStore(new ProxySettings(null, null, null, null));
            var proxy = new SystemProxy(store, Path.Combine(root, "proxy-b", "backup.json"));
            proxy.Apply(23456); proxy.Apply(23457);
            Assert(store.Current.ProxyServer == "127.0.0.1:23457");
            Assert(proxy.Restore() && store.Current == new ProxySettings(null, null, null, null));
        }));
        await CheckAsync("Crash recovery: a new instance restores from the backup file", () => Sync(() =>
        {
            var store = new FakeProxyStore(original);
            var path = Path.Combine(root, "proxy-c", "backup.json");
            new SystemProxy(store, path).Apply(23458);
            Assert(new SystemProxy(store, path).Restore() && store.Current == original);
        }));
        await CheckAsync("Proxy replaced by another program is not overwritten on restore", () => Sync(() =>
        {
            var store = new FakeProxyStore(original);
            var proxy = new SystemProxy(store, Path.Combine(root, "proxy-d", "backup.json"));
            proxy.Apply(23459);
            var foreign = new ProxySettings(1, "127.0.0.1:2080", null, null);
            store.Current = foreign;
            Assert(!proxy.Restore() && store.Current == foreign && !proxy.IsApplied);
        }));
        await CheckAsync("Lost backup never records our own proxy as the original", () => Sync(() =>
        {
            var store = new FakeProxyStore(new ProxySettings(1, "127.0.0.1:23460", SystemProxy.Bypass, null));
            var proxy = new SystemProxy(store, Path.Combine(root, "proxy-e", "backup.json"));
            proxy.Apply(23461);
            Assert(proxy.Restore() && store.Current.ProxyEnable == 0 && store.Current.ProxyServer is null);
        }));
        await CheckAsync("Corrupt proxy backup is reported, not silently discarded", () => Sync(() =>
        {
            var path = Path.Combine(root, "proxy-f", "backup.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "{broken");
            ExpectUserError(() => new SystemProxy(new FakeProxyStore(original), path).Restore());
            Assert(File.Exists(path));
            ExpectUserError(() => new SystemProxy(new FakeProxyStore(original), Path.Combine(root, "proxy-g.json")).Apply(0));
        }));
        await CheckAsync("Local port of both cores accepts HTTP CONNECT for the system proxy", async () =>
        {
            var tools = Path.Combine(AppContext.BaseDirectory, "tools");
            using var xray = new Engine(new CoreAdapter(CoreKind.Xray, Path.Combine(tools, "xray.exe")));
            await xray.StartAsync(Direct(), CancellationToken.None);
            Assert(await HttpConnectAcceptedAsync(xray.Port));
            using var singBox = new Engine(new CoreAdapter(CoreKind.SingBox, Path.Combine(tools, "sing-box.exe")));
            await singBox.StartAsync(new JsonObject { ["type"] = "direct" }, CancellationToken.None);
            Assert(await HttpConnectAcceptedAsync(singBox.Port));
        });
    }

    // CONNECT to a closed loopback port: a proxy that speaks HTTP answers with an HTTP status line either way.
    private static async Task<bool> HttpConnectAcceptedAsync(int port)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes("CONNECT 127.0.0.1:9 HTTP/1.1\r\nHost: 127.0.0.1:9\r\n\r\n"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var buffer = new byte[12];
        var read = await stream.ReadAtLeastAsync(buffer, 8, false, timeout.Token);
        return read >= 8 && Encoding.ASCII.GetString(buffer, 0, read).StartsWith("HTTP/1.", StringComparison.Ordinal);
    }
}
