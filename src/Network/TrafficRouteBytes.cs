namespace Oblachka;

// Only bytes attributed to an observed route. The remainder of a total
// includes older records and traffic whose exit could not be established.
public readonly record struct TrafficRouteBytes(long VpnUpload, long VpnDownload, long DirectUpload, long DirectDownload)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public long Vpn => VpnUpload + VpnDownload;
    [System.Text.Json.Serialization.JsonIgnore]
    public long Direct => DirectUpload + DirectDownload;

    internal bool Fits(long upload, long download) =>
        VpnUpload >= 0 && DirectUpload >= 0 && VpnUpload <= upload && DirectUpload <= upload - VpnUpload &&
        VpnDownload >= 0 && DirectDownload >= 0 && VpnDownload <= download && DirectDownload <= download - VpnDownload;

    internal TrafficRouteBytes Since(TrafficRouteBytes old) => new(
        Math.Max(0, VpnUpload - old.VpnUpload), Math.Max(0, VpnDownload - old.VpnDownload),
        Math.Max(0, DirectUpload - old.DirectUpload), Math.Max(0, DirectDownload - old.DirectDownload));

    public static TrafficRouteBytes operator +(TrafficRouteBytes a, TrafficRouteBytes b) =>
        new(a.VpnUpload + b.VpnUpload, a.VpnDownload + b.VpnDownload, a.DirectUpload + b.DirectUpload, a.DirectDownload + b.DirectDownload);
}
