using System.Windows;
using System.Windows.Controls;

namespace Oblachka;

// Сторонние подписки живут рядом с подпиской сервиса и ничем её не затрагивают.
// Вся работа с ними — в этом разделе настроек, чтобы главный экран оставался о сервисе.
public partial class MainWindow
{
    private async void AddExternalClick(object sender, RoutedEventArgs args)
    {
        if (operation is not null) return;
        var text = AppDialog.Ask(this, "Добавить подписку",
            "Ссылка подписки любого сервиса — или отдельные подключения vless://, trojan://, hy2://, JSON.",
            "https://…", "Добавить", note: "Резервные адреса той же подписки — каждый с новой строки, до пяти. Подписка появится отдельной вкладкой в «Все серверы»; остальные не изменятся.",
            multiline: true);
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            if (ServiceKey.Classify(text) == ServiceKey.InputKind.Profiles) { Import(text); CloseSettings(); return; }
            var sources = text.Contains("://") ? SubscriptionMirrors.ParseSources(text) : new[] { ServiceKey.ToSubscriptionUrl(text) };
            CloseSettings();
            await LoadSubscriptionAsync(sources, 0);
        }
        catch (UserError ex) { DetailText.Text = ex.Message; }
    }

    // Убрать все сторонние подписки и «Вручную» разом (прежняя кнопка; по одной — в списке подписок).
    private void RemoveExternalClick(object sender, RoutedEventArgs args)
    {
        if (operation is not null || externalProfiles.Count == 0) return;
        if (serviceProfiles.Count == 0)
        { DetailText.Text = "Это единственные подключения. Сначала добавьте ключ подписки на главном экране."; return; }
        var removingSelected = profile is not null && !profile.IsService;
        externalProfiles = Array.Empty<Profile>();
        if (removingSelected) profile = null;
        ApplyProfiles(!removingSelected);
        DetailText.Text = "Сторонние подписки убраны. Узлы вашей подписки на месте.";
        UpdateControls();
        QueueStateSave();
    }
}
