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

public sealed record ThemePreferenceOption(
    ThemePreference Preference,
    string Label);

public sealed record ManualChangeDurationOption(
    OverrideLifetime Lifetime,
    string Label);

public sealed record LiveReadingsRefreshOption(
    LiveReadingsCadence Cadence,
    string Label);

public sealed class MainViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private static readonly RazerBladeModelInfo TargetLaptop =
        RazerBladeModelCatalog.Find(RazerBladeModelCatalog.OpenBladeTargetModelNumber)
        ?? throw new InvalidOperationException("The supported laptop is missing from the model catalog.");
    private static readonly string TargetLaptopLabel =
        $"{TargetLaptop.DisplayName}, {TargetLaptop.ModelNumber}";

    private readonly IServiceClient serviceClient;
    private readonly IUiDispatcher dispatcher;
    private readonly IDiagnosticArchiveSaver diagnosticArchiveSaver;
    private readonly IStartupRegistration startupRegistration;
    private readonly TimeProvider timeProvider;
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
    private string operationFeedbackMessage = string.Empty;
    private string controlAccessStatus = "OpenBlade service isn’t running";
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
    private IReadOnlyList<PerformancePresetOption> availablePluggedInPerformanceOptions = [];
    private IReadOnlyList<PerformancePresetOption> availableBatteryPerformanceOptions = [];
    private IReadOnlyList<PerformancePresetOption> availableUsbCPerformanceOptions = [];
    private IReadOnlyList<FanMode> availableFanModes = [];
    private IReadOnlyList<FanMode> availableUsbCFanModes = [];
    private IReadOnlyList<LightingEffect> availableLightingEffects = [];
    private IReadOnlyList<LightingSettings> availableLightingSettings = [];
    private bool isPerformanceVisible;
    private bool isFanControlVisible;
    private bool isLightingVisible;
    private PerformancePreset selectedPerformance = PerformancePreset.Balanced;
    private FanMode selectedFanMode = FanMode.Automatic;
    private LightingSettings selectedLighting = ProfileDefaults.CreateSafeDefaults().PluggedIn.Lighting;
    private int chargeLimitPercent = 80;
    private string currentBatterySettingLabel = "Battery setting unavailable";
    private bool chargeLimitEditorDirty;
    private IReadOnlyList<int> chargeLimitOptions = [];
    private bool batteryProtectionEnabled = true;
    private bool fullChargeOverride;
    private bool temporaryFullChargeStateKnown;
    private DeviceCapabilities capabilities = DeviceCapabilities.Unavailable();
    private PowerSource activePowerSource = PowerSource.PluggedIn;
    private LiveReadings? liveReadings;
    private DateTimeOffset? lastLiveReadingsRefreshAt;
    private ManualOverride<PerformancePreset>? performanceOverride;
    private ManualOverride<FanMode>? fanOverride;
    private ManualOverride<LightingSettings>? lightingOverride;
    private ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
    private PerformancePreset pluggedInPerformanceEditor = PerformancePreset.Balanced;
    private FanMode pluggedInFanModeEditor = FanMode.Automatic;
    private LightingSettings pluggedInLightingEditor = ProfileDefaults.CreateSafeDefaults().PluggedIn.Lighting;
    private PerformancePreset batteryPerformanceEditor = PerformancePreset.BatterySaver;
    private FanMode batteryFanModeEditor = FanMode.Automatic;
    private LightingSettings batteryLightingEditor = ProfileDefaults.CreateSafeDefaults().Battery.Lighting;
    private PerformancePreset usbCPerformanceEditor = PerformancePreset.Balanced;
    private FanMode usbCFanModeEditor = FanMode.Automatic;
    private LightingSettings usbCLightingEditor = ProfileDefaults.CreateSafeDefaults().UsbC.Lighting;
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
    private LiveReadingsCadence liveReadingsRefreshCadence =
        LiveReadingsCadence.EveryTwoSeconds;
    private TrayHoverLabelMode trayHoverLabelMode =
        TrayHoverLabelMode.ProfileAndPowerFlow;
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
        TimeProvider timeProvider)
        : this(
            serviceClient,
            dispatcher,
            new UnavailableDiagnosticArchiveSaver(),
            new UnavailableStartupRegistration(),
            timeProvider)
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
        IStartupRegistration startupRegistration,
        TimeProvider? timeProvider = null)
    {
        this.serviceClient = serviceClient ?? throw new ArgumentNullException(nameof(serviceClient));
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        this.diagnosticArchiveSaver = diagnosticArchiveSaver
            ?? throw new ArgumentNullException(nameof(diagnosticArchiveSaver));
        this.startupRegistration = startupRegistration
            ?? throw new ArgumentNullException(nameof(startupRegistration));
        this.timeProvider = timeProvider ?? TimeProvider.System;

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
                && HasConfirmedPowerSource
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
            _ => Volatile.Read(ref disposed) == 0
                && IsConnected
                && HasConfirmedPowerSource);
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
        private set
        {
            if (isConnected == value)
            {
                return;
            }
            SetField(ref isConnected, value);
            NotifyPresentationState();
            NotifyTrayHoverLabel();
        }
    }

    public bool IsReadOnly
    {
        get => isReadOnly;
        private set => SetField(ref isReadOnly, value);
    }

    public bool CanControlHardware
    {
        get => canControlHardware;
        private set
        {
            if (canControlHardware == value)
            {
                return;
            }
            SetField(ref canControlHardware, value);
            NotifyPresentationState();
        }
    }

    public string ConnectionStatus
    {
        get => connectionStatus;
        private set
        {
            if (connectionStatus == value)
            {
                return;
            }
            SetField(ref connectionStatus, value);
        }
    }

    public string OperationStatusMessage
    {
        get => operationStatusMessage;
        private set
        {
            if (operationStatusMessage == value)
            {
                return;
            }
            SetField(ref operationStatusMessage, value);
            OperationFeedbackMessage = FormatOperationFeedback(value);
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(TroubleshootingLastOperationStatus)));
        }
    }

    public string OperationFeedbackMessage
    {
        get => operationFeedbackMessage;
        private set
        {
            if (operationFeedbackMessage == value)
            {
                return;
            }
            SetField(ref operationFeedbackMessage, value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasOperationFeedback)));
        }
    }

    public bool HasOperationFeedback => !string.IsNullOrWhiteSpace(OperationFeedbackMessage);

    public string ControlAccessStatus
    {
        get => controlAccessStatus;
        private set
        {
            if (controlAccessStatus == value)
            {
                return;
            }
            SetField(ref controlAccessStatus, value);
        }
    }

    public string BatteryPowerFlowLabel => FormatBatteryPowerFlow(
        LiveReadings,
        ActivePowerSource);

    public string Fan1RpmLabel => FormatFanRpm("Fan 1", LiveReadings?.Fan1Rpm);

    public string Fan2RpmLabel => FormatFanRpm("Fan 2", LiveReadings?.Fan2Rpm);

    public string Fan1RpmValue => FormatFanRpmValue(LiveReadings?.Fan1Rpm);

    public string Fan2RpmValue => FormatFanRpmValue(LiveReadings?.Fan2Rpm);

    public string LiveReadingsRefreshToolTip => lastLiveReadingsRefreshAt is { } refreshedAt
        ? $"Last checked at {refreshedAt.ToLocalTime().ToString("T", CultureInfo.CurrentCulture)}.{Environment.NewLine}Values change only when Windows or firmware reports new data."
        : "Waiting for the first live-reading check.";

    public string ActivePowerSourceLabel
    {
        get => activePowerSourceLabel;
        private set => SetField(ref activePowerSourceLabel, value);
    }

    public PowerSource ActivePowerSource
    {
        get => activePowerSource;
        private set
        {
            if (activePowerSource == value)
            {
                return;
            }

            SetField(ref activePowerSource, value);
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(IsPluggedInProfileActive)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(IsBatteryProfileActive)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(IsUsbCProfileActive)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(AvailableActivePerformanceOptions)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(SelectedPerformanceOption)));
            NotifyActiveFanSelectionBinding();
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(BatteryPowerFlowLabel)));
            NotifyTrayHoverLabel();
            NotifyCommands();
        }
    }

    public bool IsPluggedInProfileActive => ActivePowerSource == PowerSource.PluggedIn;

    public bool IsBatteryProfileActive => ActivePowerSource == PowerSource.Battery;

    public bool IsUsbCProfileActive => ActivePowerSource == PowerSource.UsbC;

    public string ActivePerformanceLabel =>
        PerformancePresetPresentation.Label(SelectedPerformance);

    public string ActiveFanModeLabel => $"{SelectedFanMode} fans";

    public string ProfileStateLabel => !IsConnected
        ? "Service unavailable"
        : latestStatus?.DeviceVerified == false
            ? "Laptop not found"
            : ActivePowerSource == PowerSource.ExternalUnknown
                ? "Checking power source"
                : CanControlHardware
                    ? "Ready"
                    : "Controls unavailable";

    public bool IsReadinessReady => IsConnected
        && latestStatus?.DeviceVerified == true
        && CanControlHardware;

    public bool IsReadinessChecking => IsConnected
        && latestStatus?.DeviceVerified == true
        && ActivePowerSource == PowerSource.ExternalUnknown;

    public bool IsReadinessWarning => IsConnected
        && latestStatus?.DeviceVerified == true
        && ActivePowerSource != PowerSource.ExternalUnknown
        && !CanControlHardware;

    public bool IsReadinessError => !IsConnected
        || latestStatus?.DeviceVerified != true;

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
        private set
        {
            if (hasExternalChanges == value)
            {
                return;
            }
            SetField(ref hasExternalChanges, value);
            NotifyPresentationState();
        }
    }

    public string ExternalChangesText
    {
        get => externalChangesText;
        private set => SetField(ref externalChangesText, value);
    }

    public bool HasAttention => HasControllerAdvisory
        || HasControllerDetectionWarning
        || HasExternalChanges;

    public string AttentionSummary
    {
        get
        {
            if (HasExternalChanges)
            {
                return "Settings changed by another app";
            }
            if (HasControllerDetectionWarning)
            {
                return "Controller detection needs attention";
            }
            return "Another Razer app is running";
        }
    }

    public string AttentionDetails
    {
        get
        {
            List<string> details = [];
            if (HasExternalChanges)
            {
                details.Add(ExternalChangesText);
            }
            if (HasControllerAdvisory)
            {
                details.Add("Another Razer app can also change these settings. OpenBlade will show changes instead of changing them back.");
            }
            if (HasControllerDetectionWarning)
            {
                details.Add("OpenBlade could not check every Razer controller. Controls remain available only when the laptop is verified.");
            }
            return string.Join("\n", details);
        }
    }

    public string TroubleshootingServiceStatus => IsConnected
        ? "Running"
        : "Unavailable";

    public string TroubleshootingLaptopStatus => !IsConnected
        ? $"{TargetLaptopLabel}; status unavailable"
        : latestStatus?.DeviceVerified == true
            ? TargetLaptopLabel
            : $"{TargetLaptopLabel}; not detected";

    public string TroubleshootingPowerSourceStatus => !IsConnected
        ? "Unavailable"
        : ActivePowerSource switch
        {
            PowerSource.PluggedIn => "Full-power adapter",
            PowerSource.UsbC => "USB-C power",
            PowerSource.Battery => "Battery",
            PowerSource.ExternalUnknown => "Checking power source",
            _ => "Unavailable",
        };

    public string TroubleshootingControlsStatus => CanControlHardware
        ? "Ready"
        : "Unavailable";

    public string TroubleshootingOtherRazerSoftwareStatus => HasControllerAdvisory
        ? string.Join(", ", latestStatus?.PotentialControllers ?? [])
        : HasControllerDetectionWarning
            ? "Could not check"
            : "None detected";

    public string TroubleshootingDetectionStatus => HasControllerDetectionWarning
        ? FormatTroubleshootingValue(ControllerDetectionWarningText)
        : "Controller check completed";

    public string TroubleshootingLastOperationStatus => string.IsNullOrWhiteSpace(
        OperationStatusMessage)
            ? "No recent actions"
            : FormatTroubleshootingValue(OperationStatusMessage);

    public IReadOnlyList<PerformancePreset> AvailablePerformancePresets
    {
        get => availablePerformancePresets;
        private set => SetField(ref availablePerformancePresets, value);
    }

    public IReadOnlyList<PerformancePresetOption> AvailablePluggedInPerformanceOptions
    {
        get => availablePluggedInPerformanceOptions;
        private set => SetField(ref availablePluggedInPerformanceOptions, value);
    }

    public IReadOnlyList<PerformancePresetOption> AvailableBatteryPerformanceOptions
    {
        get => availableBatteryPerformanceOptions;
        private set => SetField(ref availableBatteryPerformanceOptions, value);
    }

    public IReadOnlyList<PerformancePresetOption> AvailableUsbCPerformanceOptions
    {
        get => availableUsbCPerformanceOptions;
        private set => SetField(ref availableUsbCPerformanceOptions, value);
    }

    public IReadOnlyList<PerformancePresetOption> AvailableActivePerformanceOptions =>
        ActivePowerSource switch
        {
            PowerSource.PluggedIn => AvailablePluggedInPerformanceOptions,
            PowerSource.Battery => AvailableBatteryPerformanceOptions,
            PowerSource.UsbC => AvailableUsbCPerformanceOptions,
            PowerSource.ExternalUnknown => [],
            _ => [],
        };

    public PerformancePresetOption? SelectedPerformanceOption =>
        FindPerformanceOption(AvailableActivePerformanceOptions, SelectedPerformance);

    public IReadOnlyList<FanMode> AvailableFanModes
    {
        get => availableFanModes;
        private set => SetField(ref availableFanModes, value);
    }

    public IReadOnlyList<FanMode> AvailableUsbCFanModes
    {
        get => availableUsbCFanModes;
        private set => SetField(ref availableUsbCFanModes, value);
    }

    public IReadOnlyList<FanMode> AvailableActiveFanModes =>
        ActivePowerSource switch
        {
            PowerSource.PluggedIn or PowerSource.Battery => AvailableFanModes,
            PowerSource.UsbC => AvailableUsbCFanModes,
            PowerSource.ExternalUnknown => [],
            _ => [],
        };

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
        private set
        {
            if (isLightingVisible == value)
            {
                return;
            }
            SetField(ref isLightingVisible, value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasActiveChanges)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActiveChangesText)));
        }
    }

    public bool IsProfileControlVisible =>
        IsPerformanceVisible || IsFanControlVisible || IsLightingVisible;

    public PerformancePreset SelectedPerformance
    {
        get => selectedPerformance;
        private set
        {
            if (selectedPerformance == value)
            {
                return;
            }
            SetField(ref selectedPerformance, value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActivePerformanceLabel)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedPerformanceOption)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSilentSelected)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsBalancedSelected)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPerformanceSelected)));
            NotifyTrayHoverLabel();
        }
    }

    public bool IsSilentSelected => SelectedPerformance == PerformancePreset.Silent;

    public bool IsBalancedSelected => SelectedPerformance == PerformancePreset.Balanced;

    public bool IsPerformanceSelected => SelectedPerformance == PerformancePreset.Performance;

    public FanMode SelectedFanMode
    {
        get => selectedFanMode;
        private set
        {
            if (selectedFanMode == value)
            {
                return;
            }
            SetField(ref selectedFanMode, value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActiveFanModeLabel)));
        }
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

    public bool CanAdjustChargeLimit => CanSetChargeLimit(ChargeLimitPercent);

    public bool CanChangeBatteryProtection => CanToggleBatteryProtection(null);

    public string CurrentBatterySettingLabel
    {
        get => currentBatterySettingLabel;
        private set => SetField(ref currentBatterySettingLabel, value);
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
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(ProfileStateLabel)));
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

    public LiveReadings? LiveReadings
    {
        get => liveReadings;
        private set
        {
            if (EqualityComparer<LiveReadings?>.Default.Equals(liveReadings, value))
            {
                return;
            }

            liveReadings = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LiveReadings)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BatteryPowerFlowLabel)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Fan1RpmLabel)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Fan2RpmLabel)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Fan1RpmValue)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Fan2RpmValue)));
            NotifyTrayHoverLabel();
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
            AddOverrideStatus(
                active,
                "Performance",
                PerformanceOverride,
                PerformancePresetPresentation.Label);
            AddOverrideStatus(active, "Fans", FanOverride);
            AddOverrideStatus(active, "Lighting", LightingOverride);
            return active.Count == 0
                ? "No manual overrides active."
                : string.Join(" | ", active);
        }
    }

    public bool HasActiveChanges => PerformanceOverride is not null
        || FanOverride is not null
        || (IsLightingVisible && LightingOverride is not null);

    public string ActiveChangesText
    {
        get
        {
            List<string> active = [];
            AddOverrideStatus(
                active,
                "Performance",
                PerformanceOverride,
                PerformancePresetPresentation.Label);
            AddOverrideStatus(active, "Fans", FanOverride);
            if (IsLightingVisible)
            {
                AddOverrideStatus(active, "Keyboard lighting", LightingOverride);
            }
            return string.Join(" · ", active);
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
        set
        {
            if (pluggedInPerformanceEditor == value)
            {
                return;
            }

            SetEditorField(ref pluggedInPerformanceEditor, value);
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(PluggedInPerformanceOption)));
        }
    }

    public PerformancePresetOption? PluggedInPerformanceOption
    {
        get => FindPerformanceOption(
            AvailablePluggedInPerformanceOptions,
            PluggedInPerformanceEditor);
        set
        {
            if (value is not null)
            {
                PluggedInPerformanceEditor = value.Value;
            }
        }
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
        set
        {
            if (batteryPerformanceEditor == value)
            {
                return;
            }

            SetEditorField(ref batteryPerformanceEditor, value);
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(BatteryPerformanceOption)));
        }
    }

    public PerformancePresetOption? BatteryPerformanceOption
    {
        get => FindPerformanceOption(
            AvailableBatteryPerformanceOptions,
            BatteryPerformanceEditor);
        set
        {
            if (value is not null)
            {
                BatteryPerformanceEditor = value.Value;
            }
        }
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

    public PerformancePreset UsbCPerformanceEditor
    {
        get => usbCPerformanceEditor;
        set
        {
            if (usbCPerformanceEditor == value)
            {
                return;
            }

            SetEditorField(ref usbCPerformanceEditor, value);
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(UsbCPerformanceOption)));
        }
    }

    public PerformancePresetOption? UsbCPerformanceOption
    {
        get => FindPerformanceOption(
            AvailableUsbCPerformanceOptions,
            UsbCPerformanceEditor);
        set
        {
            if (value is not null)
            {
                UsbCPerformanceEditor = value.Value;
            }
        }
    }

    public FanMode UsbCFanModeEditor
    {
        get => usbCFanModeEditor;
        set => SetEditorField(ref usbCFanModeEditor, value);
    }

    public LightingSettings UsbCLightingEditor
    {
        get => usbCLightingEditor;
        set => SetEditorField(ref usbCLightingEditor, value);
    }

    public int ProfileChargeLimitEditor
    {
        get => profileChargeLimitEditor;
        set => SetEditorField(ref profileChargeLimitEditor, value);
    }

    public bool HasUnsavedProfileChanges => profileEditorsDirty;

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
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            if (theme == value)
            {
                return;
            }

            SetField(ref theme, value);
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(SelectedThemeOption)));
        }
    }

    public IReadOnlyList<ThemePreference> ThemeOptions { get; } =
        Enum.GetValues<ThemePreference>();

    public IReadOnlyList<ThemePreferenceOption> ThemeSelectionOptions { get; } =
    [
        new(ThemePreference.FollowWindows, "Follow Windows"),
        new(ThemePreference.Light, "Light"),
        new(ThemePreference.Dark, "Dark"),
    ];

    public ThemePreferenceOption SelectedThemeOption
    {
        get => ThemeSelectionOptions.Single(option => option.Preference == Theme);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            Theme = value.Preference;
        }
    }

    public bool KeepManualChangesUntilCleared
    {
        get => keepManualChangesUntilCleared;
        set
        {
            if (keepManualChangesUntilCleared == value)
            {
                return;
            }

            SetField(ref keepManualChangesUntilCleared, value);
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(SelectedManualChangeDurationOption)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(SelectedOverrideLifetime)));
        }
    }

    public IReadOnlyList<ManualChangeDurationOption> ManualChangeDurationOptions { get; } =
    [
        new(
            OverrideLifetime.UntilPowerSourceChange,
            "Until the power source changes"),
        new(OverrideLifetime.Persistent, "Until I reset them"),
    ];

    public ManualChangeDurationOption SelectedManualChangeDurationOption
    {
        get => ManualChangeDurationOptions.Single(
            option => option.Lifetime == SelectedOverrideLifetime);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            KeepManualChangesUntilCleared = value.Lifetime switch
            {
                OverrideLifetime.UntilPowerSourceChange => false,
                OverrideLifetime.Persistent => true,
                _ => throw new ArgumentOutOfRangeException(nameof(value)),
            };
        }
    }

    public OverrideLifetime SelectedOverrideLifetime => KeepManualChangesUntilCleared
        ? OverrideLifetime.Persistent
        : OverrideLifetime.UntilPowerSourceChange;

    public IReadOnlyList<LiveReadingsRefreshOption> LiveReadingsRefreshOptions { get; } =
    [
        new(LiveReadingsCadence.Off, "Off"),
        new(LiveReadingsCadence.EverySecond, "Every second"),
        new(LiveReadingsCadence.EveryTwoSeconds, "Every 2 seconds"),
        new(LiveReadingsCadence.EveryFiveSeconds, "Every 5 seconds"),
        new(LiveReadingsCadence.EveryTenSeconds, "Every 10 seconds"),
    ];

    public LiveReadingsCadence LiveReadingsRefreshCadence
    {
        get => liveReadingsRefreshCadence;
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            if (liveReadingsRefreshCadence == value)
            {
                return;
            }
            SetField(ref liveReadingsRefreshCadence, value);
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(LiveReadingsEnabled)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(SelectedLiveReadingsRefreshOption)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(LiveReadingsObservationInterval)));
            if (!LiveReadingsEnabled)
            {
                LiveReadings = null;
                ClearLiveReadingsRefreshTime();
            }
            NotifyTrayHoverLabel();
        }
    }

    public bool LiveReadingsEnabled
    {
        get => LiveReadingsRefreshCadence != LiveReadingsCadence.Off;
        set => LiveReadingsRefreshCadence = value
            ? LiveReadingsRefreshCadence == LiveReadingsCadence.Off
                ? LiveReadingsCadence.EveryTwoSeconds
                : LiveReadingsRefreshCadence
            : LiveReadingsCadence.Off;
    }

    public LiveReadingsRefreshOption SelectedLiveReadingsRefreshOption
    {
        get => LiveReadingsRefreshOptions.Single(
            option => option.Cadence == LiveReadingsRefreshCadence);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            LiveReadingsRefreshCadence = value.Cadence;
        }
    }

    public TimeSpan LiveReadingsObservationInterval =>
        LiveReadingsRefreshCadence switch
        {
            LiveReadingsCadence.Off => TimeSpan.FromSeconds(5),
            LiveReadingsCadence.EverySecond => TimeSpan.FromSeconds(1),
            LiveReadingsCadence.EveryTwoSeconds => TimeSpan.FromSeconds(2),
            LiveReadingsCadence.EveryFiveSeconds => TimeSpan.FromSeconds(5),
            LiveReadingsCadence.EveryTenSeconds => TimeSpan.FromSeconds(10),
            _ => throw new InvalidOperationException(
                "The live-readings refresh interval is invalid."),
        };

    public IReadOnlyList<TrayHoverLabelOption> TrayHoverLabelOptions { get; } =
    [
        new(TrayHoverLabelMode.OpenBlade, "OpenBlade only"),
        new(TrayHoverLabelMode.ActiveProfile, "Active profile"),
        new(TrayHoverLabelMode.BatteryPowerFlow, "Battery power flow"),
        new(TrayHoverLabelMode.ProfileAndPowerFlow, "Profile and battery power"),
    ];

    public TrayHoverLabelMode TrayHoverLabelMode
    {
        get => trayHoverLabelMode;
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            if (trayHoverLabelMode == value)
            {
                return;
            }

            SetField(ref trayHoverLabelMode, value);
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(SelectedTrayHoverLabelOption)));
            NotifyTrayHoverLabel();
        }
    }

    public TrayHoverLabelOption SelectedTrayHoverLabelOption
    {
        get => TrayHoverLabelOptions.Single(option => option.Mode == TrayHoverLabelMode);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            TrayHoverLabelMode = value.Mode;
        }
    }

    public string TrayHoverLabelText => TrayHoverLabelFormatter.Format(
        TrayHoverLabelMode,
        IsConnected,
        ActivePowerSource,
        SelectedPerformance,
        BatteryPowerFlowLabel);

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
        private set
        {
            if (startupStatusMessage == value)
            {
                return;
            }

            SetField(ref startupStatusMessage, value);
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(StartupDescription)));
        }
    }

    public string StartupDescription => string.IsNullOrWhiteSpace(StartupStatusMessage)
        ? "Launch OpenBlade automatically when you sign in."
        : StartupStatusMessage;

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

    private bool HasConfirmedPowerSource => ActivePowerSource is
        PowerSource.PluggedIn or PowerSource.Battery or PowerSource.UsbC;

    private bool CanSetPerformance(object? parameter) =>
        parameter is PerformancePreset value
        && Enum.IsDefined(value)
        && CanMutate
        && HasConfirmedPowerSource
        && latestStatus!.Capabilities!.PerformancePresets.Contains(value)
        && PerformancePresetPresentation.IsAvailable(ActivePowerSource, value)
        && !MaximumFansAreEffectiveOrConfirmed();

    private bool CanSetFanMode(object? parameter)
    {
        if (parameter is not FanMode value
            || !Enum.IsDefined(value)
            || !CanMutate
            || !HasConfirmedPowerSource
            || !latestStatus!.Capabilities!.FanModes.Contains(value))
        {
            return false;
        }

        return FanModeAvailable(ActivePowerSource, value)
            && (value == FanMode.Automatic || IsBalancedAutomaticThermalState());
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
            || !HasConfirmedPowerSource
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
            && (!enable || ActivePowerSource is PowerSource.PluggedIn or PowerSource.UsbC);
    }

    private bool CanToggleBatteryProtection(object? parameter)
    {
        if (parameter is not null
            || !CanMutate
            || !HasConfirmedPowerSource
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
        bool includeLiveReadings = false;
        await dispatcher.InvokeAsync(
            () =>
            {
                stamp = new ObservationRequestStamp(
                    connectionGeneration,
                    subscriptionConnected);
                includeLiveReadings = LiveReadingsEnabled;
            },
            cancellationToken);

        try
        {
            ServiceStatusSnapshot status = await serviceClient
                .ObserveStateAsync(includeLiveReadings, cancellationToken);
            await dispatcher.InvokeAsync(
                () =>
                {
                    bool accepted = ApplyObservedStatus(status, stamp);
                    if (accepted
                        && includeLiveReadings
                        && LiveReadingsEnabled
                        && status.LiveReadings is not null)
                    {
                        MarkLiveReadingsRefreshed();
                    }
                },
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
        CanControlHardware = controllable
            && status.ActivePowerSource != PowerSource.ExternalUnknown;
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
        CurrentBatterySettingLabel = FormatCurrentBatterySetting(confirmed?.Battery);
        SetTemporaryFullChargeState(confirmed?.Battery?.TemporaryFullCharge);
        ActivePowerSource = status.ActivePowerSource;
        LiveReadings = LiveReadingsEnabled ? status.LiveReadings : null;
        PerformanceOverride = status.PerformanceOverride;
        FanOverride = status.FanOverride;
        LightingOverride = status.LightingOverride;
        ActivePowerSourceLabel = status.ActivePowerSource switch
        {
            PowerSource.PluggedIn => "Plugged-in profile",
            PowerSource.Battery => "Battery profile",
            PowerSource.UsbC => "USB-C profile",
            PowerSource.ExternalUnknown => "Checking power source",
            _ => "Power source unavailable",
        };
        PerformanceSourceLabel = SourceLabel(
            DeviceSubsystem.Performance,
            confirmed?.Performance is not null,
            status.DeviceVerified,
            externalChanges);
        FanSourceLabel = SourceLabel(
            DeviceSubsystem.Fans,
            confirmed?.Fans is not null,
            status.DeviceVerified,
            externalChanges);
        LightingSourceLabel = SourceLabel(
            DeviceSubsystem.Lighting,
            confirmed?.Lighting is not null,
            status.DeviceVerified,
            externalChanges);
        BatterySourceLabel = FormatBatterySource(
            confirmed?.Battery,
            externalChanges.Contains(DeviceSubsystem.Battery),
            status.DeviceVerified);
        PerformancePreset[] performancePresets = capabilities.PerformancePresets
            .Where(Enum.IsDefined)
            .Order()
            .ToArray();
        if (!AvailablePerformancePresets.SequenceEqual(performancePresets))
        {
            AvailablePerformancePresets = performancePresets;
            // WinUI clears a ComboBox selection when ItemsSource is replaced.
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(SelectedPerformance)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(PluggedInPerformanceEditor)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(BatteryPerformanceEditor)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(UsbCPerformanceEditor)));
        }
        var admittedPerformancePresets = performancePresets.ToHashSet();
        IReadOnlyList<PerformancePresetOption> pluggedInOptions =
            PerformancePresetPresentation.CreateOptions(
                PowerSource.PluggedIn,
                admittedPerformancePresets);
        IReadOnlyList<PerformancePresetOption> batteryOptions =
            PerformancePresetPresentation.CreateOptions(
                PowerSource.Battery,
                admittedPerformancePresets);
        IReadOnlyList<PerformancePresetOption> usbCOptions =
            PerformancePresetPresentation.CreateOptions(
                PowerSource.UsbC,
                admittedPerformancePresets);
        bool performanceOptionsChanged = false;
        if (!AvailablePluggedInPerformanceOptions.SequenceEqual(pluggedInOptions))
        {
            AvailablePluggedInPerformanceOptions = pluggedInOptions;
            performanceOptionsChanged = true;
        }
        if (!AvailableBatteryPerformanceOptions.SequenceEqual(batteryOptions))
        {
            AvailableBatteryPerformanceOptions = batteryOptions;
            performanceOptionsChanged = true;
        }
        if (!AvailableUsbCPerformanceOptions.SequenceEqual(usbCOptions))
        {
            AvailableUsbCPerformanceOptions = usbCOptions;
            performanceOptionsChanged = true;
        }
        if (performanceOptionsChanged)
        {
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(AvailableActivePerformanceOptions)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(SelectedPerformanceOption)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(PluggedInPerformanceOption)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(BatteryPerformanceOption)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(UsbCPerformanceOption)));
        }
        FanMode[] fanModes = capabilities.FanModes
            .Where(Enum.IsDefined)
            .Order()
            .ToArray();
        if (!AvailableFanModes.SequenceEqual(fanModes))
        {
            AvailableFanModes = fanModes;
            AvailableUsbCFanModes = fanModes.Contains(FanMode.Automatic)
                ? [FanMode.Automatic]
                : [];
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(PluggedInFanModeEditor)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(BatteryFanModeEditor)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(UsbCFanModeEditor)));
            NotifyActiveFanSelectionBinding();
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
        NotifyPresentationState();
    }

    private void NotifyActiveFanSelectionBinding()
    {
        // WinUI clears SelectedItem whenever the active ItemsSource is replaced.
        // Always publish the list first, then reapply the confirmed selection.
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(AvailableActiveFanModes)));
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(SelectedFanMode)));
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
        ControlAccessStatus = "OpenBlade service isn’t running";
        NotifyCommands();
        NotifyPresentationState();
    }

    private bool ApplyObservedStatus(
        ServiceStatusSnapshot status,
        ObservationRequestStamp stamp)
    {
        if (stamp.BeganWithConnectedSubscription
            && stamp.Generation == connectionGeneration)
        {
            ApplyStatus(status);
            return true;
        }

        return false;
    }

    private void MarkLiveReadingsRefreshed()
    {
        lastLiveReadingsRefreshAt = timeProvider.GetUtcNow();
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(LiveReadingsRefreshToolTip)));
    }

    private void ClearLiveReadingsRefreshTime()
    {
        if (lastLiveReadingsRefreshAt is null)
        {
            return;
        }

        lastLiveReadingsRefreshAt = null;
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(LiveReadingsRefreshToolTip)));
    }

    private bool CanSetChargeLimit(int value)
    {
        if (!CanMutate
            || !HasConfirmedPowerSource
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
        if (FanOverride is not null)
        {
            await ClearOverrideAsync(
                "fans",
                cancellationToken => serviceClient.SetFanOverrideAsync(null, cancellationToken),
                hardwareNotApplied,
                token).ConfigureAwait(false);
        }
        if (PerformanceOverride is not null)
        {
            await ClearOverrideAsync(
                "performance",
                cancellationToken => serviceClient.SetPerformanceOverrideAsync(null, cancellationToken),
                hardwareNotApplied,
                token).ConfigureAwait(false);
        }
        if (LightingOverride is not null)
        {
            await ClearOverrideAsync(
                "lighting",
                cancellationToken => serviceClient.SetLightingOverrideAsync(null, cancellationToken),
                hardwareNotApplied,
                token).ConfigureAwait(false);
        }

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
            SetProfileEditorsDirty(false);
            LoadProfileEditors(draft);
        }
    }

    private bool CanSaveProfiles(object? parameter) =>
        profileEditorsDirty
        && CanMutate
        && TryGetSupportedProfileDraft(parameter, out _);

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
                new PowerProfile(
                    UsbCPerformanceEditor,
                    UsbCFanModeEditor,
                    UsbCLightingEditor),
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
                capabilities,
                PowerSource.PluggedIn)
            && ProfileSupportedOrUnchanged(
                draft.Battery,
                Profiles.Battery,
                capabilities,
                PowerSource.Battery)
            && ProfileSupportedOrUnchanged(
                draft.UsbC,
                Profiles.UsbC,
                capabilities,
                PowerSource.UsbC)
            && HasBatteryCapability(capabilities)
            && capabilities.ChargeLimits.Contains(
                draft.BatteryPolicy.ChargeLimitPercent);
    }

    private static bool ProfileSupportedOrUnchanged(
        PowerProfile draft,
        PowerProfile current,
        DeviceCapabilities capabilities,
        PowerSource powerSource) =>
        Enum.IsDefined(draft.Performance)
        && (capabilities.PerformancePresets.Contains(draft.Performance)
            || draft.Performance == current.Performance)
        && (PerformancePresetPresentation.IsAvailable(powerSource, draft.Performance)
            || (powerSource != PowerSource.UsbC
                && draft.Performance == current.Performance))
        && Enum.IsDefined(draft.Fans)
        && (capabilities.FanModes.Contains(draft.Fans)
            || draft.Fans == current.Fans)
        && (FanModeAvailable(powerSource, draft.Fans)
            || (powerSource != PowerSource.UsbC
                && draft.Fans == current.Fans))
        && ThermalCombinationSupported(draft.Performance, draft.Fans)
        && (LightingSupported(draft.Lighting, capabilities)
            || draft.Lighting == current.Lighting);

    private static bool ThermalCombinationSupported(
        PerformancePreset performance,
        FanMode fans) =>
        fans == FanMode.Automatic
        || (performance == PerformancePreset.Balanced && fans == FanMode.Maximum);

    private static bool FanModeAvailable(PowerSource powerSource, FanMode fans) =>
        powerSource switch
        {
            PowerSource.PluggedIn or PowerSource.Battery =>
                fans is FanMode.Automatic or FanMode.Maximum,
            PowerSource.UsbC => fans == FanMode.Automatic,
            PowerSource.ExternalUnknown => false,
            _ => false,
        };

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
                    OperationFeedbackMessage = FormatOperationResult(result);
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
            DiagnosticArchive archive = await serviceClient.ExportDiagnosticsAsync(
                LiveReadingsEnabled,
                token);
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
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(PluggedInPerformanceOption)));
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(BatteryPerformanceOption)));
        SetField(
            ref batteryFanModeEditor,
            value.Battery.Fans,
            nameof(BatteryFanModeEditor));
        SetField(
            ref batteryLightingEditor,
            value.Battery.Lighting,
            nameof(BatteryLightingEditor));
        SetField(
            ref usbCPerformanceEditor,
            value.UsbC.Performance,
            nameof(UsbCPerformanceEditor));
        SetField(
            ref usbCFanModeEditor,
            value.UsbC.Fans,
            nameof(UsbCFanModeEditor));
        SetField(
            ref usbCLightingEditor,
            value.UsbC.Lighting,
            nameof(UsbCLightingEditor));
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(UsbCPerformanceOption)));
        SetField(
            ref profileChargeLimitEditor,
            value.BatteryPolicy.ChargeLimitPercent,
            nameof(ProfileChargeLimitEditor));
        SetProfileEditorsDirty(false);
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

        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(CanAdjustChargeLimit)));
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(CanChangeBatteryProtection)));
    }

    private static string SourceLabel(
        DeviceSubsystem subsystem,
        bool confirmed,
        bool deviceVerified,
        IReadOnlyCollection<DeviceSubsystem> externalChanges) =>
        !deviceVerified
            ? "Laptop not found"
            : externalChanges.Contains(subsystem)
            ? "Changed by another app"
            : confirmed
                ? "Current setting"
                : "Controls unavailable";

    private static string FormatBatterySource(
        ConfirmedBatteryState? battery,
        bool changedOutside,
        bool deviceVerified)
    {
        if (!deviceVerified)
        {
            return "Laptop not found";
        }
        if (battery is null)
        {
            return "Controls unavailable";
        }

        string state = battery.TemporaryFullCharge switch
        {
            null => "Temporary full charge state unavailable",
            true => "Temporary full charge active",
            false when !battery.ProtectionEnabled => "Battery protection off",
            _ => $"Charge limit {battery.ChargeLimitPercent}%",
        };
        string source = changedOutside
            ? "Changed by another app"
            : "Current setting";
        return $"{source} · {state}";
    }

    private static string FormatCurrentBatterySetting(ConfirmedBatteryState? battery) =>
        battery switch
        {
            null => "Battery setting unavailable",
            { TemporaryFullCharge: true } => "Charging to 100%",
            { ProtectionEnabled: false } => "Battery protection off",
            _ => $"Charge limit {battery.ChargeLimitPercent}%",
        };

    private static string FormatBatteryPowerFlow(
        LiveReadings? readings,
        PowerSource powerSource)
    {
        if (readings?.BatteryPowerFlowMilliwatts is not int milliwatts)
        {
            return "Battery power unavailable";
        }
        if (milliwatts == 0)
        {
            return powerSource switch
            {
                PowerSource.PluggedIn or PowerSource.UsbC => "Not charging",
                PowerSource.Battery or PowerSource.ExternalUnknown =>
                    "Battery power unavailable",
                _ => "Battery power unavailable",
            };
        }
        double watts = Math.Abs((long)milliwatts) / 1000d;
        string value = watts.ToString("0.0", CultureInfo.InvariantCulture);
        return milliwatts > 0
            ? $"Charging at {value} W"
            : $"Using {value} W";
    }

    private static string FormatFanRpm(string label, int? rpm) => rpm is int value
        ? $"{label}: {value.ToString("N0", CultureInfo.CurrentCulture)} RPM"
        : $"{label}: unavailable";

    private static string FormatFanRpmValue(int? rpm) => rpm is int value
        ? $"{value.ToString("N0", CultureInfo.CurrentCulture)} RPM"
        : "Unavailable";

    private static string FormatControlAccessStatus(ServiceStatusSnapshot status)
    {
        if (!status.DeviceVerified)
        {
            return "Laptop not found";
        }

        if (status.ActivePowerSource == PowerSource.ExternalUnknown)
        {
            return "Checking power source";
        }

        return status.WritesBlocked
            ? "Controls unavailable"
            : "Ready";
    }

    private static string FormatOperationFeedback(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }
        if (message.Contains("without hardware", StringComparison.OrdinalIgnoreCase)
            || message.Contains("not applied", StringComparison.OrdinalIgnoreCase))
        {
            return "OpenBlade saved the change, but the laptop has not applied it.";
        }
        if (message.Contains("failed", StringComparison.OrdinalIgnoreCase)
            || message.Contains("could not", StringComparison.OrdinalIgnoreCase)
            || message.Contains("unavailable", StringComparison.OrdinalIgnoreCase))
        {
            return "OpenBlade couldn’t complete that action. Check Troubleshooting for details.";
        }
        return string.Empty;
    }

    private static string FormatTroubleshootingValue(string value) =>
        value.Trim().TrimEnd('.');

    private static string FormatOperationResult(ControlOperationResult result) => result.Status switch
    {
        ControlOperationStatus.Applied or ControlOperationStatus.Stored => string.Empty,
        ControlOperationStatus.Blocked => "OpenBlade saved the change, but the laptop has not applied it.",
        _ => "OpenBlade couldn’t complete that action. Check Troubleshooting for details.",
    };

    private void NotifyPresentationState()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProfileStateLabel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsReadinessReady)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsReadinessChecking)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsReadinessWarning)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsReadinessError)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasAttention)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AttentionSummary)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AttentionDetails)));
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(TroubleshootingServiceStatus)));
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(TroubleshootingLaptopStatus)));
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(TroubleshootingPowerSourceStatus)));
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(TroubleshootingControlsStatus)));
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(TroubleshootingOtherRazerSoftwareStatus)));
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(TroubleshootingDetectionStatus)));
    }

    private void NotifyTrayHoverLabel() => PropertyChanged?.Invoke(
        this,
        new PropertyChangedEventArgs(nameof(TrayHoverLabelText)));

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
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasActiveChanges)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActiveChangesText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProfileStateLabel)));
    }

    private static void AddOverrideStatus<T>(
        ICollection<string> status,
        string subsystem,
        ManualOverride<T>? value,
        Func<T, string>? formatValue = null)
    {
        if (value is null)
        {
            return;
        }

        string lifetime = value.Lifetime == OverrideLifetime.Persistent
            ? "until reset"
            : "until the power source changes";
        string displayValue = formatValue?.Invoke(value.Value)
            ?? (value.Value is null ? string.Empty : value.Value.ToString())
            ?? string.Empty;
        status.Add($"{subsystem}: {displayValue} {lifetime}");
    }

    private static PerformancePresetOption? FindPerformanceOption(
        IReadOnlyList<PerformancePresetOption> options,
        PerformancePreset value) =>
        options.FirstOrDefault(option => option.Value == value);

    private void SetEditorField<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        SetProfileEditorsDirty(true);
        SetField(ref field, value, propertyName);
        NotifyCommands();
    }

    private void SetProfileEditorsDirty(bool value)
    {
        if (profileEditorsDirty == value)
        {
            return;
        }

        profileEditorsDirty = value;
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(HasUnsavedProfileChanges)));
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
