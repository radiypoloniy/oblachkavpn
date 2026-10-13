namespace Oblachka;

public partial class MainWindow
{
    private readonly HashSet<string> favorites = new(StringComparer.Ordinal);

    // Имя и источник переживают смену ключей панели; значения в файле состояния — хеши.
    private static string FavoriteKey(Profile item) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(item.Source + ":" + item.Name)));

    private long MeasurementOrder(Profile item) => measurements.TryGetValue(Profiles.Fingerprint(item), out var result) &&
        result.Success && result.Time <= DateTimeOffset.UtcNow && DateTimeOffset.UtcNow - result.Time < TimeSpan.FromMinutes(20)
            ? result.Milliseconds : long.MaxValue;

    private void WireServerList()
    {
        Servers.SortOrder.SelectionChanged += (_, _) => FilterProfiles();
        Servers.FavoritesOnly.Click += (_, _) => FilterProfiles();
        Servers.FavoriteButton.Click += (_, _) =>
        {
            if (profile is null || operation is not null || engine.State != ConnectionState.Idle) return;
            var key = FavoriteKey(profile);
            if (!favorites.Remove(key)) favorites.Add(key);
            FilterProfiles();
            QueueStateSave();
        };
    }

    private void RefreshServerLabels()
    {
        foreach (var item in importedProfiles)
        {
            var label = measurements.TryGetValue(Profiles.Fingerprint(item), out var result)
                ? (result.Success ? $"HTTPS: {result.Milliseconds} мс" : "Нет ответа при проверке") +
                  (result.Time > DateTimeOffset.UtcNow || DateTimeOffset.UtcNow - result.Time >= TimeSpan.FromMinutes(20) ? " · устарело" : "")
                : "Не проверен";
            item.SetListStatus((favorites.Contains(FavoriteKey(item)) ? "★ · " : "") + label);
        }
    }

    private void RefreshServerListControls()
    {
        Servers.SortOrder.IsEnabled = ProfilePicker.IsEnabled;
        Servers.FavoritesOnly.IsEnabled = ProfilePicker.IsEnabled;
        Servers.FavoriteButton.IsEnabled = ProfilePicker.IsEnabled && profile is not null;
        Servers.FavoriteButton.Content = profile is not null && favorites.Contains(FavoriteKey(profile))
            ? "Убрать из избранного" : "Добавить в избранное";
        RefreshServerLabels();
    }
}
