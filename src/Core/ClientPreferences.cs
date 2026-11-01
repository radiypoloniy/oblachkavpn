using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Oblachka;

// Значения по умолчанию определены без создания окна. Существующее сохранение ClientSnapshot остаётся прежним.
public sealed class ClientPreferences : INotifyPropertyChanged
{
    private readonly Dictionary<string, bool> values = new()
    {
        [nameof(StayInTray)] = true, [nameof(AutoRecover)] = true, [nameof(AutoRefresh)] = true,
        [nameof(ConnectOnLaunch)] = true, [nameof(Notices)] = true, [nameof(LocalNetwork)] = true,
        [nameof(Hotkey)] = true, [nameof(UpdateAuto)] = true
    };
    public bool StayInTray { get => Get(); set => Set(value); }
    public bool AutoRecover { get => Get(); set => Set(value); }
    public bool AutoRefresh { get => Get(); set => Set(value); }
    public bool ConnectOnLaunch { get => Get(); set => Set(value); }
    public bool Notices { get => Get(); set => Set(value); }
    public bool LocalNetwork { get => Get(); set => Set(value); }
    public bool QuicOverTcp { get => Get(); set => Set(value); }
    public bool CompatibleStack { get => Get(); set => Set(value); }
    public bool Hotkey { get => Get(); set => Set(value); }
    public bool SystemProxy { get => Get(); set => Set(value); }
    public bool SystemTunnel { get => Get(); set => Set(value); }
    public bool RussiaDirect { get => Get(); set => Set(value); }
    public bool UpdateAuto { get => Get(); set => Set(value); }
    public bool UpdatePrerelease { get => Get(); set => Set(value); }
    public bool SecureUnknown { get => Get(); set => Set(value); }
    public bool Lockdown { get => Get(); set => Set(value); }
    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Get([CallerMemberName] string name = "") => values.TryGetValue(name, out var value) && value;
    private void Set(bool value, [CallerMemberName] string name = "")
    {
        if (Get(name) == value) return;
        values[name] = value; PropertyChanged?.Invoke(this, new(name));
    }
}
