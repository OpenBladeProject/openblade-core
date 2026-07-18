using System.ComponentModel;
using System.Runtime.CompilerServices;
using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public sealed record DisplayRefreshRateOption(
    string Key,
    string Label,
    bool IsDynamic = false);

public sealed record DisplayColorProfileOption(
    string Key,
    string Label);

public sealed record DisplaySettingsSnapshot(
    bool IsAvailable,
    IReadOnlyList<DisplayRefreshRateOption> RefreshRateOptions,
    string? SelectedRefreshRateKey,
    bool IsHdrSupported,
    bool IsHdrEnabled,
    IReadOnlyList<DisplayColorProfileOption> ColorProfileOptions,
    string? SelectedColorProfileKey,
    string? StatusMessage = null);

public sealed class DisplayProfileSettingsChangedEventArgs(
    DisplayProfileSettings previousSettings,
    DisplayProfileSettings settings)
    : EventArgs
{
    public DisplayProfileSettings PreviousSettings { get; } = previousSettings;

    public DisplayProfileSettings Settings { get; } = settings;
}

public interface IDisplaySettingsAdapter
{
    Task<DisplaySettingsSnapshot> GetStateAsync(CancellationToken token);

    Task<DisplaySettingsSnapshot> SetRefreshRateAsync(
        string refreshRateKey,
        CancellationToken token);

    Task<DisplaySettingsSnapshot> SetHdrEnabledAsync(
        bool enabled,
        CancellationToken token);

    Task<DisplaySettingsSnapshot> SetColorProfileAsync(
        string colorProfileKey,
        CancellationToken token);
}

public sealed class UnavailableDisplaySettingsAdapter : IDisplaySettingsAdapter
{
    public static UnavailableDisplaySettingsAdapter Instance { get; } = new();

    private static readonly DisplaySettingsSnapshot UnavailableState = new(
        IsAvailable: false,
        RefreshRateOptions: [],
        SelectedRefreshRateKey: null,
        IsHdrSupported: false,
        IsHdrEnabled: false,
        ColorProfileOptions: [],
        SelectedColorProfileKey: null,
        StatusMessage: "Display settings are unavailable.");

    private UnavailableDisplaySettingsAdapter()
    {
    }

    public Task<DisplaySettingsSnapshot> GetStateAsync(CancellationToken token) =>
        Task.FromResult(UnavailableState);

    public Task<DisplaySettingsSnapshot> SetRefreshRateAsync(
        string refreshRateKey,
        CancellationToken token) => Task.FromResult(UnavailableState);

    public Task<DisplaySettingsSnapshot> SetHdrEnabledAsync(
        bool enabled,
        CancellationToken token) => Task.FromResult(UnavailableState);

    public Task<DisplaySettingsSnapshot> SetColorProfileAsync(
        string colorProfileKey,
        CancellationToken token) => Task.FromResult(UnavailableState);
}

public sealed class DisplaySettingsViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private const string HdrColorProfileMessage =
        "Turn off HDR to change color profiles.";
    private const string DefaultColorProfileMessage =
        "Choose the color profile used by the built-in display.";

    private readonly IDisplaySettingsAdapter adapter;
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private IReadOnlyList<DisplayRefreshRateOption> refreshRateOptions = [];
    private DisplayRefreshRateOption? selectedRefreshRate;
    private IReadOnlyList<DisplayColorProfileOption> colorProfileOptions = [];
    private DisplayColorProfileOption? selectedColorProfile;
    private bool isAvailable;
    private bool isHdrSupported;
    private bool isHdrEnabled;
    private bool isBusy;
    private string statusMessage = "Checking the built-in display...";
    private int disposed;

    public DisplaySettingsViewModel(IDisplaySettingsAdapter adapter)
    {
        this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler<DisplayProfileSettingsChangedEventArgs>? UserSettingsChanged;

    public IReadOnlyList<DisplayRefreshRateOption> RefreshRateOptions
    {
        get => refreshRateOptions;
        private set
        {
            if (SetField(ref refreshRateOptions, value))
            {
                NotifyControlStateChanged();
            }
        }
    }

    public DisplayRefreshRateOption? SelectedRefreshRate
    {
        get => selectedRefreshRate;
        private set
        {
            if (SetField(ref selectedRefreshRate, value))
            {
                OnPropertyChanged(nameof(CurrentProfileSettings));
            }
        }
    }

    public IReadOnlyList<DisplayColorProfileOption> ColorProfileOptions
    {
        get => colorProfileOptions;
        private set
        {
            if (SetField(ref colorProfileOptions, value))
            {
                NotifyControlStateChanged();
            }
        }
    }

    public DisplayColorProfileOption? SelectedColorProfile
    {
        get => selectedColorProfile;
        private set
        {
            if (SetField(ref selectedColorProfile, value))
            {
                OnPropertyChanged(nameof(CurrentProfileSettings));
            }
        }
    }

    public bool IsAvailable
    {
        get => isAvailable;
        private set
        {
            if (SetField(ref isAvailable, value))
            {
                OnPropertyChanged(nameof(CurrentProfileSettings));
                NotifyControlStateChanged();
            }
        }
    }

    public bool IsHdrSupported
    {
        get => isHdrSupported;
        private set
        {
            if (SetField(ref isHdrSupported, value))
            {
                OnPropertyChanged(nameof(CurrentProfileSettings));
                NotifyControlStateChanged();
            }
        }
    }

    public bool IsHdrEnabled
    {
        get => isHdrEnabled;
        private set
        {
            if (SetField(ref isHdrEnabled, value))
            {
                OnPropertyChanged(nameof(ColorProfileDescription));
                OnPropertyChanged(nameof(CurrentProfileSettings));
                NotifyControlStateChanged();
            }
        }
    }

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (SetField(ref isBusy, value))
            {
                NotifyControlStateChanged();
            }
        }
    }

    public string StatusMessage
    {
        get => statusMessage;
        private set => SetField(ref statusMessage, value);
    }

    public string ColorProfileDescription => IsHdrEnabled
        ? HdrColorProfileMessage
        : DefaultColorProfileMessage;

    public bool CanChangeRefreshRate =>
        IsAvailable && !IsBusy && RefreshRateOptions.Count > 0;

    public bool CanChangeHdr => IsAvailable && IsHdrSupported && !IsBusy;

    public bool CanChangeColorProfile =>
        IsAvailable && !IsHdrEnabled && !IsBusy && ColorProfileOptions.Count > 0;

    public DisplayProfileSettings CurrentProfileSettings => IsAvailable
        ? new DisplayProfileSettings(
            SelectedRefreshRate?.Key,
            IsHdrSupported ? IsHdrEnabled : null,
            SelectedColorProfile?.Key)
        : DisplayProfileSettings.Unconfigured();

    public async Task RefreshAsync(CancellationToken token = default)
    {
        string? previousRefreshRate = SelectedRefreshRate?.Key;
        string? previousColorProfile = SelectedColorProfile?.Key;
        bool previousHdr = IsHdrEnabled;
        bool hadPriorState = IsAvailable;

        await RunSerializedAsync(adapter.GetStateAsync, token, showBusy: false);

        bool refreshChanged = hadPriorState &&
            !string.Equals(previousRefreshRate, SelectedRefreshRate?.Key, StringComparison.Ordinal);
        bool colorChanged = hadPriorState &&
            !string.Equals(previousColorProfile, SelectedColorProfile?.Key, StringComparison.Ordinal);
        bool hdrChanged = hadPriorState && previousHdr != IsHdrEnabled;
        int changedCount = (refreshChanged ? 1 : 0) + (colorChanged ? 1 : 0) + (hdrChanged ? 1 : 0);
        if (changedCount > 0)
        {
            StatusMessage = changedCount > 1
                ? "Display settings changed outside OpenBlade."
                : refreshChanged
                    ? "Refresh rate changed outside OpenBlade."
                    : hdrChanged
                        ? "HDR changed outside OpenBlade."
                        : "Color profile changed outside OpenBlade.";
        }
    }

    public Task SelectRefreshRateAsync(
        DisplayRefreshRateOption? option,
        CancellationToken token = default)
    {
        if (option is null
            || string.Equals(option.Key, SelectedRefreshRate?.Key, StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }

        DisplayProfileSettings previous = DisplayProfileSettings.Unconfigured();
        return RunSerializedAsync(
            operationToken =>
            {
                previous = CurrentProfileSettings;
                return adapter.SetRefreshRateAsync(option.Key, operationToken);
            },
            token,
            state =>
            {
                if (string.Equals(
                    state.SelectedRefreshRateKey,
                    option.Key,
                    StringComparison.Ordinal))
                {
                    RaiseUserSettingsChanged(previous);
                }
            });
    }

    public Task SetHdrEnabledAsync(bool enabled, CancellationToken token = default)
    {
        if (enabled == IsHdrEnabled)
        {
            return Task.CompletedTask;
        }

        DisplayProfileSettings previous = DisplayProfileSettings.Unconfigured();
        return RunSerializedAsync(
            operationToken =>
            {
                previous = CurrentProfileSettings;
                return adapter.SetHdrEnabledAsync(enabled, operationToken);
            },
            token,
            state =>
            {
                if (state.IsHdrEnabled == enabled)
                {
                    RaiseUserSettingsChanged(previous);
                }
            });
    }

    public Task SelectColorProfileAsync(
        DisplayColorProfileOption? option,
        CancellationToken token = default)
    {
        if (option is null
            || IsHdrEnabled
            || string.Equals(option.Key, SelectedColorProfile?.Key, StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }

        DisplayProfileSettings previous = DisplayProfileSettings.Unconfigured();
        return RunSerializedAsync(
            operationToken =>
            {
                previous = CurrentProfileSettings;
                return adapter.SetColorProfileAsync(option.Key, operationToken);
            },
            token,
            state =>
            {
                if (string.Equals(
                    state.SelectedColorProfileKey,
                    option.Key,
                    StringComparison.Ordinal))
                {
                    RaiseUserSettingsChanged(previous);
                }
            });
    }

    public Task ApplyProfileAsync(
        DisplayProfileSettings? settings,
        CancellationToken token = default)
    {
        if (settings is null || !settings.IsConfigured)
        {
            return Task.CompletedTask;
        }

        return RunSerializedAsync(
            async operationToken =>
            {
                DisplaySettingsSnapshot state = await adapter.GetStateAsync(operationToken);
                if (!state.IsAvailable)
                {
                    return state;
                }

                if (settings.RefreshRateKey is not null
                    && !string.Equals(
                        state.SelectedRefreshRateKey,
                        settings.RefreshRateKey,
                        StringComparison.Ordinal))
                {
                    state = await adapter.SetRefreshRateAsync(
                        settings.RefreshRateKey,
                        operationToken);
                }

                if (settings.HdrEnabled.HasValue
                    && state.IsHdrSupported
                    && state.IsHdrEnabled != settings.HdrEnabled.Value)
                {
                    state = await adapter.SetHdrEnabledAsync(
                        settings.HdrEnabled.Value,
                        operationToken);
                }

                if (settings.ColorProfileKey is not null
                    && !state.IsHdrEnabled
                    && !string.Equals(
                        state.SelectedColorProfileKey,
                        settings.ColorProfileKey,
                        StringComparison.Ordinal))
                {
                    state = await adapter.SetColorProfileAsync(
                        settings.ColorProfileKey,
                        operationToken);
                }

                return state;
            },
            token);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        await lifetimeCancellation.CancelAsync();
        await operationGate.WaitAsync();
        operationGate.Release();
        lifetimeCancellation.Dispose();
        operationGate.Dispose();
    }

    private async Task RunSerializedAsync(
        Func<CancellationToken, Task<DisplaySettingsSnapshot>> operation,
        CancellationToken token,
        Action<DisplaySettingsSnapshot>? applied = null,
        bool showBusy = true)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        using CancellationTokenSource linkedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                lifetimeCancellation.Token,
                token);
        await operationGate.WaitAsync(linkedCancellation.Token);
        try
        {
            if (showBusy)
            {
                IsBusy = true;
            }

            DisplaySettingsSnapshot state = await operation(linkedCancellation.Token);
            Apply(state);
            applied?.Invoke(state);
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            StatusMessage = $"Display setting failed: {exception.Message}";
        }
        finally
        {
            if (showBusy)
            {
                IsBusy = false;
            }

            operationGate.Release();
        }
    }

    private void RaiseUserSettingsChanged(DisplayProfileSettings previous) =>
        UserSettingsChanged?.Invoke(
            this,
            new DisplayProfileSettingsChangedEventArgs(previous, CurrentProfileSettings));

    private void Apply(DisplaySettingsSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        RefreshRateOptions = ReuseWhenValueEqual(
            RefreshRateOptions,
            state.RefreshRateOptions ?? []);
        SelectedRefreshRate = RefreshRateOptions.FirstOrDefault(
            option => string.Equals(
                option.Key,
                state.SelectedRefreshRateKey,
                StringComparison.Ordinal));
        ColorProfileOptions = ReuseWhenValueEqual(
            ColorProfileOptions,
            state.ColorProfileOptions ?? []);
        SelectedColorProfile = ColorProfileOptions.FirstOrDefault(
            option => string.Equals(
                option.Key,
                state.SelectedColorProfileKey,
                StringComparison.Ordinal));
        IsAvailable = state.IsAvailable;
        IsHdrSupported = state.IsHdrSupported;
        IsHdrEnabled = state.IsHdrEnabled;
        StatusMessage = state.StatusMessage ?? (state.IsAvailable
            ? "Built-in display detected."
            : "Display settings are unavailable.");
    }

    private static IReadOnlyList<T> ReuseWhenValueEqual<T>(
        IReadOnlyList<T> current,
        IReadOnlyList<T> incoming)
    {
        if (current.Count != incoming.Count)
        {
            return incoming;
        }

        EqualityComparer<T> comparer = EqualityComparer<T>.Default;
        for (int index = 0; index < current.Count; index++)
        {
            if (!comparer.Equals(current[index], incoming[index]))
            {
                return incoming;
            }
        }

        return current;
    }

    private void NotifyControlStateChanged()
    {
        OnPropertyChanged(nameof(CanChangeRefreshRate));
        OnPropertyChanged(nameof(CanChangeHdr));
        OnPropertyChanged(nameof(CanChangeColorProfile));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
