using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Oblachka;

public partial class MainWindow
{
    private void CheckCountryRows(List<string> checks)
    {
        var cases = new[] { ("🇪🇺Авто — оптимальное подключение", "EU", "Европа"),
            ("🇻🇳Рабочий сервер", "VN", "Вьетнам"), ("DE Обход #1", "DE", "Германия"),
            ("FI Хельсинки", "FI", "Финляндия"), ("Нидерланды, Amsterdam", "NL", "Нидерланды"),
            ("Switzerland Reality", "CH", "Швейцария"), ("EUОбход #2", "EU", "Европа") };
        foreach (var (name, code, countryName) in cases)
        {
            var link = "vless://11111111-1111-4111-8111-111111111111@example.invalid:443?security=tls#" + Uri.EscapeDataString(name);
            var parsed = Profiles.Parse(link) with { Source = ProfileSource.Service };
            if (parsed.Country.Code != code || parsed.Country.Name != countryName || FlagImages.Local(code) is null)
                throw new InvalidOperationException($"Synthetic country case {code}: resolved {parsed.Country.Code}/{parsed.Country.Name}, name={parsed.Name}");
            // Узел сервиса: в строке только его имя, без страны, транспорта и пометок.
            var labels = RenderRow(parsed);
            if (labels.Length != 1 || labels[0].Text != parsed.DisplayName || labels[0].Text == countryName)
                throw new InvalidOperationException("Server row replaces its subscription name with a country or includes extra labels: " + string.Join(" | ", labels.Select(l => l.Text)) + " (всего " + labels.Length + ")");
            // Сторонний узел: то же имя плюс видимая пометка источника.
            var foreignLabels = RenderRow(parsed with { Source = ProfileSource.External });
            if (foreignLabels.Length != 2 || foreignLabels[0].Text != parsed.DisplayName || foreignLabels[1].Text != "Сторонний")
                throw new InvalidOperationException("External node is not marked in the server list: " + string.Join(" | ", foreignLabels.Select(l => l.Text)));
            if (Profiles.Fingerprint(parsed) != Profiles.Fingerprint(parsed with { Name = "Другое имя" }))
                throw new InvalidOperationException("Country presentation changes connection identity");
        }
        if (CountryCatalog.Resolve("Обход без страны").Code is not null)
            throw new InvalidOperationException("Unknown country was fabricated");
        checks.Add("Subscription flags, ISO prefixes and country names resolve to Russian labels and real PNG flags; unknown locations remain unknown");
        if (ServerLabels.DisplayName("🇪🇺Авто") != "Авто" || ServerLabels.DisplayName("🇪🇺Обход 2") != "Обход 2" ||
            ServerLabels.DisplayName("🇻🇳Рабочий сервер") != "Рабочий сервер" || ServerLabels.DisplayName("Мой сервер") != "Мой сервер")
            throw new InvalidOperationException("Subscription labels were renamed instead of separating the flag");
        checks.Add("Server rows preserve subscription names and separate flags; no country replacement, transport or core labels");
    }

    // Рендерит одну строку списка по шаблону и возвращает её видимые подписи.
    private TextBlock[] RenderRow(Profile profile)
    {
        var row = (FrameworkElement)ProfilePicker.ItemTemplate.LoadContent();
        row.DataContext = profile;
        row.Measure(new Size(400, 100)); row.Arrange(new Rect(0, 0, 400, 100)); row.UpdateLayout();
        // IsVisible требует подключения к дереву окна, поэтому смотрим Visibility по цепочке
        // родителей: скрытая пометка источника не должна считаться подписью строки.
        return FindLabels(row).Where(Shown).ToArray();
    }

    private static bool Shown(DependencyObject? node)
    {
        for (; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is UIElement element && element.Visibility != Visibility.Visible) return false;
        return true;
    }

    private static IEnumerable<TextBlock> FindLabels(DependencyObject root)
    {
        if (root is TextBlock label) yield return label;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in FindLabels(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
