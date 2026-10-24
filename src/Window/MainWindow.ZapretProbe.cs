namespace Oblachka;

// Проверка zapret: один вариант (без обхода или стратегия) и путь, которым она идёт.
public partial class MainWindow
{
    // Один вариант: без обхода (winws остановлен) или стратегия. null — служба не ответила.
    private async Task<ZapretProbeResult?> ProbeVariantAsync(string strategy, string[] exclude, CancellationToken token)
    {
        if (strategy == ZapretPlan.NoBypass) await ZapretCallAsync(new(ZapretAction.Stop));
        else
        {
            var started = await ZapretCallAsync(new(ZapretAction.Start, strategy, exclude));
            if (started is not { Running: true }) throw new UserError(started?.Error ?? "Служба не запустила zapret.");
            await Task.Delay(800, token);
        }
        var probe = await ZapretProbe.RunAsync(ZapretProbeRoute, token);
        zapretJournal.Add(ZapretJournalKind.Variant, strategy, ZapretPlan.Passing(probe), probe, NetworkIdentity.Current() ?? "unknown", ZapretPathNote);
        return probe;
    }

    // Каким путём шла проверка: под «Весь компьютер» — через вход ядра туннеля, только по TCP
    // (QUIC из окна ушёл бы в туннель), без VPN — из окна, с запасной попыткой по QUIC.
    private string ZapretPathNote => SystemTunnelActive ? "проверка через ядро туннеля, только TCP" : "проверка из окна, TCP и QUIC";

    // Путь проверки — тот, которым пойдут сайты zapret: без VPN — прямо из окна, под «Весь компьютер» —
    // вход прямого трафика ядра туннеля (выход на физическую сеть через winws, DNS сети). null под
    // «Весь компьютер» — входа нет, проверять нечем.
    private SocksRoute? ZapretProbeRoute => !SystemTunnelActive ? null
        : (engine as Engine)?.DirectEntry is { } entry ? new SocksRoute(entry.Port, entry.User, entry.Password) : null;

    private bool ZapretProbeReady => !SystemTunnelActive || ZapretProbeRoute is not null;
}
