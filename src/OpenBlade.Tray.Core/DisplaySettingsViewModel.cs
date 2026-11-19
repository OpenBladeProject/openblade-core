using System.ComponentModel;
using System.Diagnostics;
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

public enum DisplaySettingsFailureKind
{
    None,
    SessionAgentUnavailable,
    ControlsUnavailable,
}

public sealed record DisplaySettingsSnapshot(
    bool IsAvailable,
    IReadOnlyList<DisplayRefreshRateOption> RefreshRateOptions,
    string? SelectedRefreshRateKey,
    bool IsHdrSupported,
    bool IsHdrEnabled,
    IReadOnlyList<DisplayColorProfileOption> ColorProfileOptions,
    string? SelectedColorProfileKey,
    string? StatusMessage = null,
    bool OperationSucceeded = true,
    string? DisplayName = null,
    int Width = 0,
    int Height = 0,
    int? BitsPerColorChannel = null,
    DisplaySettingsFailureKind FailureKind = DisplaySettingsFailureKind.None)
{
    public byte? BrightnessPercent { get; init; }
    public IReadOnlyList<byte> SupportedBrightnessLevels { get; init; } = [];
}

public sealed class DisplayProfileSettingsChangedEventArgs(
    PowerSource powerSource,
    DisplayProfileSettings previousSettings,
    DisplayProfileSettings settings)
    : EventArgs
{
    public PowerSource PowerSource { get; } = powerSource;

    public DisplayProfileSettings PreviousSettings { get; } = previousSettings;

    public DisplayProfileSettings Settings { get; } = settings;
}

public interface IDisplaySettingsAdapter
{
    Task<DisplaySettingsSnapshot> GetStateAsync(CancellationToken token);

    Task<DisplaySettingsSnapshot> SetRefreshRateAsync(
        PowerSource expectedPowerSource,
        string refreshRateKey,
        CancellationToken token);

    Task<DisplaySettingsSnapshot> SetHdrEnabledAsync(
        PowerSource expectedPowerSource,
        bool enabled,
        CancellationToken token);

    Task<DisplaySettingsSnapshot> SetColorProfileAsync(
        PowerSource expectedPowerSource,
        string colorProfileKey,
        CancellationToken token);

    Task<DisplaySettingsSnapshot> ApplyProfileAsync(
        PowerSource expectedPowerSource,
        DisplayProfileSettings settings,
        CancellationToken token);

    async Task<DisplaySettingsSnapshot> SetBrightnessAsync(
        PowerSource expectedPowerSource, byte percent, CancellationToken token) =>
        (await GetStateAsync(token).ConfigureAwait(false)) with
        {
            OperationSucceeded = false,
            StatusMessage = TrayText.Get("Display brightness is unavailable."),
        };
}

public sealed class UnavailableDisplaySettingsAdapter : IDisplaySettingsAdapter
{
    public static UnavailableDisplaySettingsAdapter Instance { get; } = new();

    private static DisplaySettingsSnapshot UnavailableState => new(
        IsAvailable: false,
        RefreshRateOptions: [],
        SelectedRefreshRateKey: null,
        IsHdrSupported: false,
        IsHdrEnabled: false,
        ColorProfileOptions: [],
        SelectedColorProfileKey: null,
        StatusMessage: TrayText.Get("Display settings are unavailable."));

    private UnavailableDisplaySettingsAdapter()
    {
    }

    public Task<DisplaySettingsSnapshot> GetStateAsync(CancellationToken token) =>
        Task.FromResult(UnavailableState);

    public Task<DisplaySettingsSnapshot> SetRefreshRateAsync(
        PowerSource expectedPowerSource,
        string refreshRateKey,
        CancellationToken token) => Task.FromResult(UnavailableState);

    public Task<DisplaySettingsSnapshot> SetHdrEnabledAsync(
        PowerSource expectedPowerSource,
        bool enabled,
        CancellationToken token) => Task.FromResult(UnavailableState);

    public Task<DisplaySettingsSnapshot> SetColorProfileAsync(
        PowerSource expectedPowerSource,
        string colorProfileKey,
        CancellationToken token) => Task.FromResult(UnavailableState);

    public Task<DisplaySettingsSnapshot> ApplyProfileAsync(
        PowerSource expectedPowerSource,
        DisplayProfileSettings settings,
        CancellationToken token) => Task.FromResult(UnavailableState);
}

public sealed class DisplaySettingsViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private const int MaximumRefreshFailureDiagnosticsPerWindow = 8;

    internal static readonly TimeSpan RefreshFailureDiagnosticInterval =
        TimeSpan.FromMinutes(5);

    private static string HdrColorProfileMessage =>
        TrayText.Get("Turn off HDR to change color profiles.");
    private static string DefaultColorProfileMessage =>
        TrayText.Get("Choose the color profile used by the built-in display.");

    private readonly IDisplaySettingsAdapter adapter;
    private readonly TimeProvider timeProvider;
    private readonly Action<string> diagnosticSink;
    private readonly HashSet<(Type ExceptionType, int HResult)> refreshFailureDiagnosticSignatures = [];
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private IReadOnlyList<DisplayRefreshRateOption> refreshRateOptions = [];
    private DisplayRefreshRateOption? selectedRefreshRate;
    private IReadOnlyList<DisplayColorProfileOption> colorProfileOptions = [];
    private DisplayColorProfileOption? selectedColorProfile;
    private bool isAvailable;
    private bool isHdrSupported;
    private bool isHdrEnabled;
    private byte? brightnessPercent;
    private IReadOnlyList<byte> supportedBrightnessLevels = [];
    private int pendingWriteCount;
    private PowerSource activePowerSource = PowerSource.ExternalUnknown;
    private DisplayProfileSettings activeProfileSettings =
        DisplayProfileSettings.Unconfigured();
    private bool hasPendingProfileChange;
    private bool profilePersistenceAvailable;
    private string statusMessage = TrayText.Get("Checking the built-in display...");
    private long? refreshFailureDiagnosticWindowTimestamp;
    private int disposed;

    public DisplaySettingsViewModel(IDisplaySettingsAdapter adapter)
        : this(
            adapter,
            TimeProvider.System,
            message => Trace.TraceError("{0}", message))
    {
    }

    internal DisplaySettingsViewModel(
        IDisplaySettingsAdapter adapter,
        TimeProvider timeProvider,
        Action<string> diagnosticSink)
    {
        this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        this.timeProvider = timeProvider
            ?? throw new ArgumentNullException(nameof(timeProvider));
        this.diagnosticSink = diagnosticSink
            ?? throw new ArgumentNullException(nameof(diagnosticSink));
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
                NotifyProfileRelationshipChanged();
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
                NotifyProfileRelationshipChanged();
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
                NotifyProfileRelationshipChanged();
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
                NotifyProfileRelationshipChanged();
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
                NotifyProfileRelationshipChanged();
            }
        }
    }

    public bool IsBusy => Volatile.Read(ref pendingWriteCount) > 0;

    private bool externalPowerPresent;
    public bool ExternalPowerPresent
    {
        get => externalPowerPresent;
        set { if (SetField(ref externalPowerPresent, value)) { NotifyControlStateChanged(); } }
    }

    private bool HasManualPowerContext => HasConfirmedPowerSource
        || ActivePowerSource == PowerSource.ExternalUnknown && ExternalPowerPresent;

    public PowerSource ActivePowerSource
    {
        get => activePowerSource;
        set
        {
            if (SetField(ref activePowerSource, value))
            {
                NotifyControlStateChanged();
                NotifyProfileRelationshipChanged();
            }
        }
    }

    public DisplayProfileSettings ActiveProfileSettings
    {
        get => activeProfileSettings;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (SetField(ref activeProfileSettings, value))
            {
                NotifyProfileRelationshipChanged();
            }
        }
    }

    public bool HasPendingProfileChange
    {
        get => hasPendingProfileChange;
        set
        {
            if (SetField(ref hasPendingProfileChange, value))
            {
                NotifyProfileRelationshipChanged();
            }
        }
    }

    public bool ProfilePersistenceAvailable
    {
        get => profilePersistenceAvailable;
        set
        {
            if (SetField(ref profilePersistenceAvailable, value))
            {
                NotifyProfileRelationshipChanged();
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
        HasManualPowerContext && IsAvailable && !IsBusy && RefreshRateOptions.Count > 0;

    public bool CanChangeHdr =>
        HasManualPowerContext && IsAvailable && IsHdrSupported && !IsBusy;

    public byte? BrightnessPercent
    {
        get => brightnessPercent;
        private set
        {
            if (SetField(ref brightnessPercent, value))
            {
                OnPropertyChanged(nameof(CurrentProfileSettings));
                OnPropertyChanged(nameof(BrightnessValue));
                OnPropertyChanged(nameof(BrightnessSliderValue));
                OnPropertyChanged(nameof(HasBrightnessReading));
                OnPropertyChanged(nameof(IsBrightnessUnavailable));
                NotifyProfileRelationshipChanged();
                NotifyControlStateChanged();
            }
        }
    }

    public IReadOnlyList<byte> SupportedBrightnessLevels => supportedBrightnessLevels;

    public double BrightnessValue => BrightnessPercent.HasValue ? BrightnessPercent.Value : double.NaN;

    public double BrightnessSliderValue => BrightnessPercent ?? 0;

    public bool HasBrightnessReading => BrightnessPercent.HasValue;

    public bool IsBrightnessUnavailable => !HasBrightnessReading;

    public bool CanChangeBrightness => HasManualPowerContext && IsAvailable && !IsBusy
        && BrightnessPercent.HasValue && SupportedBrightnessLevels.Count > 0;

    public bool CanChangeColorProfile =>
        HasManualPowerContext
        && IsAvailable
        && !IsHdrEnabled
        && !IsBusy
        && ColorProfileOptions.Count > 0;

    public bool CanCaptureCurrentSettings =>
        HasConfirmedPowerSource && IsAvailable && !IsBusy;

    public bool CanSaveCurrentSettingsToProfile =>
        ProfilePersistenceAvailable
        && CanCaptureCurrentSettings
        && (HasPendingProfileChange
            || !ActiveProfileSettings.IsConfigured
            || HasSaveableUnsetProfileField
            || !CurrentSettingsMatchActiveProfile);

    public bool HasProfileDifference =>
        ProfilePersistenceAvailable
        && HasConfirmedPowerSource
        && IsAvailable
        && ActiveProfileSettings.IsConfigured
        && !CurrentSettingsMatchActiveProfile;

    public string ProfileRelationshipStatus => !ProfilePersistenceAvailable
        || !HasConfirmedPowerSource
        || !IsAvailable
        ? TrayText.Get("Unavailable")
        : HasPendingProfileChange
            ? TrayText.Get("Temporary change not saved to this profile")
            : !ActiveProfileSettings.IsConfigured || HasSaveableUnsetProfileField
                ? TrayText.Get("Current settings are not saved to this profile")
                : CurrentSettingsMatchActiveProfile
                    ? TrayText.Get("Matches saved profile")
                    : TrayText.Get("Different from saved profile");

    private bool HasConfirmedPowerSource => ActivePowerSource is
        PowerSource.PluggedIn or PowerSource.Battery or PowerSource.UsbC;

    public DisplayProfileSettings CurrentProfileSettings => IsAvailable
        ? new DisplayProfileSettings(
            SelectedRefreshRate?.Key,
            IsHdrSupported ? IsHdrEnabled : null,
            SelectedColorProfile?.Key)
        {
            BrightnessPercent = BrightnessPercent,
        }
        : DisplayProfileSettings.Unconfigured();

    private bool HasSaveableUnsetProfileField =>
        IsAvailable
        && (ActiveProfileSettings.RefreshRateKey is null
            && SelectedRefreshRate is not null
            || !ActiveProfileSettings.HdrEnabled.HasValue
            && IsHdrSupported
            || ActiveProfileSettings.ColorProfileKey is null
            && !IsHdrEnabled
            && SelectedColorProfile is not null
            || !ActiveProfileSettings.BrightnessPercent.HasValue && BrightnessPercent.HasValue);

    private bool CurrentSettingsMatchActiveProfile =>
        ActiveProfileSettings.IsConfigured
        && IsAvailable
        && (ActiveProfileSettings.RefreshRateKey is null
            || string.Equals(
                ActiveProfileSettings.RefreshRateKey,
                SelectedRefreshRate?.Key,
                StringComparison.Ordinal))
        && (!ActiveProfileSettings.HdrEnabled.HasValue
            || ActiveProfileSettings.HdrEnabled == IsHdrEnabled)
        && BrightnessMatches(ActiveProfileSettings.BrightnessPercent, BrightnessPercent, SupportedBrightnessLevels)
        && ColorProfileMatches(ActiveProfileSettings.ColorProfileKey, IsHdrEnabled, SelectedColorProfile?.Key);

    public Task RefreshAsync(CancellationToken token = default)
    {
        string? previousRefreshRate = null;
        string? previousColorProfile = null;
        bool previousHdr = false;
        bool hadPriorState = false;

        return RunSerializedAsync(
            DisplayOperation.Refresh,
            operationToken =>
            {
                previousRefreshRate = SelectedRefreshRate?.Key;
                previousColorProfile = SelectedColorProfile?.Key;
                previousHdr = IsHdrEnabled;
                hadPriorState = IsAvailable;
                return adapter.GetStateAsync(operationToken);
            },
            token,
            state =>
            {
                if (!state.OperationSucceeded || !IsAvailable)
                {
                    return;
                }

                bool refreshChanged = hadPriorState &&
                    !string.Equals(previousRefreshRate, SelectedRefreshRate?.Key, StringComparison.Ordinal);
                bool colorChanged = hadPriorState &&
                    !string.Equals(previousColorProfile, SelectedColorProfile?.Key, StringComparison.OrdinalIgnoreCase);
                bool hdrChanged = hadPriorState && previousHdr != IsHdrEnabled;
                int changedCount = (refreshChanged ? 1 : 0) + (colorChanged ? 1 : 0) + (hdrChanged ? 1 : 0);
                if (changedCount > 0)
                {
                    StatusMessage = changedCount > 1
                        ? TrayText.Get("Display settings changed outside OpenBlade.")
                        : refreshChanged
                            ? TrayText.Get("Refresh rate changed outside OpenBlade.")
                            : hdrChanged
                                ? TrayText.Get("HDR changed outside OpenBlade.")
                                : TrayText.Get("Color profile changed outside OpenBlade.");
                }
            },
            showBusy: false);
    }

    public Task SelectRefreshRateAsync(
        DisplayRefreshRateOption? option,
        CancellationToken token = default)
    {
        if (option is null || !HasManualPowerContext)
        {
            return Task.CompletedTask;
        }

        PowerSource sourceAtStart = ActivePowerSource;
        return RunUserMutationAsync(
            DisplayOperation.RefreshRateChange,
            sourceAtStart,
            () => !string.Equals(option.Key, SelectedRefreshRate?.Key, StringComparison.Ordinal),
            operationToken => adapter.SetRefreshRateAsync(sourceAtStart, option.Key, operationToken),
            state => string.Equals(state.SelectedRefreshRateKey, option.Key, StringComparison.Ordinal),
            token);
    }

    public Task SetHdrEnabledAsync(bool enabled, CancellationToken token = default)
    {
        if (!HasManualPowerContext)
        {
            return Task.CompletedTask;
        }

        PowerSource sourceAtStart = ActivePowerSource;
        return RunUserMutationAsync(
            DisplayOperation.HdrChange,
            sourceAtStart,
            () => enabled != IsHdrEnabled,
            operationToken => adapter.SetHdrEnabledAsync(sourceAtStart, enabled, operationToken),
            state => state.IsHdrEnabled == enabled,
            token);
    }

    public Task SetBrightnessAsync(byte percent, CancellationToken token = default)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percent, (byte)100);
        if (!HasManualPowerContext || IsBusy)
        {
            return Task.CompletedTask;
        }
        byte target = percent;
        PowerSource sourceAtStart = ActivePowerSource;
        return RunUserMutationAsync(
            DisplayOperation.BrightnessChange,
            sourceAtStart,
            () =>
            {
                if (!IsAvailable || !BrightnessPercent.HasValue || SupportedBrightnessLevels.Count == 0)
                {
                    return false;
                }
                target = ResolveBrightnessLevel(percent);
                return target != BrightnessPercent;
            },
            operationToken => adapter.SetBrightnessAsync(sourceAtStart, target, operationToken),
            state => state.BrightnessPercent == target,
            token);
    }

    private byte ResolveBrightnessLevel(byte requested) =>
        FindNearestBrightnessLevel(requested, SupportedBrightnessLevels) ?? requested;

    private static byte? FindNearestBrightnessLevel(byte requested, IReadOnlyList<byte>? levels) => (levels ?? [])
        .Where(level => level <= 100).OrderBy(level => Math.Abs(level - requested))
        .ThenBy(level => level).Select(level => (byte?)level).FirstOrDefault();

    private static bool BrightnessMatches(byte? expected, byte? current, IReadOnlyList<byte>? levels) =>
        !expected.HasValue || current.HasValue && FindNearestBrightnessLevel(expected.Value, levels) == current;

    public Task SelectColorProfileAsync(
        DisplayColorProfileOption? option,
        CancellationToken token = default)
    {
        if (option is null || !HasManualPowerContext)
        {
            return Task.CompletedTask;
        }

        PowerSource sourceAtStart = ActivePowerSource;
        return RunUserMutationAsync(
            DisplayOperation.ColorProfileChange,
            sourceAtStart,
            () => !IsHdrEnabled
                && !string.Equals(option.Key, SelectedColorProfile?.Key, StringComparison.OrdinalIgnoreCase),
            operationToken => adapter.SetColorProfileAsync(sourceAtStart, option.Key, operationToken),
            state => string.Equals(state.SelectedColorProfileKey, option.Key, StringComparison.OrdinalIgnoreCase),
            token);
    }

    public async Task<bool> ApplyProfileAsync(
        PowerSource expectedPowerSource,
        DisplayProfileSettings? settings,
        CancellationToken token = default)
    {
        if (settings is null || !settings.IsConfigured)
        {
            return true;
        }

        bool succeeded = false;
        await RunSerializedAsync(
            DisplayOperation.ProfileApply,
            operationToken => adapter.ApplyProfileAsync(
                expectedPowerSource,
                settings,
                operationToken),
            token,
            state => succeeded = ProfileSettingsMatch(settings, state));
        return succeeded;
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

    private Task RunUserMutationAsync(
        DisplayOperation operationKind,
        PowerSource sourceAtStart,
        Func<bool> shouldRun,
        Func<CancellationToken, Task<DisplaySettingsSnapshot>> operation,
        Func<DisplaySettingsSnapshot, bool> confirmsTarget,
        CancellationToken token)
    {
        DisplayProfileSettings previous = DisplayProfileSettings.Unconfigured();
        return RunSerializedAsync(
            operationKind,
            operationToken =>
            {
                previous = CurrentProfileSettings;
                return operation(operationToken);
            },
            token,
            state =>
            {
                if (state.OperationSucceeded && confirmsTarget(state))
                {
                    RaiseUserSettingsChanged(sourceAtStart, previous);
                }
            },
            shouldRun: shouldRun);
    }

    private async Task RunSerializedAsync(
        DisplayOperation operationKind,
        Func<CancellationToken, Task<DisplaySettingsSnapshot>> operation,
        CancellationToken token,
        Action<DisplaySettingsSnapshot>? applied = null,
        bool showBusy = true,
        Func<bool>? shouldRun = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        using CancellationTokenSource linkedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                lifetimeCancellation.Token,
                token);
        bool enteredGate = false;
        if (showBusy)
        {
            RegisterPendingWrite();
        }

        try
        {
            await operationGate.WaitAsync(linkedCancellation.Token);
            enteredGate = true;
            // A queued refresh may change both the current value and supported
            // ranges. Decide whether to write only after its readback is applied.
            if (shouldRun is not null && !shouldRun())
            {
                return;
            }
            DisplaySettingsSnapshot state = await operation(linkedCancellation.Token);
            Apply(state, isRefresh: operationKind == DisplayOperation.Refresh);
            applied?.Invoke(state);
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (enteredGate)
        {
            ReportUnexpectedFailure(operationKind, exception);
            StatusMessage = TrayText.Get("Display setting failed.");
        }
        finally
        {
            if (enteredGate)
            {
                operationGate.Release();
            }

            if (showBusy)
            {
                UnregisterPendingWrite();
            }
        }
    }

    private void ReportUnexpectedFailure(
        DisplayOperation operationKind,
        Exception exception)
    {
        long? refreshFailureTimestamp = operationKind == DisplayOperation.Refresh
            ? timeProvider.GetTimestamp()
            : null;
        var signature = (exception.GetType(), exception.HResult);
        bool startsNewRefreshWindow = refreshFailureTimestamp is { } currentTimestamp
            && (refreshFailureDiagnosticWindowTimestamp is not { } windowTimestamp
                || timeProvider.GetElapsedTime(windowTimestamp, currentTimestamp)
                    >= RefreshFailureDiagnosticInterval);
        if (refreshFailureTimestamp.HasValue
            && !startsNewRefreshWindow
            && (refreshFailureDiagnosticSignatures.Contains(signature)
                || refreshFailureDiagnosticSignatures.Count
                    >= MaximumRefreshFailureDiagnosticsPerWindow))
        {
            return;
        }

        string operation = operationKind switch
        {
            DisplayOperation.Refresh => "refresh",
            DisplayOperation.RefreshRateChange => "refresh-rate change",
            DisplayOperation.HdrChange => "HDR change",
            DisplayOperation.BrightnessChange => "brightness change",
            DisplayOperation.ColorProfileChange => "color-profile change",
            DisplayOperation.ProfileApply => "profile apply",
            _ => operationKind.ToString(),
        };
        string message = DiagnosticTrace.FormatException(
            "OpenBlade.Tray.DisplaySettingsViewModel",
            "ApplyDisplayOperation",
            exception,
            operation);
        try
        {
            diagnosticSink(message);
            if (refreshFailureTimestamp is { } reportedTimestamp)
            {
                if (startsNewRefreshWindow)
                {
                    refreshFailureDiagnosticSignatures.Clear();
                    refreshFailureDiagnosticWindowTimestamp = reportedTimestamp;
                }

                refreshFailureDiagnosticSignatures.Add(signature);
            }
        }
        catch (Exception diagnosticException)
        {
            // Diagnostics must not replace the user-visible operation result.
            DiagnosticTrace.TraceException(
                "OpenBlade.Tray.DisplaySettingsViewModel",
                "ReportDiagnostic",
                diagnosticException);
        }
    }

    private enum DisplayOperation
    {
        Refresh,
        RefreshRateChange,
        HdrChange,
        BrightnessChange,
        ColorProfileChange,
        ProfileApply,
    }

    private void RegisterPendingWrite()
    {
        if (Interlocked.Increment(ref pendingWriteCount) == 1)
        {
            OnPropertyChanged(nameof(IsBusy));
            NotifyControlStateChanged();
        }
    }

    private void UnregisterPendingWrite()
    {
        if (Interlocked.Decrement(ref pendingWriteCount) == 0)
        {
            OnPropertyChanged(nameof(IsBusy));
            NotifyControlStateChanged();
        }
    }

    private void RaiseUserSettingsChanged(
        PowerSource powerSource,
        DisplayProfileSettings previous) =>
        UserSettingsChanged?.Invoke(
            this,
            new DisplayProfileSettingsChangedEventArgs(
                powerSource,
                previous,
                CurrentProfileSettings));

    private static bool ProfileSettingsMatch(
        DisplayProfileSettings settings,
        DisplaySettingsSnapshot state) =>
        state.OperationSucceeded
        && state.IsAvailable
        && (settings.RefreshRateKey is null
            || string.Equals(
                settings.RefreshRateKey,
                state.SelectedRefreshRateKey,
                StringComparison.Ordinal))
        && (!settings.HdrEnabled.HasValue
            || settings.HdrEnabled == state.IsHdrEnabled)
        && BrightnessMatches(settings.BrightnessPercent, state.BrightnessPercent, state.SupportedBrightnessLevels)
        && ColorProfileMatches(settings.ColorProfileKey, state.IsHdrEnabled, state.SelectedColorProfileKey);

    // Match the Session Agent's confirmation contract: SDR profiles are dormant
    // while the observed output is HDR, and Windows profile identities ignore case.
    private static bool ColorProfileMatches(string? expectedKey, bool hdrEnabled, string? currentKey) =>
        expectedKey is null || hdrEnabled
        || string.Equals(expectedKey, currentKey, StringComparison.OrdinalIgnoreCase);

    private void Apply(DisplaySettingsSnapshot state, bool isRefresh)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!state.OperationSucceeded && !string.IsNullOrWhiteSpace(state.StatusMessage))
        {
            Trace.TraceInformation(
                "Display settings operation reported: {0}",
                state.StatusMessage);
        }
        if (IsAvailable && !state.OperationSucceeded && !state.IsAvailable)
        {
            StatusMessage = FailureMessage(state.FailureKind, isRefresh);
            return;
        }

        RefreshRateOptions = StableOptionCollection.ReuseEquivalent(
            RefreshRateOptions,
            state.RefreshRateOptions ?? []);
        SelectedRefreshRate = RefreshRateOptions.FirstOrDefault(
            option => string.Equals(
                option.Key,
                state.SelectedRefreshRateKey,
                StringComparison.Ordinal));
        ColorProfileOptions = StableOptionCollection.ReuseEquivalent(
            ColorProfileOptions,
            state.ColorProfileOptions ?? []);
        SelectedColorProfile = ColorProfileOptions.FirstOrDefault(
            option => string.Equals(
                option.Key,
                state.SelectedColorProfileKey,
                StringComparison.OrdinalIgnoreCase));
        IsAvailable = state.IsAvailable;
        IsHdrSupported = state.IsHdrSupported;
        IsHdrEnabled = state.IsHdrEnabled;
        IReadOnlyList<byte> brightnessLevels = StableOptionCollection.ReuseEquivalent(
            supportedBrightnessLevels, state.SupportedBrightnessLevels ?? []);
        if (!ReferenceEquals(supportedBrightnessLevels, brightnessLevels))
        {
            supportedBrightnessLevels = brightnessLevels;
            OnPropertyChanged(nameof(SupportedBrightnessLevels));
            OnPropertyChanged(nameof(CanChangeBrightness));
            NotifyProfileRelationshipChanged();
        }
        BrightnessPercent = state.BrightnessPercent;
        StatusMessage = !state.OperationSucceeded
            ? FailureMessage(state.FailureKind, isRefresh)
            : state.IsAvailable
                ? FormatDisplayDetails(state)
                : TrayText.Get("Display settings are unavailable.");
    }

    private static string FailureMessage(
        DisplaySettingsFailureKind failureKind,
        bool isRefresh) => failureKind switch
        {
            DisplaySettingsFailureKind.SessionAgentUnavailable =>
                TrayText.Get("The display session agent is not responding."),
            DisplaySettingsFailureKind.ControlsUnavailable =>
                TrayText.Get("Display controls are unavailable."),
            _ when isRefresh => TrayText.Get("Display settings could not be refreshed."),
            _ => TrayText.Get("Display setting failed."),
        };

    private static string FormatDisplayDetails(DisplaySettingsSnapshot state)
    {
        List<string> details = [];
        if (!string.IsNullOrWhiteSpace(state.DisplayName))
        {
            details.Add(state.DisplayName);
        }
        if (state.Width > 0 && state.Height > 0)
        {
            details.Add(TrayText.Format("{0} × {1}", state.Width, state.Height));
        }

        DisplayRefreshRateOption? refresh = state.RefreshRateOptions.FirstOrDefault(
            option => string.Equals(
                option.Key,
                state.SelectedRefreshRateKey,
                StringComparison.Ordinal));
        if (refresh is not null)
        {
            details.Add(refresh.Label);
        }
        if (state.BitsPerColorChannel is > 0)
        {
            details.Add(TrayText.Format("{0}-bit", state.BitsPerColorChannel));
        }
        if (state.IsHdrSupported)
        {
            details.Add(state.IsHdrEnabled
                ? TrayText.Get("HDR on")
                : TrayText.Get("HDR off"));
        }
        if (!state.IsHdrEnabled)
        {
            DisplayColorProfileOption? colorProfile = state.ColorProfileOptions.FirstOrDefault(
                option => string.Equals(
                    option.Key,
                    state.SelectedColorProfileKey,
                    StringComparison.OrdinalIgnoreCase));
            if (colorProfile is not null)
            {
                details.Add(colorProfile.Label);
            }
        }

        return details.Count == 0
            ? TrayText.Get("Display available")
            : string.Join(TrayText.Get(" · "), details);
    }

    private void NotifyControlStateChanged()
    {
        OnPropertyChanged(nameof(CanChangeRefreshRate));
        OnPropertyChanged(nameof(CanChangeHdr));
        OnPropertyChanged(nameof(CanChangeBrightness));
        OnPropertyChanged(nameof(CanChangeColorProfile));
        OnPropertyChanged(nameof(CanCaptureCurrentSettings));
        OnPropertyChanged(nameof(CanSaveCurrentSettingsToProfile));
    }

    private void NotifyProfileRelationshipChanged()
    {
        OnPropertyChanged(nameof(ProfileRelationshipStatus));
        OnPropertyChanged(nameof(CanSaveCurrentSettingsToProfile));
        OnPropertyChanged(nameof(HasProfileDifference));
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
