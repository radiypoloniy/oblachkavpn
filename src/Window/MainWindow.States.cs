using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Oblachka.Views;

namespace Oblachka;

// Особые состояния главного экрана (design/redesign-2026-10-14, экран 15). Пока состояние
// не разрешилось, вместо списка серверов — карточка с объяснением и действиями.
//   1. Другой VPN забрал трафик (ForeignTunnel при запуске системного режима).
//   2. Связь пропала — идёт подбор или ядро перезапускается под защитой.
//   3. Рабочий сервер не найден, защита держит сеть закрытой.
//   4. Подписка заканчивается — жёлтая полоса и подвал.
public partial class MainWindow
{
    private string? foreignVpn;
    private bool networkClosed;

    private void WireStates()
    {
        Home.ForeignRetry.Click += (_, _) =>
        {
            foreignVpn = ForeignTunnel.Find();
            RefreshHome();
            if (foreignVpn is null && ConnectButton.IsEnabled) ConnectClick(this, new RoutedEventArgs());
        };
        Home.ForeignProxy.Click += async (_, _) =>
        {
            foreignVpn = null;
            await SetModeAsync(tunnel: false);
            if (!ConnectionActive && ConnectButton.IsEnabled) ConnectClick(this, new RoutedEventArgs());
        };
        Home.RecoveryStop.Click += StopClick;
        Home.ClosedPick.Click += (_, _) => Navigate("Servers");
        Home.ClosedOff.Click += (_, _) => { networkClosed = false; StopClick(this, new RoutedEventArgs()); };
        Home.SubWarnRenew.Click += (_, _) => OpenLink(RenewUrl, "страницу продления");
        Home.SubWarnPick.Click += (_, _) => Navigate("Servers");
        Home.SubWarnRefresh.Click += async (_, _) =>
        {
            if (CurrentGroup is { } group && operation is null) await LoadSubscriptionAsync(group.Sources, group.PreferredSource);
        };
        Home.SubWarnHide.Click += (_, _) =>
        {
            if (CurrentSubscription is not { WarningKey: { } key } state) return;
            Replace(state.Group with { DismissedWarning = key });
            QueueStateSave(); ApplySubscriptionWarning();
        };
    }

    // Поверх обычного статуса: возвращает true, если показано особое состояние.
    private bool ApplySpecialState(bool running)
    {
        var holding = systemTunnel?.State == SystemTunnelState.Holding;
        if (!ConnectionActive) networkClosed = false;
        // Сломанный путь программ (SystemPathProbe) держит карточку и при живом ядре.
        if (running && !holding && !findingRoute && !pathBroken) networkClosed = false;
        var closedNow = networkClosed && SystemTunnelActive && !findingRoute;
        var foreign = foreignVpn is not null && !ConnectionActive;
        var recovering = !closedNow && (findingRoute || holding) && ConnectionActive;
        PreviewState(ref closedNow, ref recovering);

        Home.ForeignCard.Visibility = foreign ? Visibility.Visible : Visibility.Collapsed;
        Home.RecoveryCard.Visibility = recovering ? Visibility.Visible : Visibility.Collapsed;
        Home.ClosedCard.Visibility = closedNow ? Visibility.Visible : Visibility.Collapsed;
        Home.ServerSection.Visibility = foreign || recovering || closedNow ? Visibility.Collapsed : Visibility.Visible;
        Spin(recovering);
        if (foreign)
        {
            var name = ForeignTunnel.FriendlyName(foreignVpn!);
            Status("Не запущено", "Мешает другой VPN", PowerState.Disabled, "WI");
            Home.ForeignTitle.Text = "Интернет уже забрал " + name;
            Home.ForeignText.Text = $"Два VPN одновременно ломают DNS — сайты перестанут открываться. Выключите {name} и нажмите «Проверить снова».";
        }
        else if (closedNow)
        {
            if (pathBroken && pathDirectOnly) Status("Сайты напрямую не открываются", "Остальное идёт через VPN", PowerState.Blocked, "EI");
            else Status("Интернет закрыт", pathBroken ? "Трафик компьютера не проходит через туннель" : "Защита держит сеть, пока нет VPN", PowerState.Blocked, "EI");
            Home.ClosedText.Text = pathBroken ? pathProblem + " Мимо туннеля ничего не утекло. Выключите VPN — обычная сеть вернётся сразу."
                : "Не удалось найти рабочий сервер. Ничего не утекло мимо туннеля, но и сайты сейчас не открываются.";
            Home.ClosedPick.Visibility = pathBroken ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
        }
        else if (recovering)
        {
            var limit = autoSelect ? 2 : 3;
            Home.RecoveryRounds.Text = Math.Min(limit, Math.Max(health.FailedRounds, findingRoute ? limit : 1)) + " из " + limit;
            Home.RecoveryStep.Text = findingRoute ? "Ищем рабочий сервер" : "Перезапускаем ядро";
            Home.RecoveryLockRow.Visibility = systemTunnel?.BlocksBypass == true ? Visibility.Visible : Visibility.Collapsed;
        }
        if (foreign || closedNow) Home.CheckChip.Visibility = Visibility.Collapsed;
        else Home.CheckChip.Visibility = Visibility.Visible;
        return foreign || closedNow || recovering;
    }

    private void Status(string title, string sub, PowerState power, string dot)
    {
        Home.StatusTitle.Text = title;
        Home.StatusSub.Text = sub;
        Home.Power.State = power;
        Home.StatusDot.SetResourceReference(Shape.FillProperty, dot);
    }

    // Подстановка состояния для превью и UI-проверок; в поставляемой сборке метода нет.
    partial void PreviewState(ref bool closedNow, ref bool recovering);

    private bool spinning;
    private void Spin(bool on)
    {
        if (on == spinning) return;
        spinning = on;
        Home.RecoverySpin.BeginAnimation(RotateTransform.AngleProperty,
            on ? new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever } : null);
    }

    // Полоса «Подписка заканчивается»: меньше 14 дней или меньше 5 ГБ.
    private void ApplySubscriptionWarning()
    {
        var state = CurrentSubscription;
        var warn = state is { HasWarning: true } && state.WarningKey != state.Group.DismissedWarning;
        Home.SubWarn.Visibility = warn ? Visibility.Visible : Visibility.Collapsed;
        Home.SubWarnText.Text = state?.WarningText ?? "";
        Home.SubWarn.BorderThickness = state?.Critical == true ? new Thickness(2) : new Thickness(0);
        Home.SubWarn.SetResourceReference(Border.BorderBrushProperty, "EI");
        Home.SubWarnText.SetResourceReference(TextBlock.ForegroundProperty, state?.Critical == true ? "EI" : "SF");
        Home.SubWarnRenew.Visibility = state?.RenewUrl is not null ? Visibility.Visible : Visibility.Collapsed;
        Home.SubWarnRefresh.IsEnabled = operation is null;
        Home.SubWarnPick.Visibility = groups.Any(g => g.Id != state?.Group.Id && ProfilesOf(g.Id).Any(p => !HiddenServer(p)))
            ? Visibility.Visible : Visibility.Collapsed;
        Home.PlanText.SetResourceReference(TextBlock.ForegroundProperty, state?.HasWarning == true ? "FootWarn" : "Mu2");
        Home.PlanText.FontWeight = state?.HasWarning == true ? FontWeights.SemiBold : FontWeights.Normal;
    }
}
