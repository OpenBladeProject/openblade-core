using System.ComponentModel;
using System.Runtime.CompilerServices;
using OpenSynapse.Contracts;

namespace OpenSynapse.Tray;

public enum ThemePreference
{
    FollowWindows,
    Light,
    Dark,
}

public sealed class MainViewModel : INotifyPropertyChanged
{
    private bool isReadOnly;
    private string connectionStatus;
    private ThemePreference theme;

    private MainViewModel(ProfileSet profiles)
    {
        isReadOnly = true;
        connectionStatus = "Service unavailable";
        SelectedPerformance = profiles.PluggedIn.Performance;
        SelectedFanMode = profiles.PluggedIn.Fans;
        ChargeLimitPercent = profiles.BatteryPolicy.ChargeLimitPercent;
        theme = ThemePreference.FollowWindows;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsReadOnly
    {
        get => isReadOnly;
        private set => SetField(ref isReadOnly, value);
    }

    public string ConnectionStatus
    {
        get => connectionStatus;
        private set => SetField(ref connectionStatus, value);
    }

    public PerformancePreset SelectedPerformance { get; }

    public FanMode SelectedFanMode { get; }

    public int ChargeLimitPercent { get; }

    public ThemePreference Theme
    {
        get => theme;
        set => SetField(ref theme, value);
    }

    public IReadOnlyList<ThemePreference> ThemeOptions { get; } = Enum.GetValues<ThemePreference>();

    public static MainViewModel CreateDisconnected() =>
        new(ProfileDefaults.CreateSafeDefaults());

    public void ApplyStatus(ServiceStatusSnapshot status)
    {
        ArgumentNullException.ThrowIfNull(status);
        IsReadOnly = !status.DeviceVerified
            || status.WritesBlocked
            || status.Conflicts.Count > 0;
        ConnectionStatus = status.Message;
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
