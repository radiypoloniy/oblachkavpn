namespace Oblachka;

public static partial class SelfTests
{
    // Идентификатор установки для лимита устройств в панели. Главное требование —
    // он случайный и не выводится из железа; сторонней подписке уходит свой, по её хосту.
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
            // Remnawave принимает x-hwid только по ^[a-zA-Z0-9=-]{10,64}$; ранний «_» делал его невидимым.
            var remnawave = new System.Text.RegularExpressions.Regex("^[a-zA-Z0-9=-]{10,64}$");
            Assert(remnawave.IsMatch(id) && !DeviceIdentity.IsValid(new string('a', DeviceIdentity.Length - 1) + "_"));
            var other = DeviceIdentity.ForHost("Sub.Other-Service.invalid");
            Assert(remnawave.IsMatch(other) && other != id && other == DeviceIdentity.ForHost("sub.other-service.invalid") && other != DeviceIdentity.ForHost("panel.third.invalid"));
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
        await check("Service gets the installation id, a third-party subscription only its own per-host id", async () =>
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
            Assert(foreign.Headers.TryGetValues("x-hwid", out var theirs) && theirs.Single() == DeviceIdentity.ForHost("sub.other-service.invalid"));
            Assert(theirs.Single() != DeviceIdentity.Current && foreign.Headers.Contains("x-device-os"));
        });
        await check("Remnawave device-limit refusal keeps the saved list and explains the limit", async () =>
        {
            // Отказ панели: пустое тело или серверы-заглушки с текстом, флаги в заголовках.
            HttpResponseMessage Refusal(string flag)
            {
                var r = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(link) };
                r.Headers.Add("x-hwid-active", "true"); r.Headers.Add(flag, "true"); r.Headers.Add("x-hwid-limit", "true");
                return r;
            }
            foreach (var (flag, words) in new[] { ("x-hwid-max-devices-reached", "устройств"), ("x-hwid-not-supported", "идентификатор") })
            {
                using var client = new HttpClient(new RecordingHandler([], () => Refusal(flag)));
                try { await Subscriptions.FetchWithClientAsync(client, "https://" + ServiceBrand.SubscriptionHosts[0] + ServiceBrand.SubscriptionPath + "abcdefghij", CancellationToken.None); Assert(false); }
                catch (UserError ex) { Assert(ex.Message.Contains(words) && ex.Message.Contains("остаются")); }
            }
            // Привязанное устройство: лимит включён (x-hwid-active), но отказа нет — список загружается.
            using var ok = new HttpClient(new RecordingHandler([], () =>
            { var r = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(link) }; r.Headers.Add("x-hwid-active", "true"); return r; }));
            var imported = await Subscriptions.FetchWithClientAsync(ok, "https://" + ServiceBrand.SubscriptionHosts[0] + ServiceBrand.SubscriptionPath + "abcdefghij", CancellationToken.None);
            Assert(imported.Profiles.Count == 1);
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
