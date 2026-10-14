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
        var dialog = new Window
        {
            Owner = this, Title = "Сторонняя подписка", Width = 680, Height = 330,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Background, FontFamily = FontFamily
        };
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock
        {
            Text = "HTTPS-адрес подписки другого сервиса. Можно указать до пяти адресов одной подписки, каждый с новой строки.",
            Foreground = Foreground, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
        });
        var input = new TextBox
        {
            MaxLength = 20 * 1024, AcceptsReturn = true, Height = 110,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Text = string.Join("\n", externalSources)
        };
        panel.Children.Add(input);
        panel.Children.Add(new TextBlock
        {
            Text = "Подписка вашего сервиса останется на месте. Узлы сторонней подписки помечаются в списке; подбор, измерения и автообновление по ним не выполняются.",
            Foreground = Foreground, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 12)
        });
        var button = new Button { Content = "Загрузить", HorizontalAlignment = HorizontalAlignment.Right };
        panel.Children.Add(button);
        string? url = null;
        button.Click += (_, _) => { url = input.Text; input.Clear(); dialog.DialogResult = true; };
        dialog.Closed += (_, _) => input.Clear();
        dialog.Content = panel; StyleDialog(dialog);
        if (dialog.ShowDialog() != true || url is null) return;
        try
        {
            var sources = SubscriptionMirrors.ParseSources(url);
            if (ServiceKey.SourceOf(sources) == ProfileSource.Service)
            { DetailText.Text = "Это адрес вашей подписки. Он добавляется ключом на главном экране."; return; }
            CloseSettings();
            await LoadSubscriptionAsync(sources, 0);
        }
        catch (UserError ex) { DetailText.Text = ex.Message; }
        finally { url = null; }
    }

    private void RemoveExternalClick(object sender, RoutedEventArgs args)
    {
        if (operation is not null || externalProfiles.Count == 0) return;
        if (serviceProfiles.Count == 0)
        { DetailText.Text = "Это единственные подключения. Сначала добавьте ключ подписки на главном экране."; return; }
        // Если сейчас выбран сторонний узел, выбор переедет на первый узел сервиса.
        var removingSelected = profile is not null && !profile.IsService;
        externalProfiles = Array.Empty<Profile>();
        externalSources = Array.Empty<string>();
        if (removingSelected) profile = null;
        ApplyProfiles(!removingSelected);
        DetailText.Text = "Сторонние подписки убраны. Узлы вашей подписки на месте.";
        UpdateControls();
        QueueStateSave();
    }

    private void RefreshExternalSummary()
    {
        Settings.ExternalSummary.Text = externalProfiles.Count == 0
            ? "Сторонних подписок нет."
            : $"Подключений из сторонних источников: {externalProfiles.Count}" +
              (externalSources.Count > 0 ? $"; адресов подписки: {externalSources.Count}." : " (добавлены ссылкой или файлом).");
        Settings.RemoveExternalButton.IsEnabled = externalProfiles.Count > 0 && operation is null;
        Settings.AddExternalButton.IsEnabled = operation is null;
    }
}
