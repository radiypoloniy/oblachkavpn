using System.Windows;

namespace Oblachka;

public partial class MainWindow
{
    private static readonly (string Site, string Exe, string Observed)[] RelatedTargets =
    [
        ("claude.ai", "claude.exe", "api.anthropic.com"),
        ("chatgpt.com", "codex.exe", "chatgpt.com"),
        ("discord.com", "discord.exe", "discord.com"),
        ("steampowered.com", "steam.exe", "steampowered.com"),
        ("spotify.com", "spotify.exe", "spotify.com"),
        ("telegram.org", "telegram.exe", "telegram.org")
    ];
    private string? quickSuggestionSet;
    private Action? quickSuggestion, serviceSuggestion;

    private InstalledApp? RelatedApp(string site)
    {
        var match = RelatedTargets.FirstOrDefault(r => DomainMatches(site, r.Site));
        return match.Exe is null ? null : appCatalog?.Select(r => r.App).FirstOrDefault(
            a => string.Equals(a.Executable, match.Exe, StringComparison.OrdinalIgnoreCase));
    }

    private static bool DomainMatches(string host, string domain) =>
        string.Equals(host, domain, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);

    private async Task OfferObservedSiteAsync(InstalledApp app, Action<string> show)
    {
        var related = RelatedTargets.FirstOrDefault(r => string.Equals(app.Executable, r.Exe, StringComparison.OrdinalIgnoreCase));
        if (related.Site is null || systemTunnel is not { Stats: { } stats } session) return;
        var set = ruleSetName;
        var draft = ruleSetDraft;
        var connections = await CoreConnections.ReadAsync(stats, CancellationToken.None);
        if (closed || !RulesPage.IsVisible || !ReferenceEquals(session, systemTunnel) || set != ruleSetName || !ReferenceEquals(draft, ruleSetDraft)) return;
        if (connections?.Any(c => string.Equals(c.ProcessPath, app.Path, StringComparison.OrdinalIgnoreCase) &&
            c.Host is { } host && DomainMatches(host, related.Observed)) == true) show(related.Site);
    }

    private void ShowQuickSuggestion(string message, Action add)
    {
        quickSuggestionSet = ruleSetName;
        quickSuggestion = add;
        Routes.Quick.SuggestionText.Text = message;
        Routes.Quick.Suggestion.Visibility = Visibility.Visible;
    }

    private void ClearQuickSuggestion()
    {
        quickSuggestion = null;
        Routes.Quick.Suggestion.Visibility = Visibility.Collapsed;
    }

    private void ShowServiceSuggestion(string message, Action add)
    {
        serviceSuggestion = add;
        Routes.ServiceSuggestionText.Text = message;
        Routes.ServiceSuggestion.Visibility = Visibility.Visible;
    }

    private void ClearServiceSuggestion()
    {
        serviceSuggestion = null;
        Routes.ServiceSuggestion.Visibility = Visibility.Collapsed;
    }
}
