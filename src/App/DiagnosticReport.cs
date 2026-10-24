using System.Text.Json;

namespace Oblachka;

public enum ClientEventCode { Idle, Validating, Starting, Running, Verified, Failed, Recovered, SubscriptionUpdated, HealthFailed }
public sealed record ClientEvent(DateTimeOffset Time, ClientEventCode Code)
{
    public string State => Code.ToString();
    public string Message => Code switch
    {
        ClientEventCode.Idle => "Подключение остановлено", ClientEventCode.Validating => "Проверка конфигурации",
        ClientEventCode.Starting => "Запуск ядра", ClientEventCode.Running => "Локальный прокси готов",
        ClientEventCode.Verified => "Запрос через туннель выполнен", ClientEventCode.Failed => "Соединение прервано",
        ClientEventCode.Recovered => "Соединение восстановлено", ClientEventCode.SubscriptionUpdated => "Подписка обновлена",
        _ => "Проверка связи не прошла"
    };
}

// No arbitrary strings from profiles, URLs, exception messages or core output enter this schema.
// Details (MainWindow.Report) — настройки, серверы номером и протоколом, программы в сети с путём
// без имени пользователя, журнал zapret; доменов и истории сайтов там нет.
public enum DiagnosticMode { LocalProxy, WindowsProxy, SystemTunnel }

public static class DiagnosticReport
{
    public static string Create(ConnectionState state, int profileCount, int sourceCount,
        IEnumerable<ClientEvent> events, IEnumerable<RouteAttempt> attempts,
        DiagnosticMode mode = DiagnosticMode.LocalProxy, bool blocksBypass = false, Blockage? blockage = null, string dpiBypass = "", bool fragmentActive = false, object? details = null) => JsonSerializer.Serialize(new
        {
            App = ServiceBrand.Label, Revision = BuildIdentity.Revision, Mode = mode.ToString(), State = state.ToString(), ImportedProfiles = profileCount,
            SubscriptionSources = sourceCount, SystemNetworkModified = mode != DiagnosticMode.LocalProxy,
            BlocksBypass = mode == DiagnosticMode.SystemTunnel && blocksBypass,
            // Последний диагноз отказа (вид, без адресов) и «Обход DPI»: режим и включён ли сейчас.
            LastBlockage = blockage?.ToString(), DpiBypass = dpiBypass, FragmentActive = fragmentActive,
            Events = events.TakeLast(100).Select(e => new { e.Time, Code = e.Code.ToString() }),
            Attempts = attempts.Take(RouteLadder.MaxSteps).Select(a => new { a.ProfileIndex, Outcome = a.Outcome is "Verified" or "Reachability" or "TargetResponse" or "StartOrProbeRejected" ? a.Outcome : "Unknown", a.Milliseconds, a.Fragment }),
            Details = details
        }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    // Путь без имени пользователя: свой профиль — %USERPROFILE%, чужие — C:\Users\*.
    public static string SafePath(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\');
        if (home.Length > 3 && (path.Equals(home, StringComparison.OrdinalIgnoreCase) || path.StartsWith(home + "\\", StringComparison.OrdinalIgnoreCase)))
            path = "%USERPROFILE%" + path[home.Length..];
        return System.Text.RegularExpressions.Regex.Replace(path, @"^([A-Za-z]:\\Users\\)[^\\]+", "$1*", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    // Последняя ошибка окна (CrashLog: тип и стек, без текста сообщения), если она свежая.
    public static string? RecentCrash(string path, DateTimeOffset now)
    {
        try
        {
            if (!File.Exists(path) || now - File.GetLastWriteTimeUtc(path) > TimeSpan.FromDays(14)) return null;
            var text = File.ReadAllText(path);
            return text.Length > 4000 ? text[..4000] : text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
