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
    // Сеть (NetworkIdentity) → стратегия; NoneNeeded — в этой сети сайты открываются и без обхода.
    private readonly Dictionary<string, string> zapretChoices = new(StringComparer.Ordinal);
    private string[] zapretDomains = Array.Empty<string>();
    private string zapretNote = "";
    private CancellationTokenSource? zapretCancel;
    private const string NoneNeeded = "-";

    // Сайты zapret идут напрямую, только когда обход работает или в этой сети не нужен.
    private bool ZapretRouting => zapretEnabled && zapretDomains.Length > 0 &&
        (zapretStatus?.Running == true || NetworkIdentity.Current() is { } net && zapretChoices.GetValueOrDefault(net) == NoneNeeded);

    private RoutingProfile ZapretProfile => new("zapret", zapretDomains.Select(d => "domain:" + d).ToArray(), [], [], [], [], []) { GlobalProxy = true };

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
            Routes.ZapretToggle.IsChecked = false;
            zapretNote = "Нужна установленная Облачка: обход запускает её сетевая служба.";
            RefreshZapret(); return;
        }
        if (!AppDialog.Confirm(this, "Обход блокировок без VPN",
            "Облачка скачает zapret (набор стратегий Flowseal с GitHub), проверит файлы и подберёт стратегию для вашей сети — это до двух минут. " +
            "YouTube, Discord и другие сайты из его списков пойдут напрямую, не тратя VPN. Антивирус может ругаться на драйвер WinDivert: это известное ложное срабатывание. " +
            "Обход работает, пока запущена Облачка.", "Включить"))
        { Routes.ZapretToggle.IsChecked = false; return; }
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

    // Подбор: сначала без обхода (вдруг не нужен), затем стратегии по очереди до первой рабочей.
    // force: false — сначала пробуем запомненную для этой сети.
    private async Task PickZapretAsync(bool force)
    {
        if (zapretBusy || !zapretEnabled) return;
        zapretBusy = true; zapretCancel = new CancellationTokenSource();
        var token = zapretCancel.Token;
        try
        {
            Note("Готовим набор zapret…");
            var status = await ZapretCallAsync(new(ZapretAction.Prepare));
            if (status is not { Ready: true }) { Note(status?.Error ?? "Не удалось подготовить zapret."); return; }
            zapretDomains = ZapretSites.Read(status.Tag!);
            var network = NetworkIdentity.Current() ?? "unknown";
            // Под «Весь компьютер» проверяем через ядро: сайты zapret должны уже идти у него напрямую.
            if (SystemTunnelActive) { forceZapretRouting = true; await ApplyZapretRoutingAsync(); }
            var exclude = await VpnAddressesAsync(token);
            if (!force && zapretChoices.GetValueOrDefault(network) is { } saved && await TryStrategyAsync(saved, exclude, token)) { Done(saved, network); return; }
            Note("Проверяем сайты без обхода…");
            await ZapretCallAsync(new(ZapretAction.Stop));
            var baseline = await ZapretProbe.RunAsync(ProbePort, token);
            if (baseline.All) { Done(NoneNeeded, network); return; }
            var names = status.Strategies;
            (string Name, int Passed)? best = null;
            for (var i = 0; i < names.Length; i++)
            {
                Note($"Подбираем стратегию: {i + 1} из {names.Length}. Можно остановить.");
                var probe = await StartAndProbeAsync(names[i], exclude, token);
                if (probe is null) continue;
                if (probe.All) { Done(names[i], network); return; }
                // Частичный успех засчитывается, только если стратегия открывает больше, чем без обхода:
                // иначе сайты пошли бы мимо VPN и сломались (например, заблокированные по DNS).
                if (probe.Passed > Math.Max(best?.Passed ?? 0, baseline.Passed)) best = (names[i], probe.Passed);
            }
            if (best is { } partial && await TryStrategyAsync(partial.Name, exclude, token, acceptPartial: true)) { Done(partial.Name, network, partial: true); return; }
            await ZapretCallAsync(new(ZapretAction.Stop));
            Note("Рабочую стратегию в этой сети не нашли — эти сайты идут через VPN.");
        }
        catch (OperationCanceledException) { await ZapretCallAsync(new(ZapretAction.Stop)); Note("Подбор остановлен — эти сайты идут через VPN."); }
        finally
        {
            zapretBusy = false; forceZapretRouting = false;
            zapretCancel.Dispose(); zapretCancel = null;
            if (!closed) await ApplyZapretRoutingAsync();
        }

        void Done(string choice, string network, bool partial = false)
        {
            zapretChoices[network] = choice; QueueStateSave();
            Note(choice == NoneNeeded ? "В этой сети эти сайты открываются и без обхода — идут напрямую."
                : (partial ? "Частично работает стратегия «" : "Работает стратегия «") + choice + "». Сайты из списка идут напрямую.");
        }
    }

    private async Task<bool> TryStrategyAsync(string name, string[] exclude, CancellationToken token, bool acceptPartial = false)
    {
        if (name == NoneNeeded) { await ZapretCallAsync(new(ZapretAction.Stop)); return (await ZapretProbe.RunAsync(ProbePort, token)).All; }
        var probe = await StartAndProbeAsync(name, exclude, token);
        return probe is { All: true } || acceptPartial && probe is { Passed: > 0 };
    }

    private async Task<ZapretProbeResult?> StartAndProbeAsync(string name, string[] exclude, CancellationToken token)
    {
        var started = await ZapretCallAsync(new(ZapretAction.Start, name, exclude));
        if (started is not { Running: true }) { if (started?.Error is { } error) Note(error); return null; }
        await Task.Delay(800, token);
        return await ZapretProbe.RunAsync(ProbePort, token);
    }

    private bool forceZapretRouting;
    private int? ProbePort => SystemTunnelActive && engine.Port > 0 ? engine.Port : null;

    private void Note(string text) { zapretNote = text; if (!closed) RefreshZapret(); }

    private void RefreshZapret()
    {
        Routes.ZapretToggle.IsChecked = zapretEnabled;
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
