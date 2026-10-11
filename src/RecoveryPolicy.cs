namespace RoutePilot;

public sealed class RecoveryPolicy
{
    private readonly Queue<DateTimeOffset> attempts = new();
    public TimeSpan? NextDelay(DateTimeOffset now)
    {
        while (attempts.Count > 0 && now - attempts.Peek() >= TimeSpan.FromMinutes(2)) attempts.Dequeue();
        if (attempts.Count >= 3) return null;
        var delay = TimeSpan.FromSeconds(Math.Pow(2, attempts.Count));
        attempts.Enqueue(now); return delay;
    }
    public void Reset() => attempts.Clear();
}
