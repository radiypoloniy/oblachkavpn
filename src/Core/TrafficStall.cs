namespace Oblachka;

// Признак обрыва по трафику ядра туннеля: программы отправляют, а в ответ не приходит ничего
// несколько секунд подряд. Так обрыв виден раньше плановой проверки связи (раз в 20–45 с).
// Простой (ни туда, ни обратно) — не обрыв: человек просто ничего не делает.
public sealed class TrafficStall
{
    public const int Seconds = 5;
    // Меньше этого — фон (DNS, keepalive), по нему ничего не скажешь.
    public const long MinUp = 512;
    private int silent;
    private bool raised;

    // Раз в секунду: скорость отдачи и приёма в байтах. true — один раз на эпизод: пора проверять связь.
    public bool Feed(long up, long down)
    {
        if (down > 0) { silent = 0; raised = false; return false; }
        if (up < MinUp) return false;
        silent++;
        if (silent < Seconds || raised) return false;
        raised = true;
        return true;
    }

    public void Reset() { silent = 0; raised = false; }
}
