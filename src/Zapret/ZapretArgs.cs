using System.Text.RegularExpressions;

namespace Oblachka;

// Белый список аргументов winws: winws запускает служба с правами SYSTEM, поэтому из стратегии
// пропускается только то, что нужно для обхода. Нет --debug (запись файла), --daemon, --pidfile,
// --user и прочего. Файловые аргументы — только {BIN}/… и {LISTS}/… без «..»: после подстановки
// они указывают внутрь защищённой папки zapret.
public static class ZapretArgs
{
    private static readonly Regex Ports = new(@"^\d{1,5}(-\d{1,5})?(,\d{1,5}(-\d{1,5})?)*$", RegexOptions.CultureInvariant);
    private static readonly Regex Words = new(@"^[a-z0-9]+([,=.:_+-][a-z0-9]+)*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex Domains = new(@"^[a-z0-9.-]+(,[a-z0-9.-]+)*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex Hex = new(@"^0x[0-9a-f]{1,512}$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex File = new(@"^\{(BIN|LISTS)\}[A-Za-z0-9_.-]{1,80}$", RegexOptions.CultureInvariant);

    private enum Kind { Flag, Ports, Words, Domains, ListFile, Payload, Number }

    private static readonly Dictionary<string, Kind> Options = new(StringComparer.Ordinal)
    {
        ["--new"] = Kind.Flag, ["--wf-tcp"] = Kind.Ports, ["--wf-udp"] = Kind.Ports, ["--filter-tcp"] = Kind.Ports, ["--filter-udp"] = Kind.Ports,
        ["--filter-l3"] = Kind.Words, ["--filter-l7"] = Kind.Words,
        ["--hostlist"] = Kind.ListFile, ["--hostlist-exclude"] = Kind.ListFile, ["--ipset"] = Kind.ListFile, ["--ipset-exclude"] = Kind.ListFile,
        ["--hostlist-domains"] = Kind.Domains, ["--hostlist-exclude-domains"] = Kind.Domains, ["--ip-id"] = Kind.Words,
        ["--dpi-desync"] = Kind.Words, ["--dpi-desync-any-protocol"] = Kind.Number, ["--dpi-desync-badseq-increment"] = Kind.Words,
        ["--dpi-desync-cutoff"] = Kind.Words, ["--dpi-desync-fooling"] = Kind.Words, ["--dpi-desync-repeats"] = Kind.Number,
        ["--dpi-desync-split-pos"] = Kind.Words, ["--dpi-desync-split-seqovl"] = Kind.Number, ["--dpi-desync-fake-tls-mod"] = Kind.Words,
        ["--dpi-desync-hostfakesplit-mod"] = Kind.Words, ["--dpi-desync-ttl"] = Kind.Number, ["--dpi-desync-autottl"] = Kind.Words,
        ["--dpi-desync-fake-tls"] = Kind.Payload, ["--dpi-desync-fake-http"] = Kind.Payload, ["--dpi-desync-fake-quic"] = Kind.Payload,
        ["--dpi-desync-fake-discord"] = Kind.Payload, ["--dpi-desync-fake-stun"] = Kind.Payload, ["--dpi-desync-fake-unknown"] = Kind.Payload,
        ["--dpi-desync-fake-unknown-udp"] = Kind.Payload, ["--dpi-desync-split-seqovl-pattern"] = Kind.Payload, ["--dpi-desync-fakedsplit-pattern"] = Kind.Payload
    };

    public static void Validate(ZapretStrategy strategy)
    {
        if (strategy.Name.Length is 0 or > 40 || strategy.Name.Any(char.IsControl)) throw new UserError("zapret: неверное имя стратегии.");
        if (strategy.Args.Length is 0 or > 400) throw new UserError("zapret: неверная длина стратегии.");
        foreach (var arg in strategy.Args) Check(arg);
    }

    private static void Check(string arg)
    {
        var eq = arg.IndexOf('=');
        var name = eq < 0 ? arg : arg[..eq];
        var value = eq < 0 ? null : arg[(eq + 1)..];
        if (!Options.TryGetValue(name, out var kind)) throw new UserError("zapret: недопустимый параметр " + Safe(name) + ".");
        var ok = kind switch
        {
            Kind.Flag => value is null,
            Kind.Ports => value is not null && Ports.IsMatch(value),
            Kind.Words => value is not null && value.Length <= 200 && Words.IsMatch(value),
            Kind.Domains => value is not null && value.Length <= 2000 && Domains.IsMatch(value),
            Kind.Number => value is not null && value.Length <= 6 && value.All(char.IsAsciiDigit),
            Kind.ListFile => value is not null && File.IsMatch(value),
            // Подмена: файл из папки zapret, шестнадцатеричные байты или «!» — встроенная подмена winws.
            _ => value is not null && (File.IsMatch(value) || Hex.IsMatch(value) || value == "!")
        };
        if (!ok) throw new UserError("zapret: недопустимое значение параметра " + Safe(name) + ".");
    }

    // Аргументы для запуска: подстановки заменены абсолютными путями папок службы.
    public static string[] Resolve(ZapretStrategy strategy, string binDirectory, string listsDirectory)
    {
        Validate(strategy);
        return strategy.Args.Select(a => a.Replace(ZapretStrategy.Bin, binDirectory.TrimEnd('\\') + "\\", StringComparison.Ordinal)
            .Replace(ZapretStrategy.Lists, listsDirectory.TrimEnd('\\') + "\\", StringComparison.Ordinal)).ToArray();
    }

    // Файлы, на которые ссылается стратегия (относительно папки zapret): «bin/x.bin», «lists/y.txt».
    public static IEnumerable<string> Files(ZapretStrategy strategy) => strategy.Args
        .Select(a => a.IndexOf('=') is var eq and >= 0 ? a[(eq + 1)..] : "")
        .Where(v => File.IsMatch(v))
        .Select(v => (v.StartsWith(ZapretStrategy.Bin, StringComparison.Ordinal) ? "bin/" + v[ZapretStrategy.Bin.Length..] : "lists/" + v[ZapretStrategy.Lists.Length..]));

    private static string Safe(string text) => new(text.Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').Take(40).ToArray());
}
