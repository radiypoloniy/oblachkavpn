using Microsoft.Win32;
using System.Text;
using System.Windows;

namespace Oblachka;

public partial class MainWindow
{
    private void WireAnalytics()
    {
        Home.AnalyticsNav.Click += (_, _) => OpenAnalytics();
        Home.TodayCard.Click += (_, _) => OpenAnalytics();
        Analytics.Closed += CloseAnalytics;
        Analytics.PeriodChanged += _ => RefreshAnalytics();
        Analytics.ExportRequested += ExportAnalytics;
        Analytics.ResetRequested += ResetAnalytics;
    }

    private void OpenAnalytics()
    {
        ShowModeMenu(false); CloseConnections();
        Servers.Visibility = Visibility.Collapsed; RulesPage.Visibility = Visibility.Collapsed; Settings.Visibility = Visibility.Collapsed; Picker.Visibility = Visibility.Collapsed;
        Doctor.Visibility = Visibility.Collapsed; SiteCheck.Visibility = Visibility.Collapsed;
        currentPage = "Analytics";
        Analytics.Show(focus: true); RefreshAnalytics();
        OpenSection();
    }

    private void CloseAnalytics()
    {
        Analytics.Visibility = Visibility.Collapsed;
        if (currentPage == "Analytics") currentPage = "Connection";
        ShowDefaultSection();
        Home.AnalyticsNav.Focus();
    }

    private void RefreshAnalytics()
    {
        if (!viewsReady || Analytics.Visibility != Visibility.Visible) return;
        var now = DateTime.Now;
        Analytics.Render(trafficHistory.Report(now, Analytics.PeriodDays), speedHistory.Report(now), meter?.Up ?? 0, meter?.Down ?? 0,
            (runningProfile ?? profile)?.DisplayName ?? "Сервер не выбран", ServiceGroup?.Metadata ?? subscriptionMetadata);
    }

    private void ExportAnalytics()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Таблица CSV (*.csv)|*.csv",
            FileName = "Облачка — аналитика " + DateTime.Now.ToString("yyyy-MM-dd") + ".csv",
            Title = "Сохранить аналитику"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var report = trafficHistory.Report(DateTime.Now, Analytics.PeriodDays);
            File.WriteAllText(dialog.FileName, TrafficHistory.Csv(report), new UTF8Encoding(true));
            DetailText.Text = "Аналитика сохранена без адресов сайтов.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DetailText.Text = "Не удалось сохранить таблицу. Проверьте доступ к выбранной папке."; }
    }

    private void ResetAnalytics()
    {
        if (!AppDialog.Confirm(this, "Сбросить статистику?", "Удалим историю трафика и программ за все периоды. Настройки VPN и подписка останутся.", "Сбросить", danger: true)) return;
        trafficHistory.Reset(); programTraffic.Reset(); speedHistory.Reset();
        trafficToday = trafficSavedBytes = 0; trafficDay = DateTime.Now.ToString("yyyy-MM-dd"); Array.Clear(trafficHours); todayShown = "";
        meterCountedUp = meter?.TotalUp ?? 0; meterCountedDown = meter?.TotalDown ?? 0;
        QueueStateSave(); RefreshTraffic(); RefreshHome();
        DetailText.Text = "История трафика удалена.";
    }
}
