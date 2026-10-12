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
    private static async Task ImportChecksAsync(Func<string, Func<Task>, Task> CheckAsync, string executable, string root)
    {
        await CheckAsync("VLESS XHTTP import preserves transport", () => Sync(() =>
        {
            var p = Profiles.Parse(link);
            Assert(p.Outbound["streamSettings"]?["xhttpSettings"]?["path"]?.GetValue<string>() == "/example");
            Assert(p.Outbound["streamSettings"]?["xhttpSettings"]?["mode"]?.GetValue<string>() == "stream-up");
        }));
        await CheckAsync("Unsupported link parameters fail instead of being lost", () => Sync(() => ExpectUserError(() => Profiles.Parse(link.Replace("#Test", "&unknown=value#Test")))));
        await CheckAsync("Ambiguous config rejected", () => Sync(() =>
        {
            var p = Profiles.Parse(link);
            var config = new JsonObject { ["outbounds"] = new JsonArray(p.Outbound.DeepClone(), p.Outbound.DeepClone()) };
            ExpectUserError(() => Profiles.Parse(config.ToJsonString()));
        }));
        await CheckAsync("TLS verification cannot be disabled", () => Sync(() =>
        {
            var p = Profiles.Parse(link);
            p.Outbound["streamSettings"]!["tlsSettings"]!["allowInsecure"] = true;
            ExpectUserError(() => Profiles.Parse(p.Outbound.ToJsonString()));
        }));
        await CheckAsync("Imported profiles cannot read external keys or write TLS secrets", () => Sync(() =>
        {
            foreach (var key in new[] { "certificateFile", "keyFile", "masterKeyLog", "dialerProxy", "MasterKeyLog", "CERTIFICATEFILE" })
            {
                var p = Profiles.Parse(link);
                p.Outbound["streamSettings"]!["tlsSettings"]![key] = "fixture-path-that-must-never-be-accessed";
                ExpectUserError(() => Profiles.Parse(p.Outbound.ToJsonString()));
            }
        }));
        await CheckAsync("Xray method alias has correct transport label", () => Sync(() =>
        {
            var p = Profiles.Parse(link);
            p.Outbound["streamSettings"]!["network"] = "tcp";
            p.Outbound["streamSettings"]!["method"] = "splithttp";
            Assert(Profiles.Parse(p.Outbound.ToJsonString()).Transport == "XHTTP / TLS");
        }));
        await CheckAsync("Runtime has loopback SOCKS and no DIRECT fallback", () => Sync(() =>
        {
            var config = Profiles.RuntimeConfig(Profiles.Parse(link).Outbound, 34567);
            Assert(config["outbounds"]!.AsArray().Count == 1);
            Assert(config["outbounds"]![0]!["protocol"]!.GetValue<string>() == "vless");
            Assert(config["inbounds"]![0]!["listen"]!.GetValue<string>() == "127.0.0.1");
            Assert(config["inbounds"]![0]!["settings"]!["udp"]!.GetValue<bool>() == false);
        }));
        await CheckAsync("Base64 subscription deduplicates connections and reports unsupported protocols", () => Sync(() =>
        {
            var source = Convert.ToBase64String(Encoding.UTF8.GetBytes(link + "\n" + link + "\nss://fixture@example.com:443"));
            var result = Subscriptions.Parse(source);
            Assert(result.Profiles.Count == 1 && result.Unsupported == 1 && result.Invalid == 0);
        }));
        await CheckAsync("Xray JSON subscriptions preserve multiple profiles and their display names", () => Sync(() =>
        {
            var first = new JsonObject { ["remarks"] = "Fixture A", ["outbounds"] = new JsonArray(Profiles.Parse(link).Outbound.DeepClone(), Direct()) };
            var second = new JsonObject { ["remarks"] = "Fixture B", ["outbounds"] = new JsonArray(Profiles.Parse(link.Replace("example.com", "example.net")).Outbound.DeepClone()) };
            var result = Subscriptions.Parse(new JsonArray(first, second).ToJsonString());
            Assert(result.Profiles.Count == 2 && result.Profiles[0].Name == "Fixture A" && result.Unsupported == 1);
        }));
        await CheckAsync("Nested XHTTP downstream cannot disable TLS verification", () => Sync(() =>
        {
            var outbound = Profiles.Parse(link).Outbound;
            outbound["streamSettings"]!["xhttpSettings"]!["extra"] = new JsonObject { ["downloadSettings"] = new JsonObject { ["tlsSettings"] = new JsonObject { ["allowInsecure"] = true } } };
            ExpectUserError(() => Profiles.Parse(outbound.ToJsonString()));
        }));
        await CheckAsync("Subscription loader rejects redirects and HTML, and bounds streamed response size", async () =>
        {
            using (var client = new HttpClient(new FixtureHandler(() => new HttpResponseMessage(HttpStatusCode.Found))))
                await ExpectUserErrorAsync(() => Subscriptions.FetchWithClientAsync(client, "https://fixture.invalid/sub", CancellationToken.None));
            using (var client = new HttpClient(new FixtureHandler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>login</html>") })))
                await ExpectUserErrorAsync(() => Subscriptions.FetchWithClientAsync(client, "https://fixture.invalid/sub", CancellationToken.None));
            using (var client = new HttpClient(new FixtureHandler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(new byte[Profiles.MaxBytes + 1])) })))
                await ExpectUserErrorAsync(() => Subscriptions.FetchWithClientAsync(client, "https://fixture.invalid/sub", CancellationToken.None));
            using (var client = new HttpClient(new FixtureHandler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(link) })))
                Assert((await Subscriptions.FetchWithClientAsync(client, "https://fixture.invalid/sub", CancellationToken.None)).Profiles.Count == 1);
        });
        await CheckAsync("Recovery budget stops restart storms and reopens after cooldown", () => Sync(() =>
        {
            var policy = new RecoveryPolicy(); var now = DateTimeOffset.UtcNow;
            Assert(policy.NextDelay(now)?.TotalSeconds is >= 1 and < 1.5);
            Assert(policy.NextDelay(now.AddSeconds(1))?.TotalSeconds is >= 2 and < 2.5);
            Assert(policy.NextDelay(now.AddSeconds(2))?.TotalSeconds is >= 4 and < 4.5);
            Assert(policy.NextDelay(now.AddSeconds(3)) is null);
            Assert(policy.NextDelay(now.AddMinutes(3))?.TotalSeconds is >= 1 and < 1.5);
        }));
        await CheckAsync("Canonical profile identity survives JSON property order and label changes", () => Sync(() =>
        {
            var original = Profiles.Parse(link);
            var reversed = new JsonObject(original.Outbound.Reverse().Select(p => new KeyValuePair<string, JsonNode?>(p.Key, p.Value?.DeepClone())));
            Assert(Profiles.Fingerprint(original) == Profiles.Fingerprint(original with { Name = "Changed", Outbound = reversed }));
        }));
        await CheckAsync("Subscription keeps one node offered under two names and collapses exact repeats", () => Sync(() =>
        {
            // Реальная подписка Remnawave отдаёт один и тот же узел и как «Авто», и как страну.
            // Склейка по конфигурации без имени молча теряла вторую запись.
            var alias = link.Replace("#Test", "#Авто");
            var both = Subscriptions.Parse(link + "\n" + alias);
            Assert(both.Profiles.Count == 2 && both.Duplicates == 0);
            Assert(Profiles.Fingerprint(both.Profiles[0]) == Profiles.Fingerprint(both.Profiles[1]));
            var repeated = Subscriptions.Parse(link + "\n" + link);
            Assert(repeated.Profiles.Count == 1 && repeated.Duplicates == 1);
        }));
        await CheckAsync("Mirror addresses require explicit HTTPS URLs and reject excess sources", () => Sync(() =>
        {
            ExpectUserError(() => SubscriptionMirrors.ParseSources("http://fixture.invalid/sub"));
            ExpectUserError(() => SubscriptionMirrors.ParseSources("https://user:password@fixture.invalid/sub"));
            ExpectUserError(() => SubscriptionMirrors.ParseSources(string.Join("\n", Enumerable.Range(0, 6).Select(i => $"https://fixture{i}.invalid/sub"))));
            Assert(SubscriptionMirrors.ParseSources("https://fixture.invalid/sub\nhttps://fixture.invalid/sub").Count == 1);
        }));
        await CheckAsync("Mirror fallback uses explicit second URL and prefers last successful source", async () =>
        {
            var sources = SubscriptionMirrors.ParseSources("https://fixture.invalid/token-one\nhttps://backup.invalid/token-two");
            var calls = new List<string>(); var imported = Subscriptions.Parse(link);
            Task<ImportResult> Fetch(string url, CancellationToken ct)
            { calls.Add(url); if (url == sources[0]) throw new UserError("Synthetic unavailable"); return Task.FromResult(imported); }
            var result = await SubscriptionMirrors.FetchAsync(sources, 0, Fetch, CancellationToken.None);
            Assert(result.SourceIndex == 1 && result.Attempts == 2 && calls.SequenceEqual(sources));
            calls.Clear(); result = await SubscriptionMirrors.FetchAsync(sources, 1, Fetch, CancellationToken.None);
            Assert(result.Attempts == 1 && calls.SequenceEqual(new[] { sources[1] }));
        });
        await CheckAsync("Mirror loading stops on access denial and cancellation", async () =>
        {
            var sources = SubscriptionMirrors.ParseSources("https://fixture.invalid/sub\nhttps://backup.invalid/sub");
            var calls = 0;
            await ExpectUserErrorAsync(() => SubscriptionMirrors.FetchAsync(sources, 0, (_, _) => { calls++; throw new SubscriptionAccessDenied(); }, CancellationToken.None));
            Assert(calls == 1);
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            try { await SubscriptionMirrors.FetchAsync(sources, 0, (_, _) => { calls++; return Task.FromResult(Subscriptions.Parse(link)); }, canceled.Token); throw new InvalidOperationException(); }
            catch (OperationCanceledException) { }
            Assert(calls == 1);
        });
        await CheckAsync("HTTP 403 from subscription is classified as access denial", async () =>
        {
            using var client = new HttpClient(new FixtureHandler(() => new HttpResponseMessage(HttpStatusCode.Forbidden)));
            try { await Subscriptions.FetchWithClientAsync(client, "https://fixture.invalid/sub", CancellationToken.None); throw new InvalidOperationException(); }
            catch (SubscriptionAccessDenied) { }
        });
        await CheckAsync("Route search advances after failed probe and leaves successful route active", async () =>
        {
            var profiles = new[] { Profiles.Parse(link), Profiles.Parse(link.Replace("example.com", "example.net")) };
            var active = false; var started = new List<Profile>();
            var search = new RouteSearch((p, _) => { active = true; started.Add(p); return Task.CompletedTask; },
                (_, _) => Task.FromResult(new ProbeResult(started.Count > 1, 12, "Synthetic", ProbeFailure.Reachability)), () => active = false);
            var result = await search.FindAsync(profiles, 0, new Uri("https://fixture.invalid/"), CancellationToken.None);
            Assert(active && result.ProfileIndex == 1 && result.Attempts.Count == 2 && started.SequenceEqual(profiles));
        });
        await CheckAsync("Route search treats check-site HTTP failure separately and stops", async () =>
        {
            var profile = Profiles.Parse(link); var starts = 0; var active = false;
            var search = new RouteSearch((_, _) => { starts++; active = true; return Task.CompletedTask; },
                (_, _) => Task.FromResult(new ProbeResult(false, 1, "Synthetic HTTP 503", ProbeFailure.TargetResponse)), () => active = false);
            await ExpectUserErrorAsync(() => search.FindAsync(new[] { profile, profile }, 0, new Uri("https://fixture.invalid/"), CancellationToken.None));
            Assert(starts == 1 && !active);
        });
        await CheckAsync("Route search bounds attempts, never bypasses proxy and ends stopped", async () =>
        {
            var p = Profiles.Parse(link); var starts = 0; var active = false;
            var search = new RouteSearch((_, _) => { starts++; active = true; return Task.CompletedTask; },
                (_, _) => Task.FromResult(new ProbeResult(false, 1, "Synthetic", ProbeFailure.Reachability)), () => active = false);
            var result = await search.FindAsync(Enumerable.Repeat(p, 20).ToArray(), 10, new Uri("https://fixture.invalid/"), CancellationToken.None);
            Assert(result.ProfileIndex is null && starts == RouteSearch.MaxAttempts && result.Attempts.Count == RouteSearch.MaxAttempts && result.Attempts[0].ProfileIndex == 10 && !active);
        });
        await CheckAsync("Cancel during route startup stops driver without trying another route", async () =>
        {
            using var canceled = new CancellationTokenSource(); var active = false; var starts = 0;
            var search = new RouteSearch((_, ct) => { starts++; active = true; canceled.Cancel(); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; },
                (_, _) => throw new InvalidOperationException(), () => active = false);
            try { await search.FindAsync(new[] { Profiles.Parse(link), Profiles.Parse(link) }, 0, new Uri("https://fixture.invalid/"), canceled.Token); throw new InvalidOperationException(); }
            catch (OperationCanceledException) { }
            Assert(starts == 1 && !active);
        });
    }
}
