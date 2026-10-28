namespace Oblachka;

public static class ProfileSelection
{
    // Строка списка целиком: источник, имя и конфигурация. Сервис может отдать один вход
    // под двумя именами — «Авто — оптимальное подключение» и страну, — и для пользователя
    // это две разные строки, хотя конфигурация у них одна. Источник в ключе нужен на
    // случай, когда тот же узел пришёл и из подписки сервиса, и из сторонней.
    // Разделитель \u0000 в имени узла встретиться не может: управляющие символы
    // отбрасываются при разборе подписки.
    // Группа в ключе: один и тот же узел может прийти из двух сторонних подписок.
    public static string Identity(Profile profile) =>
        profile.Source + "\u0000" + profile.Group + "\u0000" + profile.Name + "\u0000" + Profiles.Fingerprint(profile);

    // Где эта запись в текущем списке. Сначала точное совпадение строки, потом только
    // конфигурация: так переименование узла в панели не теряет выбор, а одинаковые
    // конфигурации под разными именами не подменяют друг друга.
    public static int IndexOf(IReadOnlyList<Profile> profiles, Profile profile)
    {
        var identity = Identity(profile);
        for (var i = 0; i < profiles.Count; i++) if (Identity(profiles[i]) == identity) return i;
        var fingerprint = Profiles.Fingerprint(profile);
        for (var i = 0; i < profiles.Count; i++) if (Profiles.Fingerprint(profiles[i]) == fingerprint) return i;
        return -1;
    }

    public static int Find(IReadOnlyList<Profile> profiles, Profile? previous) =>
        previous is null ? 0 : Math.Max(0, Locate(profiles, previous));

    // Тот же сервер в новом списке или -1, если его там нет.
    public static int Locate(IReadOnlyList<Profile> profiles, Profile previous)
    {
        var found = IndexOf(profiles, previous);
        if (found >= 0) return found;
        // Key rotation can alter every credential. Use a unique server-provided name as fallback.
        var named = profiles.Select((p, i) => (p, i)).Where(x => x.p.Core == previous.Core && x.p.Name == previous.Name).ToArray();
        return named.Length == 1 ? named[0].i : -1;
    }
}
