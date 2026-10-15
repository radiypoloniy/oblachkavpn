using System.Runtime.InteropServices;
using System.Text.Json;

namespace Oblachka;

// Исправления без прав администратора: прокси Windows, автонастройка, переменные окружения,
// кэш DNS. Прежние значения сохраняются до изменения — «Вернуть как было» их и ставит.
public sealed record RepairBackup(DateTimeOffset Time, ProxySettings? Proxy, Dictionary<string, string> Environment);

public static class NetworkRepair
{
    // Проверки подменяют путь: настоящий файл пользователя они трогать не должны.
    internal static string? BackupOverride;
    public static string BackupPath => BackupOverride ?? Path.Combine(ClientDataPaths.Root, "network-repair-backup.json");
    public static bool HasBackup => File.Exists(BackupPath);

    public static void ApplyLocal(RepairAction action, IProxySettingsStore? store = null)
    {
        store ??= new WinInetProxyStore();
        switch (action.Kind)
        {
            case RepairKind.DeadProxy or RepairKind.DeadPac:
                var current = store.Read();
                Remember(b => b with { Proxy = b.Proxy ?? current });
                // Адрес прокси оставляем — только выключаем: в настройках Windows видно, что было.
                store.Write(action.Kind == RepairKind.DeadProxy ? current with { ProxyEnable = 0 } : current with { AutoConfigUrl = null });
                break;
            case RepairKind.DeadEnvProxy when action.Target is "HTTP_PROXY" or "HTTPS_PROXY" or "ALL_PROXY":
                var value = Environment.GetEnvironmentVariable(action.Target, EnvironmentVariableTarget.User);
                if (value is null) return;
                Remember(b => { b.Environment.TryAdd(action.Target, value); return b; });
                Environment.SetEnvironmentVariable(action.Target, null, EnvironmentVariableTarget.User);
                break;
            case RepairKind.FlushDns:
                DnsFlushResolverCache();
                break;
            default: throw new UserError("Это исправление выполняет служба " + ServiceBrand.Name + ".");
        }
    }

    // true — что-то возвращено.
    public static bool Undo(IProxySettingsStore? store = null)
    {
        if (Read() is not { } backup) return false;
        if (backup.Proxy is { } proxy) (store ?? new WinInetProxyStore()).Write(proxy);
        foreach (var (name, value) in backup.Environment) Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.User);
        File.Delete(BackupPath);
        return true;
    }

    private static RepairBackup? Read()
    {
        try { return File.Exists(BackupPath) ? JsonSerializer.Deserialize<RepairBackup>(File.ReadAllText(BackupPath)) : null; }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    }

    // Первое сохранённое значение не перезаписывается: «как было» — до первого исправления.
    private static void Remember(Func<RepairBackup, RepairBackup> change)
    {
        var backup = change(Read() ?? new RepairBackup(DateTimeOffset.UtcNow, null, new()));
        Directory.CreateDirectory(Path.GetDirectoryName(BackupPath)!);
        File.WriteAllText(BackupPath, JsonSerializer.Serialize(backup));
    }

    [DllImport("dnsapi.dll")] private static extern bool DnsFlushResolverCache();
}
