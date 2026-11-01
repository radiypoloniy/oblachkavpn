using System.Text.Json;

namespace Oblachka;

public static partial class SelfTests
{
    private static async Task MemoryChecksAsync(Func<string, Func<Task>, Task> check)
    {
        await check("Connection samples share one request; caller cancellation and session replacement remain independent", async () =>
        {
            var entry = CoreStatsEntry.Create(23999);
            var source = new TaskCompletionSource<IReadOnlyList<CoreConnection>?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            var sampler = new CoreConnectionSampler((_, token) =>
            {
                Assert(!token.CanBeCanceled);
                Interlocked.Increment(ref calls);
                return calls == 1 ? source.Task : Task.FromResult<IReadOnlyList<CoreConnection>?>([]);
            });
            using var cancel = new CancellationTokenSource();
            var one = sampler.ReadAsync(entry, cancel.Token);
            var two = sampler.ReadAsync(entry, CancellationToken.None);
            Assert(calls == 1);
            cancel.Cancel();
            try { await one; Assert(false); } catch (OperationCanceledException) { }
            Assert(!two.IsCompleted);
            source.SetResult([]); Assert((await two) is { Count: 0 });
            await sampler.ReadAsync(CoreStatsEntry.Create(23999), CancellationToken.None);
            Assert(calls == 2);
            sampler.Reset();
            await sampler.ReadAsync(entry, CancellationToken.None); Assert(calls == 3);
        });
        await check("Failed connection samples are retried immediately", async () =>
        {
            var calls = 0;
            var sampler = new CoreConnectionSampler((_, _) => Task.FromResult<IReadOnlyList<CoreConnection>?>(++calls == 1 ? null : []));
            var entry = CoreStatsEntry.Create(23999);
            Assert(await sampler.ReadAsync(entry, CancellationToken.None) is null);
            Assert((await sampler.ReadAsync(entry, CancellationToken.None)) is { Count: 0 } && calls == 2);
        });
        await check("Compact connection parser preserves traffic, UDP, chains and both port formats", () => Sync(() =>
        {
            var rows = CoreConnections.Parse("""
                {"connections":[{"id":"one","upload":23,"download":9223372036854775807,"chains":["user-direct"],
                "metadata":{"processPath":"C:\\Apps\\app.exe","network":"UDP","host":"example.org","destinationIP":"::1","destinationPort":443}},
                {"id":"two","upload":-1,"download":"wrong","metadata":{"destinationPort":"853"}},null]}
                """);
            Assert(rows.Count == 2 && rows[0] is { Udp: true, Port: 443, Direct: true, Upload: 23, Download: long.MaxValue, Address: "::1", Host: "example.org" });
            Assert(rows[1] is { Port: 853, Upload: 0, Download: 0 });
            Assert(CoreConnections.Parse("{}").Count == 0 && CoreConnections.Parse("null").Count == 0);
            Assert(TrafficMeter.Parse("{\"up\":10,\"down\":20}") == (10, 20));
            Assert(TrafficMeter.Parse("{\"up\":-1,\"down\":20}") is null && TrafficMeter.Parse("[]") is null);
            try { CoreConnections.Parse("{"); Assert(false); } catch (JsonException) { }
        }));
        await check("Icon cache evicts least recently used entries and preserves null results", () => Sync(() =>
        {
            var calls = 0;
            var cache = new BoundedCache<string, object?>(2, StringComparer.OrdinalIgnoreCase);
            object? Create(string key) { calls++; return key == "missing" ? null : new object(); }
            var first = cache.GetOrAdd("first", Create);
            cache.GetOrAdd("missing", Create); cache.GetOrAdd("FIRST", Create); cache.GetOrAdd("last", Create);
            Assert(cache.Count == 2 && calls == 3 && ReferenceEquals(first, cache.GetOrAdd("first", Create)));
            cache.GetOrAdd("missing", Create); cache.GetOrAdd("missing", Create);
            Assert(cache.Count == 2 && calls == 4);
        }));
        await check("Subscription presentation invalidates at expiry and each day boundary", () => Sync(() =>
        {
            var now = DateTimeOffset.UtcNow;
            var group = new ProfileGroup("service", "Test", ProfileSource.Service, ["https://fixture.invalid/sub"])
                { Metadata = new(0, 0, 0, now.AddDays(14)) };
            Assert(SubscriptionState.RefreshKey(group, now) == 14 && SubscriptionState.RefreshKey(group, now.AddTicks(1)) == 13);
            Assert(SubscriptionState.RefreshKey(group, now.AddDays(14).AddTicks(-1)) == 0);
            Assert(SubscriptionState.RefreshKey(group, now.AddDays(14)) == -1);
            Assert(SubscriptionState.RefreshKey(group with { Metadata = null }, now) == long.MinValue);
        }));
    }
}
