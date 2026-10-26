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
    // pool — пул автовыбора (AutoPool): переключаться можно только на его серверы. Текущий
    // сервер остаётся кандидатом всегда — повторы на нём не запрещены.
    // machine — выбрана машина целиком (ServerMachines): это согласие на любой её протокол, и для
    // сторонней подписки перебор идёт по протоколам той же машины, а не только по выбранной записи.
    public static int[] For(IReadOnlyList<Profile> all, Profile current, bool anyNode = false, Func<Profile, bool>? pool = null, bool machine = false)
    {
        var selected = ProfileSelection.IndexOf(all, current);
        var key = ServerMachines.Key(current);
        var itself = Enumerable.Range(0, all.Count).Where(i => i == selected || machine && !all[i].IsFree && ServerMachines.Key(all[i]) == key).ToArray();
        // Бесплатный чужой узел — только выбранный вручную: ни автовыбор, ни подбор на него не переходят.
        bool Allowed(int i) => i == selected || !all[i].IsFree && (pool is null || pool(all[i]));
        if (anyNode) return Enumerable.Range(0, all.Count).Where(Allowed).ToArray();
        if (!current.IsService) return itself;
        var service = new List<int>();
        for (var i = 0; i < all.Count; i++) if (all[i].IsService && Allowed(i)) service.Add(i);
        return service.Count > 0 ? service.ToArray() : itself;
    }

    // Порядковый номер текущего узла внутри набора кандидатов: с него начинается перебор.
    public static int StartAt(int[] candidates, IReadOnlyList<Profile> all, Profile current)
    {
        var selected = ProfileSelection.IndexOf(all, current);
        var position = Array.IndexOf(candidates, selected);
        return position >= 0 ? position : 0;
    }

    // Порядок для «Автовыбора»: сначала замеренные — по оценке ServerMeasurements.Score (задержка и скорость), затем
    // без замера (в том числе только с откликом входа: его миллисекунды ничего не говорят),
    // в конце не ответившие. failed — узел, который только что перестал работать: он последний.
    public static int[] ByLatency(int[] candidates, IReadOnlyList<Profile> all, Func<Profile, ServerMeasurement?> latest, Profile? failed = null)
    {
        int Rank(int i) => failed is not null && ProfileSelection.Identity(all[i]) == ProfileSelection.Identity(failed) ? 3
            : latest(all[i]) switch { { Success: true, Handshake: false } => 0, null or { Success: true } => 1, _ => 2 };
        return candidates.OrderBy(Rank).ThenBy(i => latest(all[i]) is { Success: true, Handshake: false } m ? ServerMeasurements.Score(m) : double.MaxValue).ThenBy(i => i).ToArray();
    }
}
