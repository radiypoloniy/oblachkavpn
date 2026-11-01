namespace Oblachka;

public enum SubscriptionTerm { Unknown, Active, Expiring, Expired }
public enum SubscriptionTraffic { Unknown, Remaining, Low, Exhausted, Unlimited }

// Только сведения провайдера. Локальные счётчики устройства не участвуют в остатке.
public sealed record SubscriptionState
{
    public required ProfileGroup Group { get; init; }
    public required SubscriptionTerm Term { get; init; }
    public required SubscriptionTraffic Traffic { get; init; }
    public decimal? RemainingBytes { get; init; }
    public double? Used { get; init; }
    public int? Days { get; init; }
    public string? RenewUrl => Group.Metadata?.Provider?.RenewUrl ?? Group.Metadata?.WebPageUrl;
    public string? SupportUrl => Group.Metadata?.SupportUrl;
    public bool Critical => Term == SubscriptionTerm.Expired || Traffic == SubscriptionTraffic.Exhausted;
    public bool HasWarning => Term is SubscriptionTerm.Expiring or SubscriptionTerm.Expired || Traffic is SubscriptionTraffic.Low or SubscriptionTraffic.Exhausted;
    public string TermText => Term switch
    {
        SubscriptionTerm.Unknown => "Срок не сообщён",
        SubscriptionTerm.Expired => "Срок истёк " + Group.Metadata!.Expires!.Value.ToLocalTime().ToString("dd.MM.yyyy"),
        SubscriptionTerm.Expiring => Days == 0 ? "Подписка истекает сегодня" : $"Подписка ещё {Days} дн.",
        _ => "Подписка до " + Group.Metadata!.Expires!.Value.ToLocalTime().ToString("dd.MM.yyyy")
    };
    public string TrafficText => Traffic switch
    {
        SubscriptionTraffic.Unknown => "Трафик не сообщён",
        SubscriptionTraffic.Unlimited => "Трафик без ограничения",
        SubscriptionTraffic.Exhausted => "Трафик исчерпан",
        _ => $"Осталось {RemainingBytes / 1073741824m:0.#} ГБ"
    };
    public string Summary => TermText + " · " + TrafficText;
    public string UpdatedText => "Данные провайдера · " + (Group.Updated is { } at
        ? "обновлено " + at.ToLocalTime().ToString("dd.MM.yyyy HH:mm") : "время обновления не сообщено");
    public string WarningText => Group.Name + ": " + string.Join(" · ", new[]
    {
        Term is SubscriptionTerm.Expiring or SubscriptionTerm.Expired ? TermText : null,
        Traffic is SubscriptionTraffic.Low or SubscriptionTraffic.Exhausted ? TrafficText : null
    }.Where(p => p is not null)) + (Critical ? ". Обновите данные подписки." : "");

    // Порог + оплаченный срок/лимит: обновление счётчиков не отменяет скрытие,
    // а продление, пополнение и переход к более критичному порогу отменяют.
    public string? WarningKey => !HasWarning ? null : string.Join("|", Term switch
    {
        SubscriptionTerm.Expired => "expired",
        SubscriptionTerm.Expiring => Days < 1 ? "day1" : Days < 3 ? "day3" : "day14",
        _ => "term-ok"
    }, Traffic, Group.Metadata?.Expires?.ToUnixTimeSeconds(), Group.Metadata?.Total);

    internal static long RefreshKey(ProfileGroup? group, DateTimeOffset now) =>
        group?.Metadata?.Expires is not { } expires ? long.MinValue : expires <= now ? -1 : (long)Math.Floor((expires - now).TotalDays);

    public static SubscriptionState For(ProfileGroup group, DateTimeOffset now)
    {
        var meta = group.Metadata;
        var left = meta is { Total: > 0, Upload: >= 0, Download: >= 0 }
            ? (decimal?)Math.Max(0m, (decimal)meta.Total.Value - meta.Upload.Value - meta.Download.Value) : null;
        var term = meta?.Expires is not { } at ? SubscriptionTerm.Unknown
            : at <= now ? SubscriptionTerm.Expired : at - now < TimeSpan.FromDays(14) ? SubscriptionTerm.Expiring : SubscriptionTerm.Active;
        var traffic = meta?.Total == 0 ? SubscriptionTraffic.Unlimited : left is null ? SubscriptionTraffic.Unknown
            : left == 0 ? SubscriptionTraffic.Exhausted : left < 5m * 1073741824m ? SubscriptionTraffic.Low : SubscriptionTraffic.Remaining;
        return new() { Group = group, Term = term, Traffic = traffic, RemainingBytes = left,
            Used = left is { } bytes ? (double)(1m - bytes / meta!.Total!.Value) : null,
            Days = meta?.Expires is { } expires ? Math.Max(0, (int)(expires - now).TotalDays) : null };
    }
}
