using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Oblachka.Views;

namespace Oblachka;

public partial class MainWindow
{
    private void WireQuickRouting()
    {
        var quick = Routes.Quick;
        quick.Query.GotKeyboardFocus += (_, _) => { LoadAppCatalog(); RefreshQuickRouting(); };
        quick.Query.TextChanged += (_, _) => RefreshQuickRouting();
        quick.Query.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || QuickSite() is null) return;
            e.Handled = true;
            AddQuickSite(SiteAction.Tunnel);
        };
        quick.AddVpn.Click += (_, _) => AddQuickSite(SiteAction.Tunnel);
        quick.AddDirect.Click += (_, _) => AddQuickSite(SiteAction.Direct);
        quick.SiteMore.Click += (_, _) =>
        {
            if (QuickSite() is not { } site) return;
            ShowQuickActions(quick.SiteMore, () => AddQuickSite(SiteAction.Block),
                SiteTarget.Kind(site) == SiteTargetKind.Domain ? () => OpenQuickServer(site, null) : null);
        };
        quick.Browse.Click += (_, _) => OpenAppPicker();
        quick.Templates.Click += (_, _) => Routes.ShowServices();
        quick.SuggestionAdd.Click += (_, _) =>
        {
            var add = quickSuggestionSet == ruleSetName ? quickSuggestion : null;
            ClearQuickSuggestion();
            add?.Invoke();
        };
        quick.SuggestionDismiss.Click += (_, _) => ClearQuickSuggestion();
        quick.Apps.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((_, e) =>
        {
            if (e.OriginalSource is not Button { DataContext: AppRow row, Tag: string tag } button) return;
            if (tag == "more") ShowQuickActions(button, () => AddQuickApp(row.App, SiteAction.Block), () => OpenQuickServer(null, row.App));
            else AddQuickApp(row.App, tag == "direct" ? SiteAction.Direct : SiteAction.Tunnel);
        }));
    }

    private string? QuickSite()
    {
        var query = Routes.RuleDomain.Text.Trim();
        if (query.Length == 0 || query.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        try { return RoutingPlan.Create(SiteRuleInput.Target(query), SiteAction.Tunnel).Domain; }
        catch (UserError) { return null; }
    }

    private void RefreshQuickRouting()
    {
        var quick = Routes.Quick;
        var query = quick.Query.Text.Trim();
        var site = QuickSite();
        quick.SiteResult.Visibility = site is null ? Visibility.Collapsed : Visibility.Visible;
        quick.SiteName.Text = site is null ? "" : "Сайт · " + site;
        AppRow[] matches = site is not null || query.Length == 0 || appCatalog is null ? [] :
            InstalledApps.Search(appCatalog.Select(r => r.App).ToArray(), query).Take(8)
                .Join(appCatalog, a => a, r => r.App, (_, row) => row).ToArray();
        quick.Apps.ItemsSource = matches;
        quick.ResultNote.Text = site is not null || query.Length == 0 || matches.Length > 0 ? "" :
            appCatalog is null ? "Ищем программы на компьютере…" : "Совпадений нет. Можно открыть список программ и выбрать файл вручную.";
        quick.ResultNote.Visibility = quick.ResultNote.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void AddQuickSite(SiteAction action)
    {
        Routes.ShowSites(true);
        var site = QuickSite();
        AddSiteRule(Routes.RuleDomain.Text, action);
        ClearQuickSuggestion();
        if (site is not null && EditedSites.Any(r => r.Domain == site && r.Action == action) &&
            RelatedApp(site) is { } app && !EditedPrograms.Any(r => string.Equals(r.Executable, app.Executable, StringComparison.OrdinalIgnoreCase)))
            ShowQuickSuggestion("Найдена связанная программа «" + app.Name + "».", () => AddQuickApp(app, action));
    }

    private void AddQuickApp(InstalledApp app, SiteAction action)
    {
        try
        {
            if (!AddProgramRules(AppRuleScope.For(app, action), editor: true)) return;
            Routes.ShowSites(false);
            Routes.RuleDomain.Clear();
            ClearQuickSuggestion();
            DetailText.Text = "Правило для «" + app.Name + "» сохранено.";
            _ = OfferObservedSiteAsync(app, site =>
            {
                if (EditedSites.All(r => r.Domain != site))
                    ShowQuickSuggestion("Добавить сайт " + site + " с тем же маршрутом?", () =>
                    {
                        Routes.ShowSites(true);
                        AddSiteRule(site, action);
                    });
            });
        }
        catch (UserError error) { DetailText.Text = error.Message; }
    }

    private static void ShowQuickActions(Button anchor, Action block, Action? server)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        if (server is not null)
        {
            var pinned = new MenuItem { Header = "Конкретный сервер" };
            pinned.Click += (_, _) => server();
            menu.Items.Add(pinned);
        }
        var reject = new MenuItem { Header = "Заблокировать" };
        reject.Click += (_, _) => block();
        menu.Items.Add(reject);
        menu.IsOpen = true;
    }

    private void OpenQuickServer(string? domain, InstalledApp? app)
    {
        if (ruleSetDraft is not null) { DetailText.Text = "Сначала сохраните новый набор."; return; }
        try
        {
            var file = app is null ? null : ProgramRoutingPlan.Create(app.Path, SiteAction.Tunnel, allInFolder: false);
            var key = domain ?? file!.TargetKey.ToLowerInvariant();
            var id = "custom-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16].ToLowerInvariant();
            Routes.ShowServices();
            LoadCustomServiceDraft(id, domain is null ? [] : [domain], file is null ? [] : [file]);
            if (domain is not null && RelatedApp(domain) is { } related && file is null && serviceFiles.Length == 0)
                ShowServiceSuggestion("Найдена связанная программа «" + related.Name + "».", () =>
                {
                    serviceFiles = [ProgramRoutingPlan.Create(related.Path, SiteAction.Tunnel, allInFolder: false)];
                    ShowServiceFiles();
                });
            else if (app is not null)
            {
                var draft = customServiceDraft;
                _ = OfferObservedSiteAsync(app, site =>
                {
                    if (!ReferenceEquals(draft, customServiceDraft)) return;
                    ShowServiceSuggestion("Добавить связанный сайт " + site + "?", () =>
                    {
                        customServiceDraft = draft! with { Domains = [site] };
                        Routes.ServiceDraftTargets.Text = site;
                    });
                });
            }
        }
        catch (UserError error) { DetailText.Text = error.Message; }
    }
    private void SaveTemplateRules(ServicePreset preset, SiteAction action)
    {
        try
        {
            var sites = EditedSites.Where(r => !preset.Domains.Contains(r.Domain))
                .Concat(preset.Domains.Select(domain => RoutingPlan.Create(domain, action))).ToArray();
            var programs = EditedPrograms.Where(r => !serviceFiles.Any(f => string.Equals(r.Key, f.Key, StringComparison.OrdinalIgnoreCase)))
                .Concat(serviceFiles.Select(f => f with { Action = action })).ToArray();
            RoutingPlan.Validate(sites);
            ProgramRoutingPlan.Validate(programs);
            if ((EditedSites.Any(r => preset.Domains.Contains(r.Domain) && r.Action != action) ||
                 EditedPrograms.Any(r => r.Action != action && serviceFiles.Any(f => string.Equals(r.Key, f.Key, StringComparison.OrdinalIgnoreCase)))) &&
                !AppDialog.Confirm(this, "Изменить действующие правила?", "Для целей шаблона будет установлено выбранное действие.", "Изменить")) return;
            EditedSites.Clear(); EditedSites.AddRange(sites);
            EditedPrograms.Clear(); EditedPrograms.AddRange(programs);
            RulesEdited();
            Routes.ShowSites(true);
            DetailText.Text = "Правила для «" + preset.Name + "» добавлены.";
        }
        catch (UserError error) { MessageBox.Show(this, error.Message, "Маршрутизация"); }
    }

}
