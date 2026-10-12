using System.Text.Json;
using System.Text.Json.Serialization;

namespace Oblachka;

// One explicit journal path, held exclusively for this owner's lifetime. No directory scanning.
public sealed class NetworkSessionJournal : INetworkSessionJournal, IDisposable
{
    private const int MaxBytes = 65536;
    private readonly string path;
    private readonly FileStream ownership;
    private bool disposed;
    public object SyncRoot { get; } = new();
    private static readonly JsonSerializerOptions Json = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public NetworkSessionJournal(string path)
    {
        this.path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(this.path)!);
        ownership = new FileStream(this.path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public NetworkSessionRecord? Load()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!File.Exists(path)) return null;
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > MaxBytes) throw new UserError("Журнал сетевой сессии слишком большой.");
        try
        {
            using var document = JsonDocument.Parse(file);
            CheckUniqueFields(document.RootElement);
            var record = document.RootElement.Deserialize<NetworkSessionRecord>(Json) ?? throw new UserError("Пустой сетевой журнал.");
            NetworkSessionValidation.Validate(record);
            return record;
        }
        catch (JsonException) { throw new UserError("Журнал повреждён. Автоматические сетевые изменения остановлены."); }
    }

    public void Save(NetworkSessionRecord record)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        NetworkSessionValidation.Validate(record);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, Json);
        if (bytes.Length > MaxBytes) throw new UserError("Журнал сетевой сессии слишком большой.");
        var staged = path + ".pending-" + Guid.NewGuid().ToString("N");
        using (var file = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { file.Write(bytes); file.Flush(true); }
        File.Move(staged, path, true);
        // On replacement failure, leave both the old journal and pending evidence intact.
    }

    private static void CheckUniqueFields(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in node.EnumerateObject())
            {
                if (!names.Add(field.Name)) throw new UserError("Поле сетевого журнала повторяется.");
                CheckUniqueFields(field.Value);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var item in node.EnumerateArray()) CheckUniqueFields(item);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        ownership.Dispose();
    }
}
