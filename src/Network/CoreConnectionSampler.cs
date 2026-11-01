namespace Oblachka;

// Общий снимок для учёта программ и экрана соединений. Команды изменения правил и
// закрытия соединений используют CoreConnections.ReadAsync напрямую — всегда свежие данные.
internal sealed class CoreConnectionSampler
{
    private readonly object gate = new();
    private readonly Func<CoreStatsEntry, CancellationToken, Task<IReadOnlyList<CoreConnection>?>> read;
    private CoreStatsEntry? entry;
    private Task<IReadOnlyList<CoreConnection>?>? pending;
    private long started;

    internal CoreConnectionSampler(Func<CoreStatsEntry, CancellationToken, Task<IReadOnlyList<CoreConnection>?>>? read = null) =>
        this.read = read ?? CoreConnections.ReadAsync;

    internal Task<IReadOnlyList<CoreConnection>?> ReadAsync(CoreStatsEntry stats, CancellationToken token)
    {
        Task<IReadOnlyList<CoreConnection>?> task;
        lock (gate)
        {
            if (!ReferenceEquals(entry, stats) || pending is null || pending.IsCompleted &&
                (pending.Status != TaskStatus.RanToCompletion || pending.Result is null ||
                 System.Diagnostics.Stopwatch.GetElapsedTime(started) >= TimeSpan.FromMilliseconds(250)))
            {
                entry = stats;
                started = System.Diagnostics.Stopwatch.GetTimestamp();
                pending = read(stats, CancellationToken.None);
            }
            task = pending;
        }
        // Отмена одного потребителя не прерывает общий запрос другого.
        return task.WaitAsync(token);
    }

    internal void Reset() { lock (gate) { entry = null; pending = null; } }
}
