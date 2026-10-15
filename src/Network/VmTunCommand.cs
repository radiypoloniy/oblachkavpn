using System.Text.Json;

namespace Oblachka;

public static class VmTunCommand
{
    public static async Task<int> PrepareAsync(int port, string peersPath, string destination)
    {
        // Explicit VM-kit input; never reads a subscription or discovers the user's current profiles.
        if (new FileInfo(peersPath).Length > 4096) throw new UserError("Список адресов слишком большой.");
        var bytes = await File.ReadAllBytesAsync(peersPath);
        if (bytes.Length > 4096) throw new UserError("Список адресов слишком большой.");
        var peers = JsonSerializer.Deserialize<string[]>(System.Text.Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'))
            ?? throw new UserError("Отсутствует список IP.");
        var request = new NetworkLeaseRequest(1, Guid.NewGuid(), NetworkLeaseAction.Acquire, port, peers);
        await CoreConfigurationCheck.ValidateTunAsync(request, CancellationToken.None);
        using var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(file, SingBoxTunConfig.Create(request), new JsonSerializerOptions { WriteIndented = true });
        file.Flush(true);
        return 0;
    }
}
