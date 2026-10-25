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
        // Испорченный снимок — как без настройки: лучше прежнее поведение, чем отказ запуска.
        advanced = saved is not null && saved.Problem() is null ? saved with { BypassSubnets = saved.BypassSubnets ?? [] } : AdvancedOptions.Default;
        ApplyAdvanced();
        FillAdvanced();
    }

    private void ApplyAdvanced()
    {
        Subscriptions.ExternalUserAgent = advanced.SubscriptionUserAgent;
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
    }

    private void FillAdvanced()
    {
        Advanced.MtuInput.Text = advanced.Mtu?.ToString() ?? "";
        Advanced.BypassInput.Text = string.Join(Environment.NewLine, advanced.BypassSubnets);
        Advanced.DnsInput.Text = advanced.TunnelDns ?? "";
        Advanced.FragmentPackets.Text = advanced.FragmentPackets ?? ""; Advanced.FragmentLength.Text = advanced.FragmentLength ?? "";
        Advanced.FragmentInterval.Text = advanced.FragmentInterval ?? "";
        Advanced.UserAgentInput.Text = advanced.SubscriptionUserAgent ?? "";
        Advanced.ProbeInput.Text = advanced.ProbeUrl ?? "";
        Advanced.PortInput.Text = advanced.ProxyPort?.ToString() ?? "";
        foreach (var error in new[] { Advanced.MtuError, Advanced.BypassError, Advanced.DnsError, Advanced.FragmentError, Advanced.UserAgentError, Advanced.ProbeError, Advanced.PortError })
            error.Visibility = Visibility.Collapsed;
    }
}
