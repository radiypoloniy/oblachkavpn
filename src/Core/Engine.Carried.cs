using System.Diagnostics;

namespace Oblachka;

// Режим «трафик несёт ядро туннеля»: в «Весь компьютер» с выходом прямо на сервер процесс ядра окна
// простаивал бы, занимая память (~60 МБ). Здесь его нет: порт только закреплён — на него переключатель
// ядра туннеля смотрит как на запасной путь (узел без перевода), и настоящее ядро займёт его при смене
// узла. Проверка связи идёт через clash API ядра туннеля (CoreSelector.DelayAsync): тем же выходом, что
// и трафик, и мимо списков — адрес проверки, отправленный списком «напрямую», не обманет проверку.
public sealed partial class Engine
{
    private CoreStatsEntry? carrier;

    public CoreStatsEntry? CarriedBy { get; set; }

    private Task StartCarried(CoreStatsEntry entry)
    {
        Set(ConnectionState.Starting, "Трафик несёт ядро туннеля");
        Port = FreePort(preferredPort);
        preferredPort = Port;
        FragmentActive = false; stopping = false;
        carrier = entry;
        Set(ConnectionState.Running, "Трафик идёт через ядро туннеля. Связь ещё не проверена.");
        return Task.CompletedTask;
    }

    private async Task<ProbeResult> CarriedProbeAsync(CoreStatsEntry entry, Uri target, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        var delay = await CoreSelector.DelayAsync(entry, target, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(carrier, entry)) throw new UserError("Подключение остановлено во время проверки.");
        if (delay is null)
        {
            Set(ConnectionState.Running, "Ядро туннеля работает, но запрос через сервер не выполнен.");
            return new(false, watch.ElapsedMilliseconds, "Запрос не прошёл. Причина может быть в профиле, сервере, сети или выбранном сайте.", ProbeFailure.Reachability);
        }
        Set(ConnectionState.Verified, "Запрос через туннель выполнен");
        return new(true, delay.Value, $"Запрос через ядро туннеля выполнен за {delay.Value} мс. Это время запроса, а не ping.");
    }
}
