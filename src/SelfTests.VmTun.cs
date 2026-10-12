using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace RoutePilot;

public static partial class SelfTests
{
    private static async Task VmTunChecksAsync(Func<string, Func<Task>, Task> check, string root)
    {
        await check("VM GUI ticket encrypts the profile, preserves TLS identity and binds to its owner", async () =>
        {
            var original = Profiles.Parse("vless://11111111-1111-1111-1111-111111111111@example.com:443?security=tls&type=xhttp&sni=example.com&host=example.com&path=%2Ftest#Synthetic");
            var plan = await TunnelEndpoints.PrepareAsync(original, CancellationToken.None,
                (_, _) => Task.FromResult(new[] { IPAddress.Parse("192.0.2.1") }));
            var ticket = VmTunTickets.Save(plan, root);
            var bytes = await File.ReadAllBytesAsync(ticket);
            Assert(!Encoding.UTF8.GetString(bytes).Contains("11111111-1111") && !Encoding.UTF8.GetString(bytes).Contains("example.com"));
            var restored = VmTunTickets.Read(ticket, root);
            Assert(restored.OwnerId == Environment.ProcessId && restored.Peers.SequenceEqual(plan.Peers));
            Assert(restored.Profile.Outbound["streamSettings"]!["tlsSettings"]!["serverName"]!.GetValue<string>() == "example.com");
            Assert(restored.Profile.Outbound["streamSettings"]!["xhttpSettings"]!["host"]!.GetValue<string>() == "example.com");
            Assert(original.Outbound["settings"]!["vnext"]![0]!["address"]!.GetValue<string>() == "example.com");
            bytes[bytes.Length / 2] ^= 1; await File.WriteAllBytesAsync(ticket, bytes);
            try { VmTunTickets.Read(ticket, root); throw new InvalidOperationException("Tampering accepted"); }
            catch (CryptographicException) { }
        });
        await check("VM GUI rejects tickets outside its directory and disposal before startup is safe", () =>
        {
            try { VmTunTickets.Read(Path.Combine(root, "other", "request-" + new string('a', 32) + ".bin"), root); throw new InvalidOperationException(); }
            catch (UserError) { }
            using var session = new VmTunSession(); session.Dispose(); session.Dispose();
            Assert(!session.IsAlive && !session.InterfaceExists);
            return Task.CompletedTask;
        });
    }
}
