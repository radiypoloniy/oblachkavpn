using System.Windows;

namespace Oblachka;

public partial class MainWindow
{
    // Превью «Серверов» с машиной «Нидерланды», раскрытой: три протокола под строкой. «-now» — она
    // выбрана и протокол Облачка взяла сама (метка «сейчас»), «-pinned» — протокол закреплён руками.
    private string MachinePreview(string page)
    {
        var machine = MachineOf(importedProfiles.First(p => p.DisplayName == "Нидерланды"));
        expandedMachines.Add(ServerMachines.Key(machine[0]));
        if (page != "servers-machine") { autoSelect = false; machineMode = page == "servers-now"; SelectProfile(page == "servers-now" ? MachineBest(machine) : machine[1]); }
        return "servers";
    }

    // Меню «Серверы ▾» набора правил отдельным снимком (всплывающие окна в снимок страницы не попадают).
    // any — «Любые серверы»; иначе в наборе одна страна — «Нидерланды».
    internal FrameworkElement RuleSetServersPreview(bool any)
    {
        LoadServerPreview();
        ruleSetServers = any ? null : [ServerMachines.Hash(importedProfiles.First(p => p.DisplayName == "Нидерланды"))];
        FillRuleSetServers();
        var card = (FrameworkElement)Routes.RuleSetServersPopup.Child;
        Routes.RuleSetServersPopup.Child = null;
        return card;
    }

    private void CheckServerList(ICollection<string> checks)
    {
        var selected = profile!;
        Navigate("Servers");
        ToggleFavorite(Row(selected));
        var sheet = Servers.SheetList.Items.Cast<object>().ToArray();
        var favoriteHeader = Array.FindIndex(sheet, i => i is SheetHeader { Title: "Избранное" });
        if (favoriteHeader < 0 || sheet[favoriteHeader + 1] is not ServerRow { Favorite: true } starred || starred.Profile != selected ||
            profile != selected || CaptureState().Favorites.Length != 1 || sheet[0] is not SheetHeader || Servers.AutoCard.Visibility != System.Windows.Visibility.Visible)
            throw new InvalidOperationException("Favorite star did not move the server into «Избранное» or was not captured for encrypted storage");
        Servers.SearchBox.Text = "no-synthetic-match";
        if (ProfilePicker.Items.Count != 0 || profile != selected || Servers.SheetList.Items.OfType<ServerRow>().Any())
            throw new InvalidOperationException("Empty search changed connection or kept rows");
        Servers.SearchBox.Clear();
        Servers.SortOrder.SelectedIndex = 1;
        if (ProfilePicker.Items.Count != 2 || profile != selected || engine.ProcessId is not null)
            throw new InvalidOperationException("Sorting changed selected connection or started a core");
        checks.Add("Favorites star, groups, search and sorting preserve the selected connection and do not start networking");
        measurements[Profiles.Fingerprint(selected)] = new(Profiles.Fingerprint(selected), DateTimeOffset.UtcNow.AddMinutes(-21), true, 12);
        RefreshServerLabels();
        if (!selected.ListStatus.Contains("устарело") || !selected.ListStatus.Contains("★"))
            throw new InvalidOperationException("Stale measurement or favorite marker missing");
        measurements.Clear();
        ToggleFavorite(Row(selected));
        Servers.SortOrder.SelectedIndex = 0;
        Navigate("Connection");
        if (Servers.Visibility == Visibility.Visible) throw new InvalidOperationException("Server sheet did not close");
        checks.Add("Server rows mark stale measurements and favorites without changing profile names");
        var report = CreateSupportReport();
        if (!report.Contains("Revision") || !report.Contains("LocalProxy") || report.Contains("example.com"))
            throw new InvalidOperationException("Support report omitted build identity or included a server address");
        checks.Add("Support report includes build identity and actual mode without server addresses");
    }

    private async Task CheckLargeServerList(ICollection<string> checks)
    {
        var nodes = Enumerable.Range(0, 47).Select(i => Profiles.Parse(
            $"vless://{i + 100:x8}-1111-4111-8111-111111111111@node{i}.invalid:443?security=tls&type=tcp#{Uri.EscapeDataString($"FI Server {i + 1}")}")).ToArray();
        await LoadSubscriptionAsync(["https://large.invalid/sub"], 0, (_, _, _) => Task.FromResult(new MirrorResult(new ImportResult(nodes, 0, 0), 0, 1)));
        Navigate("Servers");
        UpdateLayout();
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        var list = Servers.SheetList;
        var realized = Enumerable.Range(0, list.Items.Count)
            .Count(index => list.Items[index] is ServerRow && list.ItemContainerGenerator.ContainerFromIndex(index) is not null);
        var total = list.Items.OfType<ServerRow>().Count();
        if (total != 47 || realized is 0 or >= 47)
            throw new InvalidOperationException($"Large server list expected 47 rows with a virtualized viewport: {realized} of {total} rows realized");
        checks.Add("47-server subscription virtualizes row visuals and opens as a separate tab");
        RemoveGroup(groups.Single(g => g.Sources.Contains("https://large.invalid/sub")).Id);
        Navigate("Connection");
    }
}
