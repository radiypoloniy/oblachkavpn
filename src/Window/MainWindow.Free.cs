using System.Windows;

namespace Oblachka;

// Бесплатные серверы «на свой риск» (FreeServers): согласие, загрузка списков, вкладка «Бесплатные»
// с выбором списка и плашка на главном экране, пока трафик идёт через чужой узел. Включаются только
// в «Настройки → Подключение» и больше нигде не предлагаются.
public partial class MainWindow
{
    // Версия текста согласия: новый текст — спросить заново (ClientSnapshot.FreeConsent).
    private const int FreeConsentVersion = 1;
    private int freeConsent;
    // null — выключены.
    private FreeList? freeList;
    private DateTimeOffset freeLoadedAt;
    private bool freeLoading;

    private const string FreeConsentTitle = "Бесплатные серверы — не наши";
    private const string FreeConsentText =
        "Их выкладывают неизвестные люди в открытых списках на GitHub. Мы их не запускаем, не проверяем и не отвечаем за них.\n\n" +
        "• Владелец такого сервера видит, какие сайты вы открываете, и ваш IP-адрес. Он может вести журналы и передавать их кому угодно.\n" +
        "• Не входите через них в банк, почту, Госуслуги и на любые сайты с паролями.\n" +
        "• Серверы живут часы, бывают медленными и пропадают без предупреждения.\n" +
        "• Чтобы снизить риск, мы закрываем через них незашифрованные сайты (http://) и не пускаем через них подключение к нашему сервису. " +
        "Некоторые сайты и страницы входа в Wi-Fi из-за этого не откроются.\n" +
        "• Бесплатные серверы не участвуют в автовыборе и никогда не включаются сами.";

    private void WireFree()
    {
        Settings.FreeServers.Click += async (_, _) => await ToggleFreeAsync(Settings.FreeServers.IsChecked == true);
        Servers.FreeKind.SelectionChanged += async (_, _) =>
        {
            if (freeList is null || Servers.FreeKind.SelectedIndex < 0 || (FreeList)Servers.FreeKind.SelectedIndex == freeList) return;
            freeList = (FreeList)Servers.FreeKind.SelectedIndex; QueueStateSave();
            await LoadFreeAsync(force: true);
        };
        Servers.GroupTabs.SelectionChanged += async (_, _) =>
        {
            RefreshFreePanel();
            if (FreeTabSelected) await LoadFreeAsync(force: groups.All(g => g.Id != FreeServers.GroupId));
        };
    }

    private void RestoreFree(ClientSnapshot snapshot)
    {
        freeConsent = snapshot.FreeConsent;
        freeList = freeConsent == FreeConsentVersion && Enum.TryParse<FreeList>(snapshot.FreeList, out var list) && Enum.IsDefined(list) ? list : null;
        Settings.FreeServers.IsChecked = freeList is not null;
        if (freeList is { } chosen) Servers.FreeKind.SelectedIndex = (int)chosen;
        // Узлы на диске не хранятся: без загрузки после запуска не было бы и вкладки «Бесплатные».
        if (freeList is not null) Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, async () => await LoadFreeAsync(force: true));
    }

    private async Task ToggleFreeAsync(bool on)
    {
        if (!on)
        {
            freeList = null; QueueStateSave();
            if (groups.Any(g => g.Id == FreeServers.GroupId)) RemoveGroup(FreeServers.GroupId);
            RefreshGroups();
            return;
        }
        if (freeConsent != FreeConsentVersion)
        {
            if (!AppDialog.Agree(this, FreeConsentTitle, FreeConsentText, "Я понимаю риски и включаю на свой страх и риск", "Включить"))
            { Settings.FreeServers.IsChecked = false; return; }
            freeConsent = FreeConsentVersion;
        }
        freeList ??= FreeList.Regular;
        Servers.FreeKind.SelectedIndex = (int)freeList;
        QueueStateSave(); RefreshGroups();
        await LoadFreeAsync(force: true);
    }

    // Список не старше часа не перезагружается; пока трафик идёт через бесплатный узел — тоже:
    // замена списка выбила бы его из-под подключения.
    private async Task LoadFreeAsync(bool force)
    {
        if (freeList is not { } list || freeLoading || closed) return;
        if (!force && DateTimeOffset.UtcNow - freeLoadedAt < FreeServers.MaxAge) return;
        if (ConnectionActive && runningProfile?.IsFree == true) return;
        freeLoading = true;
        FreeStatus("Загружаем список и проверяем, какие серверы отвечают…");
        DetailText.Text = "Загружаем бесплатные серверы («" + FreeServers.Label(list) + "») и проверяем, какие отвечают…";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var parsed = new List<Profile>();
            foreach (var file in FreeServers.Files(list)) parsed.AddRange(await FreeServers.FetchAsync(file, timeout.Token));
            var candidates = FreeServers.Prepare(parsed);
            var latency = new System.Collections.Concurrent.ConcurrentDictionary<Profile, long>();
            await ServerPing.PingAllAsync(candidates, (p, ms) => { if (ms is { } value) latency[p] = value; }, timeout.Token);
            if (closed || freeList != list) return;
            var best = FreeServers.Best(candidates, p => latency.TryGetValue(p, out var ms) ? ms : null);
            if (best.Count == 0) { FreeStatus(DetailText.Text = "Бесплатных серверов, которые отвечают, сейчас нет. Попробуйте другой список или позже."); return; }
            try { SetGroup(new(FreeServers.GroupId, FreeServers.GroupName, ProfileSource.Free, []) { Updated = DateTimeOffset.UtcNow }, best); }
            catch (UserError ex) { FreeStatus(DetailText.Text = ex.Message); return; }
            FreeStatus("");
            foreach (var p in best) MarkMeasured(ProfilesOf(FreeServers.GroupId).First(f => f.Name == p.Name), latency[p]);
            freeLoadedAt = DateTimeOffset.UtcNow;
            ApplyProfiles(preserveSelection: true);
            DetailText.Text = $"Бесплатных серверов: {best.Count} — лучшие по отклику из {candidates.Count}. Чужие серверы: их владельцы видят, какие сайты вы открываете.";
        }
        catch (OperationCanceledException) { if (!closed) FreeStatus(DetailText.Text = "Бесплатные серверы не загрузились за минуту. Откройте вкладку снова, чтобы повторить."); }
        finally { freeLoading = false; UpdateControls(); }
    }

    private bool FreeTabSelected => Servers.GroupTabs.Tag is string[] ids && Servers.GroupTabs.SelectedIndex > 0 &&
        Servers.GroupTabs.SelectedIndex - 1 < ids.Length && ids[Servers.GroupTabs.SelectedIndex - 1] == FreeServers.GroupId;

    // Строка состояния во вкладке «Бесплатные»: DetailText в облике 0.20 на экране не виден.
    private void FreeStatus(string text)
    {
        Servers.FreeStatus.Text = text;
        Servers.FreeStatus.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RefreshFreePanel() =>
        Servers.FreePanel.Visibility = FreeTabSelected && freeList is not null ? Visibility.Visible : Visibility.Collapsed;

    // Плашка, пока трафик идёт через бесплатный чужой узел.
    private void RefreshFreeWarning() =>
        Home.FreeWarn.Visibility = ConnectionActive && runningProfile?.IsFree == true ? Visibility.Visible : Visibility.Collapsed;
}
