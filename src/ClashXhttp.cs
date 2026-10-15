namespace Oblachka;

internal static class ClashXhttp
{
    // Тонкие настройки XHTTP (паддинг, мультиплексирование) из имён Mihomo в extra Xray:
    // без них узлы «обхода белых списков» теряли то, ради чего настроены.
    private static readonly Dictionary<string, string> XhttpNames = new()
    {
        ["x-padding-bytes"] = "xPaddingBytes", ["x-padding-obfs-mode"] = "xPaddingObfsMode", ["x-padding-key"] = "xPaddingKey",
        ["x-padding-header"] = "xPaddingHeader", ["x-padding-placement"] = "xPaddingPlacement", ["x-padding-method"] = "xPaddingMethod",
        ["uplink-http-method"] = "uplinkHTTPMethod", ["uplink-data-placement"] = "uplinkDataPlacement", ["uplink-data-key"] = "uplinkDataKey", ["uplink-chunk-size"] = "uplinkChunkSize",
        ["sc-max-each-post-bytes"] = "scMaxEachPostBytes", ["sc-min-posts-interval-ms"] = "scMinPostsIntervalMs",
        ["sc-max-buffered-posts"] = "scMaxBufferedPosts", ["no-grpc-header"] = "noGRPCHeader"
    };
    private static readonly Dictionary<string, string> XmuxNames = new()
    {
        ["max-connections"] = "maxConnections", ["max-concurrency"] = "maxConcurrency", ["c-max-reuse-times"] = "cMaxReuseTimes",
        ["c-max-lifetime-ms"] = "cMaxLifetimeMs", ["h-max-request-times"] = "hMaxRequestTimes", ["h-max-reusable-secs"] = "hMaxReusableSecs"
    };

    public static System.Text.Json.Nodes.JsonObject Extra(Dictionary<string, object?> opts)
    {
        static System.Text.Json.Nodes.JsonNode? Value(object? value) => value switch
        {
            "true" => true, "false" => false,
            string text when long.TryParse(text, out var number) => number,
            string text => text,
            _ => null
        };
        var extra = new System.Text.Json.Nodes.JsonObject();
        foreach (var (from, to) in XhttpNames) if (opts.TryGetValue(from, out var v) && Value(v) is { } node) extra[to] = node;
        if (opts.GetValueOrDefault("headers") is Dictionary<string, object?> headers)
            extra["headers"] = new System.Text.Json.Nodes.JsonObject(headers.Where(h => h.Value is string).Select(h => KeyValuePair.Create(h.Key, (System.Text.Json.Nodes.JsonNode?)(string)h.Value!)));
        if (opts.GetValueOrDefault("reuse-settings") is Dictionary<string, object?> reuse)
        {
            var xmux = new System.Text.Json.Nodes.JsonObject();
            foreach (var (from, to) in XmuxNames) if (reuse.TryGetValue(from, out var v) && Value(v) is { } node) xmux[to] = node;
            if (xmux.Count > 0) extra["xmux"] = xmux;
        }
        return extra;
    }
}
