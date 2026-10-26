// Офлайн-ключ подписи обновлений (ReleaseSignature в клиенте). Запуск: dotnet run scripts\release-key.cs -- <команда>
//   new <файл ключа>             — новый ключ ECDSA P-256, закрытый шифруется паролем (PKCS#8, AES-256, PBKDF2);
//                                   открытый печатается и ложится рядом в <файл>.pub — его вписывают в ReleaseSignature.Keys
//   sign <файл ключа> <установщик> — <установщик>.sig рядом с файлом
//   verify <файл .pub> <установщик> — проверить подпись тем же способом, что клиент
// Пароль спрашивается с клавиатуры. Файл ключа и пароль хранятся порознь и не на этом компьютере.
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

if (args.Length < 2) return Fail("Команды: new <ключ> | sign <ключ> <установщик> | verify <ключ.pub> <установщик>");
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
    case "sign" when args.Length == 3:
    {
        var (name, size, sha) = Describe(args[2]);
        using var key = ECDsa.Create();
        try { key.ImportFromEncryptedPem(File.ReadAllText(args[1]), Ask("Пароль ключа: ")); }
        catch (CryptographicException) { return Fail("Неверный пароль или это не файл ключа."); }
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
