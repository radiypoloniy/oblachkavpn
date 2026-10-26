namespace Oblachka;

// Порядок попыток после отказа — по диагнозу (ConnectionDiagnosis), а не вслепую:
//   Address   — сначала узлы на других адресах: этот адрес не пропустит никого;
//   Handshake — тот же узел с разрезанным рукопожатием, затем узлы с другим именем сайта или
//               без TLS, затем остальные TLS-узлы — тоже с разрезанным рукопожатием;
//   Tunnel    — сначала другие транспорты (RAW → XHTTP → Hysteria2 …), в том числе на том же адресе;
//   Udp, Unknown — прежний порядок кандидатов (по отклику при «Автовыборе»).
// Упавший узел в любом случае не первый (кроме попытки с разрезанным рукопожатием).
// Внутри каждого шага сначала та же машина (ServerMachines): другой протокол того же сервера
// сохраняет страну и выходной адрес — сайты не разлогинивают и не спрашивают капчу.
// FragmentMode.Always — разрезать везде, где применимо; Off — нигде.
public sealed record LadderStep(int Index, bool Fragment);

public static class RouteLadder
{
    public const int MaxSteps = 8;

    // candidates — индексы в all в уже выбранном порядке (RouteCandidates); failed — упавший узел.
    public static IReadOnlyList<LadderStep> Plan(IReadOnlyList<Profile> all, IReadOnlyList<int> candidates, Profile failed, Blockage kind, FragmentMode mode)
    {
        var failedIndex = candidates.FirstOrDefault(i => ProfileSelection.Identity(all[i]) == ProfileSelection.Identity(failed), -1);
        var machine = ServerMachines.Key(failed);
        var others = candidates.Where(i => i != failedIndex).OrderBy(i => ServerMachines.Key(all[i]) == machine ? 0 : 1).ToList();
        var host = Host(failed);
        var steps = new List<LadderStep>();
        bool Can(int i) => mode != FragmentMode.Off && TlsFragment.Applies(all[i]);
        void Add(int i, bool fragment) { if (!steps.Contains(new(i, fragment))) steps.Add(new(i, fragment)); }
        switch (kind)
        {
            case Blockage.Address:
                foreach (var i in others.Where(i => Host(all[i]) != host)) Add(i, false);
                break;
            case Blockage.Handshake:
                var name = ConnectionDiagnosis.ServerName(failed);
                if (failedIndex >= 0 && Can(failedIndex)) Add(failedIndex, true);
                foreach (var i in others.Where(i => ConnectionDiagnosis.ServerName(all[i]) is not { } n || n != name)) Add(i, false);
                foreach (var i in others.Where(Can)) Add(i, true);
                break;
            case Blockage.Tunnel:
                var family = Family(failed);
                foreach (var i in others.Where(i => Family(all[i]) != family)) Add(i, false);
                foreach (var i in others) Add(i, false);
                break;
            default:
                foreach (var i in others) Add(i, false);
                break;
        }
        // Напоследок — сам упавший узел: вдруг сбой был мгновенным. При Auto — и с разрезанным рукопожатием.
        if (failedIndex >= 0)
        {
            if (kind != Blockage.Address) Add(failedIndex, false);
            if (kind is not (Blockage.Address or Blockage.Udp) && Can(failedIndex)) Add(failedIndex, true);
        }
        if (mode == FragmentMode.Always) steps = steps.Select(s => s with { Fragment = Can(s.Index) }).Distinct().ToList();
        return steps.Take(MaxSteps).ToArray();
    }

    private static string Host(Profile profile)
    {
        try { return ProfileServer.Of(profile).Host.ToLowerInvariant(); }
        catch (Exception ex) when (ex is NullReferenceException or InvalidOperationException) { return ""; }
    }

    // Семейство транспорта: протокол, сеть и защита. «vless/tcp/reality» и «vless/xhttp/reality» — разные.
    internal static string Family(Profile profile)
    {
        var o = profile.Outbound;
        return profile.Core == CoreKind.Xray
            ? o["protocol"]?.GetValue<string>() + "/" + (o["streamSettings"]?["network"]?.GetValue<string>() ?? "tcp") + "/" + (o["streamSettings"]?["security"]?.GetValue<string>() ?? "none")
            : o["type"]?.GetValue<string>() + "/" + (o["transport"]?["type"]?.GetValue<string>() ?? "tcp") + "/" + (o["tls"]?["reality"] is not null ? "reality" : o["tls"] is not null ? "tls" : "none");
    }
}
