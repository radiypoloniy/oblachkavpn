namespace Oblachka;

public sealed class ClientProfiles
{
    public ProfilePickerState Picker { get; } = new();
    public Profile? Selected { get; set; }
    public Profile? Running { get; set; }
    public IReadOnlyList<Profile> Imported { get; set; } = Array.Empty<Profile>();
    public List<ProfileGroup> Groups { get; } = new();
    public Dictionary<string, IReadOnlyList<Profile>> GroupProfiles { get; } = new();
    public Dictionary<string, ServerTweak> Tweaks { get; } = new(StringComparer.Ordinal);
    public ServerTweak? TweakFor(Profile item) => Tweaks.GetValueOrDefault(ServerTweak.Key(item));

    public void Clear()
    {
        Picker.ItemsSource = Array.Empty<Profile>();
        Selected = Running = null; Imported = Array.Empty<Profile>();
        Groups.Clear(); GroupProfiles.Clear(); Tweaks.Clear();
    }
}
