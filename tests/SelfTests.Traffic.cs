using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Oblachka;

public static partial class SelfTests
{
    // Скорость и трафик (0.19): поток /traffic ядра туннеля, по строке в секунду.
    private static async Task TrafficChecksAsync(Func<string, Func<Task>, Task> check)
    {
        await check("Traffic meter reads the core's /traffic stream with the session secret and sums it", async () =>
        {
            Assert(TrafficMeter.Parse("{\"up\":120,\"down\":2400000}") == (120, 2400000));
            Assert(TrafficMeter.Parse("{\"up\":-1,\"down\":5}") is null && TrafficMeter.Parse("oops") is null && TrafficMeter.Parse("{}") is null);
            Assert(TrafficMeter.Rate(2_400_000) == "2,4 МБ/с" && TrafficMeter.Rate(850_000) == "850 КБ/с" && TrafficMeter.Rate(0) == "0 КБ/с");
            Assert(TrafficMeter.Size(1_234_000_000) == "1,2 ГБ" && TrafficMeter.Size(12_500_000_000) == "13 ГБ" && TrafficMeter.Size(35_000_000) == "35 МБ");
            // Поддельный интерфейс ядра: проверяет секрет и отдаёт две строки, соединение держит открытым.
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var authorized = false;
            var serve = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync();
                using var stream = client.GetStream();
                var buffer = new byte[4096]; var read = await stream.ReadAsync(buffer);
                authorized = Encoding.ASCII.GetString(buffer, 0, read).Contains("Authorization: Bearer s3cret", StringComparison.Ordinal);
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nConnection: close\r\n\r\n{\"up\":100,\"down\":1000}\n{\"up\":50,\"down\":3000}\n"));
                await Task.Delay(1500);
            });
            using var meter = new TrafficMeter(new CoreStatsEntry(port, "s3cret"));
            for (var i = 0; i < 50 && meter.Total < 4150; i++) await Task.Delay(50);
            Assert(authorized && meter.Total == 4150 && meter.Fresh && meter.Down == 3000 && meter.Up == 50);
            await serve; listener.Stop();
        });
        await check("Exit address is read from the Cloudflare trace: address and country, junk refused", () => Sync(() =>
        {
            var exit = ExitAddress.Parse("fl=123\nh=cloudflare.com\nip=185.12.34.56\nts=1\nloc=DE\ncolo=FRA\n");
            Assert(exit is not null && exit.Address.ToString() == "185.12.34.56" && exit.Country.Code == "DE" && exit.Text == "185.12.34.56 (Германия)");
            Assert(ExitAddress.Parse("ip=2a01:4f8::1\nloc=XX\n") is { Country.Code: null } v6 && v6.Text == "2a01:4f8::1");
            Assert(ExitAddress.Parse("loc=DE\n") is null && ExitAddress.Parse("ip=not-an-ip\nloc=DE") is null && ExitAddress.Parse("<html>") is null);
        }));
    }
}
