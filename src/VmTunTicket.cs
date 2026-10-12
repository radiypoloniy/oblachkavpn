using Microsoft.Win32;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace RoutePilot;

internal sealed record VmTunTicket(int Version, DateTimeOffset Created, int OwnerId, long OwnerStarted,
    Profile Profile, string[] Peers);

internal static class VmTunTickets
{
    private static readonly byte[] Entropy = System.Text.Encoding.UTF8.GetBytes("Oblachka.VmTun.1");
    internal static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Oblachka", "vm-tun");

    internal static bool IsVirtualMachine()
    {
        const string key = @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\BIOS";
        var hardware = $"{Registry.GetValue(key, "SystemProductName", "")} {Registry.GetValue(key, "SystemManufacturer", "")}";
        return new[] { "VirtualBox", "VMware", "Virtual Machine", "KVM", "QEMU", "Parallels" }
            .Any(value => hardware.Contains(value, StringComparison.OrdinalIgnoreCase));
    }

    internal static string Save(TunnelEndpointPlan plan, string? isolatedDirectory = null)
    {
        using var owner = Process.GetCurrentProcess();
        var ticket = new VmTunTicket(1, DateTimeOffset.UtcNow, owner.Id, owner.StartTime.ToUniversalTime().Ticks, plan.PinnedProfile, plan.Peers);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(ticket);
        try
        {
            if (bytes.Length > 65536) throw new UserError("Профиль слишком большой для проверки TUN.");
            var directory = isolatedDirectory ?? DirectoryPath;
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "request-" + Guid.NewGuid().ToString("N") + ".bin");
            File.WriteAllBytes(path, ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser));
            return path;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    internal static VmTunTicket Read(string path, string? isolatedDirectory = null)
    {
        path = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(path), isolatedDirectory ?? DirectoryPath, StringComparison.OrdinalIgnoreCase) ||
            !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(path), "^request-[a-f0-9]{32}\\.bin$") ||
            new FileInfo(path).Length > 70000) throw new UserError("Неверный запрос проверки TUN.");
        var bytes = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
        try
        {
            if (bytes.Length > 65536) throw new UserError("Запрос слишком большой.");
            var ticket = JsonSerializer.Deserialize<VmTunTicket>(bytes) ?? throw new UserError("Пустой запрос.");
            if (ticket.Version != 1 || ticket.Created > DateTimeOffset.UtcNow.AddSeconds(30) ||
                ticket.Created < DateTimeOffset.UtcNow.AddMinutes(-5)) throw new UserError("Запрос устарел. Запусти проверку ещё раз.");
            NetworkHelperContracts.Validate(new(1, Guid.NewGuid(), NetworkLeaseAction.Acquire, 10808, ticket.Peers));
            var parsed = Profiles.Parse(ticket.Profile.Outbound.ToJsonString());
            var address = parsed.Core == CoreKind.Xray ? Profiles.XrayEndpoint(parsed.Outbound)["address"]!.GetValue<string>() : parsed.Outbound["server"]!.GetValue<string>();
            if (!ticket.Peers.Contains(address) || parsed.Core != ticket.Profile.Core) throw new UserError("Адрес профиля не подготовлен.");
            using var owner = Process.GetProcessById(ticket.OwnerId);
            if (owner.StartTime.ToUniversalTime().Ticks != ticket.OwnerStarted ||
                !string.Equals(owner.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                throw new UserError("Окно приложения уже закрыто.");
            return ticket with { Profile = parsed };
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
