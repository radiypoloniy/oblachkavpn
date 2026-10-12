using System.Text.Json;

namespace RoutePilot;

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
public static class DiagnosticReport
{
    public static string Create(ConnectionState state, int profileCount, int sourceCount,
        IEnumerable<ClientEvent> events, IEnumerable<RouteAttempt> attempts) => JsonSerializer.Serialize(new
        {
            App = ServiceBrand.Label, Mode = "Local SOCKS TCP", State = state.ToString(), ImportedProfiles = profileCount,
            SubscriptionSources = sourceCount, SystemNetworkModified = false,
            Events = events.TakeLast(100).Select(e => new { e.Time, Code = e.Code.ToString() }),
            Attempts = attempts.Take(6).Select(a => new { a.ProfileIndex, Outcome = a.Outcome is "Verified" or "Reachability" or "TargetResponse" or "StartOrProbeRejected" ? a.Outcome : "Unknown", a.Milliseconds })
        }, new JsonSerializerOptions { WriteIndented = true });
}
