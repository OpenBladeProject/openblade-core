using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public enum ThemePreference
{
    FollowWindows,
    Light,
    Dark,
}

public sealed class MainViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly IServiceClient serviceClient;
    private readonly IUiDispatcher dispatcher;
    private readonly IDiagnosticArchiveSaver diagnosticArchiveSaver;
    private readonly IStartupRegistration startupRegistration;
    private readonly CancellationTokenSource lifetime = new();
    private readonly object startSync = new();
    private readonly AsyncRelayCommand[] commands;
    private ServiceStatusSnapshot? latestStatus;
    private Task? watchTask;
    private Task? startupInitializationTask;
    private bool isConnected;
    private bool isReadOnly = true;
    private bool canControlHardware;
    private string connectionStatus = "Service unavailable";
    private string operationStatusMessage = string.Empty;
    private string telemetryStatus = "Telemetry unavailable";
    private string controlAccessStatus = "Service disconnected · hardware controls disabled";
    private string activePowerSourceLabel = "Power source unavailable";
    private string performanceSourceLabel = "Unavailable";
    private string fanSourceLabel = "Unavailable";
    private string lightingSourceLabel = "Unavailable";
    private string batterySourceLabel = "Unavailable";
    private bool hasControllerAdvisory;
    private string controllerAdvisoryText = string.Empty;
    private bool hasControllerDetectionWarning;
    private string controllerDetectionWarningText = string.Empty;
    private bool hasExternalChanges;
    private string externalChangesText = string.Empty;
    private IReadOnlyList<PerformancePreset> availablePerformancePresets = [];
    private IReadOnlyList<FanMode> availableFanModes = [];
    private IReadOnlyList<LightingEffect> availableLightingEffects = [];
    private IReadOnlyList<LightingSettings> availableLightingSettings = [];
    private bool isPerformanceVisible;
    private bool isFanControlVisible;
    private bool isLightingVisible;
    private PerformancePreset selectedPerformance = PerformancePreset.Balanced;
    private FanMode selectedFanMode = FanMode.Automatic;
    private LightingSettings selectedLighting = ProfileDefaults.CreateSafeDefaults().PluggedIn.Lighting;
    private int chargeLimitPercent = 80;
    private bool chargeLimitEditorDirty;
    private IReadOnlyList<int> chargeLimitOptions = [];
    private bool batteryProtectionEnabled = true;
    private bool fullChargeOverride;
    private bool temporaryFullChargeStateKnown;
    private DeviceCapabilities capabilities = DeviceCapabilities.Unavailable();
    private TelemetrySnapshot? telemetry;
    private ManualOverride<PerformancePreset>? performanceOverride;
    private ManualOverride<FanMode>? fanOverride;
    private ManualOverride<LightingSettings>? lightingOverride;
    private ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
    private PerformancePreset pluggedInPerformanceEditor = PerformancePreset.Balanced;
    private FanMode pluggedInFanModeEditor = FanMode.Automatic;
    private LightingSettings pluggedInLightingEditor = ProfileDefaults.CreateSafeDefaults().PluggedIn.Lighting;
    private PerformancePreset batteryPerformanceEditor = PerformancePreset.Silent;
    private FanMode batteryFanModeEditor = FanMode.Automatic;
    private LightingSettings batteryLightingEditor = ProfileDefaults.CreateSafeDefaults().Battery.Lighting;
    private int profileChargeLimitEditor = 80;
    private bool profileEditorsDirty;
    private LightingEffect lightingEffectEditor = LightingEffect.Off;
    private int lightingBrightnessEditor;
    private int lightingRedEditor;
    private int lightingGreenEditor;
    private int lightingBlueEditor;
    private bool lightingEditorDirty;
    private ThemePreference theme = ThemePreference.FollowWindows;
    private bool keepManualChangesUntilCleared;
    private bool startAtSignIn;
    private bool startupStateKnown;
    private string startupStatusMessage = string.Empty;
    private int disposed;
    private long latestAcceptedRevision = -1;
    private long connectionGeneration;
    private bool subscriptionConnected;

    public MainViewModel(IServiceClient serviceClient, IUiDispatcher dispatcher)
        : this(
            serviceClient,
            dispatcher,
            new UnavailableDiagnosticArchiveSaver(),
            new UnavailableStartupRegistration())
    {
    }

    public MainViewModel(
        IServiceClient serviceClient,
        IUiDispatcher dispatcher,
        IDiagnosticArchiveSaver diagnosticArchiveSaver)
        : this(
            serviceClient,
            dispatcher,
            diagnosticArchiveSaver,
            new UnavailableStartupRegistration())
    {
    }

    public MainViewModel(
        IServiceClient serviceClient,
        IUiDispatcher dispatcher,
        IDiagnosticArchiveSaver diagnosticArchiveSaver,
        IStartupRegistration startupRegistration)
    {
        this.serviceClient = serviceClient ?? throw new ArgumentNullException(nameof(serviceClient));
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        this.diagnosticArchiveSaver = diagnosticArchiveSaver
            ?? throw new ArgumentNullException(nameof(diagnosticArchiveSaver));
        this.startupRegistration = startupRegistration
            ?? throw new ArgumentNullException(nameof(startupRegistration));

        SetPerformanceCommand = CreateCommand(
            SetPerformanceAsync,
            CanSetPerformance);
        SetFanModeCommand = CreateCommand(
            SetFanModeAsync,
            CanSetFanMode);
        SetLightingCommand = CreateCommand(
            SetLightingAsync,
            parameter => parameter is LightingSettings value
                && CanMutate
                && LightingSupported(value, latestStatus!.Capabilities!));
        SetChargeLimitCommand = CreateCommand(
            SetChargeLimitAsync,
            parameter => parameter is int value && CanSetChargeLimit(value));
        ToggleBatteryProtectionCommand = CreateCommand(
            ToggleBatteryProtectionAsync,
            CanToggleBatteryProtection);
        ToggleFullChargeCommand = CreateCommand(
            ToggleFullChargeAsync,
            CanSetFullChargeOverride);
        ClearManualOverridesCommand = CreateCommand(
            ClearManualOverridesAsync,
            _ => Volatile.Read(ref disposed) == 0 && IsConnected);
        SaveProfilesCommand = CreateCommand(
            SaveProfilesAsync,
            CanSaveProfiles);
        ExportDiagnosticsCommand = CreateCommand(
            ExportDiagnosticsAsync,
            _ => Volatile.Read(ref disposed) == 0 && IsConnected);
        SetStartAtSignInCommand = CreateCommand(
            SetStartAtSignInAsync,
            parameter => parameter is bool requested
                && Volatile.Read(ref disposed) == 0
                && StartupStateKnown
                && requested != StartAtSignIn);
        commands =
        [
            (AsyncRelayCommand)SetPerformanceCommand,
            (AsyncRelayCommand)SetFanModeCommand,
            (AsyncRelayCommand)SetLightingCommand,
            (AsyncRelayCommand)SetChargeLimitCommand,
            (AsyncRelayCommand)ToggleBatteryProtectionCommand,
            (AsyncRelayCommand)ToggleFullChargeCommand,
            (AsyncRelayCommand)ClearManualOverridesCommand,
            (AsyncRelayCommand)SaveProfilesCommand,
            (AsyncRelayCommand)ExportDiagnosticsCommand,
            (AsyncRelayCommand)SetStartAtSignInCommand,
        ];
    }

    private sealed class UnavailableDiagnosticArchiveSaver : IDiagnosticArchiveSaver
    {
        public Task<DiagnosticArchiveSaveResult> SaveAsync(
            DiagnosticArchive archive,
            CancellationToken token) =>
            Task.FromException<DiagnosticArchiveSaveResult>(
                new InvalidOperationException("A diagnostic archive saver was not configured."));
    }

    private sealed class UnavailableStartupRegistration : IStartupRegistration
    {
        public Task<bool> IsEnabledAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(false);
        }

        public Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken) =>
            Task.FromException(new InvalidOperationException(
                "Startup registration is unavailable."));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsConnected
    {
        get => isConnected;
        private set => SetField(ref isConnected, value);
    }

    public bool IsReadOnly
    {
        get => isReadOnly;
        private set => SetField(ref isReadOnly, value);
    }

    public bool CanControlHardware
    {
        get => canControlHardware;
        private set => SetField(ref canControlHardware, value);
    }

    public string ConnectionStatus
    {
        get => connectionStatus;
        private set => SetField(ref connectionStatus, value);
    }

    public string OperationStatusMessage
    {
        get => operationStatusMessage;
        private set => SetField(ref operationStatusMessage, value);
    }

    public string TelemetryStatus
    {
        get => telemetryStatus;
        private set => SetField(ref telemetryStatus, value);
    }

    public string ControlAccessStatus
    {
        get => controlAccessStatus;
        private set => SetField(ref controlAccessStatus, value);
    }

    public string PowerAndBatteryTelemetryLabel => FormatPowerAndBatteryTelemetry(Telemetry);

    public string CpuTelemetryLabel => FormatTemperature(Telemetry?.CpuTemperatureCelsius);

    public string GpuTelemetryLabel => FormatTemperature(Telemetry?.GpuTemperatureCelsius);

    public string FanTelemetryLabel => Telemetry?.FanRpm is int value
        ? $"{value.ToString(CultureInfo.InvariantCulture)} RPM"
        : "Unavailable";

    public string ActivePowerSourceLabel
    {
        get => activePowerSourceLabel;
        private set => SetField(ref activePowerSourceLabel, value);
    }

    public string PerformanceSourceLabel
    {
        get => performanceSourceLabel;
        private set => SetField(ref performanceSourceLabel, value);
    }

    public string FanSourceLabel
    {
        get => fanSourceLabel;
        private set => SetField(ref fanSourceLabel, value);
    }

    public string LightingSourceLabel
    {
        get => lightingSourceLabel;
        private set => SetField(ref lightingSourceLabel, value);
    }

    public string BatterySourceLabel
    {
        get => batterySourceLabel;
        private set => SetField(ref batterySourceLabel, value);
    }

    public bool HasControllerAdvisory
    {
        get => hasControllerAdvisory;
        private set => SetField(ref hasControllerAdvisory, value);
    }

    public string ControllerAdvisoryText
    {
        get => controllerAdvisoryText;
        private set => SetField(ref controllerAdvisoryText, value);
    }

    public bool HasControllerDetectionWarning
    {
        get => hasControllerDetectionWarning;
        private set => SetField(ref hasControllerDetectionWarning, value);
    }

    public string ControllerDetectionWarningText
    {
        get => controllerDetectionWarningText;
        private set => SetField(ref controllerDetectionWarningText, value);
    }

    public bool HasExternalChanges
    {
        get => hasExternalChanges;
        private set => SetField(ref hasExternalChanges, value);
    }

    public string ExternalChangesText
    {
        get => externalChangesText;
        private set => SetField(ref externalChangesText, value);
    }

    public IReadOnlyList<PerformancePreset> AvailablePerformancePresets
    {
        get => availablePerformancePresets;
        private set => SetField(ref availablePerformancePresets, value);
    }

    public IReadOnlyList<FanMode> AvailableFanModes
    {
        get => availableFanModes;
        private set => SetField(ref availableFanModes, value);
    }

    public IReadOnlyList<LightingEffect> AvailableLightingEffects
    {
        get => availableLightingEffects;
        private set => SetField(ref availableLightingEffects, value);
    }

    public IReadOnlyList<LightingSettings> AvailableLightingSettings
    {
        get => availableLightingSettings;
        private set => SetField(ref availableLightingSettings, value);
    }

    public bool IsPerformanceVisible
    {
        get => isPerformanceVisible;
        private set => SetField(ref isPerformanceVisible, value);
    }

    public bool IsFanControlVisible
    {
        get => isFanControlVisible;
        private set => SetField(ref isFanControlVisible, value);
    }

    public bool IsLightingVisible
    {
        get => isLightingVisible;
        private set => SetField(ref isLightingVisible, value);
    }

    public bool IsProfileControlVisible =>
        IsPerformanceVisible || IsFanControlVisible || IsLightingVisible;

    public PerformancePreset SelectedPerformance
    {
        get => selectedPerformance;
        private set => SetField(ref selectedPerformance, value);
    }

    public FanMode SelectedFanMode
    {
        get => selectedFanMode;
        private set => SetField(ref selectedFanMode, value);
    }

    public LightingSettings SelectedLighting
    {
        get => selectedLighting;
        private set => SetField(ref selectedLighting, value);
    }

    public int ChargeLimitPercent
    {
        get => chargeLimitPercent;
        set
        {
            if (chargeLimitPercent == value)
            {
                return;
            }

            chargeLimitEditorDirty = true;
            SetField(ref chargeLimitPercent, value);
            NotifyCommands();
        }
    }

    public IReadOnlyList<int> ChargeLimitOptions
    {
        get => chargeLimitOptions;
        private set => SetField(ref chargeLimitOptions, value);
    }

    public bool BatteryProtectionEnabled
    {
        get => batteryProtectionEnabled;
        private set
        {
            if (batteryProtectionEnabled == value)
            {
                return;
            }

            SetField(ref batteryProtectionEnabled, value);
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(BatteryProtectionActionLabel)));
        }
    }

    public string BatteryProtectionActionLabel => BatteryProtectionEnabled
        ? "Turn off battery protection"
        : "Restore charge limit";

    public bool FullChargeOverride
    {
        get => fullChargeOverride;
        private set
        {
            if (fullChargeOverride == value)
            {
                return;
            }

            SetField(ref fullChargeOverride, value);
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(EnableFullChargeOverride)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(FullChargeActionLabel)));
        }
    }

    public bool EnableFullChargeOverride => !FullChargeOverride;

    public string FullChargeActionLabel => !temporaryFullChargeStateKnown
        || !Capabilities.SupportsTemporaryFullCharge
            ? "Unavailable"
            : FullChargeOverride
                ? "Cancel charge to 100%"
                : "Charge to 100% until unplugged";

    public DeviceCapabilities Capabilities
    {
        get => capabilities;
        private set
        {
            if (EqualityComparer<DeviceCapabilities>.Default.Equals(capabilities, value))
            {
                return;
            }

            SetField(ref capabilities, value);
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(FullChargeActionLabel)));
        }
    }

    public TelemetrySnapshot? Telemetry
    {
        get => telemetry;
        private set
        {
            if (EqualityComparer<TelemetrySnapshot?>.Default.Equals(telemetry, value))
            {
                return;
            }

            telemetry = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Telemetry)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(PowerAndBatteryTelemetryLabel)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(CpuTelemetryLabel)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(GpuTelemetryLabel)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(FanTelemetryLabel)));
        }
    }

    public ManualOverride<PerformancePreset>? PerformanceOverride
    {
        get => performanceOverride;
        private set => SetOverrideField(ref performanceOverride, value);
    }

    public ManualOverride<FanMode>? FanOverride
    {
        get => fanOverride;
        private set => SetOverrideField(ref fanOverride, value);
    }

    public ManualOverride<LightingSettings>? LightingOverride
    {
        get => lightingOverride;
        private set => SetOverrideField(ref lightingOverride, value);
    }

    public string ManualOverrideStatus
    {
        get
        {
            List<string> active = [];
            AddOverrideStatus(active, "Performance", PerformanceOverride);
            AddOverrideStatus(active, "Fans", FanOverride);
            AddOverrideStatus(active, "Lighting", LightingOverride);
            return active.Count == 0
                ? "No manual overrides active."
                : string.Join(" | ", active);
        }
    }

    public ProfileSet Profiles
    {
        get => profiles;
        private set => SetField(ref profiles, value);
    }

    public PerformancePreset PluggedInPerformanceEditor
    {
        get => pluggedInPerformanceEditor;
        set => SetEditorField(ref pluggedInPerformanceEditor, value);
    }

    public FanMode PluggedInFanModeEditor
    {
        get => pluggedInFanModeEditor;
        set => SetEditorField(ref pluggedInFanModeEditor, value);
    }

    public LightingSettings PluggedInLightingEditor
    {
        get => pluggedInLightingEditor;
        set => SetEditorField(ref pluggedInLightingEditor, value);
    }

    public PerformancePreset BatteryPerformanceEditor
    {
        get => batteryPerformanceEditor;
        set => SetEditorField(ref batteryPerformanceEditor, value);
    }

    public FanMode BatteryFanModeEditor
    {
        get => batteryFanModeEditor;
        set => SetEditorField(ref batteryFanModeEditor, value);
    }

    public LightingSettings BatteryLightingEditor
    {
        get => batteryLightingEditor;
        set => SetEditorField(ref batteryLightingEditor, value);
    }

    public int ProfileChargeLimitEditor
    {
        get => profileChargeLimitEditor;
        set => SetEditorField(ref profileChargeLimitEditor, value);
    }

    public LightingEffect LightingEffectEditor
    {
        get => lightingEffectEditor;
        set => SetLightingEditorField(ref lightingEffectEditor, value);
    }

    public int LightingBrightnessEditor
    {
        get => lightingBrightnessEditor;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 100);
            SetLightingEditorField(ref lightingBrightnessEditor, value);
        }
    }

    public int LightingRedEditor
    {
        get => lightingRedEditor;
        set => SetLightingColorField(ref lightingRedEditor, value);
    }

    public int LightingGreenEditor
    {
        get => lightingGreenEditor;
        set => SetLightingColorField(ref lightingGreenEditor, value);
    }

    public int LightingBlueEditor
    {
        get => lightingBlueEditor;
        set => SetLightingColorField(ref lightingBlueEditor, value);
    }

    public LightingSettings LightingDraft => CreateLightingDraft();

    public ThemePreference Theme
    {
        get => theme;
        set => SetField(ref theme, value);
    }

    public IReadOnlyList<ThemePreference> ThemeOptions { get; } =
        Enum.GetValues<ThemePreference>();

    public bool KeepManualChangesUntilCleared
    {
        get => keepManualChangesUntilCleared;
        set => SetField(ref keepManualChangesUntilCleared, value);
    }

    public OverrideLifetime SelectedOverrideLifetime => KeepManualChangesUntilCleared
        ? OverrideLifetime.Persistent
        : OverrideLifetime.UntilPowerSourceChange;

    public bool StartAtSignIn
    {
        get => startAtSignIn;
        private set => SetField(ref startAtSignIn, value);
    }

    public bool StartupStateKnown
    {
        get => startupStateKnown;
        private set
        {
            if (startupStateKnown == value)
            {
                return;
            }

            SetField(ref startupStateKnown, value);
            NotifyCommands();
        }
    }

    public string StartupStatusMessage
    {
        get => startupStatusMessage;
        private set => SetField(ref startupStatusMessage, value);
    }

    public ICommand SetPerformanceCommand { get; }
    public ICommand SetFanModeCommand { get; }
    public ICommand SetLightingCommand { get; }
    public ICommand SetChargeLimitCommand { get; }
    public ICommand ToggleBatteryProtectionCommand { get; }
    public ICommand ToggleFullChargeCommand { get; }
    public ICommand ClearManualOverridesCommand { get; }
    public ICommand SaveProfilesCommand { get; }
    public ICommand ExportDiagnosticsCommand { get; }
    public ICommand SetStartAtSignInCommand { get; }

    private bool CanMutate =>
        Volatile.Read(ref disposed) == 0
        && IsConnected
        && latestStatus is
        {
            DeviceVerified: true,
            WritesBlocked: false,
            Capabilities: not null,
        };

    private bool CanSetPerformance(object? parameter) =>
        parameter is PerformancePreset value
        && Enum.IsDefined(value)
        && CanMutate
        && latestStatus!.Capabilities!.PerformancePresets.Contains(value)
        && !MaximumFansAreEffectiveOrConfirmed();

    private bool CanSetFanMode(object? parameter)
    {
        if (parameter is not FanMode value
            || !Enum.IsDefined(value)
            || !CanMutate
            || !latestStatus!.Capabilities!.FanModes.Contains(value))
        {
            return false;
        }

        return value == FanMode.Automatic || IsBalancedAutomaticThermalState();
    }

    private bool MaximumFansAreEffectiveOrConfirmed() =>
        latestStatus is { } status
        && (status.DesiredState.Fans.Value == FanMode.Maximum
            || status.ConfirmedState?.Fans == FanMode.Maximum);

    private bool IsBalancedAutomaticThermalState() =>
        latestStatus is { } status
        && status.DesiredState.Performance.Value == PerformancePreset.Balanced
        && status.DesiredState.Fans.Value == FanMode.Automatic
        && status.ConfirmedState is
        {
            Performance: PerformancePreset.Balanced,
            Fans: FanMode.Automatic,
        };

    private bool CanSetFullChargeOverride(object? parameter)
    {
        if (parameter is not null and not bool)
        {
            return false;
        }

        if (!CanMutate
            || latestStatus?.Capabilities is not { } capabilities
            || !HasBatteryCapability(capabilities)
            || !capabilities.SupportsTemporaryFullCharge
            || latestStatus.ConfirmedState?.Battery is not
            {
                ProtectionEnabled: true,
                ChargeLimitPercent: int currentLimit,
                TemporaryFullCharge: bool temporaryFullCharge,
            }
            || !capabilities.ChargeLimits.Contains(currentLimit))
        {
            return false;
        }

        bool enable = parameter is bool requested
            ? requested
            : !temporaryFullCharge;
        return enable != temporaryFullCharge
            && (!enable || Telemetry?.PowerSource == PowerSource.PluggedIn);
    }

    private bool CanToggleBatteryProtection(object? parameter)
    {
        if (parameter is not null
            || !CanMutate
            || latestStatus?.Capabilities is not { } capabilities
            || !HasBatteryCapability(capabilities)
            || latestStatus.ConfirmedState?.Battery is not
            {
                TemporaryFullCharge: false,
            } confirmed)
        {
            return false;
        }

        int storedLimit = latestStatus.DesiredState.Battery.ConfiguredPolicy
            .ChargeLimitPercent;
        if (!capabilities.ChargeLimits.Contains(storedLimit))
        {
            return false;
        }

        return confirmed.ProtectionEnabled
            ? capabilities.SupportsBatteryProtectionOff
                && confirmed.ChargeLimitPercent == storedLimit
            : confirmed.ChargeLimitPercent is null;
    }

    public Task StartAsync()
    {
        lock (startSync)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            watchTask ??= WatchStatusAsync(lifetime.Token);
            startupInitializationTask ??= InitializeStartupRegistrationAsync(lifetime.Token);
            return startupInitializationTask;
        }
    }

    private async Task InitializeStartupRegistrationAsync(CancellationToken cancellationToken)
    {
        try
        {
            bool enabled = await startupRegistration.IsEnabledAsync(cancellationToken)
                .ConfigureAwait(false);
            await dispatcher.InvokeAsync(
                () =>
                {
                    StartAtSignIn = enabled;
                    StartupStateKnown = true;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await dispatcher.InvokeAsync(
                () =>
                {
                    StartupStatusMessage =
                        $"Start at sign-in is unavailable: {exception.Message}";
                    StartupStateKnown = false;
                },
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task ObserveStateAsync(CancellationToken cancellationToken)
    {
        ObservationRequestStamp stamp = default;
        await dispatcher.InvokeAsync(
            () => stamp = new ObservationRequestStamp(
                connectionGeneration,
                subscriptionConnected),
            cancellationToken);

        try
        {
            ServiceStatusSnapshot status = await serviceClient
                .ObserveStateAsync(cancellationToken);
            await dispatcher.InvokeAsync(
                () => ApplyObservedStatus(status, stamp),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
    }

    private readonly record struct ObservationRequestStamp(
        long Generation,
        bool BeganWithConnectedSubscription);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        lifetime.Cancel();
        foreach (AsyncRelayCommand command in commands)
        {
            await command.DisposeAsync();
        }

        Task? activeWatch = Volatile.Read(ref watchTask);
        if (activeWatch is not null)
        {
            try
            {
                await activeWatch;
            }
            catch (OperationCanceledException)
            {
            }
        }
        lifetime.Dispose();
    }

    private AsyncRelayCommand CreateCommand(
        Func<object?, CancellationToken, Task> execute,
        Predicate<object?> canExecute) =>
        new(execute, canExecute, lifetime.Token);

    private async Task WatchStatusAsync(CancellationToken token)
    {
        try
        {
            await foreach (ServiceWatchUpdate update in serviceClient.WatchStatusAsync(token))
            {
                await dispatcher.InvokeAsync(
                    () =>
                    {
                        switch (update)
                        {
                            case ServiceWatchUpdate.Connected connected:
                                subscriptionConnected = true;
                                ApplyStatus(connected.Status);
                                break;
                            case ServiceWatchUpdate.Reconnecting:
                                MarkDisconnected("Reconnecting to service");
                                break;
                        }
                    },
                    token);
            }

            if (!token.IsCancellationRequested)
            {
                await dispatcher.InvokeAsync(MarkDisconnected, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            await dispatcher.InvokeAsync(MarkDisconnected, token);
        }
    }

    private void ApplyStatus(ServiceStatusSnapshot status)
    {
        if (status.Revision < latestAcceptedRevision)
        {
            return;
        }

        latestAcceptedRevision = status.Revision;
        latestStatus = status;
        IsConnected = true;
        bool controllable = status.DeviceVerified
            && !status.WritesBlocked;
        IsReadOnly = !controllable;
        CanControlHardware = controllable;
        ConnectionStatus = status.Message;
        ControlAccessStatus = FormatControlAccessStatus(status);
        HasControllerAdvisory = status.PotentialControllers.Count > 0;
        ControllerAdvisoryText = HasControllerAdvisory
            ? $"Another controller may change laptop settings: {string.Join(", ", status.PotentialControllers)}. Automatic profile changes pause for settings changed elsewhere; manual actions still apply."
            : string.Empty;
        HasControllerDetectionWarning = !string.IsNullOrWhiteSpace(
            status.ControllerDetectionWarning);
        ControllerDetectionWarningText = status.ControllerDetectionWarning ?? string.Empty;
        DeviceSubsystem[] externalChanges = status.ExternalChanges
            .Distinct()
            .OrderBy(value => value.ToString(), StringComparer.Ordinal)
            .ToArray();
        HasExternalChanges = externalChanges.Length > 0;
        ExternalChangesText = HasExternalChanges
            ? $"Settings changed outside OpenBlade: {string.Join(", ", externalChanges)}. Automatic profile changes for these controls are paused; manual actions still apply."
            : string.Empty;
        Profiles = status.Profiles;
        if (!profileEditorsDirty)
        {
            LoadProfileEditors(status.Profiles);
        }
        DeviceCapabilities capabilities = status.Capabilities
            ?? DeviceCapabilities.Unavailable();
        Capabilities = capabilities;
        int[] chargeLimits = HasBatteryCapability(capabilities)
            ? capabilities.ChargeLimits.Order().ToArray()
            : [];
        if (!ChargeLimitOptions.SequenceEqual(chargeLimits))
        {
            ChargeLimitOptions = chargeLimits;
        }
        ConfirmedDeviceState? confirmed = status.ConfirmedState;
        SelectedPerformance = confirmed?.Performance ?? status.DesiredState.Performance.Value;
        SelectedFanMode = confirmed?.Fans ?? status.DesiredState.Fans.Value;
        SelectedLighting = confirmed?.Lighting ?? status.DesiredState.Lighting.Value;
        if (!lightingEditorDirty)
        {
            LoadLightingEditor(SelectedLighting);
        }
        if (!chargeLimitEditorDirty)
        {
            LoadChargeLimitEditor(status);
        }
        BatteryProtectionEnabled = confirmed?.Battery?.ProtectionEnabled
            ?? status.DesiredState.Battery.ProtectionEnabled;
        SetTemporaryFullChargeState(confirmed?.Battery?.TemporaryFullCharge);
        Telemetry = status.Telemetry;
        PerformanceOverride = status.PerformanceOverride;
        FanOverride = status.FanOverride;
        LightingOverride = status.LightingOverride;
        ActivePowerSourceLabel = status.Telemetry.PowerSource == PowerSource.Battery
            ? "Battery profile"
            : "Plugged-in profile";
        PerformanceSourceLabel = SourceLabel(
            DeviceSubsystem.Performance,
            confirmed?.Performance is not null,
            externalChanges);
        FanSourceLabel = SourceLabel(
            DeviceSubsystem.Fans,
            confirmed?.Fans is not null,
            externalChanges);
        LightingSourceLabel = SourceLabel(
            DeviceSubsystem.Lighting,
            confirmed?.Lighting is not null,
            externalChanges);
        BatterySourceLabel = FormatBatterySource(
            confirmed?.Battery,
            externalChanges.Contains(DeviceSubsystem.Battery));
        TelemetryStatus = FormatTelemetry(status.Telemetry);

        AvailablePerformancePresets = capabilities.PerformancePresets
            .Where(Enum.IsDefined)
            .Order()
            .ToArray();
        FanMode[] fanModes = capabilities.FanModes
            .Where(Enum.IsDefined)
            .Order()
            .ToArray();
        if (!AvailableFanModes.SequenceEqual(fanModes))
        {
            AvailableFanModes = fanModes;
            // WinUI clears a ComboBox selection when ItemsSource is replaced.
            // Reapply the confirmed value only when the admitted set changes;
            // equivalent status refreshes must preserve a pending user choice.
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(SelectedFanMode)));
        }
        AvailableLightingEffects = capabilities.LightingEffects
            .Where(Enum.IsDefined)
            .Order()
            .ToArray();
        AvailableLightingSettings = AvailableLightingEffects
            .Select(CreateLightingChoice)
            .ToArray();
        IsPerformanceVisible = AvailablePerformancePresets.Count > 0;
        IsFanControlVisible = AvailableFanModes.Count > 0;
        IsLightingVisible = AvailableLightingEffects.Count > 0;
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(IsProfileControlVisible)));
        NotifyCommands();
    }

    private void MarkDisconnected() => MarkDisconnected("Service disconnected");

    private void MarkDisconnected(string message)
    {
        connectionGeneration++;
        subscriptionConnected = false;
        latestAcceptedRevision = -1;
        IsConnected = false;
        IsReadOnly = true;
        CanControlHardware = false;
        ConnectionStatus = message;
        ControlAccessStatus = "Service disconnected · hardware controls disabled";
        NotifyCommands();
    }

    private void ApplyObservedStatus(
        ServiceStatusSnapshot status,
        ObservationRequestStamp stamp)
    {
        if (stamp.BeganWithConnectedSubscription
            && stamp.Generation == connectionGeneration)
        {
            ApplyStatus(status);
        }
    }

    private bool CanSetChargeLimit(int value)
    {
        if (!CanMutate
            || latestStatus!.Capabilities is not { } capabilities
            || latestStatus.ConfirmedState?.Battery is not
            {
                ProtectionEnabled: true,
                ChargeLimitPercent: int currentLimit,
                TemporaryFullCharge: false,
            })
        {
            return false;
        }

        return HasBatteryCapability(capabilities)
            && capabilities.ChargeLimits.Contains(currentLimit)
            && capabilities.ChargeLimits.Contains(value);
    }

    private static bool HasBatteryCapability(DeviceCapabilities? capabilities) =>
        capabilities is not null
        && capabilities.ChargeLimits.Count is > 0 and <= 7
        && capabilities.ChargeLimits.All(
            value => value is >= 50 and <= 80 && value % 5 == 0);

    private void SetTemporaryFullChargeState(bool? value)
    {
        bool known = value.HasValue;
        bool active = value is true;
        if (temporaryFullChargeStateKnown != known)
        {
            temporaryFullChargeStateKnown = known;
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(EnableFullChargeOverride)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(FullChargeActionLabel)));
        }

        FullChargeOverride = active;
    }

    private Task SetPerformanceAsync(object? parameter, CancellationToken token) =>
        RunOperationAsync(
            cancellationToken => serviceClient.SetPerformanceOverrideAsync(
                new ManualOverride<PerformancePreset>(
                    (PerformancePreset)parameter!,
                    SelectedOverrideLifetime),
                cancellationToken),
            token);

    private Task SetFanModeAsync(object? parameter, CancellationToken token) =>
        RunOperationAsync(
            cancellationToken => serviceClient.SetFanOverrideAsync(
                new ManualOverride<FanMode>(
                    (FanMode)parameter!,
                    SelectedOverrideLifetime),
                cancellationToken),
            token);

    private Task SetLightingAsync(object? parameter, CancellationToken token) =>
        RunOperationAsync(
            cancellationToken => serviceClient.SetLightingOverrideAsync(
                new ManualOverride<LightingSettings>(
                    (LightingSettings)parameter!,
                    SelectedOverrideLifetime),
                cancellationToken),
            token);

    private async Task SetChargeLimitAsync(object? parameter, CancellationToken token)
    {
        int requestedLimit = (int)parameter!;
        long revisionAtStart = latestAcceptedRevision;
        ControlOperationStatus? resultStatus = null;
        await RunOperationAsync(
            cancellationToken => serviceClient.SetBatteryPolicyAsync(
                BatteryPolicy.Create(requestedLimit),
                cancellationToken),
            token,
            result => resultStatus = result.Status);

        if (resultStatus == ControlOperationStatus.Applied)
        {
            await dispatcher.InvokeAsync(
                () =>
                {
                    chargeLimitEditorDirty = false;
                    if (latestStatus is not null
                        && latestAcceptedRevision > revisionAtStart)
                    {
                        LoadChargeLimitEditor(latestStatus);
                    }
                },
                token);
        }
    }

    private Task ToggleBatteryProtectionAsync(object? parameter, CancellationToken token) =>
        RunOperationAsync(
            cancellationToken => serviceClient.SetBatteryProtectionAsync(
                !BatteryProtectionEnabled,
                cancellationToken),
            token);

    private Task ToggleFullChargeAsync(object? parameter, CancellationToken token) =>
        RunOperationAsync(
            cancellationToken => serviceClient.SetFullChargeOverrideAsync(
                parameter is bool enabled ? enabled : !FullChargeOverride,
                cancellationToken),
            token);

    private async Task ClearManualOverridesAsync(object? parameter, CancellationToken token)
    {
        List<string> hardwareNotApplied = [];
        await ClearOverrideAsync(
            "fans",
            cancellationToken => serviceClient.SetFanOverrideAsync(null, cancellationToken),
            hardwareNotApplied,
            token).ConfigureAwait(false);
        await ClearOverrideAsync(
            "performance",
            cancellationToken => serviceClient.SetPerformanceOverrideAsync(null, cancellationToken),
            hardwareNotApplied,
            token).ConfigureAwait(false);
        await ClearOverrideAsync(
            "lighting",
            cancellationToken => serviceClient.SetLightingOverrideAsync(null, cancellationToken),
            hardwareNotApplied,
            token).ConfigureAwait(false);

        string message;
        try
        {
            ServiceStatusSnapshot status = await serviceClient.GetStatusAsync(token)
                .ConfigureAwait(false);
            List<string> notCleared = [];
            if (status.PerformanceOverride is not null)
            {
                notCleared.Add("performance");
            }
            if (status.FanOverride is not null)
            {
                notCleared.Add("fans");
            }
            if (status.LightingOverride is not null)
            {
                notCleared.Add("lighting");
            }

            message = notCleared.Count > 0
                ? $"Could not clear {string.Join(", ", notCleared)} overrides."
                : hardwareNotApplied.Count == 0
                    ? "Manual overrides cleared."
                    : $"Manual overrides cleared; hardware restoration was not applied for {string.Join(", ", hardwareNotApplied)}.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            message = "Manual override clear requests completed; service confirmation is unavailable.";
        }

        await dispatcher.InvokeAsync(
            () => OperationStatusMessage = message,
            token).ConfigureAwait(false);
    }

    private static async Task ClearOverrideAsync(
        string subsystem,
        Func<CancellationToken, Task<ControlOperationResult>> operation,
        ICollection<string> hardwareNotApplied,
        CancellationToken token)
    {
        try
        {
            ControlOperationResult result = await operation(token).ConfigureAwait(false);
            if (result.Status != ControlOperationStatus.Applied)
            {
                hardwareNotApplied.Add(subsystem);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            hardwareNotApplied.Add(subsystem);
        }
    }

    private async Task SetStartAtSignInAsync(object? parameter, CancellationToken token)
    {
        bool requested = (bool)parameter!;
        try
        {
            await startupRegistration.SetEnabledAsync(requested, token)
                .ConfigureAwait(false);
            bool confirmed = await startupRegistration.IsEnabledAsync(token)
                .ConfigureAwait(false);
            await dispatcher.InvokeAsync(
                () =>
                {
                    StartAtSignIn = confirmed;
                    StartupStatusMessage = confirmed == requested
                        ? requested
                            ? "OpenBlade will start when you sign in."
                            : "OpenBlade will not start when you sign in."
                        : "Windows did not confirm the requested start-at-sign-in setting.";
                    NotifyCommands();
                },
                token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await dispatcher.InvokeAsync(
                () =>
                {
                    StartupStatusMessage =
                        $"Could not change start at sign-in: {exception.Message}";
                    NotifyCommands();
                },
                token).ConfigureAwait(false);
        }
    }

    private async Task SaveProfilesAsync(object? parameter, CancellationToken token)
    {
        if (!TryGetSupportedProfileDraft(parameter, out ProfileSet draft))
        {
            return;
        }

        bool accepted = await RunOperationAsync(
            cancellationToken => serviceClient.UpdateProfilesAsync(draft, cancellationToken),
            token);
        if (accepted)
        {
            Profiles = draft;
            profileEditorsDirty = false;
            LoadProfileEditors(draft);
        }
    }

    private bool CanSaveProfiles(object? parameter) =>
        CanMutate && TryGetSupportedProfileDraft(parameter, out _);

    private bool TryGetSupportedProfileDraft(object? parameter, out ProfileSet draft)
    {
        ProfileSet? candidate = parameter as ProfileSet;
        if (candidate is null)
        {
            if (ProfileChargeLimitEditor is < 50 or > 80
                || ProfileChargeLimitEditor % 5 != 0)
            {
                draft = null!;
                return false;
            }

            candidate = new ProfileSet(
                new PowerProfile(
                    PluggedInPerformanceEditor,
                    PluggedInFanModeEditor,
                    PluggedInLightingEditor),
                new PowerProfile(
                    BatteryPerformanceEditor,
                    BatteryFanModeEditor,
                    BatteryLightingEditor),
                BatteryPolicy.Create(ProfileChargeLimitEditor));
        }

        draft = candidate;

        if (latestStatus?.Capabilities is not { } capabilities)
        {
            return false;
        }

        return ProfileSupportedOrUnchanged(
                draft.PluggedIn,
                Profiles.PluggedIn,
                capabilities)
            && ProfileSupportedOrUnchanged(
                draft.Battery,
                Profiles.Battery,
                capabilities)
            && HasBatteryCapability(capabilities)
            && capabilities.ChargeLimits.Contains(
                draft.BatteryPolicy.ChargeLimitPercent);
    }

    private static bool ProfileSupportedOrUnchanged(
        PowerProfile draft,
        PowerProfile current,
        DeviceCapabilities capabilities) =>
        Enum.IsDefined(draft.Performance)
        && (capabilities.PerformancePresets.Contains(draft.Performance)
            || draft.Performance == current.Performance)
        && Enum.IsDefined(draft.Fans)
        && (capabilities.FanModes.Contains(draft.Fans)
            || draft.Fans == current.Fans)
        && ThermalCombinationSupported(draft.Performance, draft.Fans)
        && (LightingSupported(draft.Lighting, capabilities)
            || draft.Lighting == current.Lighting);

    private static bool ThermalCombinationSupported(
        PerformancePreset performance,
        FanMode fans) =>
        fans == FanMode.Automatic
        || (performance == PerformancePreset.Balanced && fans == FanMode.Maximum);

    private static bool LightingSupported(
        LightingSettings lighting,
        DeviceCapabilities capabilities)
    {
        if (!Enum.IsDefined(lighting.Effect)
            || !capabilities.LightingEffects.Contains(lighting.Effect)
            || lighting.Brightness > 100)
        {
            return false;
        }

        bool hasColor = lighting.Color != default;
        return lighting.Effect switch
        {
            LightingEffect.Off => lighting.Brightness == 0 && !hasColor,
            LightingEffect.Spectrum => !hasColor,
            LightingEffect.Static or LightingEffect.Breathing or LightingEffect.Reactive => true,
            _ => false,
        };
    }

    private async Task<bool> RunOperationAsync(
        Func<CancellationToken, Task<ControlOperationResult>> operation,
        CancellationToken token,
        Action<ControlOperationResult>? onCompleted = null)
    {
        try
        {
            ControlOperationResult result = await operation(token);
            await dispatcher.InvokeAsync(
                () =>
                {
                    OperationStatusMessage = result.Message;
                    onCompleted?.Invoke(result);
                },
                token);
            return result.Status != ControlOperationStatus.Failed;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await dispatcher.InvokeAsync(
                () => OperationStatusMessage = $"Operation failed: {exception.Message}",
                token);
            return false;
        }
    }

    private async Task ExportDiagnosticsAsync(object? parameter, CancellationToken token)
    {
        try
        {
            await dispatcher.InvokeAsync(
                () => OperationStatusMessage = "Preparing diagnostic export...",
                token);
            DiagnosticArchive archive = await serviceClient.ExportDiagnosticsAsync(token);
            DiagnosticArchiveSaveResult result = await diagnosticArchiveSaver.SaveAsync(
                archive,
                token);
            await dispatcher.InvokeAsync(
                () => OperationStatusMessage = result.Status switch
                {
                    DiagnosticArchiveSaveStatus.Canceled => "Diagnostic export canceled.",
                    DiagnosticArchiveSaveStatus.Saved
                        when !string.IsNullOrWhiteSpace(result.DestinationPath) =>
                        $"Diagnostics exported to {result.DestinationPath}.",
                    _ => throw new InvalidDataException(
                        "The diagnostic saver returned an invalid result."),
                },
                token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await dispatcher.InvokeAsync(
                () => OperationStatusMessage =
                    $"Diagnostic export failed: {exception.Message}",
                token);
        }
    }

    private void LoadLightingEditor(LightingSettings value)
    {
        SetField(ref lightingEffectEditor, value.Effect, nameof(LightingEffectEditor));
        SetField(ref lightingBrightnessEditor, value.Brightness, nameof(LightingBrightnessEditor));
        SetField(ref lightingRedEditor, (int)value.Color.Red, nameof(LightingRedEditor));
        SetField(ref lightingGreenEditor, (int)value.Color.Green, nameof(LightingGreenEditor));
        SetField(ref lightingBlueEditor, (int)value.Color.Blue, nameof(LightingBlueEditor));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LightingDraft)));
    }

    private void LoadChargeLimitEditor(ServiceStatusSnapshot status)
    {
        int value = status.ConfirmedState?.Battery?.ChargeLimitPercent
            ?? status.DesiredState.Battery.ConfiguredPolicy.ChargeLimitPercent;
        SetField(ref chargeLimitPercent, value, nameof(ChargeLimitPercent));
        NotifyCommands();
    }

    private void LoadProfileEditors(ProfileSet value)
    {
        SetField(
            ref pluggedInPerformanceEditor,
            value.PluggedIn.Performance,
            nameof(PluggedInPerformanceEditor));
        SetField(
            ref pluggedInFanModeEditor,
            value.PluggedIn.Fans,
            nameof(PluggedInFanModeEditor));
        SetField(
            ref pluggedInLightingEditor,
            value.PluggedIn.Lighting,
            nameof(PluggedInLightingEditor));
        SetField(
            ref batteryPerformanceEditor,
            value.Battery.Performance,
            nameof(BatteryPerformanceEditor));
        SetField(
            ref batteryFanModeEditor,
            value.Battery.Fans,
            nameof(BatteryFanModeEditor));
        SetField(
            ref batteryLightingEditor,
            value.Battery.Lighting,
            nameof(BatteryLightingEditor));
        SetField(
            ref profileChargeLimitEditor,
            value.BatteryPolicy.ChargeLimitPercent,
            nameof(ProfileChargeLimitEditor));
        NotifyCommands();
    }

    private LightingSettings CreateLightingDraft()
    {
        if (LightingEffectEditor == LightingEffect.Off)
        {
            return ProfileDefaults.CreateSafeDefaults().PluggedIn.Lighting;
        }

        RgbColor color = LightingEffectEditor == LightingEffect.Spectrum
            ? default
            : new RgbColor(
                (byte)LightingRedEditor,
                (byte)LightingGreenEditor,
                (byte)LightingBlueEditor);
        return new LightingSettings(
            LightingEffectEditor,
            color,
            (byte)LightingBrightnessEditor);
    }

    private LightingSettings CreateLightingChoice(LightingEffect effect)
    {
        if (effect == LightingEffect.Off)
        {
            return ProfileDefaults.CreateSafeDefaults().PluggedIn.Lighting;
        }

        bool usesColor = effect is LightingEffect.Static
            or LightingEffect.Breathing
            or LightingEffect.Reactive;
        RgbColor color = usesColor
            ? SelectedLighting.Color == default
                ? new RgbColor(0, 170, 255)
                : SelectedLighting.Color
            : default;
        byte brightness = SelectedLighting.Brightness == 0
            ? (byte)100
            : SelectedLighting.Brightness;
        return new LightingSettings(effect, color, brightness);
    }

    private void NotifyCommands()
    {
        foreach (AsyncRelayCommand command in commands)
        {
            command.NotifyCanExecuteChanged();
        }
    }

    private static string SourceLabel(
        DeviceSubsystem subsystem,
        bool confirmed,
        IReadOnlyCollection<DeviceSubsystem> externalChanges) =>
        externalChanges.Contains(subsystem)
            ? "Changed outside OpenBlade"
            : confirmed
                ? "Confirmed device state"
                : "Desired intent (not confirmed)";

    private static string FormatBatterySource(
        ConfirmedBatteryState? battery,
        bool changedOutside)
    {
        if (battery is null)
        {
            return "Desired intent (not confirmed)";
        }

        string state = battery.TemporaryFullCharge switch
        {
            null => "Temporary full charge state unavailable",
            true => "Temporary full charge active",
            false when !battery.ProtectionEnabled => "Battery protection off",
            _ => $"Charge limit {battery.ChargeLimitPercent}%",
        };
        string source = changedOutside
            ? "Changed outside OpenBlade"
            : "Confirmed device state";
        return $"{source} · {state}";
    }

    private static string FormatTelemetry(TelemetrySnapshot telemetry)
    {
        string cpu = telemetry.CpuTemperatureCelsius is double cpuValue
            ? $"CPU {cpuValue.ToString("0.#", CultureInfo.InvariantCulture)} °C"
            : "CPU unavailable";
        string gpu = telemetry.GpuTemperatureCelsius is double gpuValue
            ? $"GPU {gpuValue.ToString("0.#", CultureInfo.InvariantCulture)} °C"
            : "GPU unavailable";
        string fans = telemetry.FanRpm is int fanValue
            ? $"Fans {fanValue.ToString(CultureInfo.InvariantCulture)} RPM"
            : "Fans unavailable";
        string battery = telemetry.BatteryPercent is int batteryValue
            ? $"Battery {batteryValue.ToString(CultureInfo.InvariantCulture)}%"
            : "Battery unavailable";
        return string.Join(" · ", cpu, gpu, fans, battery);
    }

    private static string FormatPowerAndBatteryTelemetry(TelemetrySnapshot? telemetry)
    {
        if (telemetry is null)
        {
            return "Unavailable";
        }

        string source = telemetry.PowerSource == PowerSource.Battery
            ? "Battery"
            : "Plugged in";
        string battery = telemetry.BatteryPercent is int value
            ? $"{value.ToString(CultureInfo.InvariantCulture)}%"
            : "Unavailable";
        return $"{source} · {battery}";
    }

    private static string FormatTemperature(double? temperature) => temperature is double value
        ? $"{value.ToString("0.#", CultureInfo.InvariantCulture)} °C"
        : "Unavailable";

    private static string FormatControlAccessStatus(ServiceStatusSnapshot status)
    {
        if (!status.DeviceVerified)
        {
            return "Target not verified · hardware controls disabled";
        }

        return status.WritesBlocked
            ? "Read-only · hardware writes blocked"
            : "Target verified · hardware controls available";
    }

    private void SetField<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void SetOverrideField<T>(
        ref ManualOverride<T>? field,
        ManualOverride<T>? value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<ManualOverride<T>?>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ManualOverrideStatus)));
    }

    private static void AddOverrideStatus<T>(
        ICollection<string> status,
        string subsystem,
        ManualOverride<T>? value)
    {
        if (value is null)
        {
            return;
        }

        string lifetime = value.Lifetime == OverrideLifetime.Persistent
            ? "until cleared"
            : "until power source changes";
        status.Add($"{subsystem}: {value.Value} ({lifetime})");
    }

    private void SetEditorField<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        profileEditorsDirty = true;
        SetField(ref field, value, propertyName);
        NotifyCommands();
    }

    private void SetLightingColorField(ref int field, int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 255);
        SetLightingEditorField(ref field, value);
    }

    private void SetLightingEditorField<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        lightingEditorDirty = true;
        SetField(ref field, value, propertyName);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LightingDraft)));
        NotifyCommands();
    }

}
