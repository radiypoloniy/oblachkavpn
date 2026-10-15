using System.Reflection;

namespace Oblachka;

public static class BuildIdentity
{
    public static string Revision { get; } = ReadRevision();
    public static string Label => ServiceBrand.Label + " · " + Revision;

    private static string ReadRevision()
    {
        var version = typeof(BuildIdentity).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var revision = version?.Split('+').Skip(1).FirstOrDefault();
        return revision is not null && revision.Length >= 8 && revision.Take(8).All(char.IsAsciiHexDigit)
            ? revision[..8] + (revision.EndsWith("-dirty", StringComparison.Ordinal) ? " · с изменениями" : "") : "ревизия неизвестна";
    }
}
