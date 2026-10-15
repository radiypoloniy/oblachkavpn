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
public enum DiagnosticMode { LocalProxy, WindowsProxy, SystemTunnel }

public static class DiagnosticReport
{
    public static string Create(ConnectionState state, int profileCount, int sourceCount,
        IEnumerable<ClientEvent> events, IEnumerable<RouteAttempt> attempts,
        DiagnosticMode mode = DiagnosticMode.LocalProxy, bool blocksBypass = false, Blockage? blockage = null, string dpiBypass = "", bool fragmentActive = false) => JsonSerializer.Serialize(new
        {
            App = ServiceBrand.Label, Revision = BuildIdentity.Revision, Mode = mode.ToString(), State = state.ToString(), ImportedProfiles = profileCount,
            SubscriptionSources = sourceCount, SystemNetworkModified = mode != DiagnosticMode.LocalProxy,
            BlocksBypass = mode == DiagnosticMode.SystemTunnel && blocksBypass,
            // Последний диагноз отказа (вид, без адресов) и «Обход DPI»: режим и включён ли сейчас.
            LastBlockage = blockage?.ToString(), DpiBypass = dpiBypass, FragmentActive = fragmentActive,
            Events = events.TakeLast(100).Select(e => new { e.Time, Code = e.Code.ToString() }),
            Attempts = attempts.Take(RouteLadder.MaxSteps).Select(a => new { a.ProfileIndex, Outcome = a.Outcome is "Verified" or "Reachability" or "TargetResponse" or "StartOrProbeRejected" ? a.Outcome : "Unknown", a.Milliseconds, a.Fragment })
        }, new JsonSerializerOptions { WriteIndented = true });
}
