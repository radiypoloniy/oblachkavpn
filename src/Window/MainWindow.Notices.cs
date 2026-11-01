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
    private enum Notice { ServerSwitched, NetworkClosed, Expiring, BypassBroken, ProtectionRepaired, ProxyRepaired, ProxyLost, PauseEnded, Update, NetworkSecured }

    private static readonly TimeSpan NoticeQuiet = TimeSpan.FromMinutes(10);
    private readonly Dictionary<string, DateTimeOffset> noticeShown = new();
    private Action? noticeAction;

    private void WireNotices() => Settings.Notices.Click += (_, _) => QueueStateSave();

    // Нажатие на уведомление (подписка — при создании значка трея).
    private void NoticeClicked()
    {
        var action = noticeAction;
        noticeAction = null;
        (action ?? ShowWindow)();
    }

    private bool WindowOnScreen => IsVisible && IsActive && WindowState != WindowState.Minimized;

    private bool Notify(Notice kind, string title, string text, Action? onClick = null, bool warning = false, string? scope = null)
    {
        if (closed || vmTestActive || !preferences.Notices || WindowOnScreen) return false;
        var now = DateTimeOffset.UtcNow;
        if (noticeShown.TryGetValue(kind + "|" + scope, out var last) && now - last < NoticeQuiet) return false;
        noticeShown[kind + "|" + scope] = now;
        noticeAction = onClick ?? ShowWindow;
        tray.ShowBalloonTip(8000, title, text, warning ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);
        return true;
    }

    // Отдельный ключ для каждой группы и точного срока; нажатие связано с той же группой.
    private void PollExpiry()
    {
        if (CurrentSubscription is not { Group: { } group } state || group.Metadata?.Expires is not { } expires) return;
        var left = expires - DateTimeOffset.UtcNow;
        if (left <= TimeSpan.Zero || left > TimeSpan.FromDays(3) || state.WarningKey == group.DismissedWarning) return;
        var stage = left <= TimeSpan.FromDays(1) ? 1 : 3;
        var key = expires.ToUnixTimeSeconds() + "|" + stage;
        if (group.ExpiryNotice == key) return;
        var url = state.RenewUrl;
        var when = stage == 1 ? "в течение суток" : "через " + Math.Max(2, (int)Math.Ceiling(left.TotalDays)) + " дня";
        if (Notify(Notice.Expiring, group.Name + ": срок истекает " + when,
            url is not null ? "Нажмите, чтобы продлить эту подписку." : "Обновите данные подписки или выберите другой сервер.",
            url is not null ? () => OpenLink(url, "страницу продления") : null, scope: group.Id))
        {
            Replace(group with { ExpiryNotice = key });
            QueueStateSave();
        }
    }
}
