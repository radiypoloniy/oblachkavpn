using System.Text.Json.Nodes;

namespace Oblachka;

public static partial class SelfTests
{
    private static async Task UpdateChecksAsync(Func<string, Func<Task>, Task> check)
    {
        await check("Updates: a release is accepted only with this repository's installer, matching version and SHA256 digest", () => Sync(() =>
        {
            const string digest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
            string Release(string tag = "v9.1.0", string name = "Oblachka-setup-9.1.0-abc1234.exe", string? url = null, string? sha = digest,
                long size = 90_000_000, bool draft = false, bool prerelease = false) => new JsonObject
            {
                ["tag_name"] = tag, ["draft"] = draft, ["prerelease"] = prerelease, ["body"] = "Что нового",
                ["html_url"] = "https://github.com/radiypoloniy/oblachkavpn/releases/tag/" + tag,
                ["assets"] = new JsonArray(
                    new JsonObject { ["name"] = "SHA256SUMS.txt", ["browser_download_url"] = "https://github.com/radiypoloniy/oblachkavpn/releases/download/" + tag + "/SHA256SUMS.txt", ["size"] = 100 },
                    new JsonObject { ["name"] = name, ["browser_download_url"] = url ?? "https://github.com/radiypoloniy/oblachkavpn/releases/download/" + tag + "/" + name, ["digest"] = sha, ["size"] = size })
            }.ToJsonString();

            var release = AppUpdate.Parse(Release())!;
            Assert(release.Version == new Version(9, 1, 0) && release.Size == 90_000_000 && release.FileName == "Oblachka-setup-9.1.0-abc1234.exe");
            Assert(release.Sha256 == digest[7..].ToUpperInvariant() && release.Notes == "Что нового" && AppUpdate.IsNewer(release));
            Assert(!AppUpdate.IsNewer(release with { Version = AppUpdate.Current }, AppUpdate.Current, "abc12345"));
            // Чужой адрес, нет контрольной суммы, другая версия в имени, черновик, слишком большой файл.
            Assert(AppUpdate.Parse(Release(url: "https://evil.invalid/Oblachka-setup-9.1.0-abc1234.exe")) is null);
            Assert(AppUpdate.Parse(Release(url: "https://github.com/someone/fork/releases/download/v9.1.0/Oblachka-setup-9.1.0-abc1234.exe")) is null);
            Assert(AppUpdate.Parse(Release(sha: null)) is null && AppUpdate.Parse(Release(sha: "sha1:abc")) is null);
            Assert(AppUpdate.Parse(Release(name: "Oblachka-setup-9.0.0-abc1234.exe")) is null);
            Assert(AppUpdate.Parse(Release(draft: true)) is null && AppUpdate.Parse(Release(prerelease: true)) is null);
            Assert(AppUpdate.Parse(Release(size: AppUpdate.MaxInstallerBytes + 1)) is null);
            Assert(AppUpdate.Parse(Release(tag: "nightly")) is null);
            Assert(AppUpdate.Parse("{\"tag_name\":\"v9.1.0\",\"assets\":[]}") is null);
        }));
        await check("Updates: pre-releases only on request, and a pre-release of the same version is told apart by revision", () => Sync(() =>
        {
            const string digest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
            JsonObject Release(string tag, string revision, bool prerelease) => new()
            {
                ["tag_name"] = tag, ["draft"] = false, ["prerelease"] = prerelease, ["body"] = "",
                ["html_url"] = "https://github.com/radiypoloniy/oblachkavpn/releases/tag/" + tag,
                ["assets"] = new JsonArray(new JsonObject
                {
                    ["name"] = "Oblachka-setup-9.1.0-" + revision + ".exe", ["digest"] = digest, ["size"] = 90_000_000,
                    ["browser_download_url"] = "https://github.com/radiypoloniy/oblachkavpn/releases/download/" + tag + "/Oblachka-setup-9.1.0-" + revision + ".exe"
                })
            };
            var rc = Release("v9.1.0-rc.2", "bbbbbbb", true);
            Assert(AppUpdate.Parse(rc.ToJsonString()) is null);
            var pre = AppUpdate.Parse(rc.ToJsonString(), prerelease: true)!;
            Assert(pre.Prerelease && pre.Version == new Version(9, 1, 0) && pre.Revision == "bbbbbbb");
            // Список: первый подходящий сверху; пробная с тегом -rc, даже без флага prerelease.
            var list = new JsonArray(new JsonObject { ["tag_name"] = "v9.2.0", ["draft"] = true }, Release("v9.1.0-rc.3", "ccccccc", false), rc).ToJsonString();
            var first = AppUpdate.ParseList(list)!;
            Assert(first.Tag == "v9.1.0-rc.3" && first.Prerelease);
            // Тот же номер: новее, только если ревизия другая; своя ревизия неизвестна — только по номеру.
            var v = new Version(9, 1, 0);
            Assert(AppUpdate.IsNewer(pre, v, "aaaaaaaa") && !AppUpdate.IsNewer(pre, v, "bbbbbbb1") && !AppUpdate.IsNewer(pre, v, "ревизия неизвестна"));
            Assert(AppUpdate.IsNewer(pre, new Version(9, 0, 5), "bbbbbbb1") && !AppUpdate.IsNewer(pre, new Version(9, 2, 0), "aaaaaaaa"));
            // Переход при скачивании — только на HTTPS-хосты GitHub.
            Assert(AppUpdate.TrustedHost(new Uri("https://release-assets.githubusercontent.com/x")) && AppUpdate.TrustedHost(new Uri("https://github.com/a")));
            Assert(!AppUpdate.TrustedHost(new Uri("http://github.com/a")) && !AppUpdate.TrustedHost(new Uri("https://githubusercontent.com.evil.invalid/a")));
        }));
    }
}
