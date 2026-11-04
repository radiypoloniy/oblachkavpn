using System.Net;

namespace Oblachka;

internal sealed partial class ClientZapret
{
    private static string NetworkKey => NetworkIdentity.Current() ?? "unknown";

    // Подбор: сначала без обхода (вдруг не нужен), затем стратегии по очереди. YouTube и Discord
    // решаются отдельно (ZapretPlan): напрямую идёт группа, все адреса которой открылись;
    // остальные остаются в VPN. Побеждает вариант, открывший больше групп.
    // Сайты на время подбора идут как шли (через VPN): проверка ходит своим путём (ProbeRoute),
    // браузер не попадает на сломанный прямой путь и не держит потом прямые соединения.
    // force: false — сначала пробуем запомненный для этой сети.
    private async Task PickCoreAsync(bool force, CancellationToken token)
    {
        if (!Enabled) return;
        if (ConnectionBusy)
        {
            if (force) repickAt = DateTimeOffset.UtcNow; else modeChanged = true;
            return;
        }
        SystemTunnelSession? checkedSession = null;
        Busy = true; Publish();
        try
        {
            // Пока перебираются стратегии, ни один старый маршрут обхода не должен вести напрямую.
            await ReconcileAsync(token);
            if (RoutingPending || stopWhenSafe)
            {
                repickAt = DateTimeOffset.UtcNow.AddSeconds(10);
                SetNote("Ждём, пока подключение применит маршруты.");
                return;
            }
            domains = []; Watch(ZapretPlan.NoBypass, [], "");
            var candidates = Candidates;
            if (candidates.Length == 0)
            {
                stopWhenSafe = true;
                SetNote(ZapretPlan.Describe(ZapretPlan.NoBypass, [], null, KeptInVpn));
                return;
            }
            SetNote("Готовим набор zapret…");
            var status = await CallAsync(new(ZapretAction.Prepare));
            if (status is not { Ready: true }) { SetNote(status?.Error ?? "Не удалось подготовить zapret."); return; }
            token.ThrowIfCancellationRequested();
            tag = status.Tag ?? "";
            var network = NetworkKey;
            // Под «Весь компьютер» проверке нужен вход прямого трафика ядра туннеля. Его нет, если обход
            // включили уже после подключения: тогда переподключаемся с ним.
            if (TunnelActive && ProbeRoute is null)
            {
                SetNote("Переподключаемся, чтобы проверить обход…");
                if (owner.Profiles.Selected is not { } target) throw new UserError("Сначала выберите сервер VPN.");
                await owner.Connections.RestartAsync(target, rebuildTunnel: true);
                token.ThrowIfCancellationRequested();
                if (ProbeRoute is null) throw new UserError("Под «Весь компьютер» не удалось открыть прямой путь для проверки.");
            }
            checkedSession = owner.Tunnel;
            var exclude = await VpnAddressesAsync(token);
            Journal.Add(ZapretJournalKind.Pick, ZapretPlan.NoBypass, candidates, network: network, note: force ? "подбор заново" : "подбор, сначала запомненный выбор");
            if (!force && Choices.GetValueOrDefault(network) is { } saved)
            {
                var (strategy, groups) = ZapretPlan.Decode(saved);
                var wanted = groups.Intersect(candidates).ToArray();
                SetNote("Проверяем запомненный выбор…");
                if (await ProbeVariantAsync(strategy, exclude, token) is { } again && Passing(again) is { Length: > 0 } now && now.Length >= wanted.Length)
                { Done(strategy, now, again, network); return; }
            }
            SetNote("Проверяем сайты без обхода…");
            var baseline = await ProbeVariantAsync(ZapretPlan.NoBypass, exclude, token);
            (string Strategy, string[] Groups, ZapretProbeResult? Probe) best = (ZapretPlan.NoBypass, baseline is null ? [] : Passing(baseline), baseline);
            var names = status.Strategies;
            // Первая стратегия, открывшая каждую группу: если ни одна не открыла всё, их можно сшить.
            var first = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < names.Length && best.Groups.Length < candidates.Length; i++)
            {
                SetNote($"Подбираем стратегию: {i + 1} из {names.Length}. Сайты пока идут через VPN. Можно остановить.");
                if (await ProbeVariantAsync(names[i], exclude, token) is not { } probe) continue;
                var passing = Passing(probe);
                foreach (var group in passing) first.TryAdd(group, names[i]);
                if (ZapretPlan.Better(passing, best.Groups) || best.Probe is null) best = (names[i], passing, probe);
            }
            if (best.Groups.Length < candidates.Length && ZapretPlan.Stitch(first) is { } stitched)
            {
                SetNote("Сшиваем стратегии: " + ZapretPlan.StitchTitle(stitched) + "…");
                try
                {
                    if (await ProbeVariantAsync(stitched, exclude, token) is { } probe && Passing(probe) is var passing && ZapretPlan.Better(passing, best.Groups))
                        best = (stitched, passing, probe);
                }
                // Устройство блоков у этих стратегий разное — служба их не склеит; остаётся лучшая одиночная.
                catch (UserError) { Journal.Add(ZapretJournalKind.Variant, stitched, [], network: network, note: "не склеить"); }
            }
            if (best.Groups.Length == 0)
            {
                stopWhenSafe = true;
                SetNote("Рабочую стратегию не нашли — эти сайты идут через VPN. " + ZapretPlan.Describe(best.Strategy, [], best.Probe, KeptInVpn));
                Journal.Add(ZapretJournalKind.Chosen, ZapretPlan.NoBypass, [], best.Probe, network, "рабочей стратегии нет");
                return;
            }
            // Подбор мог уйти дальше лучшего варианта: возвращаемся к нему.
            token.ThrowIfCancellationRequested();
            if (best.Strategy == ZapretPlan.NoBypass) await StopForProbeAsync();
            else if (this.status is not { Running: true } || this.status.Strategy != best.Strategy)
            {
                if (await CallAsync(new(ZapretAction.Start, best.Strategy, exclude)) is not { Running: true })
                    throw new UserError("Служба не запустила zapret.");
            }
            Done(best.Strategy, best.Groups, best.Probe, network);
        }
        catch (OperationCanceledException)
        {
            if (!disposed) Abandon("Подбор остановлен.", "подбор остановлен");
        }
        catch (Exception error) when (error is UserError or IOException or TimeoutException or ObjectDisposedException)
        {
            if (!disposed) Abandon(error is UserError ? error.Message : "Служба не ответила на команду zapret.", "ошибка службы или подготовки");
        }
        finally
        {
            Busy = false; Tried = true;
            if (!disposed) { Publish(); await ReconcileAsync(CancellationToken.None); }
        }

        void Abandon(string message, string reason)
        {
            domains = []; Watch(ZapretPlan.NoBypass, [], ""); stopWhenSafe = true;
            SetNote(message);
            Journal.Add(ZapretJournalKind.Stop, ZapretPlan.NoBypass, [], note: reason);
        }

        string[] Passing(ZapretProbeResult probe) => ZapretPlan.Passing(probe).Intersect(Candidates).ToArray();

        void Done(string strategy, string[] groups, ZapretProbeResult? probe, string network)
        {
            token.ThrowIfCancellationRequested();
            if (network != NetworkKey || !ReferenceEquals(checkedSession, owner.Tunnel))
            {
                repickAt = DateTimeOffset.UtcNow.AddSeconds(3);
                throw new UserError("Сеть или подключение изменились во время подбора.");
            }
            Remember(network, strategy, groups);
            bypassing = strategy != ZapretPlan.NoBypass;
            Watch(strategy, groups, network);
            domains = ReadDomains(groups);
            SetNote(ZapretPlan.Describe(strategy, groups, probe, KeptInVpn));
            Journal.Add(ZapretJournalKind.Chosen, strategy, groups, probe, network, PathNote);
        }
    }

    // Один вариант: без обхода (winws остановлен) или стратегия. null — служба не ответила.
    private async Task<ZapretProbeResult?> ProbeVariantAsync(string strategy, string[] exclude, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (strategy == ZapretPlan.NoBypass) await StopForProbeAsync();
        else
        {
            var started = await CallAsync(new(ZapretAction.Start, strategy, exclude));
            if (started is not { Running: true }) throw new UserError(started?.Error ?? "Служба не запустила zapret.");
            await Task.Delay(800, token);
        }
        token.ThrowIfCancellationRequested();
        var route = ProbeRoute;
        if (TunnelActive && route is null) throw new UserError("Прямой путь для проверки больше недоступен.");
        var probe = await ZapretProbe.RunAsync(route, token);
        Journal.Add(ZapretJournalKind.Variant, strategy, ZapretPlan.Passing(probe), probe, NetworkKey, PathNote);
        return probe;
    }

    private async Task StopForProbeAsync()
    {
        if (await CallAsync(new(ZapretAction.Stop)) is not { Running: false, Error: null })
            throw new UserError("Служба не остановила zapret.");
    }

    // Каким путём шла проверка: под «Весь компьютер» — через вход ядра туннеля, только по TCP
    // (QUIC из приложения ушёл бы в туннель), без VPN — из приложения, с запасной попыткой по QUIC.
    private string PathNote => TunnelActive ? "проверка через ядро туннеля, только TCP" : "проверка из приложения, TCP и QUIC";

    // Путь проверки — тот, которым пойдут сайты zapret: без VPN — прямо из приложения, под «Весь компьютер» —
    // вход прямого трафика ядра туннеля (выход на физическую сеть через winws, DNS сети). null под
    // «Весь компьютер» — входа нет, проверять нечем.
    private SocksRoute? ProbeRoute => !TunnelActive ? null
        : (owner.Engine as Engine)?.DirectEntry is { } entry ? new SocksRoute(entry.Port, entry.User, entry.Password) : null;

    // Адреса узлов VPN: zapret их не трогает, даже если они попадут в его списки адресов.
    private async Task<string[]> VpnAddressesAsync(CancellationToken token)
    {
        var hosts = owner.Profiles.Imported.Select(p => { try { return ProfileServer.Of(p).Host; } catch (Exception ex) when (ex is NullReferenceException or InvalidOperationException) { return ""; } })
            .Where(h => h.Length > 0).Distinct().Take(32).ToArray();
        var result = new HashSet<string>();
        foreach (var host in hosts)
        {
            token.ThrowIfCancellationRequested();
            if (IPAddress.TryParse(host, out var ip)) { result.Add(ip.ToString()); continue; }
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(2));
                foreach (var address in await Dns.GetHostAddressesAsync(host, timeout.Token)) result.Add(address.ToString());
            }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException or OperationCanceledException) { }
            if (result.Count >= 32) break;
        }
        return result.Take(32).ToArray();
    }
}
