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
        quick.Query.TextChanged += (_, _) =>
        {
            CloseRouteEditor();
            RefreshQuickRouting();
            if (!string.IsNullOrWhiteSpace(quick.Query.Text) && QuickSite() is null) LoadAppCatalog();
        };
        quick.Query.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || QuickSite() is null) return;
            e.Handled = true;
            AddQuickSite(SiteAction.Tunnel);
        };
        quick.Browse.Click += (_, _) => OpenAppPicker();
        quick.Templates.Click += (_, _) => OpenRouteTemplates();
        quick.SuggestionAdd.Click += (_, _) =>
        {
            var add = quickSuggestionSet == ruleSetName ? quickSuggestion : null;
            ClearQuickSuggestion();
            add?.Invoke();
        };
        quick.SuggestionDismiss.Click += (_, _) => ClearQuickSuggestion();
        quick.Results.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((_, e) =>
        {
            if (e.OriginalSource is not Button { DataContext: QuickRouteTarget row, Tag: string tag }) return;
            if (tag == "edit") { OpenQuickTarget(row); return; }
            if (tag is not ("direct" or "vpn")) return;
            var action = tag == "direct" ? SiteAction.Direct : SiteAction.Tunnel;
            if (row.Application is { } app) AddQuickApp(app.App, action);
            else AddQuickSite(action);
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
        QuickRouteTarget[] matches = site is not null ? [new(site)] : query.Length == 0 || appCatalog is null ? [] :
            InstalledApps.Search(appCatalog.Select(r => r.App).ToArray(), query).Take(8)
                .Join(appCatalog, a => a, r => r.App, (_, row) => new QuickRouteTarget(row.Name, row)).ToArray();
        quick.Results.ItemsSource = matches;
        quick.Results.Visibility = matches.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        quick.ResultCard.Visibility = query.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        quick.ResultNote.Text = site is not null ? "Результат поиска" : matches.Length > 0 ? "Найденные программы" :
            appCatalog is null ? "Ищем программы на компьютере…" : "Совпадений нет. Откройте «Все программы», чтобы выбрать файл вручную.";
    }

    private void AddQuickSite(SiteAction action)
    {
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
            Routes.RuleDomain.Clear();
            ClearQuickSuggestion();
            DetailText.Text = "Правило для «" + app.Name + "» сохранено.";
            _ = OfferObservedSiteAsync(app, site =>
            {
                if (EditedSites.All(r => r.Domain != site))
                    ShowQuickSuggestion("Добавить сайт " + site + " с тем же маршрутом?", () =>
                    {
                        AddSiteRule(site, action);
                    });
            });
        }
        catch (UserError error) { DetailText.Text = error.Message; }
    }

    private void OpenQuickTarget(QuickRouteTarget target)
    {
        var app = target.Application?.App;
        var domain = app is null ? target.Name : null;
        try
        {
            var file = app is null ? null : ProgramRoutingPlan.Create(app.Path, SiteAction.Tunnel, allInFolder: false);
            var key = domain ?? file!.TargetKey.ToLowerInvariant();
            var id = "custom-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16].ToLowerInvariant();
            var existing = domain is not null ? EditedSites.FirstOrDefault(rule => rule.Domain == domain)?.Action
                : EditedPrograms.FirstOrDefault(rule => string.Equals(rule.Key, file!.Key, StringComparison.OrdinalIgnoreCase))?.Action;
            LoadCustomServiceDraft(id, domain is null ? [] : [domain], file is null ? [] : [file], existing);
            Routes.ShowTargetEditor(target.Name, templates: false);
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
                    if (!ReferenceEquals(draft, customServiceDraft) || !Routes.TargetEditor.IsVisible) return;
                    ShowServiceSuggestion("Добавить связанный сайт " + site + "?", () =>
                    {
                        customServiceDraft = draft! with { Domains = [site] };
                        ShowRouteDomains([site]);
                    });
                });
            }
        }
        catch (UserError error) { DetailText.Text = error.Message; }
    }
    private bool SaveTargetRules(string name, string[] domains, SiteAction action, string? exceptPin = null)
    {
        try
        {
            if (domains.Length == 0 && serviceFiles.Length == 0) throw new UserError("Добавьте сайт или программу.");
            var sites = EditedSites.Where(r => !domains.Contains(r.Domain))
                .Concat(domains.Select(domain => RoutingPlan.Create(domain, action))).ToArray();
            var programs = EditedPrograms.Where(r => !serviceFiles.Any(f => string.Equals(r.Key, f.Key, StringComparison.OrdinalIgnoreCase)))
                .Concat(serviceFiles.Select(f => f with { Action = action })).ToArray();
            RoutingPlan.Validate(sites);
            ProgramRoutingPlan.Validate(programs);
            if (!ConfirmPinnedConflicts(sites.Where(r => domains.Contains(r.Domain)), serviceFiles, exceptPin)) return false;
            if ((EditedSites.Any(r => domains.Contains(r.Domain) && r.Action != action) ||
                 EditedPrograms.Any(r => r.Action != action && serviceFiles.Any(f => string.Equals(r.Key, f.Key, StringComparison.OrdinalIgnoreCase)))) &&
                !AppDialog.Confirm(this, "Изменить действующие правила?", "Для выбранных сайтов и программ будет установлено новое действие.", "Изменить")) return false;
            EditedSites.Clear(); EditedSites.AddRange(sites);
            EditedPrograms.Clear(); EditedPrograms.AddRange(programs);
            RulesEdited();
            DetailText.Text = "Правило для «" + name + "» сохранено.";
            return true;
        }
        catch (UserError error) { MessageBox.Show(this, error.Message, "Маршрутизация"); return false; }
    }

}
