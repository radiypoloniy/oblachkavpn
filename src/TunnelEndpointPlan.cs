using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace RoutePilot;

public sealed record TunnelEndpointPlan(Profile PinnedProfile, string[] Peers);

public static class TunnelEndpoints
{
    public static async Task<TunnelEndpointPlan> PrepareAsync(Profile profile, CancellationToken token,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null)
    {
        var copy = (JsonObject)profile.Outbound.DeepClone();
        string host;
        if (profile.Core == CoreKind.Xray)
        {
            if (copy["streamSettings"]?["xhttpSettings"]?["extra"]?["downloadSettings"] is not null)
                throw new UserError("TUN: отдельный адрес XHTTP downstream ещё не поддержан.");
            host = copy["settings"]!["vnext"]![0]!["address"]!.GetValue<string>();
        }
        else host = copy["server"]!.GetValue<string>();
        var addresses = IPAddress.TryParse(host, out var ip) ? new[] { ip } :
            await (resolve ?? Dns.GetHostAddressesAsync)(host, token).ConfigureAwait(false);
        var peers = addresses.Select(a => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a).Distinct()
            .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).Select(a => a.ToString()).ToArray();
        NetworkHelperContracts.Validate(new(1, Guid.NewGuid(), NetworkLeaseAction.Acquire, 10808, peers));
        if (profile.Core == CoreKind.Xray)
        {
            copy["settings"]!["vnext"]![0]!["address"] = peers[0];
            var tls = copy["streamSettings"]?["tlsSettings"] as JsonObject;
            if (tls is not null && string.IsNullOrWhiteSpace(tls["serverName"]?.GetValue<string>())) tls["serverName"] = host;
        }
        else
        {
            copy["server"] = peers[0];
            var tls = copy["tls"]!.AsObject();
            if (string.IsNullOrWhiteSpace(tls["server_name"]?.GetValue<string>())) tls["server_name"] = host;
        }
        return new(profile with { Outbound = copy }, peers);
    }
}
