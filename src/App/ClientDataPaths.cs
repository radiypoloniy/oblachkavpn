namespace Oblachka;

public static class ClientDataPaths
{
    // Stable across upgrades and install folders; only this application's own directory is used.
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Oblachka");
    public static string StateFile => Path.Combine(Root, "client-state.bin");
    public static IEnumerable<string> LegacyFiles => new[]
    {
        Path.Combine(AppContext.BaseDirectory, "data", "client-state.bin"),
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "app-v06", "data", "client-state.bin"))
    }.Distinct(StringComparer.OrdinalIgnoreCase);
}
