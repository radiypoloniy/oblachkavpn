using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Oblachka;

// Подпись обновлений своим ключом. Установщик ставится, только если рядом в релизе лежит
// «<имя установщика>.sig» — подпись ECDSA P-256 над именем, размером и SHA256 файла,
// сделанная офлайн-ключом владельца (scripts/release-key.cs). Кто захватит аккаунт GitHub
// или токен релизов, выложит файл, но не подпись: без закрытого ключа клиент его не примет.
public static class ReleaseSignature
{
    // Открытые ключи (SubjectPublicKeyInfo, Base64). Два — только на время смены ключа:
    // новый добавляется выпуском раньше, чем им начинают подписывать.
    internal static readonly string[] Keys =
    [
        // Ключ владельца, создан 26.10.2026.
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEMJxd2A1HcwBh+rFvUCp5qmk4P9F2zxSzENygbD7gPSnH2KYhHuUUVdKq4PpF8KoT7rkUTL4OwKp4Ybva5P5MXQ==",
    ];

    public const string Suffix = ".sig";
    public const int MaxBytes = 1024;
    private const string P256 = "1.2.840.10045.3.1.7";

    // Что подписано. Тот же текст собирает scripts/release-key.cs — менять только вместе.
    public static byte[] Payload(string fileName, long size, string sha256) => Encoding.UTF8.GetBytes(
        "oblachka-update\n" + fileName + "\n" + size.ToString(CultureInfo.InvariantCulture) + "\n" + sha256.ToUpperInvariant() + "\n");

    public static bool Verify(AppRelease release, string signature) => Verify(release.FileName, release.Size, release.Sha256, signature, Keys);

    internal static bool Verify(string fileName, long size, string sha256, string signature, IEnumerable<string> keys)
    {
        byte[] bytes;
        try { bytes = Convert.FromBase64String(signature.Trim()); }
        catch (FormatException) { return false; }
        var payload = Payload(fileName, size, sha256);
        foreach (var key in keys)
        {
            using var ecdsa = ECDsa.Create();
            try
            {
                ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key), out _);
                if (ecdsa.ExportParameters(false).Curve.Oid.Value != P256) continue;
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException) { continue; }
            if (ecdsa.VerifyData(payload, bytes, HashAlgorithmName.SHA256)) return true;
        }
        return false;
    }
}
