using System.Security.Cryptography;

namespace Oblachka;

// Ядро системного туннеля — oblachka-core.exe: sing-box 1.14.3 той же ревизии, что и
// официальный, плюс выход "xray" со встроенным Xray (core\, scripts\build-core.ps1). Так
// XHTTP и другие протоколы Xray идут из TUN прямо на сервер, без SOCKS и второго процесса.
// Файл собирается воспроизводимо и закреплён по SHA256, как ядра с официальных релизов.
public static class TunCore
{
    public const string FileName = "oblachka-core.exe";
    public const string Sha256 = "335B1E561E0F0E2D00E287D73903E960AB5823787CEEA6BB1A95D6DA0469055F";

    public static string Program(string? tools = null) => Path.Combine(tools ?? Path.Combine(AppContext.BaseDirectory, "tools"), FileName);

    public static async Task VerifyAsync(string executable, CancellationToken token)
    {
        await using var input = File.OpenRead(executable);
        if (Convert.ToHexString(await SHA256.HashDataAsync(input, token).ConfigureAwait(false)) != Sha256)
            throw new UserError("Ядро туннеля не соответствует закреплённой версии.");
    }
}
