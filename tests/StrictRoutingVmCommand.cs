using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;

namespace Oblachka;

internal static partial class StrictRoutingVmCommand
{
    private const string TraceUrl = "https://1.1.1.1/cdn-cgi/trace";
    private const string IpUrl = "https://api.ipify.org";
    private sealed record Input(string Subscription, SavedProfile[] Profiles);

    internal static async Task<int> RunAsync(string keyPath, string report, bool holdForReboot)
    {
        if (!VmTunTickets.IsVirtualMachine() || !NetworkHelperHost.IsElevated() || !NetworkHelperConnection.ServiceRunning)
        {
            File.WriteAllText(report, "Нужна Windows VM с запущенной службой и административным запуском.");
            return 2;
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var token = timeout.Token;
        var curl = Path.Combine(Environment.SystemDirectory, "curl.exe");
        var pinnedCurl = Path.Combine(Path.GetDirectoryName(report)!, "strict-probe.exe");
        var failed = 0;
        void Note(string line) => File.AppendAllText(report, $"{DateTimeOffset.UtcNow:O} {line}{Environment.NewLine}");
        void Check(string name, bool passed)
        {
            Note($"{(passed ? "PASS" : "FAIL")} {name}");
            if (!passed) failed++;
        }
        var serviceStopped = false;
        var phase = "subscription";
        var interrupted = false;
        IHelperLifetime? helper = null;
        SystemTunnelSession? session = null;
        Engine? engine = null;
        async Task CloseSession()
        {
            try
            {
                if (session is not null) await session.DisposeAsync();
            }
            catch (Exception e) when (interrupted && e is IOException or ObjectDisposedException or UserError) { }
            finally
            {
                try { if (helper is not null) await helper.DisposeAsync(); }
                catch (Exception e) when (interrupted && e is IOException or ObjectDisposedException or UserError) { }
                engine?.Dispose();
                session = null; helper = null; engine = null;
            }
        }
        try
        {
            var source = JsonSerializer.Deserialize<Input>(await File.ReadAllTextAsync(keyPath, token))
                ?? throw new UserError("Пустые данные стенда.");
            File.Delete(keyPath);
            IReadOnlyList<Profile> profiles;
            try
            {
                profiles = (await Subscriptions.FetchAsync(ServiceKey.ToSubscriptionUrl(source.Subscription), token)).Profiles;
                Note("Current subscription loaded inside guest.");
            }
            catch (UserError error)
            {
                Note("Guest refresh unavailable. Testing the current saved profiles. " + error.Message);
                profiles = ClientStateStore.RestoreProfiles(new ClientSnapshot(1, source.Profiles, null, [source.Subscription], 0, false, false));
            }
            var eligible = profiles.Where(p => !p.IsFree).ToArray();
            phase = "prepare profiles";
            var serbia = eligible.Where(p => ServerMachines.Title([p]).Contains("Серби", StringComparison.OrdinalIgnoreCase) ||
                p.DisplayName.Contains("Serbia", StringComparison.OrdinalIgnoreCase) || p.DisplayName.Contains("🇷🇸", StringComparison.Ordinal)).ToArray();
            if (serbia.Length == 0)
            {
                Note("Нет совместимого сербского выхода в актуальной подписке.");
                return 3;
            }
            var candidates = await TunnelEndpoints.PrepareSetAsync(serbia.Concat(eligible).Distinct().Take(10).ToArray(), token);
            var pin = candidates.FirstOrDefault(p => serbia.Contains(p.Source) && DirectOutbound.From(p.PinnedProfile) is not null);
            if (pin is null)
            {
                Note("No Serbia exit is supported by the TUN core.");
                return 3;
            }
            var main = candidates.FirstOrDefault(p => !p.Peers.Intersect(pin.Peers).Any() &&
                DirectOutbound.From(p.PinnedProfile) is not null);
            if (main is null)
            {
                Note("В подписке нужен второй совместимый сервер с отдельными адресами.");
                return 3;
            }
            File.Copy(curl, pinnedCurl, overwrite: true);
            var physical = NetworkInterface.GetAllNetworkInterfaces().Select(i => i.GetIPProperties())
                .First(p => p.GatewayAddresses.Any(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork));
            var iface = physical.GetIPv4Properties().Index;
            var local = physical.UnicastAddresses.First(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).Address.ToString();
            var gateway = physical.GatewayAddresses.First(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).Address;
            using var marker = new Ping();
            Note("CLOCK ICMP");
            await marker.SendPingAsync(gateway, 600, new byte[123]);
            var baseline = await Probe(curl, TraceUrl, token);
            Note($"BASELINE {baseline ?? "unreachable"} iface={iface} local={local}");
            if (baseline is null) return 4;
            Note($"PEERS main={string.Join(',', main.Peers)} pin={string.Join(',', pin.Peers)}");
            Check("explicit physical binding works before WFP", await Probe(pinnedCurl, TraceUrl, token, local) == baseline);
            var folder = Path.GetDirectoryName(report)!;
            var probeExe = Environment.ProcessPath!;
            var quicBaseline = await QuicTrace(probeExe, folder, token) ?? await QuicTrace(probeExe, folder, token);
            var pinnedQuic = Path.Combine(AppContext.BaseDirectory, "Oblachka.StrictProbe.exe");
            if (quicBaseline is not null) File.Copy(probeExe, pinnedQuic, overwrite: true);
            else Note("SKIP HTTP/3: guest baseline did not negotiate HTTP/3.");

            var path = ProgramRoutingPlan.Create(pinnedCurl, SiteAction.Tunnel, allInFolder: false);
            var files = quicBaseline is null ? [path] : new[] { path, ProgramRoutingPlan.Create(pinnedQuic, SiteAction.Tunnel, allInFolder: false) };
            var strict = new ServiceRoute("vm-serbia", ClientServerCatalog.Id(pin.Source)!, [], files);
            async Task<SystemTunnelSession> StartSession()
            {
                interrupted = false;
                engine = new Engine(Path.Combine(AppContext.BaseDirectory, "tools", "xray.exe"), "");
                helper = await NetworkHelperConnection.ConnectAsync(token);
                await helper.Client.RecoverAsync(token);
                session = new SystemTunnelSession(engine, helper.Client, ownsResources: false) { DirectAllowed = () => true };
                await session.StartWithSparesAsync([main, pin], token, programs: [path with { Action = SiteAction.Direct }], services: [strict]);
                return session;
            }
            phase = "acquire";
            var active = await StartSession();
            Check("WFP and pinned exit applied", active.BlocksBypass && active.PinnedServices.Single().ExitIndex == 1);
            var on = await Probe(curl, TraceUrl, token);
            var protectedOn = await Probe(pinnedCurl, TraceUrl, token);
            Note($"ON ordinary={on ?? "blocked"} pinned={protectedOn ?? "blocked"}");
            Check("VPN and Serbia have distinct exits", on is not null && protectedOn is not null && on != baseline && protectedOn != baseline && on != protectedOn);
            var pinnedQuicOn = quicBaseline is null ? null : await QuicTrace(pinnedQuic, folder, token);
            Note("PHASE physical-bypass");
            Check("physically bound protected program blocked despite direct rule", await Probe(pinnedCurl, TraceUrl, token, local) is null);

            phase = "pause";
            await PauseWithOpenFlows(active, curl, pinnedCurl,
                quicBaseline is null ? null : (probeExe, pinnedQuic, folder), Note, Check, token);
            Note("PHASE pause");
            var paused = await Probe(curl, TraceUrl, token);
            var pinnedPaused = await Probe(pinnedCurl, TraceUrl, token);
            Note($"PAUSE ordinary={paused ?? "blocked"} pinned={pinnedPaused ?? "blocked"}");
            Check("ordinary traffic direct, protected exit unchanged", paused == baseline && pinnedPaused is not null && pinnedPaused == protectedOn);
            if (quicBaseline is not null)
            {
                var ordinaryQuic = await QuicTrace(probeExe, folder, token);
                var protectedQuic = await QuicTrace(pinnedQuic, folder, token);
                Check("HTTP/3 keeps the pinned exit and lets ordinary traffic out directly during pause",
                    ordinaryQuic == quicBaseline && protectedQuic is not null && protectedQuic == pinnedQuicOn && protectedQuic != quicBaseline);
            }
            var name = await Probe(curl, IpUrl, token);
            Check("ordinary HTTPS with DNS while paused", name is not null);
            Check("protected HTTPS with DNS while paused", await Probe(pinnedCurl, IpUrl, token) is not null);

            await active.SetMainPausedAsync(true, lockdown: true);
            Check("lockdown blocks ordinary and retains Serbia", await Probe(curl, TraceUrl, token) is null &&
                await Probe(pinnedCurl, TraceUrl, token) == protectedOn && protectedOn is not null);
            await active.SetMainPausedAsync(false, lockdown: false);
            Check("main VPN resumes", on is not null && await Probe(curl, TraceUrl, token) == on);
            await active.SetMainPausedAsync(true, lockdown: false);

            using (var wfp = WfpEngine.Open())
            {
                using var fault = BlockPeerPackets(wfp, pin.Peers);
                Note("PHASE pin-loss");
                var pinLost = await Probe(pinnedCurl, TraceUrl, token);
                var ordinaryDuringFault = await Probe(curl, TraceUrl, token);
                Note($"PIN LOSS ordinary={ordinaryDuringFault ?? "blocked"} pinned={pinLost ?? "blocked"}");
                Check("loss of Serbia blocks only the protected program", pinLost is null && ordinaryDuringFault == baseline);
            }
            Note("PHASE pin-recovered");
            Check("Serbia returns on removing fault", protectedOn is not null && await Probe(pinnedCurl, TraceUrl, token) == protectedOn);

            phase = "tun-crash";
            Note("PHASE tun-crash");
            interrupted = true;
            using (var core = Process.GetProcessesByName("oblachka-core").Single(p =>
                string.Equals(p.MainModule?.FileName, TunCore.Program(), StringComparison.OrdinalIgnoreCase)))
            {
                core.Kill();
                await core.WaitForExitAsync(token);
            }
            Check("protected physical egress blocked after the TUN process was killed", await Probe(pinnedCurl, TraceUrl, token, local) is null);
            for (var i = 0; i < 24 && active.State == SystemTunnelState.Running; i++) await Task.Delay(500, token);
            Check("the service heartbeat detected the lost TUN", active.State != SystemTunnelState.Running);
            await CloseSession();
            Note("PHASE reconnect");
            active = await StartSession();
            await active.SetMainPausedAsync(true, lockdown: false);
            Check("protected exit available after reacquiring the tunnel", await Probe(pinnedCurl, TraceUrl, token) == protectedOn && protectedOn is not null);

            phase = "service-crash";
            Note("PHASE service-crash");
            interrupted = true;
            using (var service = Process.GetProcessById(NetworkServiceControl.Query().ProcessId))
            {
                service.Kill();
                serviceStopped = true;
                await service.WaitForExitAsync(token);
            }
            using (var wfp = WfpEngine.Open())
                Check("strict WFP survives guest service stop", wfp.SublayerExists(WfpLockdown.StrictSublayerKey));
            Check("no protected physical egress after service stop", await Probe(pinnedCurl, TraceUrl, token, local) is null &&
                await Probe(pinnedCurl, TraceUrl, token) is null);
            await Task.Delay(TimeSpan.FromSeconds(2), token);
            await Command("sc.exe", ["start", NetworkService.Name], token);
            for (var i = 0; i < 30 && !NetworkHelperConnection.ServiceRunning; i++) await Task.Delay(500, token);
            serviceStopped = !NetworkHelperConnection.ServiceRunning;
            bool survivesRestart;
            using (var wfp = WfpEngine.Open())
                survivesRestart = !serviceStopped && wfp.SublayerExists(WfpLockdown.StrictSublayerKey);
            Check("strict WFP survives service restart", survivesRestart);
            await CloseSession();
            if (holdForReboot && survivesRestart)
            {
                Note("Strict WFP armed for the reboot check.");
                return failed == 0 ? 0 : 5;
            }
            await using (var recovery = await NetworkHelperConnection.ConnectAsync(token))
            {
                await recovery.Client.RecoverAsync(token);
                Note("PHASE clear");
                await recovery.Client.SetStrictGuardAsync(arm: false, token);
            }
            Note("PHASE recovered-probe");
            Check("explicit recovery restores ordinary direct egress", await Probe(curl, TraceUrl, token) == baseline);
            return failed == 0 ? 0 : 5;
        }
        catch (Exception error)
        {
            Note($"ERROR {phase} {error.GetType().Name}");
            if (error is UserError && phase is "acquire" or "pause" or "tun-crash" or "service-crash") Note(error.Message);
            return 6;
        }
        finally
        {
            await CloseSession();
            if (File.Exists(keyPath)) File.Delete(keyPath);
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            if (serviceStopped) await Command("sc.exe", ["start", NetworkService.Name], cleanup.Token);
        }
    }

}
