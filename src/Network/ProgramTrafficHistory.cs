namespace Oblachka;

public sealed partial class ProgramTrafficHistory
{
    private string? programEpoch;
    private bool rebaseNext;
    private readonly Dictionary<int, CoreTrafficSource> sources = new();

    // Resetting a report must not import the core's lifetime totals again.
    internal void Rebase() { active.Clear(); sources.Clear(); rebaseNext = true; }

    internal void Record(CoreProgramSnapshot snapshot, TrafficHistory history, DateTime now, Func<int, TrafficSource?>? sourceAt = null)
    {
        var baselineOnly = rebaseNext; rebaseNext = false;
        if (programEpoch != snapshot.Epoch) { active.Clear(); sources.Clear(); programEpoch = snapshot.Epoch; }
        foreach (var program in snapshot.Programs)
        {
            active.TryGetValue(program.Path, out var old);
            var up = old is null || program.Upload < old.Upload ? program.Upload : program.Upload - old.Upload;
            var down = old is null || program.Download < old.Download ? program.Download : program.Download - old.Download;
            var routes = program.Routes.Since(old?.Routes ?? default);
            active[program.Path] = new(program.Path, program.Upload, program.Download, program.Routes);
            if (!baselineOnly && (up > 0 || down > 0)) history.AddProgram(program.Path, up, down, now, routes.Fits(up, down) ? routes : default);
        }
        foreach (var source in snapshot.Sources ?? [])
        {
            sources.TryGetValue(source.Index, out var old);
            sources[source.Index] = source;
            var up = Math.Max(0, source.Upload - old.Upload);
            var down = Math.Max(0, source.Download - old.Download);
            if (!baselineOnly && (up > 0 || down > 0) && sourceAt?.Invoke(source.Index) is { } mapped)
                history.AddSource(mapped, up, down, now);
        }
    }
}
