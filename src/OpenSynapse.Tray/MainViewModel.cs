using OpenSynapse.Contracts;

namespace OpenSynapse.Tray;

public enum ThemePreference
{
    FollowWindows,
    Light,
    Dark,
}

public sealed class MainViewModel
{
    private MainViewModel(ProfileSet profiles)
    {
        IsReadOnly = true;
        ConnectionStatus = "Service unavailable";
        SelectedPerformance = profiles.PluggedIn.Performance;
        SelectedFanMode = profiles.PluggedIn.Fans;
        ChargeLimitPercent = profiles.BatteryPolicy.ChargeLimitPercent;
        Theme = ThemePreference.FollowWindows;
    }

    public bool IsReadOnly { get; }

    public string ConnectionStatus { get; }

    public PerformancePreset SelectedPerformance { get; }

    public FanMode SelectedFanMode { get; }

    public int ChargeLimitPercent { get; }

    public ThemePreference Theme { get; }

    public static MainViewModel CreateDisconnected() =>
        new(ProfileDefaults.CreateSafeDefaults());
}
