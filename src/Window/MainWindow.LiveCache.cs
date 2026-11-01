using Oblachka.Views;

namespace Oblachka;

public partial class MainWindow
{
    private void TrimLiveCache(IReadOnlyList<ConnectionRow> rows)
    {
        var current = rows.ToHashSet();
        foreach (var key in liveRows.Where(p => !current.Contains(p.Value)).Select(p => p.Key).ToArray()) liveRows.Remove(key);
        var files = rows.SelectMany(r => r.Files.Select(f => f.Path).Append(r.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sites = rows.SelectMany(r => r.Sites.Select(s => r.Path + "|" + s.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        TrimTo(liveFiles, files); TrimTo(liveSites, sites); TrimTo(liveScopes, files);
    }

    private static void TrimTo<T>(Dictionary<string, T> cache, HashSet<string> keys)
    {
        foreach (var key in cache.Keys.Where(k => !keys.Contains(k)).ToArray()) cache.Remove(key);
    }

    private void ClearLivePresentation()
    {
        Live.Load([]);
        // Заблокированная программа должна остаться доступной для снятия её правила.
        foreach (var key in liveRows.Where(p => p.Value.Rule != SiteAction.Block).Select(p => p.Key).ToArray()) liveRows.Remove(key);
        foreach (var row in liveRows.Values) { row.Files = []; row.Sites = []; }
        liveFiles.Clear(); liveSites.Clear(); liveScopes.Clear();
        lock (livePaths) livePaths.Clear();
    }
}
