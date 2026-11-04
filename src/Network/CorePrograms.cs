using System.Net;
using System.Text.Json;

namespace Oblachka;

internal sealed record CoreProgram(string Path, long Upload, long Download, int Vpn, int Direct, int Udp, DateTime LastSeen, TrafficRouteBytes Routes = default);
internal readonly record struct CoreTrafficSource(int Index, long Upload, long Download);
internal sealed record CoreProgramSnapshot(string Epoch, IReadOnlyList<CoreProgram> Programs, IReadOnlyList<CoreTrafficSource>? Sources = null);

internal static class CorePrograms
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(1) })
        { Timeout = TimeSpan.FromSeconds(2), MaxResponseContentBufferSize = 512 * 1024 };

    internal static async Task<CoreProgramSnapshot?> ReadAsync(CoreStatsEntry entry, CancellationToken token)
    {
        // An older service does not start the compact listener. Never send its
        // session secret to an unrelated program which happens to own that port.
        if (entry.ProgramsPort == 0 || !ConnectionTable.SameLoopbackListenerOwner(entry.Port, entry.ProgramsPort)) return null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{entry.ProgramsPort}/programs");
            request.Headers.Authorization = new("Bearer", entry.Secret);
            using var response = await Http.SendAsync(request, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
            return Parse(document.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException) { return null; }
    }

    internal static CoreProgramSnapshot Parse(JsonElement root)
    {
        if (root.GetProperty("version").GetInt32() != 1 || !Guid.TryParse(root.GetProperty("epoch").GetString(), out var epoch))
            throw new JsonException("Unsupported program statistics");
        var rows = root.GetProperty("programs");
        if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > TrafficHistory.MaxPrograms) throw new JsonException("Too many programs");
        var result = new List<CoreProgram>(rows.GetArrayLength());
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows.EnumerateArray())
        {
            var path = row.GetProperty("path").GetString();
            var up = row.GetProperty("upload").GetInt64(); var down = row.GetProperty("download").GetInt64();
            var vpn = row.GetProperty("vpn").GetInt32(); var direct = row.GetProperty("direct").GetInt32(); var udp = row.GetProperty("udp").GetInt32();
            var seen = row.GetProperty("lastSeen").GetInt64();
            long Bytes(string name) => row.TryGetProperty(name, out var value) ? value.GetInt64() : 0;
            var routes = new TrafficRouteBytes(Bytes("vpnUpload"), Bytes("vpnDownload"), Bytes("directUpload"), Bytes("directDownload"));
            if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || path.Any(char.IsControl) || !paths.Add(path) || up < 0 || down < 0 || vpn < 0 || direct < 0 || udp < 0 || seen < 0 || seen > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())
                throw new JsonException("Invalid program statistics");
            if (!routes.Fits(up, down)) throw new JsonException("Invalid route byte counters");
            result.Add(new(path, up, down, vpn, direct, udp, DateTimeOffset.FromUnixTimeMilliseconds(seen).UtcDateTime, routes));
        }
        return new(epoch.ToString(), result, ParseSources(root));
    }

    private static CoreTrafficSource[] ParseSources(JsonElement root)
    {
        if (!root.TryGetProperty("sources", out var rows)) return [];
        if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > DirectOutboundPolicy.MaxOutbounds)
            throw new JsonException("Too many traffic sources");
        var indices = new HashSet<int>();
        return rows.EnumerateArray().Select(row =>
        {
            var source = new CoreTrafficSource(row.GetProperty("index").GetInt32(), row.GetProperty("upload").GetInt64(), row.GetProperty("download").GetInt64());
            if (source.Index is < 0 or >= DirectOutboundPolicy.MaxOutbounds || source.Upload < 0 || source.Download < 0 || !indices.Add(source.Index))
                throw new JsonException("Invalid traffic source");
            return source;
        }).ToArray();
    }
}
