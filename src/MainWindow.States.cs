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
        Home.SubWarnRenew.Click += (_, _) => OpenSupport();
    }

    // Поверх обычного статуса: возвращает true, если показано особое состояние.
    private bool ApplySpecialState(bool running)
    {
        var holding = systemTunnel?.State == SystemTunnelState.Holding;
        if (!ConnectionActive) networkClosed = false;
        if (running && !holding && !findingRoute) networkClosed = false;
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
        else if (closedNow) Status("Интернет закрыт", "Защита держит сеть, пока нет VPN", PowerState.Blocked, "EI");
        else if (recovering)
        {
            Home.RecoveryRounds.Text = Math.Max(health.FailedRounds, findingRoute ? 3 : 1) + " из 3";
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
        var meta = subscriptionMetadata;
        var now = DateTimeOffset.UtcNow;
        var days = meta?.Expires is { } expires && expires > now ? (int?)(expires - now).TotalDays : null;
        var leftGb = meta is { Total: > 0, Upload: >= 0, Download: >= 0 }
            ? (decimal?)Math.Max(0m, (decimal)meta.Total!.Value - meta.Upload!.Value - meta.Download!.Value) / 1073741824m : null;
        var parts = new List<string>();
        if (days is < 14) parts.Add($"ещё {days} дн.");
        if (leftGb is < 5) parts.Add($"осталось {leftGb:0.#} ГБ");
        var warn = serviceProfiles.Count > 0 && parts.Count > 0;
        Home.SubWarn.Visibility = warn ? Visibility.Visible : Visibility.Collapsed;
        Home.SubWarnText.Text = warn ? "Подписка " + string.Join(" и ", parts) : "";
        Home.SubWarnRenew.Visibility = ServiceBrand.SupportUrl.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        Home.PlanText.SetResourceReference(TextBlock.ForegroundProperty, warn ? "FootWarn" : "Mu2");
        Home.PlanText.FontWeight = warn ? FontWeights.SemiBold : FontWeights.Normal;
    }
}
