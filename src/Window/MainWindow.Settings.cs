using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Shapes;

namespace Oblachka;

// Настройки нового дизайна (экраны 10–13): режим, тема, карточка подписки, правила и помощь.
// Прежние переключатели и логика остаются: вкладки лишь показывают их по-новому.
public partial class MainWindow
{
    private void WireSettings()
    {
        Live.ModeTun.Click += async (_, _) => await SetModeAsync(tunnel: true);
        Live.ModeProxy.Click += async (_, _) => await SetModeAsync(tunnel: false);
        Live.RulesOpen.Click += (_, _) => ShowRulesSection();
        Live.ConflictOpen.Click += (_, _) => ShowRulesSection();
        Settings.General.ThemeSystem.Click += (_, _) => ChooseTheme(ThemeChoice.System);
        Settings.General.ThemeLight.Click += (_, _) => ChooseTheme(ThemeChoice.Light);
        Settings.General.ThemeDark.Click += (_, _) => ChooseTheme(ThemeChoice.Dark);
        Settings.Subscription.Remember.Checked += (_, _) => { rememberConnections = true; QueueStateSave(); };
        Settings.Subscription.Remember.Unchecked += (_, _) => rememberConnections = false;
        Settings.Subscription.ChangeKeyButton.Click += SubscriptionClick;
        Settings.Subscription.RefreshListButton.Click += RefreshClick;
        Settings.Subscription.RenewButton.Click += (_, _) => OpenLink(RenewUrl, "страницу продления");
        Settings.Subscription.SupportButton.Click += (_, _) => OpenLink(subscriptionMetadata?.SupportUrl, "страницу поддержки");
        Routes.RuleList.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(RuleRowClick));
        Routes.ProgramList.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(RuleRowClick));
        Diagnostics.WhereInput.TextChanged += (_, _) => UpdateWhere();
    }

    // Сегмент переключателя из нескольких кнопок (тема, обход DPI).
    private static void MarkSegment(System.Windows.Controls.Button button, bool on)
    {
        // Выбранный сегмент — чернильный (облик 0.20).
        if (on) { button.SetResourceReference(BackgroundProperty, "Ink"); button.SetResourceReference(ForegroundProperty, "OnInk"); }
        else { button.Background = System.Windows.Media.Brushes.Transparent; button.SetResourceReference(ForegroundProperty, "Mu"); }
    }

    private void ChooseTheme(ThemeChoice choice)
    {
        ThemeManager.Apply(choice);
        RefreshSettings();
        QueueStateSave();
    }

    // Состояние вкладок по текущим данным: вызывается при открытии и после изменений.
    private void RefreshSettings()
    {
        if (!viewsReady) return;
        var general = Settings.General;
        var tunnel = SystemTunnelWanted;
        MarkSegment(Live.ModeTun, tunnel);
        MarkSegment(Live.ModeProxy, !tunnel && Settings.SystemProxy.IsChecked == true);
        Live.ModeTun.IsEnabled = Settings.SystemTunnel.IsEnabled;
        RefreshSettingsSummary();
        foreach (var (button, choice) in new[] { (general.ThemeSystem, ThemeChoice.System), (general.ThemeLight, ThemeChoice.Light), (general.ThemeDark, ThemeChoice.Dark) })
            MarkSegment(button, ThemeManager.Choice == choice);
        RefreshDpi();

        var tab = Settings.Subscription;
        var meta = subscriptionMetadata;
        tab.SubscriptionTitle.Text = CurrentGroup?.Name ?? "Подписка";
        tab.ExpiryText.Text = meta?.Expires is { } expires ? expires.ToLocalTime().ToString("dd.MM.yyyy") : "без срока";
        tab.TrafficText.Text = meta is { Total: > 0, Upload: >= 0, Download: >= 0 }
            ? $"{Math.Max(0m, (decimal)meta.Total!.Value - meta.Upload!.Value - meta.Download!.Value) / 1073741824m:0.#} ГБ"
            : meta is { Total: 0 } ? "без лимита" : "—";
        // «Продлить» и «Поддержка» — только если панель прислала ссылки: иначе кнопки вели бы в никуда.
        var renew = RenewUrl is not null;
        tab.SupportButton.Visibility = meta?.SupportUrl is not null ? Visibility.Visible : Visibility.Collapsed;
        tab.RenewButton.Visibility = renew ? Visibility.Visible : Visibility.Collapsed;
        tab.RenewColumn.Width = renew ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        tab.RefreshListButton.IsEnabled = groups.Any(g => g.IsSubscription) && operation is null;
        tab.Remember.IsChecked = rememberConnections;

        MarkCheck(Diagnostics.CoreOk, engine.ProcessId is not null);
        MarkCheck(Diagnostics.ProbeOk, lastProbe is { Success: true });
        UpdateWhere();
    }


    private static void MarkCheck(Ellipse mark, bool ok)
    {
        if (ok) { mark.SetResourceReference(Shape.FillProperty, "Ok"); mark.StrokeThickness = 0; }
        else { mark.Fill = System.Windows.Media.Brushes.Transparent; mark.SetResourceReference(Shape.StrokeProperty, "Ring"); mark.StrokeThickness = 1.5; }
    }

    // Куда ведёт «Продлить»: ссылка продления от панели (premium-url и др., ProviderOptions),
    // иначе личный кабинет из profile-web-page-url; для сервиса — его постоянный адрес, если он задан.
    private string? RenewUrl => subscriptionMetadata?.Provider?.RenewUrl ?? subscriptionMetadata?.WebPageUrl ??
        (ServiceBrand.SupportUrl.Length > 0 && CurrentGroup?.Source == ProfileSource.Service ? ServiceBrand.SupportUrl : null);

    private void OpenLink(string? url, string what)
    {
        if (SubscriptionMetadata.ParseLink(url) is not { } link) return;
        try { Process.Start(new ProcessStartInfo(link) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { DetailText.Text = "Не удалось открыть " + what + "."; }
    }

    // Метка действия меняется по кругу: через VPN → напрямую → блок. Крестик удаляет правило.
    private void RuleRowClick(object sender, RoutedEventArgs args)
    {
        if (args.OriginalSource is not Button { DataContext: RuleRow row } button) return;
        if (Equals(button.Tag, "delete")) RemoveRule(row.Rule);
        else if (Equals(button.Tag, "cycle")) CycleRule(row.Rule);
    }

    private void CycleRule(object rule)
    {
        static SiteAction Next(SiteAction action) => (SiteAction)(((int)action + 1) % 3);
        if (rule is SiteRule site && siteRules.IndexOf(site) is var i and >= 0) siteRules[i] = site with { Action = Next(site.Action) };
        else if (rule is ProgramRule program && programRules.IndexOf(program) is var j and >= 0) programRules[j] = program with { Action = Next(program.Action) };
        RefreshRules(); QueueStateSave();
        ProgramRulesChanged();
    }

    private void RemoveRule(object rule)
    {
        if (rule is SiteRule site) siteRules.Remove(site);
        else if (rule is ProgramRule program) programRules.Remove(program);
        RefreshRules(); QueueStateSave();
        ProgramRulesChanged();
    }

    // «Куда пойдёт сайт?»: самое длинное подходящее правило сайта, иначе — через VPN.
    private void UpdateWhere()
    {
        var host = Diagnostics.WhereInput.Text.Trim().TrimEnd('.').ToLowerInvariant();
        if (host.Length == 0) { Diagnostics.WhereResult.Text = ""; return; }
        var rule = siteRules.Where(r => host == r.Domain || host.EndsWith("." + r.Domain, StringComparison.Ordinal))
            .OrderByDescending(r => r.Domain.Length).FirstOrDefault();
        // Правило пользователя сильнее списков; списки — сильнее «всё остальное через VPN».
        var listed = rule is null ? whereRouting?.Decide(host) : null;
        var action = rule?.Action ?? listed ?? SiteAction.Tunnel;
        Diagnostics.WhereResult.Text = "→ " + RuleRow.ActionLabel(action) + (rule is not null ? " · " + rule.Domain : listed is not null ? " · по спискам" : "");
        Diagnostics.WhereResult.SetResourceReference(TextBlock.ForegroundProperty, action switch
        { SiteAction.Direct => "OkF", SiteAction.Block => "RF", _ => "Ac" });
    }
}
