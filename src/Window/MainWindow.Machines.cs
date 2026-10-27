using System.Windows.Media;

namespace Oblachka;

// Строки серверов и машины (ServerMachines): записи одной подписки с одним адресом — одной строкой,
// протоколы — внутри (раскрываются плашкой «3 варианта» у названия). Выбрана машина (machineMode) — протокол Облачка берёт
// сама: тот, что работал в этой сети (machineMemory), иначе лучший по замеру. Выбран протокол
// в раскрытой машине — только он, как раньше.
public partial class MainWindow
{
    private bool machineMode;
    // Сеть (NetworkPolicy.Key) → машина (ServerMachines.Hash) → отпечаток записи, что в ней работала.
    private readonly Dictionary<string, Dictionary<string, string>> machineMemory = new();
    private readonly HashSet<string> expandedMachines = new();
    private const int MaxMachinesPerNetwork = 64;

    private IReadOnlyList<Profile> MachineOf(Profile item) =>
        importedProfiles.Where(p => ServerMachines.Key(p) == ServerMachines.Key(item)).DefaultIfEmpty(item).ToArray();

    private Profile MachineBest(IReadOnlyList<Profile> members)
    {
        var remembered = currentNetwork is { } net && machineMemory.TryGetValue(net.Key, out var known) && known.TryGetValue(ServerMachines.Hash(members[0]), out var print) ? print : null;
        return ServerMachines.Best(members, LatestMeasurement, remembered, DateTimeOffset.UtcNow);
    }

    // Связь через запись проверена — в этой сети машина работает этим протоколом.
    private void RememberWorking(Profile item)
    {
        if (currentNetwork is not { } net || item.IsFree || MachineOf(item).Count < 2) return;
        if (!machineMemory.TryGetValue(net.Key, out var known))
        {
            if (machineMemory.Count >= NetworkPolicy.MaxKnown) return;
            machineMemory[net.Key] = known = new();
        }
        var key = ServerMachines.Hash(item);
        var print = Profiles.Fingerprint(item);
        if (known.TryGetValue(key, out var old) && old == print || !known.ContainsKey(key) && known.Count >= MaxMachinesPerNetwork) return;
        known[key] = print;
        QueueStateSave();
    }

    private void RestoreMachines(ClientSnapshot snapshot)
    {
        machineMode = snapshot.MachinePick;
        machineMemory.Clear();
        foreach (var (net, known) in snapshot.MachineMemory ?? [])
            if (NetworkPolicy.IsKey(net) && machineMemory.Count < NetworkPolicy.MaxKnown)
                machineMemory[net] = known.Where(p => NetworkPolicy.IsKey(p.Key) && p.Value is { Length: 64 } v && v.All(char.IsAsciiHexDigit))
                    .Take(MaxMachinesPerNetwork).ToDictionary(p => p.Key, p => p.Value);
    }

    private void PickRow(ServerRow row)
    {
        machineMode = row.IsMachine;
        if (row.IsAuto) PickServer(null);
        else if (row.Members is { } members && row.IsMachine) PickServer(MachineBest(members));
        else PickServer(row.Profile);
    }

    // Автовыбор, затем избранные машины (без избранного — первые машины подписки), до пяти строк.
    // Выбранная, если её нет среди них, подставляется последней строкой.
    private IReadOnlyList<ServerRow> QuickRows()
    {
        var rows = new List<ServerRow> { new()
        {
            IsAuto = true, Name = "Автовыбор", Selected = autoSelect,
            Note = autoSelect && profile is not null ? "сейчас: " + profile.DisplayName : "самый быстрый из пула",
            Icon = TryFindResource("IcoAuto") as Geometry, IconOnAccent = true
        } };
        var favored = importedProfiles.Where(p => favorites.Contains(FavoriteKey(p))).ToList();
        // Без избранного — первые узлы текущей подписки (сервиса или той, где выбран узел).
        if (favored.Count == 0) favored = (CurrentGroup is { } current ? ProfilesOf(current.Id) : importedProfiles).ToList();
        foreach (var machine in ServerMachines.Group(favored).Take(4)) rows.Add(MachineRow(MachineOf(machine[0]), true));
        if (!autoSelect && profile is not null && !rows.Any(r => r.Selected))
        {
            if (rows.Count == 5) rows.RemoveAt(4);
            rows.Add(machineMode ? MachineRow(MachineOf(profile), true) : Row(profile));
        }
        return rows;
    }

    // Машина одной строкой; одна запись — обычная строка. Протокол на строке — тот, что сейчас
    // выбран, а если машина не выбрана, — тот, что Облачка взяла бы.
    private ServerRow MachineRow(IReadOnlyList<Profile> members, bool note)
    {
        if (members.Count < 2) return Row(members[0], note);
        // Выбранной машина считается и тогда, когда внутри руками выбран её протокол: свёрнутой она его не прячет.
        var chosen = profile is not null && !autoSelect ? members.FirstOrDefault(m => ProfileSelection.Identity(m) == ProfileSelection.Identity(profile)) : null;
        var lead = chosen ?? MachineBest(members);
        return Row(lead, note, members, chosen is not null);
    }

    // note: false — строка под заголовком своей подписки, повторять её имя незачем.
    private ServerRow Row(Profile item) => Row(item, true);
    private ServerRow Row(Profile item, bool note) => Row(item, note, null, false);
    // member — строка протокола в раскрытой машине siblings (её имя и подпись — ServerMachines.ShortName, Hint).
    private ServerRow Row(Profile item, bool note, IReadOnlyList<Profile>? members, bool machineSelected, IReadOnlyList<Profile>? siblings = null, bool last = false)
    {
        var now = DateTimeOffset.UtcNow;
        var measured = LatestMeasurement(item);
        // У работающего подключения свежее всего — последняя проверка связи через него.
        if ((measured is null || !ServerMeasurements.Fresh(measured, now)) && runningProfile is not null && ReferenceEquals(item, runningProfile) && lastProbe is { Success: true })
            measured = new(Profiles.Fingerprint(item), now, true, lastProbe.Milliseconds);
        var (latency, dot, hint) = ServerRow.Status(measured, now, checkingServer is not null && ReferenceEquals(item, checkingServer));
        var icon = ServerRow.IconFor(item);
        var member = siblings is not null;
        var current = !autoSelect && profile is not null && ProfileSelection.Identity(item) == ProfileSelection.Identity(profile);
        return new ServerRow
        {
            Profile = item, Members = members, IsMember = member, IsLastMember = last,
            Name = member ? ServerMachines.ShortName(item, siblings!) : members is null ? item.DisplayName : ServerMachines.Title(members),
            Hint = member ? ServerMachines.Hint(item) : "",
            Badge = member && current ? machineMode ? "сейчас" : "закреплён" : null,
            Pinned = members is not null && machineSelected && !machineMode ? "только " + ServerMachines.ShortName(item, members) : null,
            Note = note && !member ? GroupNote(item) : null,
            Flag = icon is null && !member ? FlagImages.Local(item.Country.Code) : null, Icon = member ? null : icon, IconMuted = !item.IsService,
            Latency = latency, LatencyDot = dot, LatencyHint = hint, Speed = ServerRow.SpeedText(measured, now),
            Protocol = ServerRow.ProtocolFor(item), Tuned = !member && serverTweaks.ContainsKey(ServerTweak.Key(item)), PingSteps = ServerRow.StepsFor(measured),
            Selected = members is not null ? machineSelected
                : current && (!machineMode || !member && MachineOf(item).Count < 2),
            Favorite = (members ?? [item]).Any(m => favorites.Contains(FavoriteKey(m))),
            Expanded = members is not null && expandedMachines.Contains(ServerMachines.Key(item))
        };
    }

    // Строки подписки для «Всех серверов»: машина, под ней — пояснение и её протоколы, если раскрыта.
    private IEnumerable<object> MachineRows(IEnumerable<Profile> shown, bool note)
    {
        foreach (var machine in ServerMachines.Group(shown))
        {
            var row = MachineRow(machine, note);
            yield return row;
            if (!row.IsMachine || !row.Expanded) continue;
            yield return new MachineNote
            {
                Members = machine, CanReset = row.Pinned is not null,
                Text = row.Pinned is not null ? "Закреплён " + ServerMachines.ShortName(row.Profile!, machine) + "." : "Облачка выбирает сама. Можно закрепить вручную:"
            };
            for (var i = 0; i < machine.Count; i++) yield return Row(machine[i], false, null, false, machine, last: i == machine.Count - 1);
        }
    }

    // «Пусть выбирает сама» под раскрытой машиной: снять закрепление протокола.
    private void UnpinMachine(MachineNote note)
    {
        machineMode = true;
        PickServer(MachineBest(note.Members));
    }

    // «Проверить» у машины — все её протоколы по очереди.
    private async Task CheckMachineAsync(ServerRow row)
    {
        foreach (var item in row.IsMachine ? row.Members! : [row.Profile!])
        {
            await CheckServerAsync(item);
            if (closed) return;
        }
    }

    private void ToggleMachine(ServerRow row)
    {
        var key = ServerMachines.Key(row.Profile!);
        if (!expandedMachines.Remove(key)) expandedMachines.Add(key);
        RenderSheet();
    }

    // Звезда машины — на всех её записях: избранное показывается машинами.
    private void ToggleFavorite(ServerRow row)
    {
        var members = row.Members ?? [row.Profile!];
        var on = !row.Favorite;
        foreach (var key in members.Select(FavoriteKey)) if (on) favorites.Add(key); else favorites.Remove(key);
        RenderSheet(); RefreshHome(); QueueStateSave();
    }
}
