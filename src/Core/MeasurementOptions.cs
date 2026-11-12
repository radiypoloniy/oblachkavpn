namespace Oblachka;

// Политика списка серверов; проверка здоровья действующего VPN от неё не зависит.
public sealed record MeasurementOptions
{
    public bool ManualOnly { get; init; }
    public bool AllowConnected { get; init; }
    public bool BackgroundSpeed { get; init; }
    public int IntervalMinutes { get; init; } = 10;
    public int Parallelism { get; init; } = 4;
    public MeasurementOptions Normalize() => this with
    {
        IntervalMinutes = IntervalMinutes is 5 or 10 or 30 ? IntervalMinutes : 10,
        Parallelism = Parallelism is >= 1 and <= 4 ? Parallelism : 4
    };
}
