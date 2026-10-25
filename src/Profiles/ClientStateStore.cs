using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Oblachka;

public sealed record SavedProfile(string Name, JsonObject Outbound)
{
    // Источник появился в 0.9. В снимках прежних версий его нет, и тогда он выводится
    // по адресам подписки: см. RestoreProfiles.
    public ProfileSource? Source { get; init; }
    // Группа появилась в 0.11; в прежних снимках её выводит ProfileGroups.FromLegacy.
    public string? Group { get; init; }
}
public sealed record ClientSnapshot(int Version, SavedProfile[] Profiles, string? SelectedFingerprint,
    string[] SubscriptionSources, int PreferredSource, bool StayInTray, bool AutoRecover)
{
    // Имя выбранной записи рядом с её отпечатком: конфигурация может совпадать у двух
    // строк списка, например у «Авто» и страны-балансировщика за ним.
    public string? SelectedName { get; init; }
    // Адреса сторонней подписки. Адреса сервиса остались в SubscriptionSources.
    public string[] ExternalSources { get; init; } = Array.Empty<string>();
    public bool AutoRefresh { get; init; } = true;
    public bool SystemProxy { get; init; }
    // Сворачивание в трей при закрытии. Появилось в 0.9.2 и по умолчанию включено; прежний
    // StayInTray по умолчанию был выключен, и крестик закрывал приложение вместе с VPN.
    public bool? CloseToTray { get; init; }
    // Системный VPN (TUN). Не запоминался, и после каждого запуска его включали заново.
    // Нет значения — включён, если установлена сетевая служба.
    public bool? SystemTunnel { get; init; }
    // Тема оформления. Нет значения — «Как в Windows».
    public ThemeChoice? Theme { get; init; }
    public SubscriptionMetadata? Metadata { get; init; }
    public SiteRule[] RouteRules { get; init; } = Array.Empty<SiteRule>();
    public ProgramRule[] ProgramRules { get; init; } = Array.Empty<ProgramRule>();
    public ServerMeasurement[] Measurements { get; init; } = Array.Empty<ServerMeasurement>();
    public string[] Favorites { get; init; } = Array.Empty<string>();
    // Скрытые серверы подписок — те же ключи, что у избранного.
    public string[] HiddenServers { get; init; } = Array.Empty<string>();
    // Убранные подписки — идентификаторы групп.
    public string[] RemovedSubscriptions { get; init; } = Array.Empty<string>();
    // Маршрутизация сервиса (последняя присланная), решение по ней (нет значения — ещё не
    // спрашивали) и встроенный набор «Российские сайты напрямую».
    public RoutingProfile? ProviderRouting { get; init; }
    public bool? UseProviderRouting { get; init; }
    public bool RussiaDirect { get; init; }
    // «Автовыбор» на главном экране: переживает перезапуск.
    public bool AutoSelect { get; init; }
    // Проверка обновлений на GitHub раз в 12 часов (0.12). По умолчанию включена.
    public bool UpdateCheck { get; init; } = true;
    // Пробные версии (0.14.2): обновлялка предлагает и выпуски vX.Y.Z-rc.N. По умолчанию нет.
    public bool UpdatePrerelease { get; init; }
    // «Живое небо» в боковой панели; выключено — неподвижная картинка текущего часа.
    public bool LiveSky { get; init; } = true;
    // Обход DPI (0.13): Auto — разрезать рукопожатие, только когда обычное не проходит.
    public FragmentMode DpiBypass { get; init; }
    // zapret (0.13): включён ли и какая стратегия подошла в какой сети (хеш сети → имя стратегии).
    public bool ZapretEnabled { get; init; }
    // «Через VPN только YouTube и Discord» (MainWindow.Selective).
    public bool SelectiveVpn { get; init; }
    public Dictionary<string, string>? ZapretChoices { get; init; }
    // Группы zapret, которые человек оставил в VPN (0.17): «youtube», «discord».
    public string[]? ZapretVpnGroups { get; init; }
    // Пул «Автовыбора» (0.13.3): только отличия от умолчания, ключ — хеш источника и имени.
    public Dictionary<string, bool>? AutoPool { get; init; }
    // Подключаться при запуске (0.14.3): по умолчанию да, если при выходе VPN был включён (WasConnected).
    // WasConnected снимает только выключение VPN человеком; выключение компьютера и выход его не трогают.
    public bool ConnectOnLaunch { get; init; } = true;
    public bool WasConnected { get; init; }
    // Подписки и «Вручную» (0.11). Нет значения — снимок прежней версии.
    public ProfileGroup[]? Groups { get; init; }
    // Домены, на которые переехала подписка сервиса (0.18, ServiceBrand.LearnedHosts).
    public string[]? ServiceHosts { get; init; }
    // «Локальная сеть напрямую» в «Весь компьютер» (0.18). По умолчанию включена.
    public bool LocalNetwork { get; init; } = true;
    // «QUIC через TCP» в «Весь компьютер» (0.19). По умолчанию выключен.
    public bool QuicOverTcp { get; init; }
    // «Совместимость с играми» — стек туннеля mixed/1500 (0.19). По умолчанию выключена.
    public bool CompatibleStack { get; init; }
    // «Для продвинутых» (0.20). null — как без настройки.
    public AdvancedOptions? Advanced { get; init; }
    // Бесплатные серверы (0.19): версия принятого текста согласия (0 — не принят) и выбранный список.
    public int FreeConsent { get; init; }
    public string? FreeList { get; init; }
    // Трафик ядра туннеля за день (0.19): дата yyyy-MM-dd и байты в обе стороны.
    public string? TrafficDay { get; init; }
    // Тот же день по часам (0.20): 24 числа, байты; для столбиков в карточке «сегодня».
    public long[]? TrafficHours { get; init; }
    public long TrafficBytes { get; init; }
    // Ctrl+Alt+O показывает окно (0.19). По умолчанию включено.
    public bool Hotkey { get; init; } = true;
}

// The OS binds the encrypted file to the current Windows user. Never stage plaintext on disk.
public sealed class ClientStateStore(string path)
{
    private const int MaxSize = 2 * 1024 * 1024;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Oblachka.ClientState.v1");

    public void Save(ClientSnapshot snapshot)
    {
        Validate(snapshot);
        var plain = JsonSerializer.SerializeToUtf8Bytes(snapshot);
        try
        {
            if (plain.Length > MaxSize) throw new UserError("Список слишком большой для сохранения.");
            var encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
            Directory.CreateDirectory(folder);
            var staged = path + ".pending-" + Guid.NewGuid().ToString("N");
            using (var file = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(encrypted); file.Flush(true); }
            File.Move(staged, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        { throw new UserError("Не удалось сохранить подключения. Проверьте доступ к папке приложения."); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public ClientSnapshot? Load()
    {
        if (!File.Exists(path)) return null;
        byte[]? plain = null;
        try
        {
            if (new FileInfo(path).Length > MaxSize + 65536) throw new UserError("Сохранённые данные слишком большие.");
            plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            var snapshot = JsonSerializer.Deserialize<ClientSnapshot>(plain) ?? throw new UserError("Сохранённые данные пусты.");
            Validate(snapshot);
            return snapshot;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException or InvalidOperationException)
        { throw new UserError("Не удалось открыть сохранённые подключения. Добавьте подписку заново; исходный файл сохранён."); }
        finally { if (plain is not null) CryptographicOperations.ZeroMemory(plain); }
    }

    public ClientSnapshot? LoadWithMigration(IEnumerable<string> legacyFiles)
    {
        var existing = Load();
        if (existing is not null) return existing;
        foreach (var legacy in legacyFiles)
        {
            if (Path.GetFullPath(legacy).Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase) || !File.Exists(legacy)) continue;
            var snapshot = new ClientStateStore(legacy).Load();
            if (snapshot is null) continue;
            Save(snapshot);
            return snapshot;
        }
        return null;
    }

    public static IReadOnlyList<Profile> RestoreProfiles(ClientSnapshot snapshot)
    {
        // Снимок прежней версии источника не содержал. Выводим его из сохранённых адресов
        // подписки: список целиком с хостов сервиса — это узлы сервиса, иначе сторонние.
        var inferred = ServiceKey.SourceOf(snapshot.SubscriptionSources ?? Array.Empty<string>());
        return snapshot.Profiles.Select(saved =>
            Profiles.Parse(saved.Outbound.ToJsonString()) with { Name = saved.Name, Source = saved.Source ?? inferred, Group = saved.Group ?? "" }).ToArray();
    }

    private static void Validate(ClientSnapshot snapshot)
    {
        if (snapshot.Version != 1 || snapshot.Profiles is null || snapshot.Profiles.Length > ProfileGroup.MaxTotalProfiles ||
            snapshot.SubscriptionSources is null || snapshot.SubscriptionSources.Length > 5)
            throw new UserError("Формат сохранённых подключений не поддерживается.");
        if (snapshot.Profiles.Any(p => p is null || p.Outbound is null || string.IsNullOrWhiteSpace(p.Name) || p.Name.Length > 80 || p.Name.Any(char.IsControl)))
            throw new UserError("Сохранённые подключения повреждены.");
        if (snapshot.SubscriptionSources.Length > 0)
        {
            SubscriptionMirrors.ParseSources(string.Join("\n", snapshot.SubscriptionSources));
            if (snapshot.PreferredSource < 0 || snapshot.PreferredSource >= snapshot.SubscriptionSources.Length)
                throw new UserError("Сохранённый адрес подписки повреждён.");
        }
        if (snapshot.ExternalSources is null || snapshot.ExternalSources.Length > SubscriptionMirrors.MaxSources)
            throw new UserError("Сохранённые сторонние подписки повреждены.");
        if (snapshot.ExternalSources.Length > 0) SubscriptionMirrors.ParseSources(string.Join("\n", snapshot.ExternalSources));
        RestoreProfiles(snapshot);
        if (snapshot.Groups is { } groups)
        {
            if (groups.Length > ProfileGroup.MaxGroups || groups.Select(g => g?.Id).Distinct().Count() != groups.Length)
                throw new UserError("Сохранённые подписки повреждены.");
            foreach (var group in groups)
            {
                if (group is null || group.Id is null || group.Name is null || group.Sources is null ||
                    !(group.Id is ProfileGroup.ServiceId or ProfileGroup.ManualId || group.Id.Length == 16 && group.Id.StartsWith("sub-") && group.Id[4..].All(char.IsAsciiHexDigitLower)) ||
                    group.Name.Length > 40 || group.Name.Any(char.IsControl) || group.Sources.Length > SubscriptionMirrors.MaxSources ||
                    group.PreferredSource < 0 || group.Sources.Length > 0 && group.PreferredSource >= group.Sources.Length)
                    throw new UserError("Сохранённые подписки повреждены.");
                if (group.Sources.Length > 0) SubscriptionMirrors.ParseSources(string.Join("\n", group.Sources));
            }
            if (snapshot.Profiles.Any(p => p.Group is null || !groups.Any(g => g.Id == p.Group)))
                throw new UserError("Сохранённые подключения не относятся ни к одной подписке.");
        }
        if (snapshot.Favorites is null || snapshot.Favorites.Length > Subscriptions.MaxProfiles ||
            snapshot.Favorites.Any(f => f is null || f.Length != 64 || f.Any(c => !char.IsAsciiHexDigit(c))))
            throw new UserError("Сохранённое избранное повреждено.");
        if (snapshot.HiddenServers is null || snapshot.HiddenServers.Length > Subscriptions.MaxProfiles ||
            snapshot.HiddenServers.Any(f => f is null || f.Length != 64 || f.Any(c => !char.IsAsciiHexDigit(c))))
            throw new UserError("Сохранённый список скрытых серверов повреждён.");
        if (snapshot.RemovedSubscriptions is null || snapshot.RemovedSubscriptions.Length > 32 ||
            snapshot.RemovedSubscriptions.Any(id => id is not { Length: > 0 and <= 32 } || id.Any(c => !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'))))
            throw new UserError("Сохранённый список убранных подписок повреждён.");
        RoutingPlan.Validate(snapshot.RouteRules);
        ProgramRoutingPlan.Validate(snapshot.ProgramRules);
        if (snapshot.Measurements is null || snapshot.Measurements.Length > Subscriptions.MaxProfiles || snapshot.Measurements.Any(m => m is null ||
            m.Fingerprint is null || m.Fingerprint.Length != 64 || m.Fingerprint.Any(c => !char.IsAsciiHexDigit(c)) || m.Milliseconds < 0))
            throw new UserError("Сохранённые измерения повреждены.");
        if (snapshot.SelectedName is { } selectedName && (selectedName.Length > 80 || selectedName.Any(char.IsControl)))
            throw new UserError("Сохранённый выбор сервера повреждён.");
        // Тексты и ссылки пришли от сервера: в снимке они не должны вырасти, принести
        // управляющие символы или сменить HTTPS на что-то другое. У групп — так же.
        if (snapshot.ZapretChoices is { } choices && (choices.Count > 64 || choices.Any(c => c.Key.Length is 0 or > 32 || c.Value.Length is 0 or > 120 ||
            c.Key.Any(char.IsControl) || c.Value.Any(char.IsControl)))) throw new UserError("Сохранённые настройки zapret повреждены.");
        if (snapshot.ZapretVpnGroups is { } vpnGroups && (vpnGroups.Length > ZapretPlan.Groups.Length || vpnGroups.Any(g => !ZapretPlan.Groups.Any(z => z.Key == g))))
            throw new UserError("Сохранённые настройки zapret повреждены.");
        if (snapshot.ServiceHosts is { } hosts && (hosts.Length > ServiceBrand.MaxLearnedHosts ||
            hosts.Any(h => h is not { Length: > 0 and <= 253 } || Uri.CheckHostName(h) != UriHostNameType.Dns)))
            throw new UserError("Сохранённые адреса сервиса повреждены.");
        foreach (var info in (snapshot.Groups ?? Array.Empty<ProfileGroup>()).Select(g => g?.Metadata).Append(snapshot.Metadata))
            if (info is not null) SubscriptionMetadata.Validate(info);
    }
}
