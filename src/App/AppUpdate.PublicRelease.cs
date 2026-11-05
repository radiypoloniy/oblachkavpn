using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Oblachka;

public static partial class AppUpdate
{
    private const string ReleasePage = "https://github.com/" + Repository + "/releases";
    private const int MetadataLimit = 1024 * 1024;

    // The public release pages do not share the unauthenticated REST quota.
    // Asset metadata obtained here must still match the owner's signature.
    private static async Task<AppRelease?> CheckPublicReleaseAsync(HttpClient client, bool prerelease, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
            var deadline = timeout.Token;
            string[] tags;
            if (prerelease)
            {
                using var feed = await GetAsync(client, new Uri(ReleasePage + ".atom"), deadline);
                tags = ReadPublicTags(await PublicTextAsync(feed, deadline));
            }
            else
            {
                using var latest = await GetAsync(client, new Uri(ReleasePage + "/latest"), deadline, HttpMethod.Head);
                if (latest.StatusCode == HttpStatusCode.NotFound) return null;
                if (!latest.IsSuccessStatusCode) throw new HttpRequestException();
                var tag = PublicTag(latest.RequestMessage?.RequestUri?.AbsoluteUri, prerelease: false);
                tags = tag is null ? [] : [tag];
            }
            foreach (var tag in tags)
            {
                using var assets = await GetAsync(client, new Uri(ReleasePage + "/expanded_assets/" + tag), deadline);
                if (assets.StatusCode == HttpStatusCode.NotFound) continue;
                var release = ReadPublicAssets(tag, await PublicTextAsync(assets, deadline));
                if (release is null) continue;
                using var header = await GetAsync(client, release.Installer, deadline, HttpMethod.Head);
                if (!header.IsSuccessStatusCode || header.Content.Headers.ContentLength is not long size ||
                    size <= 0 || size > MaxInstallerBytes) continue;
                release = release with { Size = size };
                if (release.Signature is not null) await VerifySignatureAsync(client, release, deadline);
                return release;
            }
            if (tags.Length == 0 || !prerelease) throw new HttpRequestException();
            return null;
        }
        catch (Exception error) when (!token.IsCancellationRequested &&
            error is HttpRequestException or IOException or System.Xml.XmlException or OperationCanceledException)
        {
            throw new UserError("GitHub ограничил запросы к обновлениям. Откройте страницу релизов или повторите проверку позже.");
        }
    }

    internal static string[] ReadPublicTags(string xml)
    {
        XNamespace atom = "http://www.w3.org/2005/Atom";
        return XDocument.Parse(xml).Root?.Elements(atom + "entry").SelectMany(e => e.Elements(atom + "link"))
            .Select(link => PublicTag((string?)link.Attribute("href"), prerelease: true))
            .OfType<string>().Distinct(StringComparer.Ordinal).Take(10).ToArray() ?? [];
    }

    private static string? PublicTag(string? address, bool prerelease)
    {
        var prefix = ReleasePage + "/tag/";
        if (address is null || !address.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var tag = address[prefix.Length..];
        var match = TagName.Match(tag);
        return match.Success && (prerelease || !match.Groups[2].Success) ? tag : null;
    }

    internal static AppRelease? ReadPublicAssets(string tag, string html)
    {
        var tagMatch = TagName.Match(tag);
        if (!tagMatch.Success || !Version.TryParse(tagMatch.Groups[1].Value, out var version)) return null;
        var prefix = "/" + Repository + "/releases/download/" + tag + "/";
        foreach (Match link in Regex.Matches(html, "href=\"" + Regex.Escape(prefix) + "([^\"<>]+)\""))
        {
            var name = link.Groups[1].Value;
            var installer = InstallerName.Match(name);
            if (!installer.Success || installer.Groups[1].Value != version.ToString()) continue;
            var signed = html.Contains("href=\"" + prefix + name + ReleaseSignature.Suffix + "\"", StringComparison.Ordinal);
            var digest = Regex.Match(html, "aria-label=\"Copy to clipboard digest for " + Regex.Escape(name) +
                "\"[^<>]*?\\svalue=\"sha256:([0-9a-fA-F]{64})\"").Groups[1].Value;
            if (digest.Length != 64) continue;
            var address = DownloadPrefix + tag + "/" + name;
            return new AppRelease(version, tag, new Uri(address), 0, digest.ToUpperInvariant(), "", new Uri(ReleasePage + "/tag/" + tag))
            {
                Revision = installer.Groups[2].Value, Prerelease = tagMatch.Groups[2].Success,
                Signature = signed ? new Uri(address + ReleaseSignature.Suffix) : null
            };
        }
        return null;
    }

    private static async Task<string> PublicTextAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MetadataLimit) throw new HttpRequestException();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        var buffer = new byte[MetadataLimit + 1];
        var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken: token);
        if (read > MetadataLimit) throw new HttpRequestException();
        return Encoding.UTF8.GetString(buffer, 0, read);
    }
}
