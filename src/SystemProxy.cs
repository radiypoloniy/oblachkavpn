using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace Oblachka;

// Values of HKCU Internet Settings that the system proxy mode changes. Null means "value was absent".
public sealed record ProxySettings(int? ProxyEnable, string? ProxyServer, string? ProxyOverride, string? AutoConfigUrl);
public sealed record ProxyBackup(int Version, string Applied, ProxySettings Original);

public interface IProxySettingsStore
{
    ProxySettings Read();
    void Write(ProxySettings settings);
}

// Per-user WinINet proxy (no administrator rights). The original values are written to disk before any change,
// so a crash or power loss is undone on the next start. A value changed by someone else after us is left alone.
public sealed class SystemProxy(IProxySettingsStore store, string backupPath)
{
    public const string Bypass = "localhost;127.*;10.*;172.16.*;172.17.*;172.18.*;172.19.*;172.20.*;172.21.*;172.22.*;172.23.*;172.24.*;172.25.*;172.26.*;172.27.*;172.28.*;172.29.*;172.30.*;172.31.*;192.168.*;<local>";

    public static SystemProxy ForCurrentUser() => new(new WinInetProxyStore(), Path.Combine(ClientDataPaths.Root, "system-proxy-backup.json"));

    public bool IsApplied => File.Exists(backupPath);

    public void Apply(int port)
    {
        if (port is < 1 or > 65535) throw new UserError("Неверный локальный порт для системного прокси.");
        var target = "127.0.0.1:" + port;
        var current = store.Read();
        var backup = ReadBackup();
        if (backup is null)
        {
            // Never record our own value as the "original" (e.g. after a lost backup file).
            var original = IsOurs(current.ProxyServer) ? current with { ProxyEnable = 0, ProxyServer = null } : current;
            WriteBackup(new ProxyBackup(1, target, original));
        }
        else if (backup.Applied != target) WriteBackup(backup with { Applied = target });
        store.Write(new ProxySettings(1, target, Bypass, null));
    }

    // Returns true when the original settings were put back, false when there was nothing to restore
    // or another program had already replaced our proxy (its settings are kept).
    public bool Restore()
    {
        var backup = ReadBackup();
        if (backup is null) return false;
        var current = store.Read();
        var ours = current.ProxyEnable == 1 && current.ProxyServer == backup.Applied;
        if (ours) store.Write(backup.Original);
        File.Delete(backupPath);
        return ours;
    }

    private static bool IsOurs(string? server) => server is not null && server.StartsWith("127.0.0.1:", StringComparison.Ordinal) && !server.Contains(';');

    private ProxyBackup? ReadBackup()
    {
        if (!File.Exists(backupPath)) return null;
        try
        {
            if (new FileInfo(backupPath).Length > 16384) throw new JsonException();
            var backup = JsonSerializer.Deserialize<ProxyBackup>(File.ReadAllBytes(backupPath));
            if (backup is null || backup.Version != 1 || backup.Original is null || !IsOurs(backup.Applied)) throw new JsonException();
            return backup;
        }
        catch (JsonException) { throw new UserError("Файл восстановления системного прокси повреждён: " + backupPath + ". Проверьте настройки прокси Windows вручную."); }
    }

    private void WriteBackup(ProxyBackup backup)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(backupPath))!);
        var staged = backupPath + ".pending-" + Guid.NewGuid().ToString("N");
        using (var file = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { file.Write(JsonSerializer.SerializeToUtf8Bytes(backup)); file.Flush(true); }
        File.Move(staged, backupPath, true);
    }
}

public sealed class WinInetProxyStore : IProxySettingsStore
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    public ProxySettings Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, false);
        return new(key?.GetValue("ProxyEnable") is int enable ? enable : null, key?.GetValue("ProxyServer") as string,
            key?.GetValue("ProxyOverride") as string, key?.GetValue("AutoConfigURL") as string);
    }

    public void Write(ProxySettings settings)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(KeyPath, true))
        {
            Set(key, "ProxyEnable", settings.ProxyEnable, RegistryValueKind.DWord);
            Set(key, "ProxyServer", settings.ProxyServer, RegistryValueKind.String);
            Set(key, "ProxyOverride", settings.ProxyOverride, RegistryValueKind.String);
            Set(key, "AutoConfigURL", settings.AutoConfigUrl, RegistryValueKind.String);
        }
        // Tell WinINet consumers (browsers, most apps) to reload the settings.
        InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0);
    }

    private static void Set(RegistryKey key, string name, object? value, RegistryValueKind kind)
    {
        if (value is null) key.DeleteValue(name, false); else key.SetValue(name, value, kind);
    }

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr handle, int option, IntPtr buffer, int length);
}
