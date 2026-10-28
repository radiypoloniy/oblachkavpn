using Microsoft.Win32;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Text.Json;
using Forms = System.Windows.Forms;

namespace Oblachka;

public partial class MainWindow
{
    // Вставленная ссылка и открытый файл — всегда сторонний источник: ключ сервиса
    // вводится на первом экране и проходит через ServiceKey.
    private void Import(string input)
    {
        try
        {
            var result = ShareLinks.IsLink(input.Trim()) && !input.Trim().Contains('\n')
                ? new ImportResult(new[] { Profiles.Parse(input) }, 0, 0) : Subscriptions.Parse(input);
            // Вставленное добавляется во «Вручную» к прежним; подписки не трогаются.
            ApplyGroupImport(ProfileGroup.Manual, result.WithSource(ProfileSource.External));
        }
        catch (UserError ex) { DetailText.Text = ex.Message; }
    }
    // Прежняя форма для проверок: узлы сервиса — в его группу, остальные — во «Вручную».
    private void ApplyImport(ImportResult result, bool preserveSelection = false) =>
        ApplyGroupImport(result.Profiles.Count > 0 && result.Profiles[0].Source == ProfileSource.Service
            ? (ServiceGroup ?? new(ProfileGroup.ServiceId, ServiceBrand.Name, ProfileSource.Service, Array.Empty<string>())) with { Metadata = result.Metadata }
            : ProfileGroup.Manual, result, preserveSelection);

    // Подписка заменяет свою группу целиком; «Вручную» пополняется. Остальные группы не трогаются.
    private void ApplyGroupImport(ProfileGroup group, ImportResult result, bool preserveSelection = false)
    {
        try
        {
            SetGroup(group, group.IsManual ? ProfileGroups.Append(ProfilesOf(ProfileGroup.ManualId), result.Profiles) : result.Profiles);
        }
        catch (UserError ex) { DetailText.Text = ex.Message; return; }
        if (result.Metadata?.Routing is { } routing) ReceiveRouting(routing);
        ApplyProfiles(preserveSelection);
        // Добавленное вручную сразу выбрано: его и собирались подключать.
        if (group.IsManual && !preserveSelection && result.Profiles.Count > 0 &&
            importedProfiles.FirstOrDefault(p => p.Group == ProfileGroup.ManualId && p.Name == result.Profiles[0].Name &&
                Profiles.Fingerprint(p) == Profiles.Fingerprint(result.Profiles[0])) is { } added)
            SelectProfile(added);
        DetailText.Text = $"Добавлено подключений: {result.Profiles.Count}.";
        if (result.Unsupported + result.Invalid > 0) DetailText.Text += $" Пропущено: неподдерживаемых {result.Unsupported}, некорректных {result.Invalid}.";
        if (result.Reasons.Count > 0) DetailText.Text += " Причины: " + string.Join(" | ", result.Reasons);
        UpdateControls();
        QueueStateSave();
    }

    private void ApplyProfiles(bool preserveSelection)
    {
        // Скрытые серверы подписок в список не попадают (MainWindow.ServerRemoval).
        var combined = groups.SelectMany(g => ProfilesOf(g.Id)).Where(p => !HiddenServer(p)).ToArray();
        RefreshGroups();
        Dispatcher.BeginInvoke(RenderAutoPool);
        if (combined.Length == 0)
        {
            // Убрали последнюю подписку: список пуст, главный экран снова предлагает ключ.
            ProfilePicker.ItemsSource = importedProfiles = Array.Empty<Profile>();
            profile = null; ShowProfile(); FilterProfiles(); UpdateSubscriptionSummary(); Navigate(currentPage);
            return;
        }
        var selection = ProfileSelection.Find(combined, preserveSelection ? profile : null);
        ProfilePicker.ItemsSource = combined;
        importedProfiles = combined;
        Servers.SearchBox.Clear();
        UpdateSubscriptionSummary();
        ProfilePicker.SelectedIndex = selection;
        profile = combined[selection];
        routeAttempts = Array.Empty<RouteAttempt>();
        ShowProfile();
        FilterProfiles();
        QueuePing();
    }
    private void ProfileSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (ProfilePicker.SelectedItem is not Profile selected) return;
        profile = selected; ShowProfile(); UpdateControls(); QueueStateSave();
        QueueServerSwitch();
    }
    private void ShowProfile()
    {
        ProfileNameText.Text = profile?.DisplayName ?? "Профиль ещё не добавлен";
        TransportText.Text = profile is null ? "VLESS-ссылка или клиентский Xray / sing-box JSON" : profile.Core + " · " + profile.Transport;
    }
    private async void SubscriptionClick(object sender, RoutedEventArgs args)
    {
        var url = AppDialog.Ask(this, "Адреса подписки", "Основной и резервные HTTPS-адреса: каждый с новой строки, до пяти.", "https://…", "Загрузить",
            string.Join("\n", subscriptionSources), "Полные ссылки для каждого адреса. Они хранятся в зашифрованном виде.", multiline: true);
        if (url is null) return;
        try { await LoadSubscriptionAsync(SubscriptionMirrors.ParseSources(url), 0); }
        catch (UserError ex) { if (!closed) DetailText.Text = ex.Message; }
    }
    // Обновляем все подписки: сервиса и сторонние.
    private async void RefreshClick(object sender, RoutedEventArgs args)
    {
        if (operation is null) await RefreshAllAsync();
    }
    private async Task LoadSubscriptionAsync(IReadOnlyList<string> sources, int preferred,
        Func<IReadOnlyList<string>, int, CancellationToken, Task<MirrorResult>>? loader = null)
    {
        if (operation is not null) return;
        // Через бесплатный чужой узел подписка сервиса не ходит (ForeignGuard): обновим после него.
        if (ForeignConnected && ServiceKey.SourceOf(sources) == ProfileSource.Service)
        { DetailText.Text = "Подписка сервиса обновится после отключения от бесплатного сервера."; return; }
        manualStop = false;
        operation = new CancellationTokenSource(); engineOperation = false; UpdateControls();
        DetailText.Text = "Загружаем подписку; при отказе проверим резервные адреса…";
        try
        {
            var result = await (loader ?? SubscriptionMirrors.FetchAsync)(sources, preferred, operation.Token);
            subscriptionSchedule.Complete(DateTimeOffset.UtcNow, true, result.Import.Metadata?.UpdateInterval);
            operation.Token.ThrowIfCancellationRequested();
            if (closed) return;
            var kind = ServiceKey.SourceOf(sources);
            // Подписка, переехавшая на новый адрес, остаётся той же группой: id не пересчитывается.
            var previous = groups.FirstOrDefault(g => g.Sources.SequenceEqual(sources)) ?? groups.FirstOrDefault(g => g.Id == ProfileGroup.IdFor(sources, kind));
            var id = previous?.Id ?? ProfileGroup.IdFor(sources, kind);
            var metadata = result.Import.Metadata ?? previous?.Metadata;
            var current = result.Sources ?? sources;
            var group = new ProfileGroup(id, ProfileGroup.NameFor(current, metadata, kind), kind, current.ToArray())
                { PreferredSource = result.SourceIndex, Metadata = metadata, Updated = DateTimeOffset.UtcNow };
            var imported = result.Import.WithSource(kind);
            // Под работающим подключением новый список виден сразу, подключение не трогается: выбор
            // остаётся на работающем сервере (переименование и смена ключей его не теряют). Новый сервер,
            // не вошедший в защиту при подключении, выбирается с переподключением (SwitchServerAsync).
            // Ждёт отключения только список, где работающего сервера нет вовсе: иначе выбор перескочил
            // бы на первый сервер и сам переключил подключение. До 0.21.1 ждал любой список.
            if (engine.State is ConnectionState.Running or ConnectionState.Verified && runningProfile is { } running &&
                running.Group == id && ProfileSelection.Locate(imported.Profiles, running) < 0)
            {
                pendingImport = (group, imported);
                DetailText.Text = $"Получено подключений: {result.Import.Profiles.Count}; адрес №{result.SourceIndex + 1}. Сервера, к которому вы подключены, в подписке больше нет: новый список применится после отключения.";
            }
            else
            {
                if (pendingImport?.Group.Id == id) pendingImport = null;
                ApplyGroupImport(group, imported, true); DetailText.Text += $" Загрузка: адрес №{result.SourceIndex + 1}, попыток {result.Attempts}.";
            }
            if (result.Moved) DetailText.Text += " Подписка переехала на новый адрес, прежний оставлен запасным.";
            if (result.Sources is not null) QueueStateSave();
        }
        catch (OperationCanceledException) { subscriptionSchedule.Complete(DateTimeOffset.UtcNow, false); if (!closed) DetailText.Text = "Загрузка отменена. Предыдущий список сохранён."; }
        catch (SubscriptionAccessDenied ex) { subscriptionSchedule.AccessDenied(); if (!closed) DetailText.Text = ex.Message; }
        catch (UserError ex) { subscriptionSchedule.Complete(DateTimeOffset.UtcNow, false); if (!closed) DetailText.Text = ex.Message; }
        finally { if (manualStop) engine.Stop(); operation.Dispose(); operation = null; if (!closed) UpdateControls(); }
    }
    private void ImportFileClick(object sender, RoutedEventArgs args)
    {
        Servers.AddPopup.IsOpen = false;
        var dialog = new OpenFileDialog { Filter = "Профиль (*.json;*.txt;*.conf)|*.json;*.txt;*.conf|Все файлы|*.*", Title = "Открыть клиентский профиль" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (new FileInfo(dialog.FileName).Length > Profiles.MaxBytes) { DetailText.Text = "Файл больше 1 МБ. Выберите профиль одного подключения."; return; }
            Import(File.ReadAllText(dialog.FileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DetailText.Text = "Не удалось прочитать файл. Проверьте доступ к нему."; }
    }
    // Поле «Добавить сервер»: ссылка https:// — подписка своей группой, остальное — во «Вручную».
    private async void AddKeyClick(object sender, RoutedEventArgs args)
    {
        var text = Servers.Add.KeyInput.Text.Trim();
        if (text.Length == 0 || operation is not null) return;
        if (text.Length > Profiles.MaxBytes) { DetailText.Text = "Текст больше 1 МБ. Вставьте профиль одного подключения."; return; }
        Servers.AddPopup.IsOpen = false;
        Servers.Add.KeyInput.Clear();
        if (text.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !text.Contains('\n'))
        {
            try { await LoadSubscriptionAsync(SubscriptionMirrors.ParseSources(text), 0); }
            catch (UserError ex) { if (!closed) DetailText.Text = ex.Message; }
        }
        else Import(text);
    }
}
