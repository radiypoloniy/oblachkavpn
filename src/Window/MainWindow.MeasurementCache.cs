namespace Oblachka;

public partial class MainWindow
{
    // Скрытые узлы ещё принадлежат подпискам; их замеры нужны при возврате в список.
    // Работающий узел может пережить обновление или удаление своей подписки.
    private void TrimMeasurements()
    {
        var keys = groupProfiles.Values.SelectMany(p => p).Select(Profiles.Fingerprint).ToHashSet(StringComparer.Ordinal);
        if (runningProfile is not null) keys.Add(Profiles.Fingerprint(runningProfile));
        foreach (var cache in new[] { measurements, pings })
            foreach (var key in cache.Keys.Where(k => !keys.Contains(k)).ToArray()) cache.Remove(key);
    }
}
