using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Oblachka;

// «Помощь → Не открывается сайт?» (SiteProbe, SiteVerdict). Сайт проверяется двумя путями сразу:
// напрямую — из окна, а под «Весь компьютер» через вход «напрямую» ядра туннеля (окну прямые
// соединения закрыты защитой); через VPN — отдельным ядром с текущим сервером, как замер пинга.
// Текущее подключение не трогается. Итог — человеческим языком и правило сайта в один клик.
public partial class MainWindow
{
    private CancellationTokenSource? siteCheckCancel;
    private string? checkedHost;
    private SiteAction? suggestedAction;

    private void WireSiteCheck()
    {
        Diagnostics.SiteCheckOpenButton.Click += (_, _) => OpenSiteCheck();
        SiteCheck.BackButton.Click += (_, _) => { siteCheckCancel?.Cancel(); SiteCheck.Visibility = Visibility.Collapsed; };
        SiteCheck.CheckButton.Click += async (_, _) => await CheckSiteAsync();
        SiteCheck.SiteInput.KeyDown += async (_, e) => { if (e.Key == Key.Enter) await CheckSiteAsync(); };
        SiteCheck.SuggestButton.Click += async (_, _) => await ApplySiteSuggestionAsync();
    }

    private void OpenSiteCheck()
    {
        SiteCheck.Visibility = Visibility.Visible;
        SiteCheck.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
        SiteCheck.SiteInput.Focus();
    }

    private async Task CheckSiteAsync()
    {
        if (siteCheckCancel is not null) { siteCheckCancel.Cancel(); return; }
        var host = SiteProbe.NormalizeHost(SiteCheck.SiteInput.Text);
        if (host is null) { ShowSiteProgress("Введите адрес сайта, например chatgpt.com или https://chatgpt.com/."); return; }
        checkedHost = host;
        siteCheckCancel = new CancellationTokenSource();
        var token = siteCheckCancel.Token;
        SiteCheck.CheckButton.Content = "Остановить";
        SiteCheck.ResultPanel.Visibility = Visibility.Collapsed;
        ShowSiteProgress("Проверяем " + host + " напрямую и через VPN…");
        try
        {
            var server = runningProfile ?? profile;
            var results = await Task.WhenAll(CheckDirectAsync(host, token), CheckThroughVpnAsync(host, server, token));
            if (closed || token.IsCancellationRequested) return;
            var (route, why) = CurrentSiteRoute(host);
            ShowSiteResult(results[0], results[1], route, why, server);
        }
        catch (OperationCanceledException) { if (!closed) ShowSiteProgress("Проверка остановлена."); }
        finally
        {
            siteCheckCancel.Dispose(); siteCheckCancel = null;
            if (!closed) SiteCheck.CheckButton.Content = "Проверить";
        }
    }

    private void ShowSiteProgress(string text)
    {
        SiteCheck.Progress.Text = text;
        SiteCheck.Progress.Visibility = Visibility.Visible;
    }

    private Task<SitePath> CheckDirectAsync(string host, CancellationToken token)
    {
        if (!SystemTunnelActive) return SiteProbe.CheckAsync(host, null, token);
        return (engine as Engine)?.DirectEntry is { } entry
            ? SiteProbe.CheckAsync(host, new SocksRoute(entry.Port, entry.User, entry.Password), token)
            : Task.FromResult(SitePath.Skip("в «Весь компьютер» без сайтов «напрямую» защита не выпускает трафик мимо VPN"));
    }

    private async Task<SitePath> CheckThroughVpnAsync(string host, Profile? server, CancellationToken token)
    {
        if (server is null) return SitePath.Skip("не выбран сервер");
        if (MeasureRoute(server) is not { } route) return SitePath.Skip("адрес сервера закрыт защитой «Весь компьютер»");
        using var core = MeasureEngine();
        try
        {
            await core.StartAsync(route, token);
            return await SiteProbe.CheckAsync(host, new SocksRoute(core.Port), token);
        }
        catch (UserError) { return SitePath.Skip("сервер не запустился"); }
        finally { core.Stop(); }
    }

    // Куда сайт идёт сейчас: правило сильнее zapret, zapret — списков.
    private (SiteRoute Route, string Why) CurrentSiteRoute(string host) =>
        ConnectionActive ? SiteRouteByRules(host) : (SiteRoute.Off, "VPN выключен");

    internal (SiteRoute Route, string Why) SiteRouteByRules(string host)
    {
        static bool Covers(string domain, string host) => host == domain || host.EndsWith("." + domain, StringComparison.Ordinal);
        static SiteRoute Of(SiteAction action) => action switch { SiteAction.Direct => SiteRoute.Direct, SiteAction.Block => SiteRoute.Block, _ => SiteRoute.Tunnel };
        if (siteRules.Where(r => Covers(r.Domain, host)).OrderByDescending(r => r.Domain.Length).FirstOrDefault() is { } rule)
            return (Of(rule.Action), RuleRow.ActionLabel(rule.Action) + " · ваше правило");
        if (ZapretRouting && zapretDomains.Any(d => Covers(d, host))) return (SiteRoute.Zapret, "напрямую · zapret");
        if (whereRouting?.Decide(host) is { } listed) return (Of(listed), RuleRow.ActionLabel(listed) + " · по спискам");
        return (SiteRoute.Tunnel, "через VPN");
    }

    private void ShowSiteResult(SitePath direct, SitePath vpn, SiteRoute route, string why, Profile? server)
    {
        var verdict = SiteVerdict.Build(direct, vpn, route);
        SiteCheck.Progress.Visibility = Visibility.Collapsed;
        SiteCheck.ResultPanel.Visibility = Visibility.Visible;
        SiteCheck.VerdictTitle.Text = verdict.Title;
        SiteCheck.VerdictText.Text = verdict.Text;
        var (back, fore) = verdict.Tone switch { SiteTone.Ok => ("OkB", "OkF"), SiteTone.Warning => ("WB", "WF"), _ => ("EB", "EF") };
        SiteCheck.VerdictCard.SetResourceReference(Border.BackgroundProperty, back);
        SiteCheck.VerdictTitle.SetResourceReference(TextBlock.ForegroundProperty, fore);
        SiteCheck.VerdictText.SetResourceReference(TextBlock.ForegroundProperty, fore);
        ShowPath(SiteCheck.DirectResult, direct);
        ShowPath(SiteCheck.VpnResult, vpn);
        SiteCheck.VpnLabel.Text = server is null ? "Через VPN" : "Через VPN · " + server.DisplayName;
        SiteCheck.RouteResult.Text = why;
        suggestedAction = verdict.Suggest;
        var target = checkedHost is { } host && host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : checkedHost;
        SiteCheck.SuggestButton.Content = verdict.Suggest switch
        {
            SiteAction.Direct => "Открывать " + target + " напрямую",
            SiteAction.Tunnel => "Открывать " + target + " через VPN",
            _ => ""
        };
        var offer = verdict.Suggest is not null ? Visibility.Visible : Visibility.Collapsed;
        SiteCheck.SuggestButton.Visibility = offer; SiteCheck.SuggestNote.Visibility = offer;
        SiteCheck.SuggestButton.IsEnabled = true;
    }

    private static void ShowPath(TextBlock block, SitePath path)
    {
        block.Text = SiteVerdict.Short(path);
        block.SetResourceReference(TextBlock.ForegroundProperty, path.Opens ? "OkF" : path.Stage == SiteStage.Skipped ? "Mu" : "EF");
    }

    // Правило сайта на домен без «www.» (покрывает и поддомены) и сразу в дело: подключение
    // перезапускается, как при смене списков. Под «Весь компьютер» правила исполняет ядро туннеля,
    // поэтому там переподключение полное.
    private async Task ApplySiteSuggestionAsync()
    {
        if (checkedHost is null || suggestedAction is not { } action) return;
        var domain = checkedHost.StartsWith("www.", StringComparison.Ordinal) ? checkedHost[4..] : checkedHost;
        try
        {
            var rule = RoutingPlan.Create(domain, action);
            var index = siteRules.FindIndex(r => r.Domain == rule.Domain);
            if (index >= 0) siteRules[index] = rule;
            else if (siteRules.Count >= 100) throw new UserError("Достигнут предел 100 правил: удалите лишние в «Без VPN → Свои правила».");
            else siteRules.Add(rule);
        }
        catch (UserError ex) { ShowSiteProgress(ex.Message); return; }
        RefreshRules(); QueueStateSave(); ProgramRulesChanged();
        SiteCheck.SuggestButton.IsEnabled = false;
        if (!ConnectionActive) { ShowSiteProgress("Правило добавлено. Оно заработает, когда вы включите VPN."); return; }
        if (operation is not null || systemStopping) { ShowSiteProgress("Правило добавлено и заработает со следующего подключения."); return; }
        ShowSiteProgress("Правило добавлено. Переподключаемся, чтобы оно заработало…");
        recoveryWait?.Cancel();
        manualStop = true;
        if (systemTunnel is not null) await StopSystemTunnelAsync(); else engine.Stop();
        UpdateControls();
        if (!closed && ConnectButton.IsEnabled) await ConnectCoreAsync();
        if (!closed) ShowSiteProgress(ConnectionActive ? "Готово: правило работает. Обновите страницу в браузере." : "Правило добавлено, но подключиться заново не вышло — включите VPN.");
    }
}
