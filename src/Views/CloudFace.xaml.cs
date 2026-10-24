using System.Windows;
using System.Windows.Controls;

namespace Oblachka.Views;

public enum CloudMood { Sleep, Wait, Happy, Worried }

// Облачко-аватар в карточке статуса (CloudFace.xaml).
public partial class CloudFace : UserControl
{
    private CloudMood mood = CloudMood.Sleep;

    public CloudFace() => InitializeComponent();

    public CloudMood Mood
    {
        get => mood;
        set
        {
            if (mood == value) return;
            mood = value;
            Sleep.Visibility = value == CloudMood.Sleep ? Visibility.Visible : Visibility.Collapsed;
            Wait.Visibility = value == CloudMood.Wait ? Visibility.Visible : Visibility.Collapsed;
            Happy.Visibility = value == CloudMood.Happy ? Visibility.Visible : Visibility.Collapsed;
            Worried.Visibility = value == CloudMood.Worried ? Visibility.Visible : Visibility.Collapsed;
            Body.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, value switch
            {
                CloudMood.Sleep => "NightCloud", CloudMood.Worried => "SunsetCloud", _ => "CloudWhite"
            });
        }
    }
}
