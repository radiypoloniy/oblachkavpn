using System.Windows;

namespace Oblachka;

// Маршрутизация по спискам: правила сервиса (заголовок routing) и встроенный набор
// «Российские сайты напрямую». Исполняет ядро подключения (RoutingPlan), категории
// разворачиваются из баз адресов, скачанных с согласия пользователя (GeoStore).
public partial class MainWindow
{
    private RoutingProfile? providerRouting;
    // null — сервис прислал правила, а пользователя ещё не спрашивали.
    private bool? useProviderRouting;
    private bool geoDownloading;
    private string? geoProgress;

    private void WireRouting()
    {
        Settings.RussiaDirect.Click += async (_, _) => await RoutingChangedAsync();
        Bypass.ProviderRouting.Click += async (_, _) => { useProviderRouting = Bypass.ProviderRouting.IsChecked == true; await RoutingChangedAsync(); };
        Diagnostics.GeoButton.Click += async (_, _) => await DownloadGeoAsync(force: true);
        Home.RoutingOfferAccept.Click += async (_, _) => { useProviderRouting = true; await RoutingChangedAsync(); };
        Home.RoutingOfferDecline.Click += (_, _) => { useProviderRouting = false; RefreshRouting(); QueueStateSave(); };
    }

    // Новые правила сервиса. Решение пользователя сохраняется, пока не сменилось имя набора:
    // обновление тех же правил не должно переспрашивать, другой набор — должен.
    private void ReceiveRouting(RoutingProfile routing)
    {
        if (providerRouting is not null && providerRouting.Name != routing.Name) useProviderRouting = null;
        providerRouting = routing;
        RefreshRouting();
    }

    private IEnumerable<RoutingProfile> ActiveRoutingProfiles()
    {
        // Сайты zapret — первыми: напрямую через обход, даже если набор сервиса шлёт их в VPN.
        foreach (var zapret in ZapretRoutingProfiles()) yield return zapret;
        if (SelectiveProfile() is { } selective) yield return selective;
        if (RoutingProfile.Custom(advanced.GeoDirect, advanced.GeoProxy, advanced.GeoBlock) is { } custom) yield return custom;
        if (useProviderRouting == true && providerRouting is not null) yield return providerRouting;
        if (Settings.RussiaDirect.IsChecked == true) yield return RoutingProfile.RussiaDirect;
    }

    private bool HasDirectEntry => (engine as Engine)?.DirectEntry is not null;

    private CompiledRouting? CurrentRouting() => CompiledRouting.Compile(ActiveRoutingProfiles(), GeoStore.Default);

    // Какие базы нужны включённым наборам; для предложения — и набору сервиса до согласия.
    private static IEnumerable<GeoStore.Kind> NeededGeo(IEnumerable<RoutingProfile> profiles)
    {
        var list = profiles.ToArray();
        if (list.Any(p => p.GeositeTags.Any())) yield return GeoStore.Kind.Geosite;
        if (list.Any(p => p.GeoipTags.Any())) yield return GeoStore.Kind.Geoip;
    }

    private static string SizeHint(IEnumerable<GeoStore.Kind> kinds) =>
        kinds.Sum(k => k == GeoStore.Kind.Geosite ? 11 : 16) is var mb and > 0 ? "≈ " + mb + " МБ" : "";

    private async Task RoutingChangedAsync()
    {
        RefreshRouting();
        QueueStateSave();
        await DownloadGeoAsync(force: false);
        // Списки исполняет ядро подключения: чтобы они заработали, подключение перезапускается,
        // как при смене режима. Во время запуска или замера не трогаем — применятся при следующем.
        if (closed || !ConnectionActive) return;
        if (operation is not null || systemStopping) { DetailText.Text = "Списки применятся со следующего подключения."; return; }
        recoveryWait?.Cancel();
        // Списки в ядре туннеля — новые файлы на ходу: ни ядро, ни туннель не перезапускаются.
        if (TunnelListsActive && await UpdateTunnelListsAsync()) { DetailText.Text = "Списки применены; туннель и защита не снимались."; return; }
        // «Весь компьютер»: если у сессии уже есть вход для прямого трафика (или он не нужен),
        // перезапускается только ядро — туннель и защита остаются, связь пропадает на секунду.
        // Полностью переподключаться приходится, только когда вход для прямого трафика нужен впервые.
        if (SystemTunnelActive && systemTunnel is not null && (HasDirectEntry || CurrentRouting()?.HasDirect != true && !zapretEnabled))
        {
            await ConnectCoreAsync();
            if (closed) return;
            if (systemTunnel?.State == SystemTunnelState.Running && engine.State is ConnectionState.Running or ConnectionState.Verified)
            { DetailText.Text = "Списки применены; туннель и защита не снимались."; return; }
            // Ядро под сессией не поднялось: защита осталась, а трафик нести некому — весь интернет
            // закрыт. Не прячем это за «применено», а переподключаемся полностью.
            if (manualStop || systemTunnel is null) return;
        }
        if (await ReconnectAsync()) DetailText.Text = "Списки включены, подключение перезапущено.";
    }

    // Полное переподключение: новая сессия «Весь компьютер» с новым входом прямого трафика.
    private async Task<bool> ReconnectAsync()
    {
        manualStop = true;
        if (systemTunnel is not null) await StopSystemTunnelAsync(); else engine.Stop();
        UpdateControls();
        if (closed || !ConnectButton.IsEnabled) return false;
        await ConnectCoreAsync();
        return true;
    }

    // Недостающие базы скачиваются; force — обновить и имеющиеся.
    private async Task DownloadGeoAsync(bool force)
    {
        if (geoDownloading) return;
        var profiles = ActiveRoutingProfiles().ToArray();
        var needed = (force ? NeededGeo(profiles.Length > 0 ? profiles : new[] { RoutingProfile.RussiaDirect }) : NeededGeo(profiles))
            .Where(k => force || !GeoStore.Default.Has(k)).ToArray();
        if (needed.Length == 0) return;
        geoDownloading = true;
        try
        {
            foreach (var kind in needed)
            {
                var url = kind == GeoStore.Kind.Geosite
                    ? profiles.Select(p => p.GeositeUrl).FirstOrDefault(u => u is not null) ?? RoutingProfile.DefaultGeosite
                    : profiles.Select(p => p.GeoipUrl).FirstOrDefault(u => u is not null) ?? RoutingProfile.DefaultGeoip;
                var label = kind == GeoStore.Kind.Geosite ? "сайтов" : "IP-адресов";
                geoProgress = "Скачиваем базу " + label + "…"; RefreshRouting();
                var progress = new Progress<double>(p => { geoProgress = $"Скачиваем базу {label}: {p:P0}"; RefreshRouting(); });
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(6));
                await GeoStore.Default.DownloadAsync(kind, url, timeout.Token, progress);
            }
            if (!closed) DetailText.Text = "Базы адресов скачаны.";
        }
        catch (UserError ex) { if (!closed) DetailText.Text = ex.Message; }
        catch (OperationCanceledException) { if (!closed) DetailText.Text = "Скачивание базы адресов прервано."; }
        finally { geoDownloading = false; geoProgress = null; if (!closed) RefreshRouting(); }
    }

    private void RefreshRouting()
    {
        if (!viewsReady) return;
        var provider = providerRouting;
        Bypass.ProviderRoutingRow.Visibility = provider is null ? Visibility.Collapsed : Visibility.Visible;
        if (provider is not null)
        {
            Bypass.ProviderRouting.IsChecked = useProviderRouting == true;
            Bypass.ProviderRoutingTitle.Text = "Правила сервиса «" + provider.Name + "»";
            Bypass.ProviderRoutingText.Text = Describe(provider);
        }
        var site = GeoStore.Default.Info(GeoStore.Kind.Geosite);
        var ip = GeoStore.Default.Info(GeoStore.Kind.Geoip);
        string Line(GeoStore.FileInfo info, string name) => name + " " + (info.Bytes / 1048576.0).ToString("0.#") + " МБ от " + info.Updated.ToLocalTime().ToString("dd.MM");
        Diagnostics.GeoStatus.Text = geoProgress ?? (site is null && ip is null ? "Базы адресов не скачаны"
            : "Базы адресов: " + string.Join(", ", new[] { site is null ? null : Line(site, "сайты"), ip is null ? null : Line(ip, "IP") }.OfType<string>()));
        Diagnostics.GeoButton.Content = site is null && ip is null ? "Скачать" : "Обновить";
        Diagnostics.GeoButton.IsEnabled = !geoDownloading;
        Settings.RussiaDirect.IsEnabled = Bypass.ProviderRouting.IsEnabled = !geoDownloading;

        var offer = provider is not null && useProviderRouting is null;
        Home.RoutingOffer.Visibility = offer ? Visibility.Visible : Visibility.Collapsed;
        if (offer)
        {
            var size = SizeHint(NeededGeo(new[] { provider! }).Where(k => !GeoStore.Default.Has(k)));
            Home.RoutingOfferTitle.Text = "Сервис предлагает маршрутизацию";
            Home.RoutingOfferText.Text = "«" + provider!.Name + "»: " + Describe(provider) + "." +
                (size.Length > 0 ? " Для этого скачаем базы адресов (" + size + ")." : "");
        }
        whereRouting = CurrentRouting();
    }

    // Развёрнутые списки для «Куда пойдёт сайт?»: собираются при смене настроек, не на каждую букву.
    private CompiledRouting? whereRouting;

    // «российские сайты и .ru напрямую, 2 блок…» — коротко о том, что делает набор.
    private static string Describe(RoutingProfile profile)
    {
        var parts = new List<string>();
        var direct = profile.DirectSites.Length;
        if (direct > 0) parts.Add(profile.DirectSites.Any(s => s.Contains("category-ru") || s == "domain:ru") ? "российские сайты напрямую" : direct + " правил напрямую");
        if (profile.BlockSites.Length + profile.BlockIp.Length > 0) parts.Add((profile.BlockSites.Length + profile.BlockIp.Length) + " блокировок");
        if (profile.ProxySites.Length + profile.ProxyIp.Length > 0) parts.Add((profile.ProxySites.Length + profile.ProxyIp.Length) + " через VPN");
        parts.Add(profile.GlobalProxy ? "остальное через VPN" : "остальное напрямую");
        return string.Join(", ", parts);
    }
}
