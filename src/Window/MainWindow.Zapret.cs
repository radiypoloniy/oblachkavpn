﻿using System.Net;
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
    // Домены групп, которые идут напрямую (ZapretPlan).
    private string[] zapretDomains = Array.Empty<string>();
    private string zapretTag = "";
    // Выбранный вариант работает через winws, а не «обход не нужен».
    private bool zapretBypassing;
    private string zapretNote = "";
    private CancellationTokenSource? zapretCancel;
    // Группы, которые человек сам оставил в VPN («YouTube — через VPN»): их подбор не проверяет.
    private readonly HashSet<string> zapretVpnGroups = new(StringComparer.Ordinal);
    // Подбор в этом запуске уже прошёл (с любым итогом). Нет — карточка не пишет «не помог».
    private bool zapretTried;

    // Сайты zapret идут напрямую, только когда обход работает или в этой сети не нужен.
    // Под «Весь компьютер» — выходом ядра туннеля на физическую сеть; пакеты winws защита
    // пропускает по разрешениям службы (WfpReinjection).
    private bool ZapretRouting => zapretEnabled && !zapretBusy && zapretDomains.Length > 0 && (!zapretBypassing || zapretStatus?.Running == true);

    private RoutingProfile ZapretProfile => new("zapret", zapretDomains.Select(d => "domain:" + d).ToArray(), [], [], [], [], []) { GlobalProxy = true };

    // Группы, которые подбор проверяет: все, кроме оставленных в VPN.
    private string[] ZapretCandidates => ZapretPlan.Groups.Select(g => g.Key).Where(k => !zapretVpnGroups.Contains(k)).ToArray();

    private void WireZapret()
    {
        Bypass.ZapretToggle.Click += async (_, _) =>
        {
            if (Bypass.ZapretToggle.IsChecked == true) await EnableZapretAsync(); else await DisableZapretAsync();
        };
        Bypass.ZapretPick.Click += async (_, _) =>
        {
            if (zapretBusy) { zapretCancel?.Cancel(); return; }
            await PickZapretAsync(force: true);
        };
        Bypass.YouTubeBypass.Click += async (_, _) => await SetZapretGroupAsync("youtube", Bypass.YouTubeBypass.IsChecked == true);
        Bypass.DiscordBypass.Click += async (_, _) => await SetZapretGroupAsync("discord", Bypass.DiscordBypass.IsChecked == true);
        RefreshZapret();
    }

    // «Через обход» или «через VPN» для одной группы. В VPN — сразу, без подбора; обратно — подбор
    // заново: для этой группы стратегию ещё не искали.
    private async Task SetZapretGroupAsync(string key, bool bypass)
    {
        if (bypass) zapretVpnGroups.Remove(key); else zapretVpnGroups.Add(key);
        QueueStateSave();
        if (!zapretEnabled) { RefreshZapret(); return; }
        if (bypass) { await PickZapretAsync(force: true); return; }
        zapretCancel?.Cancel();
        var groups = zapretActiveGroups.Where(g => g != key).ToArray();
        WatchZapret(zapretActiveStrategy, groups, zapretActiveNetwork);
        zapretDomains = ZapretSites.Read(zapretTag, ZapretPlan.Groups.Where(g => groups.Contains(g.Key)).Select(g => g.List));
        if (groups.Length == 0 && zapretBypassing) await ZapretCallAsync(new(ZapretAction.Stop));
        Note(ZapretPlan.Describe(zapretActiveStrategy, groups, null, zapretVpnGroups));
        await ApplyZapretRoutingAsync();
    }

    private async Task EnableZapretAsync()
    {
        if (!NetworkHelperConnection.ServiceRunning)
        {
            Bypass.ZapretToggle.IsChecked = false; Home.BypassToggle.IsChecked = false;
            zapretNote = "Нужна установленная Облачка: обход запускает её сетевая служба.";
            RefreshZapret(); return;
        }
        if (!AppDialog.Confirm(this, "Обход блокировок без VPN",
            "Облачка скачает zapret (набор стратегий Flowseal с GitHub), проверит файлы и подберёт стратегию для вашей сети — это до двух минут. " +
            "YouTube, Discord и другие сайты из его списков пойдут напрямую, не тратя VPN. Антивирус может ругаться на драйвер WinDivert: это известное ложное срабатывание. " +
            "Обход работает, пока запущена Облачка.", "Включить"))
        { Bypass.ZapretToggle.IsChecked = false; Home.BypassToggle.IsChecked = false; return; }
        zapretEnabled = true; zapretTried = false; QueueStateSave();
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
    // Сайты на время подбора идут как шли (через VPN): проверка ходит своим путём (ZapretProbeRoute),
    // браузер не попадает на сломанный прямой путь и не держит потом прямые соединения.
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
            var candidates = ZapretCandidates;
            if (candidates.Length == 0)
            {
                await ZapretCallAsync(new(ZapretAction.Stop));
                zapretDomains = Array.Empty<string>();
                Note(ZapretPlan.Describe(ZapretPlan.NoBypass, [], null, zapretVpnGroups));
                return;
            }
            // Пока winws перебирает стратегии, прямой путь сломан: сайты zapret — в VPN (их открытые
            // соединения закрываются), вернутся напрямую с итогом подбора.
            await ApplyZapretRoutingAsync();
            Note("Готовим набор zapret…");
            var status = await ZapretCallAsync(new(ZapretAction.Prepare));
            if (status is not { Ready: true }) { Note(status?.Error ?? "Не удалось подготовить zapret."); return; }
            zapretTag = status.Tag!;
            var network = NetworkIdentity.Current() ?? "unknown";
            // Под «Весь компьютер» проверке нужен вход прямого трафика ядра туннеля. Его нет, если обход
            // включили уже после подключения: тогда переподключаемся с ним.
            if (SystemTunnelActive && ZapretProbeRoute is null)
            {
                Note("Переподключаемся, чтобы проверить обход…");
                var reconnected = await ReconnectAsync();
                if (!reconnected || ZapretProbeRoute is null) throw new UserError("Под «Весь компьютер» не удалось открыть прямой путь для проверки.");
            }
            var exclude = await VpnAddressesAsync(token);
            zapretJournal.Add(ZapretJournalKind.Pick, ZapretPlan.NoBypass, candidates, network: network, note: force ? "подбор заново" : "подбор, сначала запомненный выбор");
            if (!force && zapretChoices.GetValueOrDefault(network) is { } saved)
            {
                var (strategy, groups) = ZapretPlan.Decode(saved);
                var wanted = groups.Intersect(candidates).ToArray();
                Note("Проверяем запомненный выбор…");
                if (await ProbeVariantAsync(strategy, exclude, token) is { } again && Passing(again) is { Length: > 0 } now && now.Length >= wanted.Length)
                { Done(strategy, now, again, network); return; }
            }
            Note("Проверяем сайты без обхода…");
            var baseline = await ProbeVariantAsync(ZapretPlan.NoBypass, exclude, token);
            (string Strategy, string[] Groups, ZapretProbeResult? Probe) best = (ZapretPlan.NoBypass, baseline is null ? [] : Passing(baseline), baseline);
            var names = status.Strategies;
            for (var i = 0; i < names.Length && best.Groups.Length < candidates.Length; i++)
            {
                Note($"Подбираем стратегию: {i + 1} из {names.Length}. Сайты пока идут через VPN. Можно остановить.");
                if (await ProbeVariantAsync(names[i], exclude, token) is not { } probe) continue;
                var passing = Passing(probe);
                if (ZapretPlan.Better(passing, best.Groups) || best.Probe is null) best = (names[i], passing, probe);
            }
            if (best.Groups.Length == 0)
            {
                await ZapretCallAsync(new(ZapretAction.Stop));
                zapretDomains = Array.Empty<string>();
                Note("Рабочую стратегию не нашли — эти сайты идут через VPN. " + ZapretPlan.Describe(best.Strategy, [], best.Probe, zapretVpnGroups));
                zapretJournal.Add(ZapretJournalKind.Chosen, ZapretPlan.NoBypass, [], best.Probe, network, "рабочей стратегии нет");
                return;
            }
            // Подбор мог уйти дальше лучшего варианта: возвращаемся к нему.
            if (best.Strategy == ZapretPlan.NoBypass) await ZapretCallAsync(new(ZapretAction.Stop));
            else if (zapretStatus?.Strategy != best.Strategy && await ZapretCallAsync(new(ZapretAction.Start, best.Strategy, exclude)) is not { Running: true })
                throw new UserError("Служба не запустила zapret.");
            Done(best.Strategy, best.Groups, best.Probe, network);
        }
        catch (OperationCanceledException) { await ZapretCallAsync(new(ZapretAction.Stop)); zapretDomains = Array.Empty<string>(); Note("Подбор остановлен — эти сайты идут через VPN."); zapretJournal.Add(ZapretJournalKind.Stop, ZapretPlan.NoBypass, [], note: "подбор остановлен"); }
        // Служба не запустила winws (антивирус, драйвер, ответ службы) — дело не в стратегии,
        // перебирать остальные бессмысленно: раньше это давало «ничего не работает» за секунду.
        catch (UserError ex) { await ZapretCallAsync(new(ZapretAction.Stop)); zapretDomains = Array.Empty<string>(); Note(ex.Message + " Эти сайты идут через VPN."); zapretJournal.Add(ZapretJournalKind.Stop, ZapretPlan.NoBypass, [], note: "ошибка службы или подготовки"); }
        finally
        {
            zapretBusy = false; zapretTried = true;
            zapretCancel.Dispose(); zapretCancel = null;
            if (!closed) await ApplyZapretRoutingAsync();
        }

        string[] Passing(ZapretProbeResult probe) => ZapretPlan.Passing(probe).Intersect(ZapretCandidates).ToArray();

        void Done(string strategy, string[] groups, ZapretProbeResult? probe, string network)
        {
            zapretChoices[network] = ZapretPlan.Encode(strategy, groups); QueueStateSave();
            zapretBypassing = strategy != ZapretPlan.NoBypass;
            WatchZapret(strategy, groups, network);
            zapretDomains = ZapretSites.Read(zapretTag, ZapretPlan.Groups.Where(g => groups.Contains(g.Key)).Select(g => g.List));
            Note(ZapretPlan.Describe(strategy, groups, probe, zapretVpnGroups));
            zapretJournal.Add(ZapretJournalKind.Chosen, strategy, groups, probe, network, ZapretPathNote);
        }
    }

    private void Note(string text) { zapretNote = text; if (!closed) RefreshZapret(); }

    private void RefreshZapret()
    {
        Bypass.ZapretToggle.IsChecked = zapretEnabled;
        RefreshSettingsSummary(); RefreshDirectChip();
        Bypass.ZapretText.Text = zapretNote.Length > 0 ? zapretNote : "Через обход блокировок zapret: быстрее и не тратит VPN";
        Bypass.ZapretPick.Visibility = zapretEnabled ? Visibility.Visible : Visibility.Collapsed;
        Bypass.ZapretPick.Content = zapretBusy ? "Остановить подбор" : "Подобрать заново";
        Bypass.ZapretGroups.Visibility = zapretEnabled ? Visibility.Visible : Visibility.Collapsed;
        Bypass.YouTubeBypass.IsChecked = !zapretVpnGroups.Contains("youtube");
        Bypass.DiscordBypass.IsChecked = !zapretVpnGroups.Contains("discord");
        Bypass.YouTubeState.Text = ZapretGroupState("youtube");
        Bypass.DiscordState.Text = ZapretGroupState("discord");
    }

    private string ZapretGroupState(string key) =>
        zapretVpnGroups.Contains(key) ? "Через VPN — так выбрано"
        : zapretBusy ? "Проверяем…"
        : zapretActiveGroups.Contains(key) ? (zapretActiveStrategy == ZapretPlan.NoBypass ? "Напрямую, обход не нужен" : "Через обход")
        : zapretTried ? "Через VPN: обход в этой сети не открыл" : "Ещё не проверяли";

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
