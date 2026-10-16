using System.Net;
using System.Net.Sockets;

namespace Oblachka;

public static partial class SelfTests
{
    private static async Task SiteCheckChecksAsync(Func<string, Func<Task>, Task> check)
    {
        await check("Site check normalizes addresses and rejects what is not a domain", () => Sync(() =>
        {
            Assert(SiteProbe.NormalizeHost("https://ChatGPT.com/c/123?x=1") == "chatgpt.com");
            Assert(SiteProbe.NormalizeHost("  www.youtube.com:443 ") == "www.youtube.com");
            Assert(SiteProbe.NormalizeHost("госуслуги.рф") == "xn--c1aapkosapc.xn--p1ai");
            Assert(SiteProbe.NormalizeHost("") is null && SiteProbe.NormalizeHost("1.2.3.4") is null && SiteProbe.NormalizeHost("localhost") is null);
        }));
        await check("Site check treats private and zero DNS answers as provider stubs", () => Sync(() =>
        {
            Assert(SiteProbe.Bogus(IPAddress.Parse("0.0.0.0")) && SiteProbe.Bogus(IPAddress.Parse("127.0.0.1")) && SiteProbe.Bogus(IPAddress.Parse("10.10.34.35")));
            Assert(SiteProbe.Bogus(IPAddress.Parse("192.168.1.1")) && SiteProbe.Bogus(IPAddress.Parse("172.20.0.1")) && SiteProbe.Bogus(IPAddress.Parse("::1")));
            Assert(!SiteProbe.Bogus(IPAddress.Parse("104.18.32.47")) && !SiteProbe.Bogus(IPAddress.Parse("172.15.0.1")) && !SiteProbe.Bogus(IPAddress.Parse("2606:4700::6810:2047")));
        }));
        await check("Site check reads block pages, stalls, 403 and server errors from the response", () => Sync(() =>
        {
            const string ok = "HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\nhello";
            Assert(SiteProbe.Classify(ok, true, 10).Stage == SiteStage.Ok);
            Assert(SiteProbe.Classify(ok, false, 10).Stage == SiteStage.Ok);
            Assert(SiteProbe.Classify("HTTP/1.1 200 OK\r\nContent-Length: 90000\r\n\r\n<html>", false, 10).Stage == SiteStage.Stall);
            Assert(SiteProbe.Classify("HTTP/1.1 200 OK\r\nContent-Length: 90000\r\n\r\n" + new string('a', 20000), false, 10).Stage == SiteStage.Ok);
            Assert(SiteProbe.Classify("HTTP/1.1 2", false, 10).Stage == SiteStage.Stall && SiteProbe.Classify("", false, 10).Stage == SiteStage.Stall);
            Assert(SiteProbe.Classify("HTTP/1.1 451 Unavailable\r\n\r\n", true, 10).Stage == SiteStage.BlockPage);
            Assert(SiteProbe.Classify("HTTP/1.1 302 Found\r\nLocation: http://warning.rt.ru/?id=1\r\n\r\n", true, 10).Stage == SiteStage.BlockPage);
            Assert(SiteProbe.Classify("HTTP/1.1 200 OK\r\n\r\n<h1>Доступ к информационному ресурсу ограничен</h1>", true, 10).Stage == SiteStage.BlockPage);
            Assert(SiteProbe.Classify("HTTP/1.1 403 Forbidden\r\n\r\n", true, 10) is { Stage: SiteStage.Forbidden, Status: 403 });
            Assert(SiteProbe.Classify("HTTP/1.1 502 Bad Gateway\r\n\r\n", true, 10) is { Stage: SiteStage.ServerError, Status: 502 });
            Assert(SiteProbe.Classify("HTTP/1.1 301 Moved\r\nLocation: https://www.example.com/\r\n\r\n", true, 10).Stage == SiteStage.Ok);
        }));
        await check("Site verdict suggests the rule that fixes the site", () => Sync(() =>
        {
            SitePath Ok() => new(SiteStage.Ok, 100, 200);
            SitePath Dpi() => new(SiteStage.Handshake, 100);
            Assert(SiteVerdict.Build(Dpi(), Ok(), SiteRoute.Direct) is { Suggest: SiteAction.Tunnel, Tone: SiteTone.Warning });
            Assert(SiteVerdict.Build(Dpi(), Ok(), SiteRoute.Zapret).Suggest == SiteAction.Tunnel);
            Assert(SiteVerdict.Build(Dpi(), Ok(), SiteRoute.Tunnel) is { Suggest: null, Tone: SiteTone.Ok });
            Assert(SiteVerdict.Build(Ok(), new(SiteStage.Forbidden, 100, 403), SiteRoute.Tunnel) is { Suggest: SiteAction.Direct } verdict && verdict.Text.Contains("из-за границы"));
            Assert(SiteVerdict.Build(Ok(), new(SiteStage.Forbidden, 100, 403), SiteRoute.Direct).Suggest is null);
            Assert(SiteVerdict.Build(Ok(), Ok(), SiteRoute.Tunnel).Suggest == SiteAction.Direct && SiteVerdict.Build(Ok(), Ok(), SiteRoute.Direct).Suggest is null);
            Assert(SiteVerdict.Build(new(SiteStage.NoName, 1), new(SiteStage.NoName, 1), SiteRoute.Tunnel).Title == "Такого сайта нет");
            Assert(SiteVerdict.Build(new(SiteStage.ServerError, 1, 503), new(SiteStage.ServerError, 1, 503), SiteRoute.Tunnel).Text.Contains("503"));
            Assert(SiteVerdict.Build(Dpi(), new(SiteStage.Address, 1), SiteRoute.Tunnel) is { Tone: SiteTone.Error, Suggest: null });
            Assert(SiteVerdict.Build(SitePath.Skip("нет входа"), Ok(), SiteRoute.Tunnel) is { Suggest: null } skipped && skipped.Text.Contains("нет входа"));
            Assert(SiteVerdict.Build(Ok(), SitePath.Skip("не выбран сервер"), SiteRoute.Off).Title == "Напрямую сайт открывается");
            Assert(SiteVerdict.Build(Ok(), Ok(), SiteRoute.Block).Title.Contains("правилом"));
        }));
        await check("Site check over SOCKS with a password: dropped handshakes mean the address is closed", async () =>
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var sawAuth = 0;
            var server = Task.Run(async () =>
            {
                for (var round = 0; round < 2; round++)
                {
                    using var client = await listener.AcceptTcpClientAsync();
                    var stream = client.GetStream();
                    var hello = new byte[3]; await stream.ReadExactlyAsync(hello);
                    await stream.WriteAsync(new byte[] { 5, 2 });
                    var head = new byte[2]; await stream.ReadExactlyAsync(head);
                    var user = new byte[head[1] + 1]; await stream.ReadExactlyAsync(user);
                    var pass = new byte[user[^1]]; await stream.ReadExactlyAsync(pass);
                    if (System.Text.Encoding.ASCII.GetString(pass) == "fixturepassword1") Interlocked.Increment(ref sawAuth);
                    await stream.WriteAsync(new byte[] { 1, 0 });
                    var connect = new byte[5]; await stream.ReadExactlyAsync(connect);
                    var rest = new byte[connect[4] + 2]; await stream.ReadExactlyAsync(rest);
                    await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 80 });
                    // Ни ответа на рукопожатие, ни данных: соединение закрывается, как у фильтра.
                }
            });
            var result = await SiteProbe.CheckAsync("localhost", new SocksRoute(port, "fixtureuser00001", "fixturepassword1"), CancellationToken.None);
            await server;
            Assert(result.Stage == SiteStage.Address && sawAuth == 2);
        });
    }
}
