namespace Oblachka;

internal sealed record CoreProgramSample(CoreProgramSnapshot? Summary, IReadOnlyList<CoreConnection>? Connections);

// One source per session: switching sources midway would count traffic twice.
// An old service falls back once. A temporary failure of a supported API retries
// that API and leaves its cumulative counters to catch up on the next sample.
internal sealed class CoreProgramSampler(
    Func<CoreStatsEntry, CancellationToken, Task<IReadOnlyList<CoreConnection>?>> legacy,
    Func<CoreStatsEntry, CancellationToken, Task<CoreProgramSnapshot?>>? compact = null)
{
    private bool? supported;
    internal bool UsesLegacy => supported == false;
    internal async Task<CoreProgramSample?> ReadAsync(CoreStatsEntry stats, CancellationToken token)
    {
        if (supported != false)
        {
            var summary = await (compact ?? CorePrograms.ReadAsync)(stats, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (summary is not null) { supported = true; return new(summary, null); }
            if (supported == true) return null;
            supported = false;
        }
        var connections = await legacy(stats, token).ConfigureAwait(false);
        return connections is null ? null : new(null, connections);
    }
}
