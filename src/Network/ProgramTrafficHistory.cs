namespace Oblachka;

public sealed partial class ProgramTrafficHistory
{
    private string? programEpoch;
    private bool rebaseNext;

    // Resetting a report must not import the core's lifetime totals again.
    internal void Rebase() { active.Clear(); rebaseNext = true; }

    internal void Record(CoreProgramSnapshot snapshot, TrafficHistory history, DateTime now)
    {
        var baselineOnly = rebaseNext; rebaseNext = false;
        if (programEpoch != snapshot.Epoch) { active.Clear(); programEpoch = snapshot.Epoch; }
        foreach (var program in snapshot.Programs)
        {
            active.TryGetValue(program.Path, out var old);
            var up = old is null || program.Upload < old.Upload ? program.Upload : program.Upload - old.Upload;
            var down = old is null || program.Download < old.Download ? program.Download : program.Download - old.Download;
            var routes = program.Routes.Since(old?.Routes ?? default);
            active[program.Path] = new(program.Path, program.Upload, program.Download, program.Routes);
            if (!baselineOnly && (up > 0 || down > 0)) history.AddProgram(program.Path, up, down, now, routes.Fits(up, down) ? routes : default);
        }
    }
}
