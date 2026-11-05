namespace Oblachka;

internal static class SiteRuleInput
{
    internal static string Target(string value)
    {
        var input = value.Trim();
        var explicitScheme = input.Contains("://", StringComparison.Ordinal);
        if (!explicitScheme && (!input.Contains('/') || AdvancedOptions.TryParseSubnet(input, out _, out _)))
        {
            if (input.IndexOfAny(['?', '#']) < 0) return input;
        }
        var candidate = explicitScheme ? input : "https://" + input;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") ||
            uri.UserInfo.Length != 0 || uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6))
            throw new UserError("Вставьте ссылку на сайт, например https://example.com.");
        return uri.IdnHost;
    }
}
