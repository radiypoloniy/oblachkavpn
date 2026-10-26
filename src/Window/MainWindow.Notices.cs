using System.Windows;
using Forms = System.Windows.Forms;

namespace Oblachka;

// Уведомления Windows о том, что случилось, пока окно свёрнуто: сервер сменился при обрыве,
// интернет закрыт, подписка кончается, обход перестал работать, защиту поставили заново,
// пауза кончилась, вышла новая версия. Окно на экране — молчим: там всё и так видно.
//
// Показывает значок в трее (Windows 10/11 выводит это как обычное уведомление). Кнопок в нём нет —
// действие выполняется по нажатию на само уведомление; настоящие кнопки требуют WinRT (+25 МБ).
// Одно и то же — не чаще раза в 10 минут: обрыв, который чинится и снова рвётся, не засыпает экран.
public partial class MainWindow
{
    private enum Notice { ServerSwitched, NetworkClosed, Expiring, BypassBroken, ProtectionRepaired, ProxyRepaired, ProxyLost, PauseEnded, Update }

    private static readonly TimeSpan NoticeQuiet = TimeSpan.FromMinutes(10);
    private readonly Dictionary<Notice, DateTimeOffset> noticeShown = new();
    private Action? noticeAction;
    // О каком сроке подписки уже сказали: «2026-12-05|3» — за три дня, «…|1» — за день.
    private string? expiryNotice;

    private void WireNotices() => Settings.Notices.Click += (_, _) => QueueStateSave();

    // Нажатие на уведомление (подписка — при создании значка трея).
    private void NoticeClicked()
    {
        var action = noticeAction;
        noticeAction = null;
        (action ?? ShowWindow)();
    }

    private bool WindowOnScreen => IsVisible && IsActive && WindowState != WindowState.Minimized;

    private void Notify(Notice kind, string title, string text, Action? onClick = null, bool warning = false)
    {
        if (closed || vmTestActive || Settings.Notices.IsChecked != true || WindowOnScreen) return;
        var now = DateTimeOffset.UtcNow;
        if (noticeShown.TryGetValue(kind, out var last) && now - last < NoticeQuiet) return;
        noticeShown[kind] = now;
        noticeAction = onClick ?? ShowWindow;
        tray.ShowBalloonTip(8000, title, text, warning ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);
    }

    // Срок подписки сервиса: за 3 дня и за день — по разу на каждый срок, и после перезапуска тоже.
    private void PollExpiry()
    {
        if (subscriptionMetadata?.Expires is not { } expires || CurrentGroup is not { IsSubscription: true }) return;
        var left = expires - DateTimeOffset.UtcNow;
        if (left <= TimeSpan.Zero || left > TimeSpan.FromDays(3)) return;
        var stage = left <= TimeSpan.FromDays(1) ? 1 : 3;
        var key = expires.ToString("yyyy-MM-dd") + "|" + stage;
        if (expiryNotice == key || WindowOnScreen || Settings.Notices.IsChecked != true) return;
        expiryNotice = key;
        QueueStateSave();
        var when = stage == 1 ? "завтра" : "через " + Math.Max(2, (int)Math.Ceiling(left.TotalDays)) + " дня";
        Notify(Notice.Expiring, "Подписка кончается " + when, RenewUrl is not null ? "Нажмите, чтобы продлить." : "Продлите её, чтобы VPN не выключился.",
            RenewUrl is not null ? () => OpenLink(RenewUrl, "страницу продления") : null);
    }
}
