using System.Windows;

namespace Oblachka;

public partial class MainWindow
{
    private string? previewState;
    partial void PreviewState(ref bool closedNow, ref bool recovering)
    {
        if (previewState == "closed") closedNow = true;
        if (previewState == "recovery") recovering = true;
    }

    private void CheckSpecialStates(ICollection<string> checks)
    {
        foreignVpn = "throne-tun";
        RefreshHome();
        if (Home.ForeignCard.Visibility != Visibility.Visible || Home.ServerSection.Visibility != Visibility.Collapsed ||
            Home.StatusTitle.Text != "Не запущено" || Home.Power.State != Views.PowerState.Disabled || !Home.ForeignTitle.Text.EndsWith("Throne"))
            throw new InvalidOperationException("Foreign VPN state is not shown: " + Home.ForeignTitle.Text);
        foreignVpn = null;
        previewState = "closed"; RefreshHome();
        if (Home.ClosedCard.Visibility != Visibility.Visible || Home.Power.State != Views.PowerState.Blocked || Home.StatusTitle.Text != "Интернет закрыт")
            throw new InvalidOperationException("Closed network state is not shown");
        if (Home.ClosedPick.Visibility != Visibility.Visible || !Home.ClosedText.Text.StartsWith("Не удалось найти"))
            throw new InvalidOperationException("Closed card without a working server lost its server choice");
        // Путь программ сломан (SystemPathProbe): причина на карточке, другой сервер не поможет.
        pathBroken = true; pathProblem = "Сервер отвечает, но программы компьютера через туннель в интернет не выходят.";
        RefreshHome();
        if (Home.ClosedPick.Visibility != Visibility.Collapsed || !Home.ClosedText.Text.StartsWith(pathProblem) ||
            Home.StatusSub.Text != "Трафик компьютера не проходит через туннель")
            throw new InvalidOperationException("Broken system path is not explained: " + Home.ClosedText.Text);
        pathBroken = false; pathProblem = "";
        previewState = null; RefreshHome();
        if (Home.ForeignCard.Visibility != Visibility.Collapsed || Home.ClosedCard.Visibility != Visibility.Collapsed || Home.ServerSection.Visibility != Visibility.Visible)
            throw new InvalidOperationException("Special state did not clear");
        checks.Add("Main screen shows foreign-VPN and closed-network cards instead of the server list and clears them");
    }

    internal void PrepareStatePreview(string state)
    {
        if (state == "foreign") foreignVpn = "throne-tun"; else previewState = state;
        if (state == "subscription")
        {
            serviceProfiles = importedProfiles.Select(p => p with { Source = ProfileSource.Service }).ToArray();
            subscriptionMetadata = new SubscriptionMetadata(0, 3_000_000_000, 6_400_000_000, DateTimeOffset.UtcNow.AddDays(5));
            UpdateSubscriptionSummary();
        }
        RefreshHome();
    }
}
