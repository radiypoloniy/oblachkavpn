namespace RoutePilot;

public static class ProfileSelection
{
    public static int Find(IReadOnlyList<Profile> profiles, Profile? previous)
    {
        if (previous is null) return 0;
        var exact = profiles.ToList().FindIndex(p => Profiles.Fingerprint(p) == Profiles.Fingerprint(previous));
        if (exact >= 0) return exact;
        // Key rotation can alter every credential. Use a unique server-provided name as fallback.
        var named = profiles.Select((p, i) => (p, i)).Where(x => x.p.Core == previous.Core && x.p.Name == previous.Name).ToArray();
        return named.Length == 1 ? named[0].i : 0;
    }
}
