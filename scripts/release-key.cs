// Офлайн-ключ подписи обновлений (ReleaseSignature в клиенте). Запуск: dotnet run scripts\release-key.cs -- <команда>
//   new <файл ключа>             — новый ключ ECDSA P-256, закрытый шифруется паролем (PKCS#8, AES-256, PBKDF2);
//                                   открытый печатается и ложится рядом в <файл>.pub — его вписывают в ReleaseSignature.Keys
//   protect <файл ключа>          — один раз: пароль с клавиатуры, ключ сохраняется на этом компьютере под
//                                   защитой Windows (DPAPI, только эта учётная запись); дальше подпись без пароля
//   sign <файл ключа|stored> <установщик> — <установщик>.sig рядом с файлом; stored — сохранённый protect
//   verify <файл .pub> <установщик> — проверить подпись тем же способом, что клиент
// Ключа нет на GitHub: кто получит доступ к репозиторию, подписать обновление не сможет.
#:package System.Security.Cryptography.ProtectedData@10.0.0
#pragma warning disable CA1416 // только Windows: ключ сохраняет DPAPI
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

if (args.Length < 2) return Fail("Команды: new <ключ> | protect <ключ> | sign <ключ|stored> <установщик> | verify <ключ.pub> <установщик>");
switch (args[0])
{
    case "new" when args.Length == 2:
    {
        var path = Path.GetFullPath(args[1]);
        if (File.Exists(path)) return Fail("Файл уже есть, перезаписывать ключ нельзя: " + path);
        var password = Ask("Пароль ключа (от 12 символов): ");
        if (password.Length < 12) return Fail("Пароль короче 12 символов.");
        if (Ask("Ещё раз: ") != password) return Fail("Пароли не совпали.");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = key.ExportEncryptedPkcs8PrivateKeyPem(password, new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 600_000));
        File.WriteAllText(path, pem + "\n");
        var pub = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        File.WriteAllText(path + ".pub", pub + "\n");
        Console.WriteLine("Ключ: " + path);
        Console.WriteLine("Открытый ключ (в ReleaseSignature.Keys): " + pub);
        return 0;
    }
    case "protect" when args.Length == 2:
    {
        using var key = ECDsa.Create();
        try { key.ImportFromEncryptedPem(File.ReadAllText(args[1]), Ask("Пароль ключа: ")); }
        catch (CryptographicException) { return Fail("Неверный пароль или это не файл ключа."); }
        Directory.CreateDirectory(Path.GetDirectoryName(StoredPath())!);
        File.WriteAllBytes(StoredPath(), ProtectedData.Protect(key.ExportPkcs8PrivateKey(), EntropyBytes(), DataProtectionScope.CurrentUser));
        Console.WriteLine("Ключ сохранён для этой учётной записи: " + StoredPath());
        Console.WriteLine("Открытый ключ: " + Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
        return 0;
    }
    case "sign" when args.Length == 3:
    {
        var (name, size, sha) = Describe(args[2]);
        using var key = ECDsa.Create();
        try
        {
            if (args[1] == "stored")
            {
                if (!File.Exists(StoredPath())) return Fail("Ключ не сохранён: сначала protect <файл ключа>.");
                key.ImportPkcs8PrivateKey(ProtectedData.Unprotect(File.ReadAllBytes(StoredPath()), EntropyBytes(), DataProtectionScope.CurrentUser), out _);
            }
            else key.ImportFromEncryptedPem(File.ReadAllText(args[1]), Ask("Пароль ключа: "));
        }
        catch (CryptographicException) { return Fail("Неверный пароль, не тот файл ключа или ключ сохранён другой учётной записью."); }
        var signature = Convert.ToBase64String(key.SignData(Payload(name, size, sha), HashAlgorithmName.SHA256));
        File.WriteAllText(args[2] + ".sig", signature);
        Console.WriteLine(name + "  " + size + "  " + sha);
        Console.WriteLine("Подпись: " + args[2] + ".sig");
        return 0;
    }
    case "verify" when args.Length == 3:
    {
        var (name, size, sha) = Describe(args[2]);
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(File.ReadAllText(args[1]).Trim()), out _);
        var ok = key.VerifyData(Payload(name, size, sha), Convert.FromBase64String(File.ReadAllText(args[2] + ".sig").Trim()), HashAlgorithmName.SHA256);
        Console.WriteLine(ok ? "Подпись верна." : "Подпись НЕ верна.");
        return ok ? 0 : 1;
    }
    default:
        return Fail("Неизвестная команда или не те аргументы.");
}

// Сохранённый ключ — вне репозитория, в профиле пользователя.
static byte[] EntropyBytes() => Encoding.UTF8.GetBytes("Oblachka.ReleaseKey.v1");
static string StoredPath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Oblachka-release", "release-key.dpapi");

// Тот же текст, что ReleaseSignature.Payload в клиенте — менять только вместе.
static byte[] Payload(string fileName, long size, string sha256) => Encoding.UTF8.GetBytes(
    "oblachka-update\n" + fileName + "\n" + size.ToString(CultureInfo.InvariantCulture) + "\n" + sha256.ToUpperInvariant() + "\n");

static (string Name, long Size, string Sha) Describe(string path)
{
    using var file = File.OpenRead(path);
    return (Path.GetFileName(path), file.Length, Convert.ToHexString(SHA256.HashData(file)));
}

static string Ask(string prompt)
{
    Console.Write(prompt);
    if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
    var text = new StringBuilder();
    for (var key = Console.ReadKey(true); key.Key != ConsoleKey.Enter; key = Console.ReadKey(true))
    {
        if (key.Key == ConsoleKey.Backspace) { if (text.Length > 0) text.Length--; }
        else if (!char.IsControl(key.KeyChar)) text.Append(key.KeyChar);
    }
    Console.WriteLine();
    return text.ToString();
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}
