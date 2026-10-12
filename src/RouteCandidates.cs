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
    public static int[] For(IReadOnlyList<Profile> all, Profile current)
    {
        var selected = ProfileSelection.IndexOf(all, current);
        var itself = selected >= 0 ? new[] { selected } : Array.Empty<int>();
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
}
