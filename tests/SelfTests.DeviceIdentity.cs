namespace Oblachka;

public static partial class SelfTests
{
    // Идентификатор установки для лимита устройств в панели. Главное требование —
    // он случайный и не выводится из железа, а уходит только на хосты сервиса.
    private static async Task DeviceIdentityChecksAsync(Func<string, Func<Task>, Task> check)
    {
        await check("Installation identifier is random, stable within a run and shaped for a header", () => Sync(() =>
        {
            var id = DeviceIdentity.Current;
            Assert(DeviceIdentity.IsValid(id) && id.Length == DeviceIdentity.Length);
            Assert(ReferenceEquals(id, DeviceIdentity.Current) || id == DeviceIdentity.Current);
            // Значение не должно совпадать ни с чем, что описывает это железо или учётную запись.
            var machineFacts = new[] { Environment.MachineName, Environment.UserName, Environment.OSVersion.VersionString };
            foreach (var fact in machineFacts)
                Assert(!id.Contains(fact, StringComparison.OrdinalIgnoreCase));
            Assert(!DeviceIdentity.IsValid(null) && !DeviceIdentity.IsValid("") && !DeviceIdentity.IsValid("слишком-короткий"));
            Assert(!DeviceIdentity.IsValid(new string('a', DeviceIdentity.Length - 1)));
            Assert(!DeviceIdentity.IsValid(new string('a', DeviceIdentity.Length - 1) + "="));
            // Версия системы огрублена до старшей: точная сборка панели не нужна.
            Assert(DeviceIdentity.OperatingSystem == "Windows");
            Assert(DeviceIdentity.OperatingSystemVersion.All(char.IsAsciiDigit) && DeviceIdentity.OperatingSystemVersion.Length <= 3);
            // Значения заголовков передаются однобайтовой кодировкой: кириллица в них
            // ломает сам запрос, а не только вид в панели.
            Assert(DeviceIdentity.DeviceModel.StartsWith(ServiceBrand.LatinName, StringComparison.Ordinal));
            foreach (var value in new[] { DeviceIdentity.Current, DeviceIdentity.OperatingSystem, DeviceIdentity.OperatingSystemVersion, DeviceIdentity.DeviceModel, ServiceBrand.UserAgent })
                Assert(DeviceIdentity.IsHeaderSafe(value));
            Assert(!DeviceIdentity.IsHeaderSafe("Облачка 0.9") && !DeviceIdentity.IsHeaderSafe("") && !DeviceIdentity.IsHeaderSafe("плохо\tзначение"));
        }));
        await check("Device headers go to the service host only and never to a third-party subscription", async () =>
        {
            var seen = new List<HttpRequestMessage>();
            using var handler = new RecordingHandler(seen, () => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new StringContent(link) });
            using var client = new HttpClient(handler);

            await Subscriptions.FetchWithClientAsync(client, "https://" + ServiceBrand.SubscriptionHosts[0] + ServiceBrand.SubscriptionPath + "abcdefghij", CancellationToken.None);
            var mine = seen[^1];
            Assert(mine.Headers.TryGetValues("x-hwid", out var hwid) && hwid.Single() == DeviceIdentity.Current);
            Assert(mine.Headers.Contains("x-device-os") && mine.Headers.Contains("x-ver-os") && mine.Headers.Contains("x-device-model"));

            await Subscriptions.FetchWithClientAsync(client, "https://sub.other-service.invalid/sub/abcdefghij", CancellationToken.None);
            var foreign = seen[^1];
            Assert(!foreign.Headers.Contains("x-hwid") && !foreign.Headers.Contains("x-device-os"));
            Assert(!foreign.Headers.Contains("x-ver-os") && !foreign.Headers.Contains("x-device-model"));
        });
        await check("Anonymised diagnostic report does not carry the installation identifier", () => Sync(() =>
        {
            var report = DiagnosticReport.Create(ConnectionState.Idle, 3, 1,
                new[] { new ClientEvent(DateTimeOffset.Now, ClientEventCode.Idle) },
                new[] { new RouteAttempt(0, "Verified", 12) });
            Assert(!report.Contains(DeviceIdentity.Current, StringComparison.Ordinal));
            Assert(!report.Contains(Environment.MachineName, StringComparison.OrdinalIgnoreCase));
        }));
    }

    private sealed class RecordingHandler(List<HttpRequestMessage> seen, Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { seen.Add(request); return Task.FromResult(response()); }
    }
}
