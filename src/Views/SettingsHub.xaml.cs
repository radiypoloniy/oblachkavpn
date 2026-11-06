using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Oblachka.Views;

public partial class SettingsHub : UserControl
{
    public sealed record Topic(string Title, string Detail, string IconKey, Geometry Icon);

    public SettingsHub()
    {
        InitializeComponent();
        LaunchCloud.Mood = CloudMood.Happy;
        SetTopic(NetworkOpen, "Сети и защита", "Правила для Wi-Fi и блокировка интернета без VPN", "IcoShield");
        SetTopic(AppearanceOpen, "Вид и уведомления", "Тема, трей, уведомления и горячая клавиша", "IcoApp");
        SetTopic(SubscriptionOpen, "Подписки и ключ", "Срок подписки, остаток трафика и список серверов", "IcoKey");
        SetTopic(TransferOpen, "Обновления и перенос", "Версия приложения и резервная копия настроек", "IcoRefresh");
        SetAdvanced(false);
        SizeChanged += (_, args) => Topics.Columns = args.NewSize.Width >= 530 ? 2 : 1;
    }

    internal void SetAdvanced(bool changed) => SetTopic(AdvancedOpen, "Тонкая настройка",
        changed ? "Настройки изменены · туннель, DNS и диагностика" : "Туннель, DNS и диагностика", "IcoSliders");

    private void SetTopic(Button button, string title, string detail, string icon)
    {
        button.Content = new Topic(title, detail, icon, (Geometry)FindResource(icon));
        System.Windows.Automation.AutomationProperties.SetName(button, title);
    }
}
