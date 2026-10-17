using System.Net;
using System.Windows;

namespace Oblachka;

// «Правила → YouTube и Discord без VPN»: zapret (набор Flowseal) в службе. Сайты из его списков
// идут мимо VPN, а фильтр обходит winws. Стратегия подбирается в этой сети и запоминается для неё.
// Нет рабочей стратегии — эти сайты остаются в VPN: обход не должен ломать то, что работало.
public partial class MainWindow
{
    private bool zapretEnabled, zapretBusy;
    private ZapretStatus? zapretStatus;
    // Сеть (NetworkIdentity) → выбор «стратегия|группы» (ZapretPlan.Encode); стратегия «-» — без обхода.
    private readonly Dictionary<string, string> zapretChoices = new(StringComparer.Ordinal);
    // Домены групп, которые идут напрямую (ZapretPlan); на время подбора — все домены набора.
    private string[] zapretDomains = Array.Empty<string>();
    private string zapretTag = "";
    // Выбранный вариант работает через winws, а не «обход не нужен».
    private bool zapretBypassing;
    private string zapretNote = "";
    private CancellationTokenSource? zapretCancel;

    // Сайты zapret идут напрямую, только когда обход работает или в этой сети не нужен.
    // Под «Весь компьютер» — через вход прямого трафика ядра туннеля; пакеты winws защита
    // пропускает по разрешениям службы (WfpReinjection).
    private bool ZapretRouting => zapretEnabled && !zapretBusy && zapretDomains.Length > 0 && (!zapretBypassing || zapretStatus?.Running == true);

    private RoutingProfile ZapretProfile => new("zapret",
        (forceZapretRouting ? ZapretSites.Read(zapretTag) : zapretDomains).Select(d => "domain:" + d).ToArray(), [], [], [], [], []) { GlobalProxy = true };

    private void WireZapret()
    {
        Routes.ZapretToggle.Click += async (_, _) =>
        {
            if (Routes.ZapretToggle.IsChecked == true) await EnableZapretAsync(); else await DisableZapretAsync();
        };
        Routes.ZapretPick.Click += async (_, _) =>
        {
            if (zapretBusy) { zapretCancel?.Cancel(); return; }
            await PickZapretAsync(force: true);
        };
        RefreshZapret();
    }

    private async Task EnableZapretAsync()
    {
        if (!NetworkHelperConnection.ServiceRunning)
        {
            Routes.ZapretToggle.IsChecked = false; Home.BypassToggle.IsChecked = false;
            zapretNote = "Нужна установленная Облачка: обход запускает её сетевая служба.";
            RefreshZapret(); return;
        }
        if (!AppDialog.Confirm(this, "Обход блокировок без VPN",
            "Облачка скачает zapret (набор стратегий Flowseal с GitHub), проверит файлы и подберёт стратегию для вашей сети — это до двух минут. " +
            "YouTube, Discord и другие сайты из его списков пойдут напрямую, не тратя VPN. Антивирус может ругаться на драйвер WinDivert: это известное ложное срабатывание. " +
            "Обход работает, пока запущена Облачка.", "Включить"))
        { Routes.ZapretToggle.IsChecked = false; Home.BypassToggle.IsChecked = false; return; }
        zapretEnabled = true; QueueStateSave();
        await PickZapretAsync(force: false);
    }

    private async Task DisableZapretAsync()
    {
        zapretCancel?.Cancel();
        zapretEnabled = false; QueueStateSave();
        await ZapretCallAsync(new(ZapretAction.Stop));
        zapretNote = "";
        await ApplyZapretRoutingAsync();
    }

    // Подбор: сначала без обхода (вдруг не нужен), затем стратегии по очереди. YouTube и Discord
    // решаются отдельно (ZapretPlan): напрямую идёт группа, все адреса которой открылись;
    // остальные остаются в VPN. Побеждает вариант, открывший больше групп.
    // force: false — сначала пробуем запомненный для этой сети.
    private async Task PickZapretAsync(bool force)
    {
        if (zapretBusy || !zapretEnabled) return;
        zapretBusy = true; zapretCancel = new CancellationTokenSource();
        // Присмотр — только за итогом этого подбора; если он ничего не найдёт, присматривать не за чем.
        WatchZapret(ZapretPlan.NoBypass, [], "");
        var token = zapretCancel.Token;
        try
        {
            Note("Готовим набор zapret…");
            var status = await ZapretCallAsync(new(ZapretAction.Prepare));
            if (status is not { Ready: true }) { Note(status?.Error ?? "Не удалось подготовить zapret."); return; }
            zapretTag = status.Tag!;
            var network = NetworkIdentity.Current() ?? "unknown";
            // Под «Весь компьютер» проверяем через ядро: все сайты zapret на время подбора идут у него напрямую.
            if (SystemTunnelActive)
            {
                forceZapretRouting = true; await ApplyZapretRoutingAsync();
                // Проверять можно, только когда ядро действительно пустило все сайты zapret напрямую.
                for (var i = 0; i < 40 && coreZapretKey != "*"; i++) await Task.Delay(250, token);
                if (coreZapretKey != "*") throw new UserError("Не удалось перезапустить ядро с маршрутами для проверки.");
            }
            var exclude = await VpnAddressesAsync(token);
            var all = ZapretPlan.Groups.Length;
            if (!force && zapretChoices.GetValueOrDefault(network) is { } saved)
            {
                var (strategy, groups) = ZapretPlan.Decode(saved);
                Note("Проверяем запомненный выбор…");
                if (await ProbeVariantAsync(strategy, exclude, token) is { } again && ZapretPlan.Passing(again) is { Length: > 0 } now && now.Length >= groups.Length)
                { Done(strategy, now, again, network); return; }
            }
            Note("Проверяем сайты без обхода…");
            var baseline = await ProbeVariantAsync(ZapretPlan.NoBypass, exclude, token);
            (string Strategy, string[] Groups, ZapretProbeResult? Probe) best = (ZapretPlan.NoBypass, baseline is null ? [] : ZapretPlan.Passing(baseline), baseline);
            var names = status.Strategies;
            for (var i = 0; i < names.Length && best.Groups.Length < all; i++)
            {
                Note($"Подбираем стратегию: {i + 1} из {names.Length}. Можно остановить.");
                if (await ProbeVariantAsync(names[i], exclude, token) is not { } probe) continue;
                var passing = ZapretPlan.Passing(probe);
                if (ZapretPlan.Better(passing, best.Groups) || best.Probe is null) best = (names[i], passing, probe);
            }
            if (best.Groups.Length == 0)
            {
                await ZapretCallAsync(new(ZapretAction.Stop));
                zapretDomains = Array.Empty<string>();
                Note("Рабочую стратегию не нашли — эти сайты идут через VPN. " + ZapretPlan.Describe(best.Strategy, [], best.Probe));
                return;
            }
            // Подбор мог уйти дальше лучшего варианта: возвращаемся к нему.
            if (best.Strategy == ZapretPlan.NoBypass) await ZapretCallAsync(new(ZapretAction.Stop));
            else if (zapretStatus?.Strategy != best.Strategy && await ZapretCallAsync(new(ZapretAction.Start, best.Strategy, exclude)) is not { Running: true })
                throw new UserError("Служба не запустила zapret.");
            Done(best.Strategy, best.Groups, best.Probe, network);
        }
        catch (OperationCanceledException) { await ZapretCallAsync(new(ZapretAction.Stop)); zapretDomains = Array.Empty<string>(); Note("Подбор остановлен — эти сайты идут через VPN."); }
        // Служба не запустила winws (антивирус, драйвер, ответ службы) — дело не в стратегии,
        // перебирать остальные бессмысленно: раньше это давало «ничего не работает» за секунду.
        catch (UserError ex) { await ZapretCallAsync(new(ZapretAction.Stop)); zapretDomains = Array.Empty<string>(); Note(ex.Message + " Эти сайты идут через VPN."); }
        finally
        {
            zapretBusy = false; forceZapretRouting = false;
            zapretCancel.Dispose(); zapretCancel = null;
            if (!closed) await ApplyZapretRoutingAsync();
        }

        void Done(string strategy, string[] groups, ZapretProbeResult? probe, string network)
        {
            zapretChoices[network] = ZapretPlan.Encode(strategy, groups); QueueStateSave();
            zapretBypassing = strategy != ZapretPlan.NoBypass;
            WatchZapret(strategy, groups, network);
            zapretDomains = ZapretSites.Read(zapretTag, ZapretPlan.Groups.Where(g => groups.Contains(g.Key)).Select(g => g.List));
            Note(ZapretPlan.Describe(strategy, groups, probe));
        }
    }

    // Один вариант: без обхода (winws остановлен) или стратегия. null — служба не ответила.
    private async Task<ZapretProbeResult?> ProbeVariantAsync(string strategy, string[] exclude, CancellationToken token)
    {
        if (strategy == ZapretPlan.NoBypass) { await ZapretCallAsync(new(ZapretAction.Stop)); return await ZapretProbe.RunAsync(ProbePort, token); }
        return await StartAndProbeAsync(strategy, exclude, token);
    }

    private async Task<ZapretProbeResult?> StartAndProbeAsync(string name, string[] exclude, CancellationToken token)
    {
        var started = await ZapretCallAsync(new(ZapretAction.Start, name, exclude));
        if (started is not { Running: true }) throw new UserError(started?.Error ?? "Служба не запустила zapret.");
        await Task.Delay(800, token);
        return await ZapretProbe.RunAsync(ProbePort, token);
    }

    private bool forceZapretRouting;
    private int? ProbePort => SystemTunnelActive && engine.Port > 0 ? engine.Port : null;

    private void Note(string text) { zapretNote = text; if (!closed) RefreshZapret(); }

    private void RefreshZapret()
    {
        Routes.ZapretToggle.IsChecked = zapretEnabled;
        RefreshSettingsSummary(); RefreshDirectChip();
        Routes.ZapretText.Text = zapretNote.Length > 0 ? zapretNote : "Через обход блокировок zapret: быстрее и не тратит VPN";
        Routes.ZapretPick.Visibility = zapretEnabled ? Visibility.Visible : Visibility.Collapsed;
        Routes.ZapretPick.Content = zapretBusy ? "Остановить подбор" : "Подобрать заново";
    }

    // Адреса узлов VPN: zapret их не трогает, даже если они попадут в его списки адресов.
    private async Task<string[]> VpnAddressesAsync(CancellationToken token)
    {
        var hosts = importedProfiles.Select(p => { try { return ProfileServer.Of(p).Host; } catch (Exception ex) when (ex is NullReferenceException or InvalidOperationException) { return ""; } })
            .Where(h => h.Length > 0).Distinct().Take(32).ToArray();
        var result = new HashSet<string>();
        foreach (var host in hosts)
        {
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
