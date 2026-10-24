using System.Text.Json;

namespace Oblachka;

// Журнал решений zapret: что проверяли, что открылось и почему группа ушла в VPN. Карточка
// иногда пишет «не работает», когда YouTube в браузере идёт через обход, — без журнала не
// понять, какой адрес не прошёл и с какой скоростью. Адреса проверки — постоянные из
// ZapretProbe.Targets, сеть — только хеш (NetworkIdentity), поэтому журнал можно отдать в
// отчёт поддержке. Хранится в файле рядом с данными окна: сбой видят уже после перезапуска.
public enum ZapretJournalKind { Pick, Variant, Chosen, Watch, Demote, Stop }

public sealed record ZapretJournalTarget(string Host, bool Ok, long Bytes, long Milliseconds, string Failure, bool Quic);

public sealed record ZapretJournalEntry(DateTimeOffset Time, ZapretJournalKind Kind, string Strategy, string[] Groups)
{
    public string Network { get; init; } = "";
    public ZapretJournalTarget[] Targets { get; init; } = [];
    public string Note { get; init; } = "";
}

public sealed class ZapretJournal
{
    public const int Capacity = 80;
    private readonly List<ZapretJournalEntry> entries = new();
    private readonly string? path;

    public ZapretJournal(string? path = null)
    {
        this.path = path;
        if (path is null) return;
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<ZapretJournalEntry[]>(File.ReadAllText(path)) is { } saved)
                entries.AddRange(saved.TakeLast(Capacity));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }

    public static string DefaultPath => Path.Combine(ClientDataPaths.Root, "zapret-journal.json");

    public IReadOnlyList<ZapretJournalEntry> Entries { get { lock (entries) return entries.ToArray(); } }

    public void Add(ZapretJournalKind kind, string strategy, IEnumerable<string> groups, ZapretProbeResult? probe = null, string network = "", string note = "")
    {
        var entry = new ZapretJournalEntry(DateTimeOffset.UtcNow, kind, strategy, groups.ToArray())
        {
            Network = network.Length > 6 ? network[..6] : network, Targets = probe is null ? [] : TargetsOf(probe), Note = note
        };
        ZapretJournalEntry[] snapshot;
        lock (entries)
        {
            entries.Add(entry);
            if (entries.Count > Capacity) entries.RemoveRange(0, entries.Count - Capacity);
            snapshot = entries.ToArray();
        }
        if (path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(snapshot));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static ZapretJournalTarget[] TargetsOf(ZapretProbeResult probe) =>
        probe.Details.Select((d, i) => new ZapretJournalTarget(i < ZapretProbe.Targets.Length ? ZapretProbe.Targets[i].Host : "#" + i,
            d.Ok, d.Bytes, d.Milliseconds, d.Failure.ToString(), d.Quic)).ToArray();
}
