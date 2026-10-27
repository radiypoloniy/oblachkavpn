using System.Security.Cryptography;
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
                long size = 90_000_000, bool draft = false, bool prerelease = false, string? sigUrl = null, long sigSize = 96)
            {
                var installer = url ?? "https://github.com/radiypoloniy/oblachkavpn/releases/download/" + tag + "/" + name;
                return new JsonObject
                {
                    ["tag_name"] = tag, ["draft"] = draft, ["prerelease"] = prerelease, ["body"] = "Что нового",
                    ["html_url"] = "https://github.com/radiypoloniy/oblachkavpn/releases/tag/" + tag,
                    ["assets"] = new JsonArray(
                        new JsonObject { ["name"] = "SHA256SUMS.txt", ["browser_download_url"] = "https://github.com/radiypoloniy/oblachkavpn/releases/download/" + tag + "/SHA256SUMS.txt", ["size"] = 100 },
                        new JsonObject { ["name"] = name, ["browser_download_url"] = installer, ["digest"] = sha, ["size"] = size },
                        new JsonObject { ["name"] = name + ".sig", ["browser_download_url"] = sigUrl ?? installer + ".sig", ["size"] = sigSize })
                }.ToJsonString();
            }

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
            // Подпись лежит рядом с установщиком того же релиза и не больше килобайта; без неё релиза нет.
            Assert(release.Signature == new Uri("https://github.com/radiypoloniy/oblachkavpn/releases/download/v9.1.0/Oblachka-setup-9.1.0-abc1234.exe.sig"));
            Assert(AppUpdate.Parse(Release(sigUrl: "https://github.com/radiypoloniy/oblachkavpn/releases/download/v9.0.0/Oblachka-setup-9.1.0-abc1234.exe.sig")) is null);
            Assert(AppUpdate.Parse(Release(sigSize: 0)) is null && AppUpdate.Parse(Release(sigSize: ReleaseSignature.MaxBytes + 1)) is null);
            // Подписи ещё нет — релиз виден («ждёт подписи»), но без адреса подписи его не поставить.
            var unsigned = JsonNode.Parse(Release())!.AsObject();
            unsigned["assets"]!.AsArray().RemoveAt(2);
            var waiting = AppUpdate.Parse(unsigned.ToJsonString())!;
            Assert(waiting.Signature is null && waiting.Version == new Version(9, 1, 0));
            Assert(Throws<UserError>(() => AppUpdate.DownloadAsync(waiting, null, CancellationToken.None).GetAwaiter().GetResult()));
        }));
        await check("Updates: the signature binds file name, size and SHA256 to the owner's P-256 key, and the client carries that key", () => Sync(() =>
        {
            const string name = "Oblachka-setup-9.1.0-abc1234.exe", sha = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
            using var owner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var wide = ECDsa.Create(ECCurve.NamedCurves.nistP384);
            string[] keys = [Convert.ToBase64String(owner.ExportSubjectPublicKeyInfo())];
            var good = Convert.ToBase64String(owner.SignData(ReleaseSignature.Payload(name, 90_000_000, sha), HashAlgorithmName.SHA256));
            Assert(ReleaseSignature.Verify(name, 90_000_000, sha, good + "\n", keys) && ReleaseSignature.Verify(name, 90_000_000, sha.ToLowerInvariant(), good, keys));
            // Другой файл, размер, хеш или версия в имени — подпись не подходит.
            Assert(!ReleaseSignature.Verify("Oblachka-setup-9.0.0-abc1234.exe", 90_000_000, sha, good, keys));
            Assert(!ReleaseSignature.Verify(name, 90_000_001, sha, good, keys) && !ReleaseSignature.Verify(name, 90_000_000, "1" + sha[1..], good, keys));
            // Чужой ключ, ключ не P-256, мусор вместо подписи или ключа.
            var foreign = Convert.ToBase64String(stranger.SignData(ReleaseSignature.Payload(name, 90_000_000, sha), HashAlgorithmName.SHA256));
            Assert(!ReleaseSignature.Verify(name, 90_000_000, sha, foreign, keys));
            var widePayload = Convert.ToBase64String(wide.SignData(ReleaseSignature.Payload(name, 90_000_000, sha), HashAlgorithmName.SHA256));
            Assert(!ReleaseSignature.Verify(name, 90_000_000, sha, widePayload, [Convert.ToBase64String(wide.ExportSubjectPublicKeyInfo())]));
            Assert(!ReleaseSignature.Verify(name, 90_000_000, sha, "не base64", keys) && !ReleaseSignature.Verify(name, 90_000_000, sha, good, ["!!", "AAAA"]));
            Assert(!ReleaseSignature.Verify(name, 90_000_000, sha, good, []));
            // В сборке есть хотя бы один ключ владельца, и каждый — P-256: иначе обновления не ставились бы вовсе.
            Assert(ReleaseSignature.Keys.Length is 1 or 2);
            foreach (var key in ReleaseSignature.Keys)
            {
                using var ecdsa = ECDsa.Create();
                ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key), out _);
                Assert(ecdsa.ExportParameters(false).Curve.Oid.Value == "1.2.840.10045.3.1.7");
            }
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
                }, new JsonObject
                {
                    ["name"] = "Oblachka-setup-9.1.0-" + revision + ".exe.sig", ["size"] = 96,
                    ["browser_download_url"] = "https://github.com/radiypoloniy/oblachkavpn/releases/download/" + tag + "/Oblachka-setup-9.1.0-" + revision + ".exe.sig"
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
