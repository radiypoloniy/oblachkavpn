using System.Windows;
using System.Windows.Controls;

namespace RoutePilot.Views;

public sealed class CountryFlag : Image
{
    public static readonly DependencyProperty CodeProperty = DependencyProperty.Register(nameof(Code), typeof(string), typeof(CountryFlag),
        new PropertyMetadata(null, (owner, _) => ((CountryFlag)owner).Refresh()));
    public string? Code { get => (string?)GetValue(CodeProperty); set => SetValue(CodeProperty, value); }

    public CountryFlag()
    {
        Loaded += (_, _) => Refresh();
        IsVisibleChanged += (_, _) => { if (IsVisible) Refresh(); };
    }

    private async void Refresh()
    {
        var code = Code;
        Source = FlagImages.Local(code);
        if (code is null || !IsVisible || !FlagImages.AllowNetwork) return;
        var downloaded = await FlagImages.LoadAsync(code);
        if (Code == code && downloaded is not null) Source = downloaded;
    }
}
