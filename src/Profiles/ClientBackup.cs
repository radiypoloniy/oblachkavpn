using System.Text.Json;
using System.Text.Json.Nodes;

namespace Oblachka;

// Резервная копия настроек в файл: тот же снимок, что в зашифрованном файле состояния
// (ClientStateStore), но открытым JSON в обёртке с форматом, версией и датой. Шифрование DPAPI
// привязано к учётной записи Windows, а копия нужна как раз при переезде на другой компьютер.
// В ней ключи подписок и свои серверы — окно предупреждает, что файл нельзя пересылать.
public static class ClientBackup
{
    public const string Format = "oblachka-backup";
    private const int MaxBytes = 8 * 1024 * 1024;

    public static byte[] Write(ClientSnapshot snapshot, DateTimeOffset saved) =>
        JsonSerializer.SerializeToUtf8Bytes(new JsonObject
        {
            ["format"] = Format, ["version"] = 1, ["saved"] = saved.ToString("O"),
            ["state"] = JsonSerializer.SerializeToNode(snapshot)
        }, new JsonSerializerOptions { WriteIndented = true });

    public static (ClientSnapshot Snapshot, DateTimeOffset? Saved) Read(byte[] bytes)
    {
        if (bytes.Length is 0 or > MaxBytes) throw new UserError("Это не резервная копия Облачки.");
        try
        {
            if (JsonNode.Parse(bytes) is not JsonObject root || (string?)root["format"] != Format || root["state"] is not JsonObject state)
                throw new UserError("Это не резервная копия Облачки.");
            if ((int?)root["version"] != 1) throw new UserError("Копия сделана более новой версией Облачки — сначала обновите приложение.");
            var snapshot = state.Deserialize<ClientSnapshot>() ?? throw new UserError("Резервная копия пуста.");
            return (snapshot, DateTimeOffset.TryParse((string?)root["saved"], out var saved) ? saved : null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw new UserError("Файл повреждён или это не резервная копия Облачки.", ex);
        }
    }
}
