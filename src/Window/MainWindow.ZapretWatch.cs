namespace Oblachka;

// Присмотр за группами zapret, которые идут напрямую. Подбор — разовая проверка, а фильтр
// провайдера меняется: вечером стратегия может перестать работать, и человек, который не
// заглядывает в настройки, решит, что сломан VPN. Поэтому прямые группы перепроверяются
// раз в 5 минут тем же путём, что у программ. Две неудачи подряд — группа возвращается в
// VPN сразу, а через минуту запускается новый подбор. Смена сети — тоже повод перепроверить.
public partial class MainWindow
{
    private string zapretActiveStrategy = ZapretPlan.NoBypass, zapretActiveNetwork = "";
    private string[] zapretActiveGroups = [];
    private readonly Dictionary<string, int> zapretMisses = new(StringComparer.Ordinal);
    private DateTimeOffset zapretNextWatch = DateTimeOffset.MaxValue, zapretRepickAt = DateTimeOffset.MaxValue;
    private bool zapretWatching;
    private readonly ZapretJournal zapretJournal = new(ZapretJournal.DefaultPath);
    // Группы, которые присмотр вернул в VPN, пока новый подбор их не вернул: жёлтая плашка на главном.
    private string[] zapretDemoted = [];
    private static readonly TimeSpan ZapretWatchEvery = TimeSpan.FromMinutes(5), ZapretRecheck = TimeSpan.FromSeconds(30);

    private void WatchZapret(string strategy, string[] groups, string network)
    {
        zapretActiveStrategy = strategy; zapretActiveGroups = groups; zapretActiveNetwork = network;
        if (network.Length > 0) zapretDemoted = zapretDemoted.Except(groups).ToArray();
        zapretMisses.Clear();
        zapretNextWatch = groups.Length > 0 ? DateTimeOffset.UtcNow + ZapretWatchEvery : DateTimeOffset.MaxValue;
    }

    private void PollZapretWatch()
    {
        if (closed || !zapretEnabled || zapretBusy || zapretWatching) return;
        var now = DateTimeOffset.UtcNow;
        if (now >= zapretRepickAt) { zapretRepickAt = DateTimeOffset.MaxValue; _ = PickZapretAsync(force: true); return; }
        // Подбор при запуске пропустился (служба ещё не работала) — начинаем, как только она готова.
        // Иначе карточка так и висела бы в «не помог», хотя ничего не проверяли.
        if (!zapretTried && !zapretAfterConnect && operation is null && !systemStopping && NetworkHelperConnection.ServiceRunning)
        { _ = PickZapretAsync(force: false); return; }
        if (zapretActiveGroups.Length == 0 || now < zapretNextWatch || operation is not null || systemStopping || !ZapretProbeReady) return;
        // Другая сеть: запомненный для неё выбор (или новый подбор) вместо проверки старого.
        if ((NetworkIdentity.Current() ?? "unknown") != zapretActiveNetwork) { zapretNextWatch = DateTimeOffset.MaxValue; _ = PickZapretAsync(force: false); return; }
        zapretWatching = true;
        _ = Check();

        async Task Check()
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var probe = await ZapretProbe.RunAsync(ZapretProbeRoute, timeout.Token, quicViaTunnel: SystemTunnelActive);
                if (closed || zapretBusy) return;
                var passing = ZapretPlan.Passing(probe);
                zapretJournal.Add(ZapretJournalKind.Watch, zapretActiveStrategy, passing, probe, zapretActiveNetwork, SystemTunnelActive ? "присмотр через ядро туннеля, TCP и QUIC" : ZapretPathNote);
                var lost = new List<string>();
                foreach (var group in zapretActiveGroups)
                {
                    if (passing.Contains(group)) { zapretMisses.Remove(group); continue; }
                    zapretMisses[group] = zapretMisses.GetValueOrDefault(group) + 1;
                    if (zapretMisses[group] >= 2) lost.Add(group);
                }
                zapretNextWatch = DateTimeOffset.UtcNow + (zapretMisses.Count > 0 && lost.Count == 0 ? ZapretRecheck : ZapretWatchEvery);
                if (lost.Count > 0) await DemoteZapretAsync(lost, probe);
            }
            catch (OperationCanceledException) { zapretNextWatch = DateTimeOffset.UtcNow + ZapretRecheck; }
            finally { zapretWatching = false; }
        }
    }

    // Группы, переставшие открываться, — сразу в VPN; остальные остаются как были.
    private async Task DemoteZapretAsync(IReadOnlyCollection<string> lost, ZapretProbeResult probe)
    {
        var groups = zapretActiveGroups.Except(lost).ToArray();
        zapretDemoted = zapretDemoted.Union(lost).ToArray();
        foreach (var group in lost) zapretMisses.Remove(group);
        zapretChoices[zapretActiveNetwork] = ZapretPlan.Encode(zapretActiveStrategy, groups); QueueStateSave();
        zapretActiveGroups = groups;
        zapretDomains = ZapretSites.Read(zapretTag, ZapretPlan.Groups.Where(g => groups.Contains(g.Key)).Select(g => g.List));
        if (groups.Length == 0 && zapretBypassing) await ZapretCallAsync(new(ZapretAction.Stop));
        zapretJournal.Add(ZapretJournalKind.Demote, zapretActiveStrategy, groups, network: zapretActiveNetwork, note: "в VPN: " + string.Join(",", lost));
        var titles = ZapretPlan.Groups.Where(g => lost.Contains(g.Key)).Select(g => g.Title);
        Note(string.Join(", ", titles) + " перестал открываться напрямую — вернули в VPN, подбираем заново. " +
            ZapretPlan.Describe(zapretActiveStrategy, groups, probe, zapretVpnGroups));
        await ApplyZapretRoutingAsync();
        zapretRepickAt = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(1);
    }
}
