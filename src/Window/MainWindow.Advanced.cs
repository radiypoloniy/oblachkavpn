using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Oblachka.Views;

namespace Oblachka;

// «Для продвинутых» (AdvancedOptions, AdvancedPage). Поле сохраняется, когда из него уходят или
// нажимают Enter: неверное значение не сохраняется, под полем — что не так. Применение: окно —
// сразу (порт прокси, User-Agent, адрес проверки, форма фрагментации), туннель — со следующего
// подключения (TunnelTuning уходит в службу при Acquire).
public partial class MainWindow
{
    private AdvancedOptions advanced = AdvancedOptions.Default;
    private AdvancedPage Advanced => Settings.Advanced;

    private void WireAdvanced()
    {
        Settings.General.AdvancedOpen.Click += (_, _) => OpenSettings(SettingsPanel.AdvancedPage);
        Advanced.StackAuto.Click += (_, _) => SetAdvanced(advanced with { Stack = TunnelStack.Auto });
        Advanced.StackGvisor.Click += (_, _) => SetAdvanced(advanced with { Stack = TunnelStack.Gvisor });
        Advanced.StackMixed.Click += (_, _) => SetAdvanced(advanced with { Stack = TunnelStack.Mixed });
        Advanced.StackSystem.Click += (_, _) => SetAdvanced(advanced with { Stack = TunnelStack.System });
        Field(Advanced.MtuInput, Advanced.MtuError, text => Number(text, out var mtu) is { } bad ? bad
            : AdvancedOptions.CheckMtu(mtu) ?? Keep(advanced with { Mtu = mtu }));
        Field(Advanced.BypassInput, Advanced.BypassError, text =>
        {
            var subnets = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToArray();
            return AdvancedOptions.CheckSubnets(subnets) ?? Keep(advanced with { BypassSubnets = subnets });
        });
        Field(Advanced.DnsInput, Advanced.DnsError, text => AdvancedOptions.CheckDns(Empty(text)) ?? Keep(advanced with { TunnelDns = Empty(text) }));
        foreach (var box in new[] { Advanced.FragmentPackets, Advanced.FragmentLength, Advanced.FragmentInterval })
            Field(box, Advanced.FragmentError, _ =>
            {
                string? packets = Empty(Advanced.FragmentPackets.Text)?.ToLowerInvariant(), length = Empty(Advanced.FragmentLength.Text), interval = Empty(Advanced.FragmentInterval.Text);
                return AdvancedOptions.CheckFragment(packets, length, interval) ?? Keep(advanced with { FragmentPackets = packets, FragmentLength = length, FragmentInterval = interval });
            });
        Field(Advanced.UserAgentInput, Advanced.UserAgentError, text => AdvancedOptions.CheckUserAgent(Empty(text)) ?? Keep(advanced with { SubscriptionUserAgent = Empty(text) }));
        Field(Advanced.ProbeInput, Advanced.ProbeError, text => AdvancedOptions.CheckProbe(Empty(text)) ?? Keep(advanced with { ProbeUrl = Empty(text) }));
        Field(Advanced.PortInput, Advanced.PortError, text => Number(text, out var port) is { } bad ? bad
            : AdvancedOptions.CheckPort(port) ?? Keep(advanced with { ProxyPort = port }));
        Advanced.Ipv6Auto.Click += (_, _) => SetAdvanced(advanced with { NoIpv6 = false });
        Advanced.Ipv6Off.Click += (_, _) => SetAdvanced(advanced with { NoIpv6 = true });
        Field(Advanced.UdpInput, Advanced.UdpError, text => Number(text, out var minutes) is { } bad ? bad
            : AdvancedOptions.CheckUdpTimeout(minutes) ?? Keep(advanced with { UdpTimeout = minutes }));
        Field(Advanced.DirectDnsInput, Advanced.DirectDnsError, text => AdvancedOptions.CheckDirectDns(Empty(text)) ?? Keep(advanced with { DirectDns = Empty(text) }));
        Advanced.UtlsChoice.ItemsSource = new[] { "как в ссылке" }.Concat(Utls.Names).ToArray();
        Advanced.UtlsChoice.SelectionChanged += (_, _) =>
        {
            if (fillingAdvanced) return;
            SetAdvanced(advanced with { Utls = Advanced.UtlsChoice.SelectedIndex > 0 ? Utls.Names[Advanced.UtlsChoice.SelectedIndex - 1] : null });
        };
        Advanced.ExternalHwid.Click += (_, _) => SetAdvanced(advanced with { NoExternalHwid = Advanced.ExternalHwid.IsChecked != true });
        Field(Advanced.MarginInput, Advanced.MarginError, text => Number(text, out var percent) is { } bad ? bad
            : AdvancedOptions.CheckMargin(percent) ?? Keep(advanced with { SwitchMargin = percent }));
        Advanced.LogOff.Click += (_, _) => SetAdvanced(advanced with { CoreLog = CoreLogLevel.Off });
        Advanced.LogWarn.Click += (_, _) => SetAdvanced(advanced with { CoreLog = CoreLogLevel.Warn });
        Advanced.LogDebug.Click += (_, _) => SetAdvanced(advanced with { CoreLog = CoreLogLevel.Debug });
        Advanced.OpenLogButton.Click += (_, _) =>
        {
            // Есть файл — показать его в папке; нет — открыть саму папку (журнала ещё не было).
            var arguments = File.Exists(TunnelTuning.LogPath) ? "/select,\"" + TunnelTuning.LogPath + "\"" : "\"" + NetworkService.DataRoot + "\"";
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true }); }
            catch (System.ComponentModel.Win32Exception) { DetailText.Text = "Не удалось открыть папку журнала: " + NetworkService.DataRoot; }
        };
        Advanced.ResetButton.Click += (_, _) => { SetAdvanced(AdvancedOptions.Default); FillAdvanced(); };
        Advanced.UserAgentInput.Tag = ServiceBrand.UserAgent;
    }

    private static string? Empty(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    private static string? Number(string text, out int? value)
    {
        value = null;
        if (Empty(text) is not { } trimmed) return null;
        if (!int.TryParse(trimmed, out var parsed)) return "Нужно число.";
        value = parsed; return null;
    }
    // Проверка прошла: новое значение — в настройки. null — ошибки нет.
    private string? Keep(AdvancedOptions next) { SetAdvanced(next); return null; }

    // Поле: сохранение при уходе и по Enter (в многострочном — Ctrl+Enter); ошибка — под полем.
    private static void Field(TextBox box, TextBlock error, Func<string, string?> save)
    {
        void Commit()
        {
            var problem = save(box.Text);
            error.Text = problem ?? "";
            error.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
        }
        box.LostKeyboardFocus += (_, _) => Commit();
        box.KeyDown += (_, args) => { if (args.Key == Key.Enter && (!box.AcceptsReturn || Keyboard.Modifiers == ModifierKeys.Control)) Commit(); };
    }

    private void SetAdvanced(AdvancedOptions next)
    {
        if (next == advanced && next.BypassSubnets.SequenceEqual(advanced.BypassSubnets)) { MarkStack(); return; }
        advanced = next;
        ApplyAdvanced();
        QueueStateSave();
    }

    private void RestoreAdvanced(AdvancedOptions? saved)
    {
        // Испорченный снимок — как без настройки: лучше прежнее поведение, чем отказ запуска. DNS, который
        // больше не поддерживается (tls://, quic:// пробной сборки), отбрасывается один, без остального.
        saved = saved is null ? null : saved with { BypassSubnets = saved.BypassSubnets ?? [], TunnelDns = AdvancedOptions.CheckDns(saved.TunnelDns) is null ? saved.TunnelDns : null };
        advanced = saved is not null && saved.Problem() is null ? saved : AdvancedOptions.Default;
        ApplyAdvanced();
        FillAdvanced();
    }

    private void ApplyAdvanced()
    {
        Subscriptions.ExternalUserAgent = advanced.SubscriptionUserAgent;
        Subscriptions.ExternalHwid = !advanced.NoExternalHwid;
        Utls.Default = advanced.Utls;
        ServerMeasurements.Stickiness = 1 + (advanced.SwitchMargin ?? 20) / 100.0;
        TunnelHealth.Custom = advanced.ProbeUrl is { } probe ? new Uri(probe) : null;
        if (engine is Engine core && advanced.ProxyPort is { } port) core.PreferredPort = port;
        MarkStack();
        Settings.General.AdvancedTitle.Text = advanced.IsDefault ? "Тонкая настройка" : "Тонкая настройка — изменена";
    }

    private void MarkStack()
    {
        MarkSegment(Advanced.StackAuto, advanced.Stack == TunnelStack.Auto);
        MarkSegment(Advanced.StackGvisor, advanced.Stack == TunnelStack.Gvisor);
        MarkSegment(Advanced.StackMixed, advanced.Stack == TunnelStack.Mixed);
        MarkSegment(Advanced.StackSystem, advanced.Stack == TunnelStack.System);
        MarkSegment(Advanced.Ipv6Auto, !advanced.NoIpv6);
        MarkSegment(Advanced.Ipv6Off, advanced.NoIpv6);
        MarkSegment(Advanced.LogOff, advanced.CoreLog == CoreLogLevel.Off);
        MarkSegment(Advanced.LogWarn, advanced.CoreLog == CoreLogLevel.Warn);
        MarkSegment(Advanced.LogDebug, advanced.CoreLog == CoreLogLevel.Debug);
    }

    private bool fillingAdvanced;
    private void FillAdvanced()
    {
        fillingAdvanced = true;
        Advanced.UtlsChoice.SelectedIndex = advanced.Utls is { } fp ? Array.IndexOf(Utls.Names, fp) + 1 : 0;
        fillingAdvanced = false;
        Advanced.ExternalHwid.IsChecked = !advanced.NoExternalHwid;
        Advanced.MarginInput.Text = advanced.SwitchMargin?.ToString() ?? "";
        Advanced.MtuInput.Text = advanced.Mtu?.ToString() ?? "";
        Advanced.BypassInput.Text = string.Join(Environment.NewLine, advanced.BypassSubnets);
        Advanced.DnsInput.Text = advanced.TunnelDns ?? "";
        Advanced.FragmentPackets.Text = advanced.FragmentPackets ?? ""; Advanced.FragmentLength.Text = advanced.FragmentLength ?? "";
        Advanced.FragmentInterval.Text = advanced.FragmentInterval ?? "";
        Advanced.UserAgentInput.Text = advanced.SubscriptionUserAgent ?? "";
        Advanced.ProbeInput.Text = advanced.ProbeUrl ?? "";
        Advanced.PortInput.Text = advanced.ProxyPort?.ToString() ?? "";
        Advanced.UdpInput.Text = advanced.UdpTimeout?.ToString() ?? "";
        Advanced.DirectDnsInput.Text = advanced.DirectDns ?? "";
        foreach (var error in new[] { Advanced.MtuError, Advanced.BypassError, Advanced.DnsError, Advanced.FragmentError, Advanced.UserAgentError, Advanced.ProbeError, Advanced.PortError, Advanced.UdpError, Advanced.DirectDnsError, Advanced.MarginError })
            error.Visibility = Visibility.Collapsed;
    }
}
