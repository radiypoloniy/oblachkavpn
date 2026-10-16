namespace Oblachka;

// Куда сайт идёт сейчас: VPN выключен — всё напрямую; иначе правило, списки или zapret.
public enum SiteRoute { Off, Tunnel, Direct, Zapret, Block }

public enum SiteTone { Ok, Warning, Error }

// Итог «Почему не открывается сайт?»: что сказать человеку и какое правило предложить.
// Suggest — действие для кнопки (правило сайта); null — менять нечего.
public sealed record SiteVerdict(string Title, string Text, SiteTone Tone, SiteAction? Suggest)
{
    public static SiteVerdict Build(SitePath direct, SitePath vpn, SiteRoute route)
    {
        var viaDirect = route is SiteRoute.Direct or SiteRoute.Zapret or SiteRoute.Off;
        if (vpn.Stage == SiteStage.Skipped && direct.Stage == SiteStage.Skipped)
            return new("Проверить не получилось", "Напрямую: " + direct.Note + ". Через VPN: " + vpn.Note + ".", SiteTone.Warning, null);
        if (route == SiteRoute.Block)
            return new("Сайт заблокирован вашим правилом", "Он в правилах с действием «Блокировать». Уберите правило в «Настройки → Без VPN → Свои правила».", SiteTone.Warning, null);

        // Один путь не проверить — говорим только о том, что видели.
        if (direct.Stage == SiteStage.Skipped)
            return vpn.Opens
                ? new("Через VPN сайт открывается", "Напрямую проверить не получилось: " + direct.Note + ". " +
                    (route == SiteRoute.Tunnel ? "Сейчас сайт и так идёт через VPN. " + TunnelHint : "Пустим его через VPN."), SiteTone.Ok,
                    route == SiteRoute.Tunnel ? null : SiteAction.Tunnel)
                : new("Через VPN сайт не открывается", "Через VPN " + Explain(vpn) + ". Напрямую проверить не получилось: " + direct.Note + ".", SiteTone.Error, null);
        if (vpn.Stage == SiteStage.Skipped)
            return direct.Opens
                ? new("Напрямую сайт открывается", "Через VPN проверить не получилось: " + vpn.Note + ". " + BrowserHint, SiteTone.Ok, null)
                : new("Напрямую сайт не открывается", "Напрямую " + Explain(direct) + ". Через VPN проверить не получилось: " + vpn.Note + ".", SiteTone.Warning, null);

        if (direct.Opens && vpn.Opens)
            return route == SiteRoute.Tunnel
                ? new("Сайт открывается", "И напрямую, и через VPN. Сейчас он идёт через VPN — напрямую будет быстрее и не потратит трафик VPN. " + BrowserHint, SiteTone.Ok, SiteAction.Direct)
                : new("Сайт открывается", "И напрямую, и через VPN. " + BrowserHint, SiteTone.Ok, null);

        if (!direct.Opens && vpn.Opens)
        {
            var why = "Напрямую " + Explain(direct) + ".";
            return route switch
            {
                SiteRoute.Off => new("Блокировка в вашей сети", why + " Через VPN открывается — включите VPN.", SiteTone.Warning, SiteAction.Tunnel),
                SiteRoute.Zapret => new("Обход zapret не справляется", why + " Через VPN открывается: пустим сайт через VPN.", SiteTone.Warning, SiteAction.Tunnel),
                SiteRoute.Direct => new("Блокировка в вашей сети", why + " Сейчас сайт идёт напрямую — поэтому и не открывается. Через VPN открывается.", SiteTone.Warning, SiteAction.Tunnel),
                _ => new("Блокировка в вашей сети, VPN её обходит", why + " Сейчас сайт и так идёт через VPN. " + TunnelHint, SiteTone.Ok, null)
            };
        }

        if (direct.Opens && !vpn.Opens)
        {
            var why = vpn.Stage == SiteStage.Forbidden ? "Через VPN сайт отвечает «доступ запрещён»: он не пускает из-за границы. Так делают госуслуги, банки и магазины." :
                "Через VPN " + Explain(vpn) + ".";
            return viaDirect
                ? new("Сайт открывается напрямую", why + " Сейчас он идёт напрямую — всё в порядке. " + BrowserHint, SiteTone.Ok, null)
                : new("Сайт не пускает через VPN", why + " Напрямую открывается.", SiteTone.Warning, SiteAction.Direct);
        }

        // Не открывается нигде: чаще всего дело в самом сайте.
        if (direct.Stage == SiteStage.NoName && vpn.Stage is SiteStage.NoName or SiteStage.Address)
            return new("Такого сайта нет", "Имя сайта не находится ни напрямую, ни через VPN. Проверьте, нет ли опечатки.", SiteTone.Error, null);
        if (direct.Stage == SiteStage.ServerError || vpn.Stage == SiteStage.ServerError)
            return new("Сайт сломался сам", "Он отвечает ошибкой сервера (HTTP " + Math.Max(direct.Status, vpn.Status) + "). Это не блокировка: подождите, пока сайт починят.", SiteTone.Error, null);
        return new("Сайт не открывается ни напрямую, ни через VPN",
            "Напрямую " + Explain(direct) + ". Через VPN " + Explain(vpn) + ". Если это не опечатка, попробуйте другой сервер.", SiteTone.Error, null);
    }

    private const string BrowserHint = "Если в браузере он всё равно не открывается — обновите страницу без кэша (Ctrl+F5) или проверьте расширения.";
    private const string TunnelHint = "Если в браузере он не открывается, проверьте режим: в «Браузеры и программы» браузер должен брать системный прокси.";

    // Продолжение фразы «Напрямую …» / «Через VPN …».
    public static string Explain(SitePath path) => path.Stage switch
    {
        SiteStage.Ok => "открывается",
        SiteStage.NoName => "имя сайта не находится",
        SiteStage.FakeAddress => "DNS отдаёт адрес-заглушку — так провайдер прячет сайт",
        SiteStage.Address => "до адреса сайта не доходит соединение: адрес заблокирован или сайт выключен",
        SiteStage.Handshake => "соединение есть, но фильтр (DPI) обрывает его, увидев имя сайта",
        SiteStage.Certificate => "отвечает чужой сертификат — провайдер подменяет сайт",
        SiteStage.BlockPage => "провайдер показывает страницу блокировки",
        SiteStage.Stall => "сайт начинает отвечать и замирает — так фильтр душит трафик",
        SiteStage.Forbidden => "сайт отвечает «доступ запрещён» (HTTP 403)",
        SiteStage.ServerError => $"сайт отвечает ошибкой сервера (HTTP {path.Status})",
        _ => "проверить не получилось: " + path.Note
    };

    // Короткая подпись для строки пути.
    public static string Short(SitePath path) => path.Stage switch
    {
        SiteStage.Ok => $"открывается · {path.Milliseconds} мс",
        SiteStage.NoName => "имя не находится",
        SiteStage.FakeAddress => "адрес-заглушка в DNS",
        SiteStage.Address => "адрес недоступен",
        SiteStage.Handshake => "режет DPI по имени",
        SiteStage.Certificate => "подменён сертификат",
        SiteStage.BlockPage => "страница блокировки",
        SiteStage.Stall => "замирает на загрузке",
        SiteStage.Forbidden => "доступ запрещён (403)",
        SiteStage.ServerError => $"ошибка сайта ({path.Status})",
        _ => "не проверить"
    };
}
