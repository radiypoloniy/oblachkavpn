using System.Security.Cryptography;
using System.Text;

namespace Oblachka;

// Группа подключений — вкладка в «Все серверы», как в Throne: подписка сервиса, любое число
// сторонних подписок (у каждой свои адреса, срок и трафик) и «Вручную» — ссылки, файлы, QR.
// Узел знает свою группу (Profile.Group). Повторное добавление той же подписки обновляет её
// группу, а не заводит вторую.
public sealed record ProfileGroup(string Id, string Name, ProfileSource Source, string[] Sources)
{
    public const string ServiceId = "service";
    public const string ManualId = "manual";
    public const int MaxGroups = 20;
    public const int MaxTotalProfiles = 600;

    public int PreferredSource { get; init; }
    public SubscriptionMetadata? Metadata { get; init; }
    public DateTimeOffset? Updated { get; init; }
    public bool IsManual => Id == ManualId;
    public bool IsSubscription => Sources.Length > 0;

    public static ProfileGroup Manual => new(ManualId, "Вручную", ProfileSource.External, Array.Empty<string>());

    // Одна и та же подписка — один идентификатор: по хосту и пути первого адреса, без запроса.
    public static string IdFor(IReadOnlyList<string> sources, ProfileSource source)
    {
        if (source == ProfileSource.Service) return ServiceId;
        var key = Uri.TryCreate(sources[0], UriKind.Absolute, out var uri) ? (uri.Host + uri.AbsolutePath).ToLowerInvariant() : sources[0];
        return "sub-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..12].ToLowerInvariant();
    }

    public static string NameFor(IReadOnlyList<string> sources, SubscriptionMetadata? metadata, ProfileSource source) =>
        source == ProfileSource.Service ? ServiceBrand.Name
        : metadata?.Title is { Length: > 0 } title ? ProfileGroups.Clean(title)
        : Uri.TryCreate(sources[0], UriKind.Absolute, out var uri) ? uri.Host : "Подписка";
}

public static class ProfileGroups
{
    // Порядок вкладок: сервис, сторонние подписки в порядке добавления, «Бесплатные», «Вручную» последней.
    public static IReadOnlyList<ProfileGroup> Ordered(IEnumerable<ProfileGroup> groups) =>
        groups.OrderBy(g => g.Id == ProfileGroup.ServiceId ? 0 : g.IsManual ? 3 : g.Source == ProfileSource.Free ? 2 : 1).ToArray();

    // Ручные подключения добавляются к прежним; одинаковые (имя и конфигурация) не повторяются.
    public static IReadOnlyList<Profile> Append(IReadOnlyList<Profile> existing, IEnumerable<Profile> added)
    {
        var seen = existing.Select(p => p.Name + "\u0000" + Profiles.Fingerprint(p)).ToHashSet();
        var result = existing.ToList();
        foreach (var profile in added)
            if (seen.Add(profile.Name + "\u0000" + Profiles.Fingerprint(profile))) result.Add(profile with { Source = ProfileSource.External, Group = ProfileGroup.ManualId });
        return result;
    }

    // Снимок до групп (0.10): узлы сервиса, одна сторонняя подписка (ExternalSources) и ручные
    // узлы вперемешку. Сторонние узлы при ExternalSources — подписка, без них — «Вручную».
    public static IReadOnlyList<ProfileGroup> FromLegacy(ClientSnapshot snapshot, IReadOnlyList<Profile> restored)
    {
        var groups = new List<ProfileGroup>();
        if (snapshot.SubscriptionSources.Length > 0 || restored.Any(p => p.IsService))
            groups.Add(new(ProfileGroup.ServiceId, ServiceBrand.Name, ProfileSource.Service, snapshot.SubscriptionSources)
                { PreferredSource = snapshot.PreferredSource, Metadata = snapshot.Metadata });
        if (restored.Any(p => !p.IsService))
            groups.Add(snapshot.ExternalSources.Length > 0
                ? new(ProfileGroup.IdFor(snapshot.ExternalSources, ProfileSource.External), ProfileGroup.NameFor(snapshot.ExternalSources, null, ProfileSource.External), ProfileSource.External, snapshot.ExternalSources)
                : ProfileGroup.Manual);
        return groups;
    }

    // Имя группы в правиле и на экране: не длиннее 40 знаков и без управляющих символов.
    public static string Clean(string? name) => SubscriptionMetadata.Clean(name) is { } clean ? clean.Length > 40 ? clean[..40] : clean : "Подписка";
}
