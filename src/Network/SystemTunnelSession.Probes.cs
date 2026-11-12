using System.Text.Json.Nodes;

namespace Oblachka;

public sealed partial class SystemTunnelSession
{
    internal async Task<(Profile Profile, IAsyncDisposable? Access)> PrepareProbeAsync(Profile profile, CancellationToken token)
    {
        if (State != SystemTunnelState.Running || helper is not NetworkHelperClient client)
            throw new UserError("Сетевая служба пока не готова к проверке.");

        var pinned = PinnedFor(profile) ?? (await TunnelEndpoints.PrepareAsync(profile, token,
            (host, ct) => DohResolver.ResolveProbeAsync(host, profile.IsService, ct)).ConfigureAwait(false)).PinnedProfile;
        var (host, port) = ProfileServer.Of(pinned);
        var core = pinned.Core == CoreKind.Xray ? ProbeCore.Xray : ProfileServer.IsAmnezia(pinned) ? ProbeCore.Oblachka : ProbeCore.SingBox;
        // Проверка Hysteria2 использует основной порт из профиля. Остальные параметры протокола сохраняются.
        if (pinned.Outbound.ContainsKey("server_ports"))
        {
            var outbound = (JsonObject)pinned.Outbound.DeepClone();
            outbound.Remove("server_ports");
            pinned = pinned with { Outbound = outbound };
        }
        var transport = pinned.Outbound["streamSettings"]?["network"]?.GetValue<string>();
        var alpn = pinned.Outbound["streamSettings"]?["tlsSettings"]?["alpn"] as JsonArray;
        var udp = ProfileServer.IsUdp(pinned) || transport is "kcp" or "quic" or "hysteria" ||
            transport == "xhttp" && alpn is { Count: 1 } && alpn[0]?.GetValue<string>() == "h3";
        var id = Guid.NewGuid();
        var endpoint = new ProbeEndpoint(core, host, port, udp);
        await client.SetProbeAccessAsync(new(id, endpoint), token).ConfigureAwait(false);
        return (pinned, new ProbeAccess(client, id));
    }

    private sealed class ProbeAccess(NetworkHelperClient client, Guid id) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try { await client.SetProbeAccessAsync(new(id, null), CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) when (ex is UserError or IOException or ObjectDisposedException or OperationCanceledException)
            { /* Канал закрылся или разрешение истекло. Служба снимает его сама. */ }
        }
    }
}
