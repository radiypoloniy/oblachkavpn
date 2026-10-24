using System.Text.Json.Nodes;

namespace Oblachka;

// Заслоны, пока трафик идёт через бесплатный чужой узел (FreeServers, Profile.IsFree):
//   TCP 80 закрыт — незашифрованные сайты владелец узла мог бы читать и подменять. Заодно не
//     откроются страницы входа в Wi-Fi: об этом говорит экран согласия;
//   домены сервиса закрыты — ни подписка, ни личный кабинет через чужой узел не ходят: его
//     владелец не узнает адрес подписки и то, что человек — клиент сервиса.
// DNS в «Весь компьютер» и так идёт DoH внутри туннеля (SingBoxTunConfig), отдельного заслона не нужно.
// В «Весь компьютер» заслоны ставит ядро туннеля (аренда ForeignNode и правила сайтов), в
// «Браузеры и программы» — ядро окна (RoutingPlan.Compile).
public static class ForeignGuard
{
    public static IReadOnlyList<string> Domains =>
        ServiceBrand.ServiceDomains.Concat(ServiceBrand.LearnedHosts).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public static IReadOnlyList<SiteRule> SiteRules => Domains.Select(d => RoutingPlan.Create(d, SiteAction.Block)).ToArray();

    // Заслоны впереди своих правил человека: правило «сайт сервиса через VPN» их не обходит.
    public static SiteRule[] With(IEnumerable<SiteRule> rules, int limit)
    {
        var guard = SiteRules;
        return guard.Concat(rules.Where(r => !guard.Any(g => g.Domain == r.Domain))).Take(Math.Max(limit, guard.Count)).ToArray();
    }

    public static JsonObject XrayHttpRule(string blockTag) =>
        new() { ["type"] = "field", ["network"] = "tcp", ["port"] = "80", ["outboundTag"] = blockTag };

    public static JsonObject SingBoxHttpRule() =>
        new() { ["network"] = "tcp", ["port"] = 80, ["action"] = "reject" };
}
