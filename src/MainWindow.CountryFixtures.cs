using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RoutePilot;

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
            var parsed = Profiles.Parse(link);
            if (parsed.Country.Code != code || parsed.Country.Name != countryName || FlagImages.Local(code) is null)
                throw new InvalidOperationException($"Synthetic country case {code}: resolved {parsed.Country.Code}/{parsed.Country.Name}, name={parsed.Name}");
            var row = (FrameworkElement)ProfilePicker.ItemTemplate.LoadContent();
            row.DataContext = parsed;
            row.Measure(new Size(400, 100)); row.Arrange(new Rect(0, 0, 400, 100)); row.UpdateLayout();
            var labels = FindLabels(row).ToArray();
            if (labels.Length != 1 || labels[0].Text != parsed.DisplayName || labels[0].Text == countryName)
                throw new InvalidOperationException("Server row replaces its subscription name with a country or includes extra labels");
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

    private static IEnumerable<TextBlock> FindLabels(DependencyObject root)
    {
        if (root is TextBlock label) yield return label;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in FindLabels(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
