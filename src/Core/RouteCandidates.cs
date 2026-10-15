namespace Oblachka;

// Какие узлы вправе перебирать подбор маршрута и автовосстановление.
//
// Переключить человека на стороннюю конфигурацию мы не вправе: её параметры, срок
// действия и владельца мы не знаем. Поэтому перебор идёт только по узлам сервиса.
// Если выбран сторонний узел, перебор ограничен им самим: повторы остаются, смены узла нет.
//
// Возвращаются индексы в исходном списке, чтобы вызывающий мог выделить найденный узел
// в том же порядке, в каком он показан пользователю.
public static class RouteCandidates
{
    // anyNode — включён «Автовыбор»: это согласие на переключение между всеми узлами
    // подписок, в том числе сторонней (а в редакции «вставьте свою подписку» других узлов и нет).
    public static int[] For(IReadOnlyList<Profile> all, Profile current, bool anyNode = false)
    {
        var selected = ProfileSelection.IndexOf(all, current);
        var itself = selected >= 0 ? new[] { selected } : Array.Empty<int>();
        if (anyNode) return Enumerable.Range(0, all.Count).ToArray();
        if (!current.IsService) return itself;
        var service = new List<int>();
        for (var i = 0; i < all.Count; i++) if (all[i].IsService) service.Add(i);
        return service.Count > 0 ? service.ToArray() : itself;
    }

    // Порядковый номер текущего узла внутри набора кандидатов: с него начинается перебор.
    public static int StartAt(int[] candidates, IReadOnlyList<Profile> all, Profile current)
    {
        var selected = ProfileSelection.IndexOf(all, current);
        var position = Array.IndexOf(candidates, selected);
        return position >= 0 ? position : 0;
    }

    // Порядок для «Автовыбора»: сначала с успешным откликом — от быстрого к медленному, затем
    // без замера, в конце не ответившие. failed — узел, который только что перестал работать:
    // он идёт последним, чтобы подбор не начинал с него.
    public static int[] ByLatency(int[] candidates, IReadOnlyList<Profile> all, Func<Profile, ServerMeasurement?> latest, Profile? failed = null)
    {
        int Rank(int i) => failed is not null && ProfileSelection.Identity(all[i]) == ProfileSelection.Identity(failed) ? 3
            : latest(all[i]) switch { { Success: true } => 0, null => 1, _ => 2 };
        return candidates.OrderBy(Rank).ThenBy(i => latest(all[i]) is { Success: true } m ? m.Milliseconds : long.MaxValue).ThenBy(i => i).ToArray();
    }
}
