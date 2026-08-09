using System.Collections.ObjectModel;
using System.Collections.Specialized;
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

public sealed record LiveReadingsRefreshOption(
    LiveReadingsCadence Cadence,
    string Label);

public sealed record ProfileComparisonOption(
    PowerSource Source,
    string Label);

public sealed record LightingDirectionOption(
    LightingDirection Value,
    string Label);

public sealed record ProfileValuePresentation(
    string Text,
    string SettingName,
    string ProfileName,
    bool IsPending = false,
    string PendingText = "")
{
    public string? PendingDescription => IsPending
        ? $"Pending: {PendingText}"
        : null;

    public string AccessibilityDescription => IsPending
        ? $"{ProfileName} {SettingName}: {Text}. Pending: {PendingText}"
        : $"{ProfileName} {SettingName}: {Text}";
}

public sealed record ProfilePresentation(
    ProfileValuePresentation PowerMode,
    ProfileValuePresentation GpuTuning,
    ProfileValuePresentation Fans,
    ProfileValuePresentation LightingEffect,
    ProfileValuePresentation LightingBrightness,
    ProfileValuePresentation LightingColor,
    ProfileValuePresentation LogoLighting,
    ProfileValuePresentation LightingDisplayOff,
    ProfileValuePresentation LightingIdle,
    ProfileValuePresentation DisplayRefreshRate,
    ProfileValuePresentation DisplayHdr,
    ProfileValuePresentation DisplayColorProfile,
    ProfileValuePresentation FunctionKeyBehavior,
    ProfileValuePresentation GamingMode,
    ProfileValuePresentation GamingModeWindowsKey,
    ProfileValuePresentation GamingModeAltTab,
    ProfileValuePresentation StartupAnimation);

public sealed partial class MainViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private const string UnavailableLabel = "Unavailable";
    private const byte DefaultLightingBrightness = 50;
    private static readonly TimeSpan ReconnectVisualGracePeriod =
        TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan PerformancePresentationHoldGracePeriod =
        TimeSpan.FromSeconds(2);

    private enum ProfileDraftGroup
    {
        Thermal,
        Lighting,
        Display,
        Laptop,
    }

    private sealed record PendingPerformanceHold(
        PerformanceOverride Requested,
        PowerSource PowerSource,
        DateTimeOffset? ExpiresAt);

    private static readonly IReadOnlyList<ProfileComparisonOption> ComparisonOptions =
    [
        new(PowerSource.PluggedIn, "AC"),
        new(PowerSource.UsbC, "USB-C"),
        new(PowerSource.Battery, "On battery"),
    ];

    private static readonly IReadOnlyList<LightingEffect> KnownLightingEffects =
        Enum.GetValues<LightingEffect>()
            .Where(Enum.IsDefined)
            .Order()
            .ToArray();

    private static readonly IReadOnlyList<LogoLightingMode> KnownLogoLightingModes =
        Enum.GetValues<LogoLightingMode>()
            .Where(Enum.IsDefined)
            .Order()
            .ToArray();

    private static readonly IReadOnlyList<LightingEffectOption> KnownLightingEffectOptions =
        KnownLightingEffects
            .Select(CreateLightingEffectOption)
            .OrderBy(option => option.Effect == LightingEffect.Off ? 0 : 1)
            .ThenBy(option => option.Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static readonly IReadOnlyList<LogoLightingModeOption> KnownLogoLightingModeOptions =
        KnownLogoLightingModes
            .Select(mode => new LogoLightingModeOption(mode, mode.ToString()))
            .ToArray();

    private static readonly IReadOnlyList<CustomPerformanceLevel> AllCustomPerformanceLevels =
        Enum.GetValues<CustomPerformanceLevel>();

    private static readonly IReadOnlyList<LightingDirectionOption> StandardLightingDirectionOptions =
    [
        new(LightingDirection.Leftward, "Leftward"),
        new(LightingDirection.Rightward, "Rightward"),
    ];

    private static readonly IReadOnlyList<LightingDirectionOption> TidalLightingDirectionOptions =
    [
        new(LightingDirection.Leftward, "Outward"),
        new(LightingDirection.Rightward, "Inward"),
    ];

    private readonly IServiceClient serviceClient;
    private readonly IUiDispatcher dispatcher;
    private readonly IDiagnosticArchiveSaver diagnosticArchiveSaver;
    private readonly IStartupRegistration startupRegistration;
    private readonly TimeProvider timeProvider;
    private readonly string uiVersion;
    private readonly string sessionAgentVersion;
    private readonly string installedServiceVersion;
    private readonly string applicationBaseDirectory;
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
    private string troubleshootingRecentErrorStatus = string.Empty;
    private string controlAccessStatus = "OpenBlade service isn’t running";
    private string activePowerSourceLabel = UnavailableLabel;
    private string performanceSourceLabel = UnavailableLabel;
    private string fanSourceLabel = UnavailableLabel;
    private string lightingSourceLabel = UnavailableLabel;
    private string batterySourceLabel = UnavailableLabel;
    private bool hasControllerAdvisory;
    private string controllerAdvisoryText = string.Empty;
    private bool hasControllerDetectionWarning;
    private string controllerDetectionWarningText = string.Empty;
    private bool hasExternalChanges;
    private string externalChangesText = string.Empty;
    private bool alwaysOverrideExternalLightingChanges;
    private bool allowUntestedFirmware;
    private KeyboardLightingControlMode keyboardLightingControlMode;
    private IReadOnlyList<PerformancePreset> availablePerformancePresets = [];
    private IReadOnlyList<PerformancePresetOption> availablePluggedInPerformanceOptions = [];
    private IReadOnlyList<PerformancePresetOption> availableBatteryPerformanceOptions = [];
    private IReadOnlyList<PerformancePresetOption> availableUsbCPerformanceOptions = [];
    private IReadOnlyList<FanMode> availableFanModes = [];
    private IReadOnlyList<FanMode> availableUsbCFanModes = [];
    private IReadOnlyList<FanModeOption> availableFanModeOptions = [];
    private IReadOnlyList<FanModeOption> availableUsbCFanModeOptions = [];
    private IReadOnlyList<LightingEffect> availableLightingEffects = [];
    private IReadOnlyList<LightingEffectOption> availableLightingEffectOptions =
        KnownLightingEffectOptions;
    private IReadOnlyList<LogoLightingModeOption> availableLogoLightingModeOptions =
        KnownLogoLightingModeOptions;
    private IReadOnlyList<LightingSettings> availableLightingSettings = [];
    private bool isPerformanceVisible;
    private bool isFanControlVisible;
    private PerformancePreset selectedPerformance = PerformancePreset.Balanced;
    private CustomPerformanceLevel selectedCustomCpuLevel = CustomPerformanceLevel.Medium;
    private CustomPerformanceLevel selectedCustomGpuLevel = CustomPerformanceLevel.High;
    private PendingPerformanceHold? pendingPerformance;
    private IReadOnlyList<CustomPerformanceLevel> availableCustomCpuLevels =
        AllCustomPerformanceLevels;
    private IReadOnlyList<CustomPerformanceLevel> availableCustomGpuLevels =
        AllCustomPerformanceLevels;
    private GpuClockOffsetCapabilities? gpuClockOffsetCapabilities;
    private bool gpuClockTuningEnabled;
    private bool gpuClockTuningConfigured;
    private int gpuCoreClockOffsetMegahertz;
    private int gpuMemoryClockOffsetMegahertz;
    private FanMode selectedFanMode = FanMode.Automatic;
    private FanControlSettings? pendingFanControl;
    private bool isFanControlOperationInProgress;
    private bool isPerformanceOperationInProgress;
    private bool isLightingOperationInProgress;
    private bool isChargeLimitOperationInProgress;
    private bool isBatteryProtectionOperationInProgress;
    private bool isFullChargeOperationInProgress;
    private bool isReapplyChangedSettingsOperationInProgress;
    private bool isStartupAnimationOperationInProgress;
    private bool isFunctionKeyOperationInProgress;
    private bool isGamingModeOperationInProgress;
    private bool startupAnimationReadPending;
    private bool functionKeyReadPending;
    private bool gamingModeReadPending;
    private int fixedFanSpeedRpm = FanControlSettings.Default.FixedSpeedRpm;
    private bool useHigherFanTemperature = true;
    private IReadOnlyList<FanCurveEditorPoint> cpuFanCurveEditorPoints =
        ManualFanCurve.Default.Cpu.Select(point => new FanCurveEditorPoint(point)).ToArray();
    private IReadOnlyList<FanCurveEditorPoint> gpuFanCurveEditorPoints =
        ManualFanCurve.Default.Gpu.Select(point => new FanCurveEditorPoint(point)).ToArray();
    private LightingSettings selectedLighting = ProfileDefaults.CreateSafeDefaults().PluggedIn.Lighting;
    private int chargeLimitPercent = 80;
    private string currentBatterySettingLabel = UnavailableLabel;
    private bool chargeLimitEditorDirty;
    private IReadOnlyList<int> chargeLimitOptions = [];
    private bool batteryProtectionEnabled = true;
    private bool fullChargeOverride;
    private bool temporaryFullChargeStateKnown;
    private DeviceCapabilities capabilities = DeviceCapabilities.Unavailable();
    private PowerSource activePowerSource = PowerSource.PluggedIn;
    private LiveReadings? liveReadings;
    private bool dashboardTelemetryPresentationActive = true;
    private DateTimeOffset? lastLiveReadingsRefreshAt;
    private PerformanceOverride? performanceOverride;
    private ManualOverride<FanMode>? fanOverride;
    private ManualOverride<LightingSettings>? lightingOverride;
    private ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
    private ProfileComparisonOption selectedProfileComparisonOption = ComparisonOptions[0];
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
    private bool keyboardEffectUnconfirmed;
    private int lightingBrightnessEditor;
    private int lightingRedEditor;
    private int lightingGreenEditor;
    private int lightingBlueEditor;
    private int lightingSecondaryRedEditor;
    private int lightingSecondaryGreenEditor;
    private int lightingSecondaryBlueEditor;
    private BreathingColorMode lightingBreathingColorModeEditor = BreathingColorMode.Single;
    private StarlightColorMode lightingStarlightColorModeEditor = StarlightColorMode.Single;
    private StarlightLightingDuration lightingStarlightDurationEditor =
        StarlightLightingDuration.Medium;
    private ReactiveLightingDuration lightingReactiveDurationEditor =
        ReactiveLightingDuration.Medium;
    private bool lightingRandomColorsEditor;
    private LightingDirection lightingDirectionEditor = LightingDirection.Rightward;
    private LightingRotationDirection lightingRotationDirectionEditor =
        LightingRotationDirection.Clockwise;
    private AmbientAwarenessRegion lightingAmbientAwarenessRegionEditor =
        AmbientAwarenessRegion.Full;
    private double lightingAudioMeterColorBoostEditor = 1;
    private int lightingAudioMeterDecayMillisecondsEditor =
        LightingSettingsFactory.DefaultAudioMeterDecayMilliseconds;
    private LogoLightingMode? logoLightingModeEditor;
    private bool lightingEditorDirty;
    private bool lightingTurnOffWithDisplayEditor;
    private bool lightingIdleTimeoutEnabledEditor;
    private int lightingIdleTimeoutMinutesEditor = 30;
    private bool lightingPowerPolicyEditorDirty;
    private PowerSource? lastConfirmedLightingEditorPowerSource;
    private PowerSource? activeProfileDraftSource;
    private PerformancePreset? activeProfileDraftPerformance;
    private CustomPerformanceSettings? activeProfileDraftCustomPerformance;
    private GpuClockTuningSettings? activeProfileDraftGpuClockTuning;
    private FanMode? activeProfileDraftFans;
    private FanControlSettings? activeProfileDraftFanControl;
    private LightingSettings? activeProfileDraftLighting;
    private LightingPowerPolicy? activeProfileDraftLightingPowerPolicy;
    private DisplayProfileSettings? activeProfileDraftDisplay;
    private LaptopBehaviorProfileSettings? activeProfileDraftLaptopBehavior;
    private LaptopBehaviorProfileSettings? activeProfileOriginalLaptopBehavior;
    private DisplayProfileSettings? activeProfileOriginalDisplay;
    private ThemePreference theme = ThemePreference.FollowWindows;
    private LiveReadingsCadence liveReadingsRefreshCadence =
        LiveReadingsCadence.EveryTwoSeconds;
    private TrayHoverLabelMode trayHoverLabelMode =
        TrayHoverLabelMode.ProfileAndPowerFlow;
    private bool startAtSignIn;
    private bool startupStateKnown;
    private bool? startupAnimationEnabled;
    private FunctionKeyPrimaryMode? functionKeyPrimary;
    private bool? gamingModeEnabled;
    private string startupStatusMessage = string.Empty;
    private TrayIconState trayIconState = TrayIconState.GenericError;
    private int disposed;
    private long latestAcceptedRevision = -1;
    private long connectionGeneration;
    private long reconnectGeneration;
    private bool subscriptionConnected;
    private bool changingTelemetryPreferences;
    private bool suppressProfileDifferenceNotifications;

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
        TimeProvider? timeProvider = null,
        string? uiVersion = null,
        string? sessionAgentVersion = null,
        string? installedServiceVersion = null,
        string? applicationBaseDirectory = null)
    {
        this.serviceClient = serviceClient ?? throw new ArgumentNullException(nameof(serviceClient));
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        this.diagnosticArchiveSaver = diagnosticArchiveSaver
            ?? throw new ArgumentNullException(nameof(diagnosticArchiveSaver));
        this.startupRegistration = startupRegistration
            ?? throw new ArgumentNullException(nameof(startupRegistration));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.uiVersion = string.IsNullOrWhiteSpace(uiVersion)
            ? OpenBladeProductVersion.Read(typeof(MainViewModel).Assembly)
            : uiVersion.Trim();
        this.sessionAgentVersion = ComponentVersionValue(sessionAgentVersion);
        this.installedServiceVersion = ComponentVersionValue(installedServiceVersion);
        this.applicationBaseDirectory = string.IsNullOrWhiteSpace(applicationBaseDirectory)
            ? AppContext.BaseDirectory
            : applicationBaseDirectory.Trim();

        DashboardTelemetryItems = new ObservableCollection<LiveReadingVisibilityOption>(
            TelemetryPreferenceDefaults.DashboardItems.Select(kind =>
                new LiveReadingVisibilityOption(kind, LiveReadingLabel(kind), true)));
        TrayHoverLabelItems = new ObservableCollection<TrayHoverLabelItemOption>(
            TelemetryPreferenceDefaults.TrayHoverItems.Select(item =>
                new TrayHoverLabelItemOption(
                    item.Kind,
                    TrayHoverLabelItemLabel(item.Kind),
                    item.IsEnabled)));
        DashboardTelemetryItems.CollectionChanged += OnDashboardTelemetryItemsChanged;
        TrayHoverLabelItems.CollectionChanged += OnTrayHoverLabelItemsChanged;
        SubscribeTelemetryOptions(DashboardTelemetryItems);
        SubscribeTrayHoverOptions(TrayHoverLabelItems);

        SetPerformanceCommand = CreateCommand(
            SetPerformanceAsync,
            CanSetPerformance);
        SetCustomPerformanceCommand = CreateCommand(
            SetCustomPerformanceAsync,
            CanSetCustomPerformance);
        SetFanModeCommand = CreateCommand(
            SetFanModeAsync,
            CanSetFanMode);
        SetFanControlCommand = CreateCommand(
            SetFanControlAsync,
            CanSetFanControl);
        SetLightingCommand = CreateCommand(
            SetLightingAsync,
            parameter => parameter is LightingSettings value
                && !IsLightingOperationInProgress
                && CanMutate
                && HasConfirmedPowerSource
                && LightingSupported(value, CommandCapabilities));
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
        DiscardProfileDraftCommand = CreateCommand(
            DiscardProfileDraftAsync,
            _ => HasActiveProfileDraft);
        SaveThermalProfileDraftCommand = CreateCommand(
            (_, token) => SaveActiveProfileDraftAsync(ProfileDraftGroup.Thermal, token),
            _ => CanSaveActiveProfileDraft(ProfileDraftGroup.Thermal));
        DiscardThermalProfileDraftCommand = CreateCommand(
            (_, token) => DiscardActiveProfileDraftAsync(ProfileDraftGroup.Thermal, token),
            _ => HasThermalProfileDifference);
        SaveLightingProfileDraftCommand = CreateCommand(
            (_, token) => SaveActiveProfileDraftAsync(ProfileDraftGroup.Lighting, token),
            _ => CanSaveActiveProfileDraft(ProfileDraftGroup.Lighting));
        DiscardLightingProfileDraftCommand = CreateCommand(
            (_, token) => DiscardActiveProfileDraftAsync(ProfileDraftGroup.Lighting, token),
            _ => HasLightingProfileDifference);
        SaveLightingPowerPolicyCommand = CreateCommand(
            SaveLightingPowerPolicyAsync,
            CanSaveLightingPowerPolicy);
        SaveDisplayProfileDraftCommand = CreateCommand(
            (_, token) => SaveActiveProfileDraftAsync(ProfileDraftGroup.Display, token),
            _ => CanSaveActiveProfileDraft(ProfileDraftGroup.Display));
        DiscardDisplayProfileDraftCommand = CreateCommand(
            (_, token) => DiscardActiveProfileDraftAsync(ProfileDraftGroup.Display, token),
            _ => HasDisplayProfileDraft);
        SaveLaptopProfileDraftCommand = CreateCommand(
            (_, token) => SaveActiveProfileDraftAsync(ProfileDraftGroup.Laptop, token),
            _ => CanSaveActiveProfileDraft(ProfileDraftGroup.Laptop));
        DiscardLaptopProfileDraftCommand = CreateCommand(
            (_, token) => DiscardActiveProfileDraftAsync(ProfileDraftGroup.Laptop, token),
            _ => HasLaptopPageProfileDifference);
        ExportDiagnosticsCommand = CreateCommand(
            ExportDiagnosticsAsync,
            _ => Volatile.Read(ref disposed) == 0 && IsConnected);
        SetStartAtSignInCommand = CreateCommand(
            SetStartAtSignInAsync,
            parameter => parameter is bool requested
                && Volatile.Read(ref disposed) == 0
                && StartupStateKnown
                && requested != StartAtSignIn);
        ApplyOneShotLaptopControlCommand = CreateCommand(
            ApplyOneShotLaptopControlAsync,
            CanApplyOneShotLaptopControl);
        commands =
        [
            (AsyncRelayCommand)SetPerformanceCommand,
            (AsyncRelayCommand)SetCustomPerformanceCommand,
            (AsyncRelayCommand)SetFanModeCommand,
            (AsyncRelayCommand)SetFanControlCommand,
            (AsyncRelayCommand)SetLightingCommand,
            (AsyncRelayCommand)SetChargeLimitCommand,
            (AsyncRelayCommand)ToggleBatteryProtectionCommand,
            (AsyncRelayCommand)ToggleFullChargeCommand,
            (AsyncRelayCommand)ClearManualOverridesCommand,
            (AsyncRelayCommand)SaveProfilesCommand,
            (AsyncRelayCommand)DiscardProfileDraftCommand,
            (AsyncRelayCommand)SaveThermalProfileDraftCommand,
            (AsyncRelayCommand)DiscardThermalProfileDraftCommand,
            (AsyncRelayCommand)SaveLightingProfileDraftCommand,
            (AsyncRelayCommand)DiscardLightingProfileDraftCommand,
            (AsyncRelayCommand)SaveLightingPowerPolicyCommand,
            (AsyncRelayCommand)SaveDisplayProfileDraftCommand,
            (AsyncRelayCommand)DiscardDisplayProfileDraftCommand,
            (AsyncRelayCommand)SaveLaptopProfileDraftCommand,
            (AsyncRelayCommand)DiscardLaptopProfileDraftCommand,
            (AsyncRelayCommand)ExportDiagnosticsCommand,
            (AsyncRelayCommand)SetStartAtSignInCommand,
            (AsyncRelayCommand)ApplyOneShotLaptopControlCommand,
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

    public event Func<PowerSource, DisplayProfileSettings, CancellationToken, Task<bool>>?
        SavedDisplayProfileRestoreRequested;

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
            NotifyFirmwareFieldLoadingState();
        }
    }

    public bool IsServiceUnavailable => !IsConnected;

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

    public bool CanRequestHardwareControl => CanControlHardware
        || IsRetainedPowerSourceRecheck(latestStatus);

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
            Notify(nameof(HasOperationFeedback));
        }
    }

    public bool HasOperationFeedback => !string.IsNullOrWhiteSpace(OperationFeedbackMessage);

    public string TroubleshootingRecentErrorStatus
    {
        get => troubleshootingRecentErrorStatus;
        private set
        {
            if (troubleshootingRecentErrorStatus == value)
            {
                return;
            }
            SetField(ref troubleshootingRecentErrorStatus, value);
            Notify(nameof(HasTroubleshootingRecentError));
        }
    }

    public bool HasTroubleshootingRecentError => !string.IsNullOrWhiteSpace(
        TroubleshootingRecentErrorStatus);

    public string ControlAccessStatus
    {
        get => IsConnected && latestStatus is { } status
            ? FormatControlAccessStatus(status)
            : controlAccessStatus;
        private set
        {
            if (controlAccessStatus == value)
            {
                return;
            }
            SetField(ref controlAccessStatus, value);
        }
    }

    public string BatteryPowerFlowLabel => IsConnected
        ? FormatBatteryPowerFlow(LiveReadings, ActivePowerSource)
        : UnavailableLabel;

    public string Fan1RpmLabel => IsConnected
        ? FormatFanRpm("Fan 1", LiveReadings?.Fan1Rpm)
        : UnavailableLabel;

    public string Fan2RpmLabel => IsConnected
        ? FormatFanRpm("Fan 2", LiveReadings?.Fan2Rpm)
        : UnavailableLabel;

    public string Fan1RpmValue => FormatFanRpmValue(LiveReadings?.Fan1Rpm);

    public string Fan2RpmValue => FormatFanRpmValue(LiveReadings?.Fan2Rpm);

    public string CpuTemperatureValue =>
        FormatTemperatureValue(LiveReadings?.CpuTemperatureCelsius);

    public string GpuTemperatureValue =>
        FormatTemperatureValue(LiveReadings?.GpuTemperatureCelsius);

    public ObservableCollection<LiveReadingVisibilityOption> DashboardTelemetryItems
    {
        get;
    }

    public ObservableCollection<TrayHoverLabelItemOption> TrayHoverLabelItems
    {
        get;
    }

    public IReadOnlyList<LiveReadingGroupDisplayItem> VisibleDashboardTelemetryGroups
    {
        get
        {
            List<LiveReadingGroupDisplayItem> groups = [];
            if (DashboardTelemetryEnabled(LiveReadingItemKind.BatteryPower))
            {
                groups.Add(new LiveReadingGroupDisplayItem(
                    "Battery",
                    BatteryPowerFlowLabel));
            }

            string temperatureValue = FormatDashboardTemperatures(
                DashboardTelemetryEnabled(LiveReadingItemKind.CpuTemperature),
                CpuTemperatureValue,
                DashboardTelemetryEnabled(LiveReadingItemKind.GpuTemperature)
                    && LiveReadings?.GpuTemperatureCelsius is not null,
                GpuTemperatureValue);
            if (temperatureValue.Length > 0)
            {
                groups.Add(new LiveReadingGroupDisplayItem(
                    "Temperatures",
                    temperatureValue));
            }

            bool showFan1 = DashboardTelemetryEnabled(LiveReadingItemKind.Fan1Speed);
            bool showFan2 = DashboardTelemetryEnabled(LiveReadingItemKind.Fan2Speed);
            string fanValue = (showFan1, showFan2) switch
            {
                (true, true) when LiveReadings?.Fan1Rpm is null
                    && LiveReadings?.Fan2Rpm is null => UnavailableLabel,
                (true, true) =>
                    $"{Fan1RpmValue} / {Fan2RpmValue}",
                (true, false) => Fan1RpmValue,
                (false, true) => Fan2RpmValue,
                _ => string.Empty,
            };
            if (fanValue.Length > 0)
            {
                string label = (showFan1, showFan2) switch
                {
                    (true, true) => "Fans",
                    (true, false) => "Fan 1",
                    _ => "Fan 2",
                };
                groups.Add(new LiveReadingGroupDisplayItem(label, fanValue));
            }
            return groups;
        }
    }

    public bool ShowDashboardLiveReadings =>
        LiveReadingsEnabled && DashboardTelemetryItems.Any(option => option.IsEnabled);

    public IReadOnlyList<LiveReadingItemKind> EnabledDashboardTelemetryItems =>
        DashboardTelemetryItems
            .Where(option => option.IsEnabled)
            .Select(option => option.Kind)
            .ToArray();

    public IReadOnlyList<TrayHoverLabelItemPreference> TrayHoverLabelPreferences =>
        TrayHoverLabelItems
            .Select(option => new TrayHoverLabelItemPreference(
                option.Kind,
                option.IsEnabled))
            .ToArray();

    public void ApplyTelemetryPreferences(
        IEnumerable<LiveReadingItemKind> dashboardItems,
        IEnumerable<TrayHoverLabelItemPreference> trayHoverItems)
    {
        ArgumentNullException.ThrowIfNull(dashboardItems);
        ArgumentNullException.ThrowIfNull(trayHoverItems);

        changingTelemetryPreferences = true;
        try
        {
            HashSet<LiveReadingItemKind> enabledDashboard = dashboardItems
                .Where(Enum.IsDefined)
                .ToHashSet();
            foreach (LiveReadingVisibilityOption option in DashboardTelemetryItems)
            {
                option.IsEnabled = enabledDashboard.Contains(option.Kind);
            }

            IReadOnlyList<TrayHoverLabelItemPreference> normalized =
                TelemetryPreferenceDefaults.NormalizeTrayHoverItems(
                    trayHoverItems,
                    TrayHoverLabelMode);
            UnsubscribeTrayHoverOptions(TrayHoverLabelItems);
            TrayHoverLabelItems.CollectionChanged -= OnTrayHoverLabelItemsChanged;
            TrayHoverLabelItems.Clear();
            foreach (TrayHoverLabelItemPreference item in normalized)
            {
                TrayHoverLabelItems.Add(new TrayHoverLabelItemOption(
                    item.Kind,
                    TrayHoverLabelItemLabel(item.Kind),
                    item.IsEnabled));
            }
            SubscribeTrayHoverOptions(TrayHoverLabelItems);
            TrayHoverLabelItems.CollectionChanged += OnTrayHoverLabelItemsChanged;
        }
        finally
        {
            changingTelemetryPreferences = false;
        }
        SyncLegacyTrayHoverLabelMode();
        NotifyDashboardTelemetryPreferencesChanged();
        NotifyTrayHoverPreferencesChanged();
    }

    public string LiveReadingsRefreshToolTip => !IsConnected
        ? "Live readings are unavailable while the OpenBlade service is not running."
        : lastLiveReadingsRefreshAt is { } refreshedAt
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

            if (value is PowerSource.PluggedIn or PowerSource.Battery or PowerSource.UsbC
                && activeProfileDraftSource is { } draftSource
                && draftSource != value)
            {
                ClearActiveProfileDraft();
            }

            SetField(ref activePowerSource, value);
            if (value is PowerSource.PluggedIn or PowerSource.Battery or PowerSource.UsbC)
            {
                SelectedProfileComparisonOption = ComparisonOptions.First(option => option.Source == value);
            }
            NotifySelectedProfilePresentation();
            Notify(nameof(IsPluggedInProfileActive));
            Notify(nameof(IsBatteryProfileActive));
            Notify(nameof(IsUsbCProfileActive));
            Notify(nameof(AvailableActivePerformanceOptions));
            Notify(nameof(SelectedPerformanceOption));
            Notify(nameof(IsCustomPerformanceVisible));
            Notify(nameof(IsGpuClockTuningVisible));
            Notify(nameof(IsGpuClockTuningAvailable));
            Notify(nameof(CanToggleGpuClockTuning));
            Notify(nameof(CanEditGpuClockTuning));
            Notify(nameof(ActiveSavedDisplayProfile));
            Notify(nameof(SaveToActiveProfileLabel));
            NotifyGamingModeKeyPolicy();
            NotifyActiveFanSelectionBinding();
            Notify(nameof(BatteryPowerFlowLabel));
            NotifyTrayHoverLabel();
            NotifyCommands();
        }
    }

    public bool IsPluggedInProfileActive => ActivePowerSource == PowerSource.PluggedIn;

    public bool IsBatteryProfileActive => ActivePowerSource == PowerSource.Battery;

    public bool IsUsbCProfileActive => ActivePowerSource == PowerSource.UsbC;

    public string SaveToActiveProfileLabel => ActivePowerSource switch
    {
        PowerSource.PluggedIn => "Save to AC profile",
        PowerSource.Battery => "Save to battery profile",
        PowerSource.UsbC => "Save to USB-C profile",
        _ => "Save to profile",
    };

    public IReadOnlyList<ProfileComparisonOption> ProfileComparisonOptions => ComparisonOptions;

    public ProfileComparisonOption SelectedProfileComparisonOption
    {
        get => selectedProfileComparisonOption;
        set
        {
            if (value is null || selectedProfileComparisonOption == value)
            {
                return;
            }

            SetField(ref selectedProfileComparisonOption, value);
            NotifySelectedProfilePresentation();
        }
    }

    public string ActivePerformanceLabel => IsConnected
        ? PerformancePresetPresentation.Label(SelectedPerformance)
        : UnavailableLabel;

    public string ActiveFanModeLabel => IsConnected
        ? $"{FanModePresentation.Label(SelectedFanMode)} fans"
        : UnavailableLabel;

    public string ProfileStateLabel => !IsConnected
        ? "Service unavailable"
        : IsCheckingLaptopConnection(latestStatus)
            ? "Checking laptop"
            : latestStatus?.DeviceVerified == false
                ? UnavailableLaptopReason
                : ActivePowerSource == PowerSource.ExternalUnknown
                    ? "Checking power source"
                    : CanControlHardware || IsRetainedPowerSourceRecheck(latestStatus)
                        ? "Ready"
                        : "Controls unavailable";

    public bool IsReadinessReady => IsConnected
        && latestStatus?.DeviceVerified == true
        && !IsCheckingLaptopConnection(latestStatus)
        && (CanControlHardware || IsRetainedPowerSourceRecheck(latestStatus));

    public bool IsReadinessChecking => IsConnected
        && (IsCheckingLaptopConnection(latestStatus)
            || latestStatus?.DeviceVerified == true
            && ActivePowerSource == PowerSource.ExternalUnknown);

    public bool IsReadinessWarning => IsConnected
        && (IsFirmwareAdmissionBlocked
            || latestStatus?.DeviceVerified == true
                && ActivePowerSource != PowerSource.ExternalUnknown
                && !IsCheckingLaptopConnection(latestStatus)
                && !IsRetainedPowerSourceRecheck(latestStatus)
                && !CanControlHardware);

    public bool IsReadinessError => !IsConnected
        || (!IsCheckingLaptopConnection(latestStatus)
            && latestStatus?.DeviceVerified != true
            && !IsFirmwareAdmissionBlocked);

    public string PerformanceSourceLabel
    {
        get => performanceSourceLabel;
        private set
        {
            if (performanceSourceLabel == value)
            {
                return;
            }
            SetField(ref performanceSourceLabel, value);
            Notify(nameof(ThermalProfileRelationshipStatus));
        }
    }

    public string FanSourceLabel
    {
        get => fanSourceLabel;
        private set
        {
            if (fanSourceLabel == value)
            {
                return;
            }
            SetField(ref fanSourceLabel, value);
            Notify(nameof(ThermalProfileRelationshipStatus));
        }
    }

    public string LightingSourceLabel
    {
        get => lightingSourceLabel;
        private set
        {
            if (lightingSourceLabel == value)
            {
                return;
            }
            SetField(ref lightingSourceLabel, value);
            Notify(nameof(LightingProfileRelationshipStatus));
        }
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

    public bool IsReapplyChangedSettingsOperationInProgress
    {
        get => isReapplyChangedSettingsOperationInProgress;
        private set
        {
            if (isReapplyChangedSettingsOperationInProgress == value)
            {
                return;
            }
            SetField(ref isReapplyChangedSettingsOperationInProgress, value);
            Notify(nameof(CanReapplyChangedSettings));
        }
    }

    public bool CanReapplyChangedSettings => HasExternalChanges
        && CanControlHardware
        && HasReapplicableExternalChanges
        && !IsReapplyChangedSettingsOperationInProgress;

    private bool HasReapplicableExternalChanges =>
        latestStatus?.ExternalChanges.Any(subsystem =>
            subsystem != DeviceSubsystem.Lighting
            || latestStatus.KeyboardLightingControlMode
                != KeyboardLightingControlMode.WindowsDynamicLighting) == true;

    public bool AlwaysOverrideExternalLightingChanges
    {
        get => alwaysOverrideExternalLightingChanges;
        private set => SetField(ref alwaysOverrideExternalLightingChanges, value);
    }

    public bool AllowUntestedFirmware
    {
        get => allowUntestedFirmware;
        private set => SetField(ref allowUntestedFirmware, value);
    }

    public KeyboardLightingControlMode KeyboardLightingControlMode
    {
        get => keyboardLightingControlMode;
        private set
        {
            if (keyboardLightingControlMode != value)
            {
                SetField(ref keyboardLightingControlMode, value);
                Notify(nameof(UseWindowsDynamicLighting));
                Notify(nameof(CanControlLighting));
                Notify(nameof(IsLightingControlUnavailable));
                Notify(nameof(IsLightingControlUnavailableNoticeVisible));
                Notify(nameof(LightingControlUnavailableMessage));
                Notify(nameof(ShowOpenBladeKeyboardLightingControls));
                Notify(nameof(CanReapplyChangedSettings));
            }
        }
    }

    public bool UseWindowsDynamicLighting =>
        KeyboardLightingControlMode
            == KeyboardLightingControlMode.WindowsDynamicLighting;

    public bool IsWindowsDynamicLightingAvailable =>
        Capabilities.LampArrayLightingAccess is not null;

    public bool ShowOpenBladeKeyboardLightingControls =>
        !UseWindowsDynamicLighting;

    public async Task<bool> SetKeyboardLightingControlModeAsync(
        KeyboardLightingControlMode mode,
        CancellationToken cancellationToken)
    {
        bool accepted = await RunOperationAsync(
            token => serviceClient.SetKeyboardLightingControlModeAsync(mode, token),
            cancellationToken);
        if (accepted)
        {
            await dispatcher.InvokeAsync(
                () => KeyboardLightingControlMode = mode,
                cancellationToken);
        }
        return accepted;
    }

    public async Task<bool> SetExternalLightingOverridePolicyAsync(
        bool enabled,
        CancellationToken cancellationToken)
    {
        bool accepted = await RunOperationAsync(
            token => serviceClient.SetExternalLightingOverridePolicyAsync(enabled, token),
            cancellationToken);
        if (accepted)
        {
            await dispatcher.InvokeAsync(
                () => AlwaysOverrideExternalLightingChanges = enabled,
                cancellationToken);
        }

        return accepted;
    }

    public async Task<bool> SetFirmwareCompatibilityOverrideAsync(
        bool enabled,
        CancellationToken cancellationToken)
    {
        bool accepted = await RunOperationAsync(
            token => serviceClient.SetFirmwareCompatibilityOverrideAsync(enabled, token),
            cancellationToken);
        if (accepted)
        {
            await dispatcher.InvokeAsync(
                () => AllowUntestedFirmware = enabled,
                cancellationToken);
        }

        return accepted;
    }

    public async Task<bool> ReapplyChangedSettingsAsync(
        CancellationToken cancellationToken)
    {
        if (!CanReapplyChangedSettings)
        {
            return false;
        }

        IsReapplyChangedSettingsOperationInProgress = true;
        try
        {
            return await RunOperationAsync(
                serviceClient.ReapplyChangedSettingsAsync,
                cancellationToken);
        }
        finally
        {
            IsReapplyChangedSettingsOperationInProgress = false;
        }
    }

    public bool HasAttention => IsConnected
        && (HasControllerAdvisory
            || HasControllerDetectionWarning
            || HasExternalChanges);

    public string AttentionSummary
    {
        get
        {
            if (!HasAttention)
            {
                return string.Empty;
            }
            if (HasExternalChanges)
            {
                return "Laptop controls changed outside OpenBlade";
            }
            if (HasControllerDetectionWarning)
            {
                return "Controller detection needs attention";
            }
            return "Another Razer app is running";
        }
    }

    public string AttentionBadgeLabel => !HasAttention
        ? string.Empty
        : HasExternalChanges
            ? "Changed outside OpenBlade"
            : HasControllerDetectionWarning
                ? "Check status"
                : "Razer app active";

    public string AttentionDetails
    {
        get
        {
            if (!HasAttention)
            {
                return string.Empty;
            }
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

    public string AboutUiVersionStatus => uiVersion;

    public string AboutSessionAgentVersionStatus => sessionAgentVersion;

    public string AboutServiceVersionStatus =>
        !IsConnected || string.IsNullOrWhiteSpace(latestStatus?.ServiceVersion)
            ? installedServiceVersion
            : latestStatus.ServiceVersion.Trim();

    public string AboutDetectedLaptopStatus
    {
        get
        {
            if (!IsConnected)
            {
                return "Unavailable";
            }

            string? modelNumber = latestStatus?.Firmware?.ModelNumber;
            if (HasConcreteFirmwareValue(modelNumber))
            {
                RazerBladeModelInfo? model = RazerBladeModelCatalog.Find(modelNumber);
                return model is null
                    ? FirmwareValue(modelNumber)
                    : $"{model.DisplayName}, {model.ModelNumber}";
            }

            return latestStatus?.DeviceVerified == true
                ? "Verified Razer Blade"
                : "Not detected";
        }
    }

    public FirmwareVersions? DetectedFirmware => latestStatus?.Firmware;

    public string TroubleshootingUsbDeviceStatus
    {
        get
        {
            RazerBladeModelInfo? model = RazerBladeModelCatalog.Find(
                latestStatus?.Firmware?.ModelNumber);
            return model is null
                ? UnavailableLabel
                : $"VID {model.UsbVendorId} · PID {model.UsbProductId}";
        }
    }

    public string AboutBiosVersionStatus => FirmwareValue(latestStatus?.Firmware?.BiosVersion);

    public string AboutEcVersionStatus => FirmwareValue(latestStatus?.Firmware?.EcVersion);

    public string AboutMcuVersionStatus =>
        McuFirmwareVersionPresentation.Format(latestStatus?.Firmware?.McuVersion);

    public string AboutVbiosVersionStatus => FirmwareValue(latestStatus?.Firmware?.VbiosVersion);

    public string AboutBatteryHealthStatus => !IsConnected
        ? UnavailableLabel
        : FormatBatteryHealth(latestStatus?.BatteryHealth);

    public bool HasUntestedPlatformVersion =>
        CurrentTestedPlatformAssessment is
        {
            IsEvaluated: true,
            MatchesTestedConfiguration: false,
        };

    public bool HasIncompatibleFirmware => latestStatus?.FirmwareCompatibility is
        FirmwareCompatibilityStatus.UntestedBlocked
        or FirmwareCompatibilityStatus.UntestedAllowed;

    private string UnavailableLaptopReason => IsFirmwareAdmissionBlocked
        ? "Incompatible firmware"
        : "Laptop not found";

    private bool IsFirmwareAdmissionBlocked =>
        latestStatus?.FirmwareCompatibility
            == FirmwareCompatibilityStatus.UntestedBlocked;

    public string PlatformCompatibilityWarningTitle =>
        CurrentTestedPlatformAssessment.Mismatches.Any(mismatch =>
            string.Equals(
                mismatch.Component,
                "Model",
                StringComparison.OrdinalIgnoreCase))
            ? "Incompatible hardware"
            : HasIncompatibleFirmware
                ? "Incompatible firmware"
                : "Untested platform configuration";

    public string DashboardTestedPlatformWarningMessage =>
        FormatTestedPlatformWarning(CurrentTestedPlatformAssessment);

    public string AboutTestedPlatformWarningMessage
    {
        get
        {
            string warning = DashboardTestedPlatformWarningMessage;
            return string.IsNullOrEmpty(warning)
                ? string.Empty
                : HasIncompatibleFirmware && latestStatus?.DeviceVerified != true
                    ? $"{warning} This is a compatibility notice, not a hardware failure. "
                        + (AllowUntestedFirmware
                            ? "The firmware override is enabled, and OpenBlade is checking control access."
                            : "Controls remain blocked unless you explicitly enable the firmware override.")
                    : $"{warning} This is a compatibility notice, not a hardware failure. "
                        + "Controls remain available, but behavior may differ.";
        }
    }

    private TestedPlatformAssessment CurrentTestedPlatformAssessment
    {
        get
        {
            if (!IsConnected)
            {
                return new(false, []);
            }

            string? modelNumber = latestStatus?.Firmware?.ModelNumber;
            if (!HasConcreteFirmwareValue(modelNumber))
            {
                return new(false, []);
            }

            return TestedPlatformCatalog.Assess(
                modelNumber,
                latestStatus?.Firmware);
        }
    }

    private static string ComponentVersionValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "Unavailable" : value.Trim();

    public string TroubleshootingPowerSourceStatus => !IsConnected
        ? "Unavailable"
        : ActivePowerSource switch
        {
            PowerSource.PluggedIn => "AC",
            PowerSource.UsbC => "USB-C",
            PowerSource.Battery => "Battery",
            PowerSource.ExternalUnknown => "Checking power source",
            _ => "Unavailable",
        };

    public string TroubleshootingControlsStatus => IsConnected
        ? ControlAccessStatus
        : "Unavailable";

    public string TroubleshootingKeyboardDriverModeStatus => !IsConnected
        ? "Unavailable"
        : latestStatus?.KeyboardDriverModeActive switch
        {
            true => "Loaded",
            false => "Not loaded",
            _ => "Unknown",
        };

    public string TroubleshootingOtherRazerSoftwareStatus
    {
        get
        {
            string running = string.Join(", ", latestStatus?.PotentialControllers ?? []);
            if (!HasControllerDetectionWarning)
            {
                return string.IsNullOrWhiteSpace(running) ? "None" : running;
            }

            string warning = FormatTroubleshootingValue(ControllerDetectionWarningText);
            return string.IsNullOrWhiteSpace(running)
                ? $"Could not check: {warning}"
                : $"{running}; check incomplete: {warning}";
        }
    }

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

    public IReadOnlyList<FanModeOption> AvailableActiveFanModeOptions =>
        ActivePowerSource switch
        {
            PowerSource.PluggedIn => availableFanModeOptions,
            PowerSource.Battery or PowerSource.UsbC => availableUsbCFanModeOptions,
            PowerSource.ExternalUnknown => [],
            _ => [],
        };

    public FanModeOption? SelectedFanModeOption =>
        AvailableActiveFanModeOptions.FirstOrDefault(
            option => option.Value == SelectedFanMode);

    public IReadOnlyList<LightingEffect> AvailableLightingEffects
    {
        get => availableLightingEffects;
        private set => SetField(ref availableLightingEffects, value);
    }

    public IReadOnlyList<LightingEffectOption> AvailableLightingEffectOptions
    {
        get => availableLightingEffectOptions;
        private set => SetField(ref availableLightingEffectOptions, value);
    }

    public IReadOnlyList<LightingSettings> AvailableLightingSettings
    {
        get => availableLightingSettings;
        private set => SetField(ref availableLightingSettings, value);
    }

    public IReadOnlyList<LogoLightingModeOption> AvailableLogoLightingModeOptions
    {
        get => availableLogoLightingModeOptions;
        private set => SetField(ref availableLogoLightingModeOptions, value);
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

    public bool IsLightingVisible => true;

    public bool HasLightingCapability => AvailableLightingEffects.Count > 0;

    public bool CanControlLighting =>
        CanRequestHardwareControl
        && HasLightingCapability
        && !UseWindowsDynamicLighting;

    public bool IsLightingControlUnavailable =>
        !CanControlLighting && !IsCheckingLaptopConnection(latestStatus);

    public bool IsLightingControlUnavailableNoticeVisible =>
        IsLightingControlUnavailable && !UseWindowsDynamicLighting;

    public string LightingControlUnavailableMessage
    {
        get
        {
            if (UseWindowsDynamicLighting)
            {
                return "Windows Dynamic Lighting owns the keyboard. OpenBlade will not change keyboard brightness, effects, or lighting power policies.";
            }

            if (!IsConnected)
            {
                return "OpenBlade can’t connect to its service, so lighting controls are unavailable.";
            }

            if (latestStatus?.DeviceVerified != true)
            {
                return IsFirmwareAdmissionBlocked
                    ? "OpenBlade found a supported laptop with incompatible firmware, so lighting controls are unavailable unless you enable the firmware override."
                    : "OpenBlade can’t find a supported laptop, so lighting controls are unavailable.";
            }

            if (!HasLightingCapability && latestStatus.PotentialControllers.Count > 0)
            {
                return "Another Razer app may be using the laptop’s lighting interface. OpenBlade will enable these controls when it becomes available.";
            }

            if (!HasLightingCapability && HasControllerDetectionWarning)
            {
                return "OpenBlade couldn’t confirm whether another Razer app is using the laptop’s lighting interface. Controls will remain disabled until access is available.";
            }

            if (!HasLightingCapability)
            {
                return "OpenBlade couldn’t access the laptop’s lighting interface. Another lighting app may be using it.";
            }

            if (ActivePowerSource == PowerSource.ExternalUnknown)
            {
                return "OpenBlade is checking the laptop before enabling lighting controls.";
            }

            return "Laptop controls are currently unavailable. Check Troubleshooting for details.";
        }
    }

    public bool IsProfileControlVisible =>
        IsPerformanceVisible || IsFanControlVisible || HasLightingCapability;

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
            Notify(nameof(ActivePerformanceLabel));
            Notify(nameof(SelectedPerformanceOption));
            Notify(nameof(IsSilentSelected));
            Notify(nameof(IsBalancedSelected));
            Notify(nameof(IsPerformanceSelected));
            Notify(nameof(IsCustomPerformanceVisible));
            Notify(nameof(IsGpuClockTuningVisible));
            Notify(nameof(IsGpuClockTuningAvailable));
            Notify(nameof(CanToggleGpuClockTuning));
            Notify(nameof(CanEditGpuClockTuning));
            Notify(nameof(CanEditFanControl));
            NotifyTrayHoverLabel();
        }
    }

    public bool IsSilentSelected => SelectedPerformance == PerformancePreset.Silent;

    public bool IsBalancedSelected => SelectedPerformance == PerformancePreset.Balanced;

    public bool IsPerformanceSelected => SelectedPerformance == PerformancePreset.Performance;

    public bool IsCustomPerformanceVisible =>
        ActivePowerSource == PowerSource.PluggedIn
        && SelectedPerformance == PerformancePreset.Custom
        && AvailablePerformancePresets.Contains(PerformancePreset.Custom);

    public bool IsGpuClockTuningVisible =>
        ActivePowerSource == PowerSource.PluggedIn
        && (AvailablePerformancePresets.Contains(PerformancePreset.Custom)
            || Capabilities.GpuOverclock is { Access: DeviceFeatureAccess.ReadWrite });

    public bool IsGpuClockTuningAvailable =>
        IsGpuClockTuningVisible
        && GpuClockOffsetCapabilities?.IsWellFormed == true;

    public bool IsGpuClockTuningEnabled
    {
        get => gpuClockTuningEnabled;
        private set
        {
            if (gpuClockTuningEnabled == value)
            {
                return;
            }

            SetField(ref gpuClockTuningEnabled, value);
            Notify(nameof(CanEditGpuClockTuning));
        }
    }

    public bool IsSpecialKeyBindingsEditable =>
        Capabilities.SpecialKeyBindingsAccess == DeviceFeatureAccess.ReadWrite
        && Capabilities.WritableSpecialKeySlots != SpecialKeySlotSet.None;

    public bool IsSpecialKeyBindingSlotEditable(SpecialKeySlot slot)
    {
        if (Capabilities.SpecialKeyBindingsAccess != DeviceFeatureAccess.ReadWrite)
        {
            return false;
        }

        SpecialKeySlotSet required = slot switch
        {
            SpecialKeySlot.M1 => SpecialKeySlotSet.M1,
            SpecialKeySlot.M2 => SpecialKeySlotSet.M2,
            SpecialKeySlot.M3 => SpecialKeySlotSet.M3,
            SpecialKeySlot.M4 => SpecialKeySlotSet.M4,
            SpecialKeySlot.M5 => SpecialKeySlotSet.M5,
            _ => SpecialKeySlotSet.None,
        };
        return required != SpecialKeySlotSet.None
            && (Capabilities.WritableSpecialKeySlots & required) != 0;
    }

    public GpuClockOffsetCapabilities? GpuClockOffsetCapabilities
    {
        get => gpuClockOffsetCapabilities;
        private set
        {
            if (gpuClockOffsetCapabilities == value)
            {
                return;
            }

            SetField(ref gpuClockOffsetCapabilities, value);
            Notify(nameof(IsGpuClockTuningAvailable));
            Notify(nameof(CanEditGpuClockTuning));
            Notify(nameof(GpuCoreClockOffsetMinimum));
            Notify(nameof(GpuCoreClockOffsetMaximum));
            Notify(nameof(GpuMemoryClockOffsetMinimum));
            Notify(nameof(GpuMemoryClockOffsetMaximum));
        }
    }

    public int GpuCoreClockOffsetMegahertz
    {
        get => gpuCoreClockOffsetMegahertz;
        private set => SetField(ref gpuCoreClockOffsetMegahertz, value);
    }

    public int GpuMemoryClockOffsetMegahertz
    {
        get => gpuMemoryClockOffsetMegahertz;
        private set => SetField(ref gpuMemoryClockOffsetMegahertz, value);
    }

    public int GpuCoreClockOffsetMinimum =>
        GpuClockOffsetCapabilities?.Core.MinimumMegahertz ?? 0;

    public int GpuCoreClockOffsetMaximum =>
        GpuClockOffsetCapabilities?.Core.MaximumMegahertz ?? 0;

    public int GpuMemoryClockOffsetMinimum =>
        GpuClockOffsetCapabilities?.Memory.MinimumMegahertz ?? 0;

    public int GpuMemoryClockOffsetMaximum =>
        GpuClockOffsetCapabilities?.Memory.MaximumMegahertz ?? 0;

    public GpuClockOffsetSettings CurrentGpuClockOffsets => new(
        GpuCoreClockOffsetMegahertz,
        GpuMemoryClockOffsetMegahertz);

    public CustomPerformanceLevel SelectedCustomCpuLevel
    {
        get => selectedCustomCpuLevel;
        private set
        {
            if (selectedCustomCpuLevel == value)
            {
                return;
            }

            SetField(ref selectedCustomCpuLevel, value);
            RefreshCustomPerformanceOptions();
        }
    }

    public CustomPerformanceLevel SelectedCustomGpuLevel
    {
        get => selectedCustomGpuLevel;
        private set
        {
            if (selectedCustomGpuLevel == value)
            {
                return;
            }

            SetField(ref selectedCustomGpuLevel, value);
            RefreshCustomPerformanceOptions();
        }
    }

    public IReadOnlyList<CustomPerformanceLevel> AvailableCustomCpuLevels =>
        availableCustomCpuLevels;

    public IReadOnlyList<CustomPerformanceLevel> AvailableCustomGpuLevels =>
        availableCustomGpuLevels;

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
            Notify(nameof(ActiveFanModeLabel));
            Notify(nameof(SelectedFanModeOption));
            Notify(nameof(IsFixedFanSpeedVisible));
            Notify(nameof(IsManualFanCurveVisible));
        }
    }

    public int FixedFanSpeedRpm
    {
        get => fixedFanSpeedRpm;
        set
        {
            int normalized = ActiveFanSpeedRange.Quantize(value);
            if (fixedFanSpeedRpm == normalized)
            {
                return;
            }

            SetField(ref fixedFanSpeedRpm, normalized);
            Notify(nameof(CurrentFanControlSettings));
        }
    }

    public int FixedFanSpeedMinimumRpm => ActiveFanSpeedRange.MinimumRpm;

    public int FixedFanSpeedMaximumRpm => ActiveFanSpeedRange.MaximumRpm;

    public int FixedFanSpeedStepRpm => ActiveFanSpeedRange.StepRpm;

    private FanSpeedRange ActiveFanSpeedRange =>
        Capabilities.FanSpeedRange is { IsWellFormed: true } range
            ? range
            : FanSpeedRange.Standard;

    public bool UseHigherFanTemperature
    {
        get => useHigherFanTemperature;
        set
        {
            if (useHigherFanTemperature == value)
            {
                return;
            }

            SetField(ref useHigherFanTemperature, value);
            Notify(nameof(CurrentFanControlSettings));
        }
    }

    public IReadOnlyList<FanCurveEditorPoint> CpuFanCurveEditorPoints =>
        cpuFanCurveEditorPoints;

    public IReadOnlyList<FanCurveEditorPoint> GpuFanCurveEditorPoints =>
        gpuFanCurveEditorPoints;

    public bool IsFixedFanSpeedVisible => SelectedFanMode == FanMode.Fixed;

    public bool IsManualFanCurveVisible => SelectedFanMode == FanMode.ManualCurve;

    public bool IsFanControlOperationInProgress
    {
        get => isFanControlOperationInProgress;
        private set
        {
            if (isFanControlOperationInProgress == value)
            {
                return;
            }

            SetField(ref isFanControlOperationInProgress, value);
            NotifyCommands();
            Notify(nameof(CanEditFanControl));
        }
    }

    public bool CanEditFanControl =>
        CanRequestHardwareControl
        && !IsFanControlOperationInProgress;

    public bool CanEditPerformance =>
        CanRequestHardwareControl && !IsPerformanceOperationInProgress;

    public bool CanEditGpuClockTuning =>
        CanToggleGpuClockTuning
        && IsGpuClockTuningEnabled
        && IsGpuClockTuningAvailable;

    public bool CanToggleGpuClockTuning =>
        CanEditPerformance
        && IsGpuClockTuningVisible
        && Capabilities.GpuOverclock is { Access: DeviceFeatureAccess.ReadWrite };

    public bool IsPerformanceOperationInProgress
    {
        get => isPerformanceOperationInProgress;
        private set
        {
            if (isPerformanceOperationInProgress != value)
            {
                SetField(ref isPerformanceOperationInProgress, value);
                NotifyCommands();
                Notify(nameof(CanEditPerformance));
                Notify(nameof(CanToggleGpuClockTuning));
                Notify(nameof(CanEditGpuClockTuning));
            }
        }
    }

    public bool IsLightingOperationInProgress
    {
        get => isLightingOperationInProgress;
        private set
        {
            if (isLightingOperationInProgress != value)
            {
                SetField(ref isLightingOperationInProgress, value);
                NotifyCommands();
            }
        }
    }

    public bool IsChargeLimitOperationInProgress
    {
        get => isChargeLimitOperationInProgress;
        private set
        {
            if (isChargeLimitOperationInProgress != value)
            {
                SetField(ref isChargeLimitOperationInProgress, value);
                NotifyCommands();
            }
        }
    }

    public bool IsBatteryProtectionOperationInProgress
    {
        get => isBatteryProtectionOperationInProgress;
        private set
        {
            if (isBatteryProtectionOperationInProgress != value)
            {
                SetField(ref isBatteryProtectionOperationInProgress, value);
                NotifyCommands();
            }
        }
    }

    public bool IsFullChargeOperationInProgress
    {
        get => isFullChargeOperationInProgress;
        private set
        {
            if (isFullChargeOperationInProgress != value)
            {
                SetField(ref isFullChargeOperationInProgress, value);
                NotifyCommands();
            }
        }
    }

    public bool IsStartupAnimationFieldLoading =>
        IsOneShotFirmwareFieldLoading(
            OneShotLaptopControl.StartupAnimation,
            isStartupAnimationOperationInProgress,
            startupAnimationReadPending);

    public bool IsFunctionKeyFieldLoading =>
        IsOneShotFirmwareFieldLoading(
            OneShotLaptopControl.FunctionKeyPrimary,
            isFunctionKeyOperationInProgress,
            functionKeyReadPending);

    public bool IsGamingModeFieldLoading =>
        IsOneShotFirmwareFieldLoading(
            OneShotLaptopControl.GamingMode,
            isGamingModeOperationInProgress,
            gamingModeReadPending);

    public bool CanEditStartupAnimation =>
        CanMutate && SupportsStartupAnimation && !IsStartupAnimationFieldLoading;

    public bool CanEditFunctionKeyPrimary =>
        CanMutate && SupportsFunctionKeyPrimary && !IsFunctionKeyFieldLoading;

    public bool CanEditGamingMode =>
        CanMutate && SupportsGamingMode && !IsGamingModeFieldLoading;

    public FanControlSettings CurrentFanControlSettings => new(
        SelectedFanMode,
        FixedFanSpeedRpm,
        new ManualFanCurve(
            CpuFanCurveEditorPoints.Select(point => point.ToValue()).ToArray(),
            GpuFanCurveEditorPoints.Select(point => point.ToValue()).ToArray(),
            UseHigherFanTemperature));

    public void UpdateFanCurvePoint(FanCurveEditorPoint point, int speedRpm)
    {
        ArgumentNullException.ThrowIfNull(point);
        IReadOnlyList<FanCurveEditorPoint> source = CpuFanCurveEditorPoints.Contains(point)
            ? CpuFanCurveEditorPoints
            : GpuFanCurveEditorPoints.Contains(point)
                ? GpuFanCurveEditorPoints
                : throw new ArgumentException(
                    "The fan-curve point does not belong to the active editor.",
                    nameof(point));
        int index = source.ToList().IndexOf(point);
        FanSpeedRange range = ActiveFanSpeedRange;
        int minimum = index == 0
            ? range.MinimumRpm
            : source[index - 1].SpeedRpm;
        int maximum = index == source.Count - 1
            ? range.MaximumRpm
            : source[index + 1].SpeedRpm;
        point.SpeedRpm = Math.Clamp(
            range.Quantize(speedRpm),
            minimum,
            maximum);
        if (ReferenceEquals(source, CpuFanCurveEditorPoints))
        {
            cpuFanCurveEditorPoints = source
                .Select(value => new FanCurveEditorPoint(value.ToValue()))
                .ToArray();
            Notify(nameof(CpuFanCurveEditorPoints));
        }
        else
        {
            gpuFanCurveEditorPoints = source
                .Select(value => new FanCurveEditorPoint(value.ToValue()))
                .ToArray();
            Notify(nameof(GpuFanCurveEditorPoints));
        }
        Notify(nameof(CurrentFanControlSettings));
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
            Notify(nameof(BatteryProtectionActionLabel));
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
            Notify(nameof(EnableFullChargeOverride));
            Notify(nameof(FullChargeActionLabel));
            Notify(nameof(ProfileStateLabel));
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
            RefreshCustomPerformanceOptions();
            int normalizedFanSpeed = ActiveFanSpeedRange.Quantize(fixedFanSpeedRpm);
            SetField(
                ref fixedFanSpeedRpm,
                normalizedFanSpeed,
                nameof(FixedFanSpeedRpm));
            Notify(nameof(FixedFanSpeedMinimumRpm));
            Notify(nameof(FixedFanSpeedMaximumRpm));
            Notify(nameof(FixedFanSpeedStepRpm));
            Notify(nameof(CurrentFanControlSettings));
            Notify(nameof(FullChargeActionLabel));
            Notify(nameof(HasOneShotLaptopControls));
            Notify(nameof(HasKeyboardLaptopControls));
            Notify(nameof(SupportsStartupAnimation));
            Notify(nameof(SupportsFunctionKeyPrimary));
            Notify(nameof(SupportsGamingMode));
            Notify(nameof(IsGpuClockTuningVisible));
            Notify(nameof(IsGpuClockTuningAvailable));
            Notify(nameof(CanToggleGpuClockTuning));
            Notify(nameof(CanEditGpuClockTuning));
            Notify(nameof(IsSpecialKeyBindingsEditable));
            Notify(nameof(IsWindowsDynamicLightingAvailable));
            Notify(nameof(CanControlLighting));
            NotifyGamingModeKeyPolicy();
            NotifyFirmwareFieldLoadingState();
        }
    }

    public bool HasOneShotLaptopControls => Capabilities.OneShotLaptopControls.Count > 0;

    public bool HasKeyboardLaptopControls =>
        SupportsFunctionKeyPrimary || SupportsGamingMode;

    public bool SupportsStartupAnimation => Capabilities.OneShotLaptopControls.Contains(
        OneShotLaptopControl.StartupAnimation);

    public bool? StartupAnimationEnabled
    {
        get => startupAnimationEnabled;
        private set
        {
            if (startupAnimationEnabled == value)
            {
                return;
            }

            startupAnimationEnabled = value;
            Notify(nameof(StartupAnimationEnabled));
            Notify(nameof(StartupAnimationToggleIsOn));
            Notify(nameof(IsStartupAnimationFieldLoading));
        }
    }

    public bool StartupAnimationToggleIsOn => StartupAnimationEnabled is true;

    public bool SupportsFunctionKeyPrimary => Capabilities.OneShotLaptopControls.Contains(
        OneShotLaptopControl.FunctionKeyPrimary);

    public FunctionKeyPrimaryMode? FunctionKeyPrimary
    {
        get => functionKeyPrimary;
        private set
        {
            if (functionKeyPrimary == value)
            {
                return;
            }

            functionKeyPrimary = value;
            Notify(nameof(FunctionKeyPrimary));
            Notify(nameof(FunctionKeyPrimaryStateLabel));
            Notify(nameof(IsFunctionKeyFieldLoading));
        }
    }

    public string FunctionKeyPrimaryStateLabel => FunctionKeyPrimary switch
    {
        FunctionKeyPrimaryMode.FunctionKeys => "Function keys",
        FunctionKeyPrimaryMode.MultimediaKeys => "Media keys",
        null => "Checking",
        _ => "Checking",
    };

    public bool SupportsGamingMode => Capabilities.OneShotLaptopControls.Contains(
        OneShotLaptopControl.GamingMode);

    public bool? GamingModeEnabled
    {
        get => gamingModeEnabled;
        private set
        {
            if (gamingModeEnabled == value)
            {
                return;
            }

            gamingModeEnabled = value;
            Notify(nameof(GamingModeEnabled));
            Notify(nameof(GamingModeToggleIsOn));
            Notify(nameof(IsGamingModeFieldLoading));
        }
    }

    public bool GamingModeToggleIsOn => GamingModeEnabled is true;

    public bool BlockWindowsKeyInGamingMode =>
        CurrentGamingModeKeyPolicy.BlockWindowsKey;

    public bool BlockAltTabInGamingMode =>
        CurrentGamingModeKeyPolicy.BlockAltTab;

    public bool CanEditGamingModeKeyPolicy =>
        CanMutate && SupportsGamingMode && HasConfirmedPowerSource;

    public async Task<bool> SetGamingModeKeyPolicyAsync(
        bool blockWindowsKey,
        bool blockAltTab,
        CancellationToken cancellationToken)
    {
        if (!CanEditGamingModeKeyPolicy)
        {
            return false;
        }

        PowerSource source = ActivePowerSource;
        var policy = new GamingModeKeyPolicy(blockWindowsKey, blockAltTab);
        PowerProfile savedProfile = GetProfile(source);
        PowerProfile updatedProfile = savedProfile with
        {
            LaptopBehavior = savedProfile.EffectiveLaptopBehavior with
            {
                GamingModeKeyPolicy = policy,
            },
        };
        ProfileSet updatedProfiles = source switch
        {
            PowerSource.PluggedIn => Profiles with { PluggedIn = updatedProfile },
            PowerSource.Battery => Profiles with { Battery = updatedProfile },
            PowerSource.UsbC => Profiles with { UsbC = updatedProfile },
            _ => throw new ArgumentOutOfRangeException(nameof(source)),
        };
        bool accepted = await RunOperationAsync(
            token => serviceClient.UpdateProfilesAsync(updatedProfiles, token),
            cancellationToken);
        if (!accepted)
        {
            return false;
        }

        await dispatcher.InvokeAsync(
            () =>
            {
                LaptopBehaviorProfileSettings? rebasedDraft =
                    activeProfileDraftSource == source
                        ? activeProfileDraftLaptopBehavior
                        : null;
                Profiles = updatedProfiles;
                if (rebasedDraft is not null)
                {
                    rebasedDraft = rebasedDraft with
                    {
                        GamingModeKeyPolicy = policy,
                    };
                    if (activeProfileOriginalLaptopBehavior is not null)
                    {
                        activeProfileOriginalLaptopBehavior =
                            activeProfileOriginalLaptopBehavior with
                            {
                                GamingModeKeyPolicy = policy,
                            };
                    }
                    activeProfileDraftLaptopBehavior = rebasedDraft;
                    MarkActiveProfileDraft(
                        source,
                        laptopBehavior: rebasedDraft);
                }
            },
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    private GamingModeKeyPolicy CurrentGamingModeKeyPolicy =>
        ActivePowerSource is PowerSource.PluggedIn or PowerSource.Battery or PowerSource.UsbC
            ? (activeProfileDraftLaptopBehavior
                    ?? GetProfile(ActivePowerSource).EffectiveLaptopBehavior)
                .EffectiveGamingModeKeyPolicy
            : GamingModeKeyPolicy.Disabled;

    public LiveReadings? LiveReadings
    {
        get => liveReadings;
        private set => SetLiveReadings(value, publishPresentation: true);
    }

    private void SetLiveReadings(
        LiveReadings? value,
        bool publishPresentation)
    {
        // Hidden tray refreshes may update this backing value without notifying XAML.
        // MainWindow.xaml binds only to derived telemetry properties, which are replayed
        // when SetDashboardTelemetryPresentationActive reactivates the dashboard.
        if (EqualityComparer<LiveReadings?>.Default.Equals(liveReadings, value))
        {
            return;
        }

        liveReadings = value;
        if (publishPresentation)
        {
            Notify(nameof(LiveReadings));
            NotifyDashboardTelemetryValuesChanged();
        }
        NotifyTrayHoverLabel();
    }

    public void SetDashboardTelemetryPresentationActive(bool active)
    {
        if (dashboardTelemetryPresentationActive == active)
        {
            return;
        }

        dashboardTelemetryPresentationActive = active;
        if (active)
        {
            NotifyDashboardTelemetryValuesChanged();
            Notify(nameof(ShowDashboardLiveReadings));
            Notify(nameof(LiveReadingsRefreshToolTip));
        }
    }

    public PerformanceOverride? PerformanceOverride
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
                "Power mode",
                PerformanceOverride,
                PerformancePresetPresentation.Label);
            AddOverrideStatus(active, "Fans", FanOverride);
            AddOverrideStatus(
                active,
                "Lighting",
                LightingOverride,
                FormatLightingProfile);
            return active.Count == 0
                ? "No manual overrides active."
                : string.Join(" | ", active);
        }
    }

    public bool HasActiveChanges => PerformanceOverride is not null
        || FanOverride is not null
        || (HasLightingCapability && LightingOverride is not null);

    public string ActiveChangesText
    {
        get
        {
            List<string> active = [];
            AddOverrideStatus(
                active,
                "Power mode",
                PerformanceOverride,
                PerformancePresetPresentation.Label);
            AddOverrideStatus(active, "Fans", FanOverride);
            if (HasLightingCapability)
            {
                AddOverrideStatus(
                    active,
                    "Keyboard lighting",
                    LightingOverride,
                    FormatLightingProfile);
            }
            return string.Join(" · ", active);
        }
    }

    public ProfileSet Profiles
    {
        get => profiles;
        private set
        {
            if (profiles == value)
            {
                return;
            }

            SetField(ref profiles, value);
            NotifyProfileSummaries();
            NotifyGamingModeKeyPolicy();
        }
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
            Notify(nameof(PluggedInPerformanceOption));
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
            Notify(nameof(BatteryPerformanceOption));
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
            Notify(nameof(UsbCPerformanceOption));
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

    public bool HasUnsavedProfileChanges => profileEditorsDirty || HasActiveProfileDraft;

    public bool HasProfileEditorChanges => profileEditorsDirty;

    public bool HasActiveProfileDraft => activeProfileDraftSource is not null;

    public bool HasThermalProfileDraft =>
        activeProfileDraftPerformance is not null
        || activeProfileDraftCustomPerformance is not null
        || activeProfileDraftGpuClockTuning is not null
        || activeProfileDraftFans is not null
        || activeProfileDraftFanControl is not null;

    public bool HasLightingProfileDraft => activeProfileDraftLighting is not null
        || activeProfileDraftLightingPowerPolicy is not null;

    public bool HasLightingProfileSaveOpportunity =>
        HasLightingProfileDraft || HasSaveableUnsetLightingProfileField;

    public bool HasLightingProfileDifference =>
        HasConfirmedPowerSource
        && (HasLightingHardwareProfileDifference
            || GetProfile(ActivePowerSource).EffectiveLightingPowerPolicy
                != (activeProfileDraftLightingPowerPolicy ?? LightingPowerPolicyDraft));

    public bool HasLightingProfileActionOpportunity =>
        HasLightingProfileSaveOpportunity || HasLightingProfileDifference;

    public bool HasDisplayProfileDraft => activeProfileDraftDisplay is not null;

    public bool HasLaptopProfileDraft => activeProfileDraftLaptopBehavior is not null;

    public bool HasLaptopPageProfileDraft => HasThermalProfileDraft || HasLaptopProfileDraft;

    public bool HasLaptopPageProfileSaveOpportunity =>
        HasLaptopPageProfileDraft || HasSaveableUnsetLaptopProfileField;

    public bool HasThermalProfileDifference =>
        HasConfirmedPowerSource && HasThermalProfileDifferenceCore;

    public bool HasLaptopBehaviorProfileDifference =>
        HasConfirmedPowerSource && HasLaptopBehaviorProfileDifferenceCore;

    public bool HasLaptopPageProfileDifference =>
        HasThermalProfileDifference || HasLaptopBehaviorProfileDifference;

    public bool HasLaptopPageProfileActionOpportunity =>
        HasLaptopPageProfileSaveOpportunity || HasLaptopPageProfileDifference;

    public string ThermalProfileRelationshipStatus => ProfileRelationshipStatus(
        HasThermalProfileDraft,
        PerformanceSourceLabel == "Changed outside OpenBlade"
            || FanSourceLabel == "Changed outside OpenBlade");

    public string LightingProfileRelationshipStatus => ProfileRelationshipStatus(
        HasLightingProfileDraft,
        LightingSourceLabel == "Changed outside OpenBlade");

    public bool IsPluggedInProfileDraft =>
        activeProfileDraftSource == PowerSource.PluggedIn;

    public bool IsBatteryProfileDraft =>
        activeProfileDraftSource == PowerSource.Battery;

    public bool IsUsbCProfileDraft =>
        activeProfileDraftSource == PowerSource.UsbC;

    public string PluggedInProfileThermalSummary =>
        FormatThermalProfile(GetPresentedProfile(PowerSource.PluggedIn));

    public string PluggedInProfileLightingSummary =>
        FormatLightingProfile(GetPresentedProfile(PowerSource.PluggedIn).Lighting);

    public string PluggedInProfileDisplaySummary =>
        FormatDisplayProfile(GetPresentedProfile(PowerSource.PluggedIn).Display);

    public string BatteryProfileThermalSummary =>
        FormatThermalProfile(GetPresentedProfile(PowerSource.Battery));

    public string BatteryProfileLightingSummary =>
        FormatLightingProfile(GetPresentedProfile(PowerSource.Battery).Lighting);

    public string BatteryProfileDisplaySummary =>
        FormatDisplayProfile(GetPresentedProfile(PowerSource.Battery).Display);

    public string UsbCProfileThermalSummary =>
        FormatThermalProfile(GetPresentedProfile(PowerSource.UsbC));

    public string UsbCProfileLightingSummary =>
        FormatLightingProfile(GetPresentedProfile(PowerSource.UsbC).Lighting);

    public string UsbCProfileDisplaySummary =>
        FormatDisplayProfile(GetPresentedProfile(PowerSource.UsbC).Display);

    public ProfilePresentation PluggedInProfilePresentation =>
        CreateProfilePresentation(PowerSource.PluggedIn);

    public ProfilePresentation UsbCProfilePresentation =>
        CreateProfilePresentation(PowerSource.UsbC);

    public ProfilePresentation BatteryProfilePresentation =>
        CreateProfilePresentation(PowerSource.Battery);

    public ProfilePresentation SelectedProfilePresentation =>
        CreateProfilePresentation(SelectedProfileComparisonOption.Source);

    public string LightingDisplayOffPolicyPresentation =>
        GetProfile(SelectedProfileComparisonOption.Source)
            .EffectiveLightingPowerPolicy.TurnOffWhenDisplayTurnsOff
            ? "Turn lighting off"
            : "Keep lighting on";

    public string LightingIdlePolicyPresentation =>
        GetProfile(SelectedProfileComparisonOption.Source)
            .EffectiveLightingPowerPolicy.IdleTimeoutMinutes is int minutes
            ? $"{minutes.ToString(CultureInfo.CurrentCulture)} minutes"
            : "Never";

    public bool IsSelectedProfileActive =>
        SelectedProfileComparisonOption.Source == ActivePowerSource;

    public bool IsSelectedProfileDraft => SelectedProfileComparisonOption.Source switch
    {
        PowerSource.PluggedIn => IsPluggedInProfileDraft,
        PowerSource.UsbC => IsUsbCProfileDraft,
        PowerSource.Battery => IsBatteryProfileDraft,
        _ => false,
    };

    public DisplayProfileSettings ActiveSavedDisplayProfile =>
        ActivePowerSource is PowerSource.PluggedIn or PowerSource.Battery or PowerSource.UsbC
            ? GetProfile(ActivePowerSource).Display ?? DisplayProfileSettings.Unconfigured()
            : DisplayProfileSettings.Unconfigured();

    public string ActiveProfileDraftName => activeProfileDraftSource switch
    {
        PowerSource.PluggedIn => "AC",
        PowerSource.Battery => "On battery",
        PowerSource.UsbC => "USB-C",
        _ => "Current profile",
    };

    public string SavedActiveProfileSummary => activeProfileDraftSource is { } source
        ? FormatPowerProfile(GetProfile(source))
        : string.Empty;

    public string PendingActiveProfileSummary => GetActiveProfileDraft() is { } draft
        ? FormatPowerProfile(draft)
        : string.Empty;

    public void RecordDisplayProfileChange(
        PowerSource powerSource,
        DisplayProfileSettings settings,
        DisplayProfileSettings? previousSettings = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        MarkActiveProfileDraft(
            powerSource,
            display: settings,
            previousDisplay: previousSettings);
    }

    public async Task UseActiveDisplayProfileSettingsAsync(
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (ActivePowerSource is not (
                PowerSource.PluggedIn or PowerSource.Battery or PowerSource.UsbC))
        {
            return;
        }

        PowerSource source = ActivePowerSource;
        DisplayProfileSettings saved =
            GetProfile(source).Display ?? DisplayProfileSettings.Unconfigured();
        if (!saved.IsConfigured)
        {
            return;
        }

        bool restored = await RestoreSavedDisplayProfileAsync(source, saved, token);
        if (restored && ActivePowerSource == source)
        {
            ClearActiveProfileDraft(ProfileDraftGroup.Display);
        }
        else if (ActivePowerSource != source)
        {
            await ReportProfileRestoreSourceChangedAsync(token);
        }
    }

    public LightingEffect LightingEffectEditor
    {
        get => lightingEffectEditor;
        set
        {
            LightingEffect previous = lightingEffectEditor;
            if (!Enum.IsDefined(value))
            {
                return;
            }

            SetKeyboardEffectUnconfirmed(false);
            if (!SetLightingEditorField(ref lightingEffectEditor, value))
            {
                return;
            }

            SeedVisibleLightingDefaults(previous, value);

            NotifyLightingParameterPresentation();
            Notify(nameof(SelectedLightingEffectOption));
        }
    }

    public LightingEffectOption? SelectedLightingEffectOption
    {
        get => keyboardEffectUnconfirmed
            ? null
            : AvailableLightingEffectOptions.FirstOrDefault(
                option => option.Effect == LightingEffectEditor);
        set
        {
            if (value is not null)
            {
                bool wasUnconfirmed = keyboardEffectUnconfirmed;
                SetKeyboardEffectUnconfirmed(false);
                if (wasUnconfirmed && lightingEffectEditor == value.Effect)
                {
                    SeedVisibleLightingDefaults(lightingEffectEditor, value.Effect);
                    SetLightingEditorDirty(true);
                    Notify(nameof(LightingDraft));
                    NotifyLightingParameterPresentation();
                    NotifyCommands();
                    return;
                }

                LightingEffectEditor = value.Effect;
            }
        }
    }

    public bool IsKeyboardEffectUnconfirmed => keyboardEffectUnconfirmed;

    public string KeyboardEffectDescription => keyboardEffectUnconfirmed
        ? "The active effect was set outside OpenBlade and cannot be read from firmware."
        : "Choose a supported keyboard lighting effect.";

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
        set
        {
            if (SetLightingColorField(ref lightingRedEditor, value))
            {
                Notify(nameof(LightingPrimaryColorEditor));
            }
        }
    }

    public int LightingGreenEditor
    {
        get => lightingGreenEditor;
        set
        {
            if (SetLightingColorField(ref lightingGreenEditor, value))
            {
                Notify(nameof(LightingPrimaryColorEditor));
            }
        }
    }

    public int LightingBlueEditor
    {
        get => lightingBlueEditor;
        set
        {
            if (SetLightingColorField(ref lightingBlueEditor, value))
            {
                Notify(nameof(LightingPrimaryColorEditor));
            }
        }
    }

    public RgbColor LightingPrimaryColorEditor
    {
        get => new(
            (byte)LightingRedEditor,
            (byte)LightingGreenEditor,
            (byte)LightingBlueEditor);
        set => SetLightingColorEditor(value, secondary: false);
    }

    public int LightingSecondaryRedEditor
    {
        get => lightingSecondaryRedEditor;
        set
        {
            if (SetLightingColorField(ref lightingSecondaryRedEditor, value))
            {
                Notify(nameof(LightingSecondaryColorEditor));
            }
        }
    }

    public int LightingSecondaryGreenEditor
    {
        get => lightingSecondaryGreenEditor;
        set
        {
            if (SetLightingColorField(ref lightingSecondaryGreenEditor, value))
            {
                Notify(nameof(LightingSecondaryColorEditor));
            }
        }
    }

    public int LightingSecondaryBlueEditor
    {
        get => lightingSecondaryBlueEditor;
        set
        {
            if (SetLightingColorField(ref lightingSecondaryBlueEditor, value))
            {
                Notify(nameof(LightingSecondaryColorEditor));
            }
        }
    }

    public RgbColor LightingSecondaryColorEditor
    {
        get => new(
            (byte)LightingSecondaryRedEditor,
            (byte)LightingSecondaryGreenEditor,
            (byte)LightingSecondaryBlueEditor);
        set => SetLightingColorEditor(value, secondary: true);
    }

    public IReadOnlyList<BreathingColorMode> LightingBreathingColorModes { get; } =
        Enum.GetValues<BreathingColorMode>();

    public BreathingColorMode LightingBreathingColorModeEditor
    {
        get => lightingBreathingColorModeEditor;
        set
        {
            if (!Enum.IsDefined(value)
                || !SetLightingEditorField(ref lightingBreathingColorModeEditor, value))
            {
                return;
            }

            SeedBreathingColorDefaults(value);

            NotifyLightingParameterPresentation();
        }
    }

    public IReadOnlyList<ReactiveLightingDuration> LightingReactiveDurations { get; } =
        Enum.GetValues<ReactiveLightingDuration>();

    public ReactiveLightingDuration LightingReactiveDurationEditor
    {
        get => lightingReactiveDurationEditor;
        set
        {
            if (!Enum.IsDefined(value))
            {
                return;
            }

            SetLightingEditorField(ref lightingReactiveDurationEditor, value);
        }
    }

    public IReadOnlyList<StarlightColorMode> LightingStarlightColorModes { get; } =
        Enum.GetValues<StarlightColorMode>();

    public StarlightColorMode LightingStarlightColorModeEditor
    {
        get => lightingStarlightColorModeEditor;
        set
        {
            if (!Enum.IsDefined(value)
                || !SetLightingEditorField(ref lightingStarlightColorModeEditor, value))
            {
                return;
            }

            SeedStarlightColorDefaults(value);
            NotifyLightingParameterPresentation();
        }
    }

    public IReadOnlyList<StarlightLightingDuration> LightingStarlightDurations { get; } =
        Enum.GetValues<StarlightLightingDuration>();

    public StarlightLightingDuration LightingStarlightDurationEditor
    {
        get => lightingStarlightDurationEditor;
        set
        {
            if (!Enum.IsDefined(value))
            {
                return;
            }

            SetLightingEditorField(ref lightingStarlightDurationEditor, value);
        }
    }

    public bool LightingRandomColorsEditor
    {
        get => lightingRandomColorsEditor;
        set
        {
            if (!SetLightingEditorField(ref lightingRandomColorsEditor, value))
            {
                return;
            }

            NotifyLightingParameterPresentation();
        }
    }

    public IReadOnlyList<LightingDirection> LightingDirections { get; } =
        Enum.GetValues<LightingDirection>();

    public IReadOnlyList<LightingDirectionOption> LightingDirectionOptions =>
        LightingEffectEditor == LightingEffect.Tidal
            ? TidalLightingDirectionOptions
            : StandardLightingDirectionOptions;

    public LightingDirectionOption SelectedLightingDirectionOption
    {
        get => LightingDirectionOptions.First(option => option.Value == LightingDirectionEditor);
        set
        {
            if (value is not null)
            {
                LightingDirectionEditor = value.Value;
            }
        }
    }

    public LightingDirection LightingDirectionEditor
    {
        get => lightingDirectionEditor;
        set
        {
            if (!Enum.IsDefined(value))
            {
                return;
            }

            if (SetLightingEditorField(ref lightingDirectionEditor, value))
            {
                Notify(nameof(SelectedLightingDirectionOption));
            }
        }
    }

    public IReadOnlyList<LightingRotationDirection> LightingRotationDirections { get; } =
        Enum.GetValues<LightingRotationDirection>();

    public LightingRotationDirection LightingRotationDirectionEditor
    {
        get => lightingRotationDirectionEditor;
        set
        {
            if (!Enum.IsDefined(value))
            {
                return;
            }

            SetLightingEditorField(ref lightingRotationDirectionEditor, value);
        }
    }

    public IReadOnlyList<AmbientAwarenessRegionOption>
        AvailableAmbientAwarenessRegionOptions
    { get; } =
        Enum.GetValues<AmbientAwarenessRegion>()
            .Select(region => new AmbientAwarenessRegionOption(
                region,
                region == AmbientAwarenessRegion.Full ? "Full screen" : region.ToString()))
            .ToArray();

    public AmbientAwarenessRegion LightingAmbientAwarenessRegionEditor
    {
        get => lightingAmbientAwarenessRegionEditor;
        set
        {
            if (!Enum.IsDefined(value))
            {
                return;
            }

            if (SetLightingEditorField(ref lightingAmbientAwarenessRegionEditor, value))
            {
                Notify(nameof(SelectedAmbientAwarenessRegionOption));
            }
        }
    }

    public double LightingAudioMeterColorBoostEditor
    {
        get => lightingAudioMeterColorBoostEditor;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 4);
            if (!double.IsFinite(value)
                || value * 4 != Math.Truncate(value * 4))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            SetLightingEditorField(ref lightingAudioMeterColorBoostEditor, value);
        }
    }

    public int LightingAudioMeterDecayMillisecondsEditor
    {
        get => lightingAudioMeterDecayMillisecondsEditor;
        set
        {
            if (value is
                    < LightingSettingsFactory.MinAudioMeterDecayMilliseconds
                    or > LightingSettingsFactory.MaxAudioMeterDecayMilliseconds
                || value % LightingSettingsFactory.AudioMeterDecayStepMilliseconds != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            if (SetLightingEditorField(
                ref lightingAudioMeterDecayMillisecondsEditor,
                value))
            {
                Notify(nameof(LightingAudioMeterDecayValue));
            }
        }
    }

    public string LightingAudioMeterDecayValue => FormatAudioMeterDecay(
        lightingAudioMeterDecayMillisecondsEditor);

    public AmbientAwarenessRegionOption? SelectedAmbientAwarenessRegionOption
    {
        get => AvailableAmbientAwarenessRegionOptions.FirstOrDefault(
            option => option.Region == LightingAmbientAwarenessRegionEditor);
        set
        {
            if (value is not null)
            {
                LightingAmbientAwarenessRegionEditor = value.Region;
            }
        }
    }

    public LogoLightingMode? LogoLightingModeEditor
    {
        get => logoLightingModeEditor;
        set
        {
            if (value is { } mode && !Enum.IsDefined(mode))
            {
                return;
            }

            if (SetLightingEditorField(ref logoLightingModeEditor, value))
            {
                Notify(nameof(SelectedLogoLightingModeOption));
            }
        }
    }

    public LogoLightingModeOption? SelectedLogoLightingModeOption
    {
        get => AvailableLogoLightingModeOptions.FirstOrDefault(
            option => option.Mode == LogoLightingModeEditor);
        set
        {
            if (value is not null)
            {
                LogoLightingModeEditor = value.Mode;
            }
        }
    }

    public bool IsLogoLightingVisible => AvailableLogoLightingModeOptions.Count > 0;

    public bool LightingShowsBrightness => LightingEffectEditor != LightingEffect.Off;

    public bool LightingShowsBreathingColorMode =>
        LightingEffectEditor == LightingEffect.Breathing;

    public bool LightingShowsStarlightColorMode =>
        LightingEffectEditor == LightingEffect.Starlight;

    public bool LightingShowsPrimaryColor =>
        LightingEffectEditor is
            LightingEffect.Static or LightingEffect.Reactive or LightingEffect.Ripple
        || LightingEffectEditor == LightingEffect.Breathing
            && LightingBreathingColorModeEditor != BreathingColorMode.Random
        || LightingEffectEditor == LightingEffect.Tidal
            && !LightingRandomColorsEditor
        || LightingEffectEditor == LightingEffect.Starlight
            && LightingStarlightColorModeEditor != StarlightColorMode.Random;

    public bool LightingShowsSecondaryColor =>
        LightingEffectEditor == LightingEffect.Breathing
            && LightingBreathingColorModeEditor == BreathingColorMode.Dual
        || LightingEffectEditor == LightingEffect.Tidal
            && !LightingRandomColorsEditor
        || LightingEffectEditor == LightingEffect.Starlight
            && LightingStarlightColorModeEditor == StarlightColorMode.Dual;

    public string LightingPrimaryColorLabel =>
        LightingShowsSecondaryColor ? "Primary color" : "Color";

    public string LightingPrimaryColorDescription =>
        LightingShowsSecondaryColor
            ? "Choose the effect's first color."
            : "Choose the keyboard color.";

    public bool LightingShowsReactiveDuration =>
        LightingEffectEditor == LightingEffect.Reactive;

    public bool LightingShowsStarlightDuration =>
        LightingEffectEditor == LightingEffect.Starlight;

    public bool LightingShowsRandomColors =>
        LightingEffectEditor == LightingEffect.Tidal;

    public bool LightingShowsDirection =>
        LightingEffectEditor is LightingEffect.Tidal or LightingEffect.Wave;

    public bool LightingShowsRotationDirection =>
        LightingEffectEditor == LightingEffect.Wheel;

    public bool LightingShowsAmbientAwarenessRegion =>
        LightingEffectEditor == LightingEffect.AmbientAwareness;

    public bool LightingShowsAudioMeterControls =>
        LightingEffectEditor == LightingEffect.AudioMeter;

    public LightingSettings LightingDraft => CreateLightingDraft();

    public bool IsLightingApplyPending => lightingEditorDirty;

    public bool LightingTurnOffWithDisplayEditor
    {
        get => lightingTurnOffWithDisplayEditor;
        set => SetLightingPowerPolicyEditorField(
            ref lightingTurnOffWithDisplayEditor,
            value);
    }

    public bool LightingIdleTimeoutEnabledEditor
    {
        get => lightingIdleTimeoutEnabledEditor;
        set => SetLightingPowerPolicyEditorField(
            ref lightingIdleTimeoutEnabledEditor,
            value);
    }

    public int LightingIdleTimeoutMinutesEditor
    {
        get => lightingIdleTimeoutMinutesEditor;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 60);
            SetLightingPowerPolicyEditorField(
                ref lightingIdleTimeoutMinutesEditor,
                value);
        }
    }

    public bool HasUnsavedLightingPowerPolicy => lightingPowerPolicyEditorDirty;

    public LightingPowerPolicy LightingPowerPolicyDraft => new(
        LightingTurnOffWithDisplayEditor,
        LightingIdleTimeoutEnabledEditor ? LightingIdleTimeoutMinutesEditor : null);

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
            Notify(nameof(SelectedThemeOption));
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
            Notify(nameof(LiveReadingsEnabled));
            Notify(nameof(SelectedLiveReadingsRefreshOption));
            Notify(nameof(LiveReadingsObservationInterval));
            Notify(nameof(ShowDashboardLiveReadings));
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
            LiveReadingsCadence.Off => Timeout.InfiniteTimeSpan,
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
            ApplyLegacyTrayHoverLabelMode(value);
            Notify(nameof(SelectedTrayHoverLabelOption));
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

    public string TrayHoverLabelText => BuildTrayHoverLabel().Text;

    public bool HasTrayHoverLabelOverflow => BuildTrayHoverLabel().HasOmittedItems;

    public string TrayHoverLabelOverflowMessage
    {
        get
        {
            TrayHoverLabelFormatResult result = BuildTrayHoverLabel();
            if (!result.HasOmittedItems)
            {
                return string.Empty;
            }

            string omitted = string.Join(", ", result.OmittedItems.Select(kind =>
                TrayHoverLabelItems.FirstOrDefault(option => option.Kind == kind)?.Label
                    ?? kind.ToString()));
            return $"Not shown: {omitted}. Windows limits tray hover text to {TrayHoverLabelFormatter.MaximumLength} characters.";
        }
    }

    public TrayIconState TrayIconState
    {
        get => trayIconState;
        private set => SetField(ref trayIconState, value);
    }

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
            Notify(nameof(StartupDescription));
        }
    }

    public string StartupDescription => string.IsNullOrWhiteSpace(StartupStatusMessage)
        ? "Launch OpenBlade automatically when you sign in."
        : StartupStatusMessage;

    public ICommand SetPerformanceCommand { get; }
    public ICommand SetCustomPerformanceCommand { get; }
    public ICommand SetFanModeCommand { get; }
    public ICommand SetFanControlCommand { get; }
    public ICommand SetLightingCommand { get; }
    public ICommand SetChargeLimitCommand { get; }
    public ICommand ToggleBatteryProtectionCommand { get; }
    public ICommand ToggleFullChargeCommand { get; }
    public ICommand ClearManualOverridesCommand { get; }
    public ICommand SaveProfilesCommand { get; }
    public ICommand DiscardProfileDraftCommand { get; }
    public ICommand SaveThermalProfileDraftCommand { get; }
    public ICommand DiscardThermalProfileDraftCommand { get; }
    public ICommand SaveLightingProfileDraftCommand { get; }
    public ICommand DiscardLightingProfileDraftCommand { get; }
    public ICommand SaveLightingPowerPolicyCommand { get; }
    public ICommand SaveDisplayProfileDraftCommand { get; }
    public ICommand DiscardDisplayProfileDraftCommand { get; }
    public ICommand SaveLaptopProfileDraftCommand { get; }
    public ICommand DiscardLaptopProfileDraftCommand { get; }
    public ICommand ExportDiagnosticsCommand { get; }
    public ICommand SetStartAtSignInCommand { get; }
    public ICommand ApplyOneShotLaptopControlCommand { get; }

    public ApplyOneShotLaptopControl StartupAnimationOnAction { get; } = new(
        OneShotLaptopControl.StartupAnimation,
        Enabled: true);

    public ApplyOneShotLaptopControl StartupAnimationOffAction { get; } = new(
        OneShotLaptopControl.StartupAnimation,
        Enabled: false);

    public ApplyOneShotLaptopControl FunctionKeysPrimaryAction { get; } = new(
        OneShotLaptopControl.FunctionKeyPrimary,
        FunctionKeyMode: FunctionKeyPrimaryMode.FunctionKeys);

    public ApplyOneShotLaptopControl MultimediaKeysPrimaryAction { get; } = new(
        OneShotLaptopControl.FunctionKeyPrimary,
        FunctionKeyMode: FunctionKeyPrimaryMode.MultimediaKeys);

    public ApplyOneShotLaptopControl GamingModeOnAction { get; } = new(
        OneShotLaptopControl.GamingMode,
        Enabled: true);

    public ApplyOneShotLaptopControl GamingModeOffAction { get; } = new(
        OneShotLaptopControl.GamingMode,
        Enabled: false);

    private bool CanMutate =>
        Volatile.Read(ref disposed) == 0
        && IsConnected
        && latestStatus is
        {
            DeviceVerified: true,
            Capabilities: not null,
            ControlAccess: SessionControlAccess.Active,
        }
        && (!latestStatus.WritesBlocked || IsRetainedPowerSourceRecheck(latestStatus))
        && !IsCheckingLaptopConnection(latestStatus);

    private bool HasConfirmedPowerSource => ActivePowerSource is
        PowerSource.PluggedIn or PowerSource.Battery or PowerSource.UsbC;

    private DeviceCapabilities CommandCapabilities =>
        IsRetainedPowerSourceRecheck(latestStatus)
            ? Capabilities
            : latestStatus?.Capabilities ?? DeviceCapabilities.Unavailable();

    private bool CanSetPerformance(object? parameter) =>
        parameter is PerformancePreset value
        && Enum.IsDefined(value)
        && !IsPerformanceOperationInProgress
        && CanMutate
        && HasConfirmedPowerSource
        && CommandCapabilities.PerformancePresets.Contains(value)
        && PerformancePresetPresentation.IsAvailable(ActivePowerSource, value);

    private bool CanSetCustomPerformance(object? parameter) =>
        parameter is CustomPerformanceSettings value
        && CustomPerformanceSettings.IsWellFormed(value)
        && CommandCapabilities.CustomPerformance?.Supports(value) != false
        && IsCustomPerformanceVisible
        && !IsPerformanceOperationInProgress
        && CanMutate
        && HasConfirmedPowerSource;

    private bool SupportsCustomPerformance(
        CustomPerformanceLevel cpu,
        CustomPerformanceLevel gpu)
    {
        var combination = new CustomPerformanceCombination(cpu, gpu);
        return combination.IsWellFormed
            && (Capabilities.CustomPerformance?.AllowedCombinations.Contains(combination)
                ?? true);
    }

    private void RefreshCustomPerformanceOptions()
    {
        CustomPerformanceLevel[] cpu = AllCustomPerformanceLevels
            .Where(level => SupportsCustomPerformance(level, SelectedCustomGpuLevel))
            .ToArray();
        CustomPerformanceLevel[] gpu = AllCustomPerformanceLevels
            .Where(level => SupportsCustomPerformance(SelectedCustomCpuLevel, level))
            .ToArray();
        if (!availableCustomCpuLevels.SequenceEqual(cpu))
        {
            availableCustomCpuLevels = cpu;
            Notify(nameof(AvailableCustomCpuLevels));
        }
        if (!availableCustomGpuLevels.SequenceEqual(gpu))
        {
            availableCustomGpuLevels = gpu;
            Notify(nameof(AvailableCustomGpuLevels));
        }
    }

    private static string FormatObservedRange(DeviceNumericRange? range, string unit)
    {
        if (range?.IsWellFormed != true)
        {
            return UnavailableLabel;
        }

        string suffix = string.IsNullOrEmpty(unit) ? string.Empty : $" {unit}";
        string step = range.Step.HasValue
            ? $", {range.Step.Value}{suffix} steps"
            : ", step not yet validated";
        return $"{range.Minimum}–{range.Maximum}{suffix}{step}";
    }

    private static string FormatFeatureAccess(
        DeviceFeatureAccess? access,
        string observedOnlyText) => access switch
        {
            DeviceFeatureAccess.ReadWrite => "Available",
            DeviceFeatureAccess.ReadOnly => "Read-only",
            DeviceFeatureAccess.ObservedOnly => observedOnlyText,
            _ => UnavailableLabel,
        };

    private bool CanSetFanMode(object? parameter)
    {
        if (parameter is not FanMode value
            || !Enum.IsDefined(value)
            || IsFanControlOperationInProgress
            || !CanMutate
            || !HasConfirmedPowerSource
            || !CommandCapabilities.FanModes.Contains(value))
        {
            return false;
        }

        return FanModeAvailable(ActivePowerSource, value)
            && (value == FanMode.Automatic
                || ActivePowerSource == PowerSource.PluggedIn);
    }

    private bool CanSetFanControl(object? parameter) =>
        parameter is FanControlSettings value
        && CanSetFanMode(value.Mode)
        && (value.Mode != FanMode.Fixed
            || ActiveFanSpeedRange.Supports(value.FixedSpeedRpm));

    private bool CanApplyOneShotLaptopControl(object? parameter) =>
        parameter is ApplyOneShotLaptopControl value
        && value.IsWellFormed()
        && !IsOneShotOperationInProgress(value.Control)
        && !IsOneShotFieldLoading(value.Control)
        && CanMutate
        && CommandCapabilities.OneShotLaptopControls.Contains(value.Control);

    private bool IsOneShotOperationInProgress(OneShotLaptopControl control) => control switch
    {
        OneShotLaptopControl.StartupAnimation => isStartupAnimationOperationInProgress,
        OneShotLaptopControl.FunctionKeyPrimary => isFunctionKeyOperationInProgress,
        OneShotLaptopControl.GamingMode => isGamingModeOperationInProgress,
        _ => false,
    };

    private bool IsOneShotFieldLoading(OneShotLaptopControl control) => control switch
    {
        OneShotLaptopControl.StartupAnimation => IsStartupAnimationFieldLoading,
        OneShotLaptopControl.FunctionKeyPrimary => IsFunctionKeyFieldLoading,
        OneShotLaptopControl.GamingMode => IsGamingModeFieldLoading,
        _ => false,
    };

    private bool IsOneShotFirmwareFieldLoading(
        OneShotLaptopControl control,
        bool operationInProgress,
        bool readPending) =>
        operationInProgress
        || IsConnected
        && Capabilities.OneShotLaptopControls.Contains(control)
        && readPending;

    private bool CanSetFullChargeOverride(object? parameter)
    {
        if (parameter is not null and not bool)
        {
            return false;
        }

        DeviceCapabilities capabilities = CommandCapabilities;
        ServiceStatusSnapshot? status = latestStatus;
        if (IsFullChargeOperationInProgress
            || !CanMutate
            || !HasConfirmedPowerSource
            || !HasBatteryCapability(capabilities)
            || !capabilities.SupportsTemporaryFullCharge
            || status?.ConfirmedState?.Battery is not
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
        DeviceCapabilities capabilities = CommandCapabilities;
        ServiceStatusSnapshot? status = latestStatus;
        if (parameter is not null
            || IsBatteryProtectionOperationInProgress
            || !CanMutate
            || !HasConfirmedPowerSource
            || !HasBatteryCapability(capabilities)
            || status?.ConfirmedState?.Battery is not
            {
                TemporaryFullCharge: false,
            } confirmed)
        {
            return false;
        }

        int storedLimit = status.DesiredState.Battery.ConfiguredPolicy
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

    public Task ObserveStateAsync(CancellationToken cancellationToken) =>
        ObserveStateAsync(includeDashboardReadings: true, cancellationToken);

    public Task ObserveTrayStateAsync(CancellationToken cancellationToken) =>
        ObserveStateAsync(includeDashboardReadings: false, cancellationToken);

    private async Task ObserveStateAsync(
        bool includeDashboardReadings,
        CancellationToken cancellationToken)
    {
        ObservationRequestStamp stamp = default;
        LiveReadingSensors requestedLiveReadings = LiveReadingSensors.None;
        bool refreshControlAccess = false;
        await dispatcher.InvokeAsync(
            () =>
            {
                stamp = new ObservationRequestStamp(
                    connectionGeneration,
                    subscriptionConnected);
                requestedLiveReadings = ResolveRequestedLiveReadings(
                    includeDashboardReadings);
                refreshControlAccess = IsConnected
                    && latestStatus?.ControlAccess != SessionControlAccess.Active;
            },
            cancellationToken);
        if (refreshControlAccess)
        {
            ServiceStatusSnapshot status = await serviceClient
                .GetStatusAsync(cancellationToken);
            await dispatcher.InvokeAsync(
                () => ApplyObservedStatus(
                    status,
                    stamp,
                    trayOnly: !includeDashboardReadings),
                cancellationToken);
            return;
        }

        if (requestedLiveReadings == LiveReadingSensors.None)
        {
            return;
        }

        try
        {
            ServiceStatusSnapshot status = await serviceClient
                .ObserveStateAsync(requestedLiveReadings, cancellationToken);
            await dispatcher.InvokeAsync(
                () =>
                {
                    bool accepted = ApplyObservedStatus(
                        status,
                        stamp,
                        trayOnly: !includeDashboardReadings);
                    if (accepted
                        && requestedLiveReadings != LiveReadingSensors.None
                        && LiveReadingsEnabled
                        && status.LiveReadings is not null)
                    {
                        MarkLiveReadingsRefreshed(
                            publishPresentation: includeDashboardReadings);
                    }
                },
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
    }

    public async Task ShutdownOpenBladeAsync(CancellationToken cancellationToken)
    {
        ControlOperationResult result = await serviceClient
            .ShutdownOpenBladeAsync(cancellationToken)
            .ConfigureAwait(false);
        if (result.Status != ControlOperationStatus.Stored)
        {
            throw new InvalidOperationException(result.Message);
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
        if (serviceClient is IAsyncDisposable asyncDisposableClient)
        {
            await asyncDisposableClient.DisposeAsync();
        }
        else if (serviceClient is IDisposable disposableClient)
        {
            disposableClient.Dispose();
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
                                reconnectGeneration++;
                                ApplyStatus(connected.Status);
                                break;
                            case ServiceWatchUpdate.Reconnecting:
                                MarkReconnecting("Reconnecting to service");
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

    private void ApplyStatus(
        ServiceStatusSnapshot status,
        bool trayOnly = false)
    {
        if (trayOnly && TryApplyTrayLiveReadingsOnly(status))
        {
            return;
        }

        bool wasSuppressed = suppressProfileDifferenceNotifications;
        suppressProfileDifferenceNotifications = true;
        try
        {
            ApplyStatusCore(status);
        }
        finally
        {
            suppressProfileDifferenceNotifications = wasSuppressed;
        }

        if (!suppressProfileDifferenceNotifications)
        {
            NotifyProfileDifferenceState();
        }
    }

    private void ApplyStatusCore(ServiceStatusSnapshot status)
    {
        if (status.Revision < latestAcceptedRevision)
        {
            return;
        }

        bool performanceHoldReleased = ReleasePendingPerformanceHold(status);
        ServiceStatusSnapshot? previousStatus = latestStatus;
        latestAcceptedRevision = status.Revision;
        latestStatus = status;
        if (IsConnected
            && previousStatus is not null
            && !performanceHoldReleased
            && StatusMetadataEqual(previousStatus, status))
        {
            ApplyLiveReadings(status);
            return;
        }

        bool checkingLaptopConnection = IsCheckingLaptopConnection(status);
        DeviceSubsystem[] externalChanges = ApplyConnectionPresentation(
            status,
            checkingLaptopConnection);
        DeviceCapabilities capabilities = ApplyCapabilityPresentation(
            status,
            previousStatus);
        ConfirmedDeviceState? confirmed = status.ConfirmedState;
        ApplyDeviceStatePresentation(
            status,
            confirmed,
            capabilities,
            checkingLaptopConnection);
        ApplySourcePresentation(status, confirmed, externalChanges);
        ApplyCapabilityOptions(capabilities);
        NotifyCommands();
        NotifyPresentationState();
    }

    private void ApplyDeviceStatePresentation(
        ServiceStatusSnapshot status,
        ConfirmedDeviceState? confirmed,
        DeviceCapabilities capabilities,
        bool checkingLaptopConnection)
    {
        UpdateOneShotReadbacks(status);
        SelectedPerformance = pendingPerformance?.Requested.Value
            ?? ResolvePresentedPerformance(status, confirmed, capabilities);
        SelectedFanMode = pendingFanControl?.Mode
            ?? ResolvePresentedFanMode(status, confirmed, capabilities);
        LoadFanControlEditor(
            pendingFanControl
            ?? status.FanControlOverride
            ?? status.DesiredState.FanControl
            ?? FanControlSettings.FromLegacy(SelectedFanMode));
        PrepareLightingEditorForPowerSource(status.ActivePowerSource);
        if (!checkingLaptopConnection
            && !lightingEditorDirty
            && !IsLightingOperationInProgress)
        {
            SetKeyboardEffectUnconfirmed(
                confirmed?.Lighting is null
                && status.LightingOverride is null
                && status.LightingRendererOwnership is
                    LightingRendererOwnershipState.None or
                    LightingRendererOwnershipState.Faulted);
        }
        SelectedLighting = confirmed?.Lighting ?? status.DesiredState.Lighting.Value;
        if (confirmed?.LogoLighting is { } confirmedLogo)
        {
            SelectedLighting = SelectedLighting with { LogoMode = confirmedLogo };
        }
        if (!lightingEditorDirty && !checkingLaptopConnection)
        {
            LoadLightingEditor(SelectedLighting);
            if (keyboardEffectUnconfirmed && LightingBrightnessEditor == 0)
            {
                // PID 02E0 reports brightness independently but cannot report
                // its current matrix effect. Zero in this state means unknown,
                // not that the lit keyboard is actually at zero percent. Start
                // a newly selected visible effect at a neutral, visible level.
                SetField(
                    ref lightingBrightnessEditor,
                    (int)DefaultLightingBrightness,
                    nameof(LightingBrightnessEditor));
            }
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
        CustomPerformanceSettings customPerformance =
            pendingPerformance?.Requested is
            { Value: PerformancePreset.Custom, CustomPerformance: { } pendingCustom }
                ? pendingCustom
                : confirmed is
                { Performance: PerformancePreset.Custom, CustomPerformance: { } observed }
                ? observed
                : status.DesiredState.CustomPerformance
                    ?? status.Profiles.PluggedIn.CustomPerformance
                    ?? CustomPerformanceSettings.Default;
        SelectedCustomCpuLevel = customPerformance.Cpu;
        SelectedCustomGpuLevel = customPerformance.Gpu;
        GpuClockTuningSettings? gpuClockTuning = pendingPerformance?.Requested.GpuClockTuning
            ?? status.DesiredState.GpuClockTuning;
        if (gpuClockTuning is null
            && customPerformance.GpuClockOffsets is { } legacyOffsets)
        {
            gpuClockTuning = new GpuClockTuningSettings(
                Enabled: true,
                legacyOffsets);
        }
        GpuClockOffsetCapabilities = confirmed?.GpuClockOffsets;
        gpuClockTuningConfigured = gpuClockTuning is not null;
        IsGpuClockTuningEnabled = gpuClockTuning?.Enabled == true;
        GpuClockOffsetSettings presentedGpuOffsets =
            gpuClockTuning?.Offsets
            ?? GpuClockOffsetCapabilities?.Current
            ?? GpuClockOffsetSettings.Default;
        GpuCoreClockOffsetMegahertz = presentedGpuOffsets.CoreMegahertz;
        GpuMemoryClockOffsetMegahertz = presentedGpuOffsets.MemoryMegahertz;
        TrayIconState = TrayIconStateResolver.Resolve(
            isConnected: true,
            status.DeviceVerified,
            status.ActivePowerSource,
            SelectedPerformance);
        ApplyLiveReadings(status);
        PerformanceOverride = status.PerformanceOverride;
        FanOverride = status.FanOverride;
        LightingOverride = status.LightingOverride;
        ActivePowerSourceLabel = status.ActivePowerSource switch
        {
            PowerSource.PluggedIn => "AC profile",
            PowerSource.Battery => "Battery profile",
            PowerSource.UsbC => "USB-C profile",
            PowerSource.ExternalUnknown => "Checking power source",
            _ => "Power source unavailable",
        };
    }

    private void ApplySourcePresentation(
        ServiceStatusSnapshot status,
        ConfirmedDeviceState? confirmed,
        IReadOnlyCollection<DeviceSubsystem> externalChanges)
    {
        PerformanceSourceLabel = SourceLabel(
            DeviceSubsystem.Performance,
            confirmed?.Performance is not null,
            status,
            externalChanges);
        FanSourceLabel = SourceLabel(
            DeviceSubsystem.Fans,
            confirmed?.Fans is not null,
            status,
            externalChanges);
        LightingSourceLabel = SourceLabel(
            DeviceSubsystem.Lighting,
            confirmed?.Lighting is not null,
            status,
            externalChanges);
        BatterySourceLabel = FormatBatterySource(
            confirmed?.Battery,
            externalChanges.Contains(DeviceSubsystem.Battery),
            status);
    }

    private DeviceSubsystem[] ApplyConnectionPresentation(
        ServiceStatusSnapshot status,
        bool checkingLaptopConnection)
    {
        IsConnected = true;
        bool controllable = status.ControlAccess == SessionControlAccess.Active
            && status.DeviceVerified
            && !status.WritesBlocked
            && !checkingLaptopConnection;
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
            ? $"Changed outside OpenBlade: {string.Join(", ", externalChanges.Select(SubsystemLabel))}. Your saved profiles are unchanged. Automatic profile updates for these controls are paused until you change them in OpenBlade."
            : string.Empty;
        AlwaysOverrideExternalLightingChanges =
            status.AlwaysOverrideExternalLightingChanges;
        AllowUntestedFirmware = status.AllowUntestedFirmware;
        KeyboardLightingControlMode = status.KeyboardLightingControlMode;
        Profiles = status.Profiles;
        if (!lightingPowerPolicyEditorDirty)
        {
            LoadLightingPowerPolicyEditor(
                status.ActivePowerSource is PowerSource.PluggedIn
                    or PowerSource.Battery
                    or PowerSource.UsbC
                    ? GetPresentedProfile(status.ActivePowerSource)
                        .EffectiveLightingPowerPolicy
                    : LightingPowerPolicy.Disabled);
        }
        if (!profileEditorsDirty)
        {
            LoadProfileEditors(status.Profiles);
        }

        return externalChanges;
    }

    private DeviceCapabilities ApplyCapabilityPresentation(
        ServiceStatusSnapshot status,
        ServiceStatusSnapshot? previousStatus)
    {
        DeviceCapabilities incomingCapabilities = status.Capabilities
            ?? DeviceCapabilities.Unavailable();
        DeviceCapabilities capabilities =
            ShouldPreserveCapabilityPresentationDuringRefresh(
                status,
                previousStatus,
                incomingCapabilities)
                ? Capabilities
                : incomingCapabilities;
        Capabilities = capabilities;
        int[] chargeLimits = HasBatteryCapability(capabilities)
            ? capabilities.ChargeLimits.Order().ToArray()
            : [];
        if (!ChargeLimitOptions.SequenceEqual(chargeLimits))
        {
            ChargeLimitOptions = chargeLimits;
        }

        return capabilities;
    }

    private void ApplyCapabilityOptions(DeviceCapabilities capabilities)
    {
        PerformancePreset[] performancePresets = capabilities.PerformancePresets
            .Where(Enum.IsDefined)
            .Order()
            .ToArray();
        if (!AvailablePerformancePresets.SequenceEqual(performancePresets))
        {
            AvailablePerformancePresets = performancePresets;
            // WinUI clears a ComboBox selection when ItemsSource is replaced.
            Notify(nameof(SelectedPerformance));
            Notify(nameof(IsCustomPerformanceVisible));
            Notify(nameof(IsGpuClockTuningVisible));
            Notify(nameof(IsGpuClockTuningAvailable));
            Notify(nameof(CanToggleGpuClockTuning));
            Notify(nameof(CanEditGpuClockTuning));
            Notify(nameof(PluggedInPerformanceEditor));
            Notify(nameof(BatteryPerformanceEditor));
            Notify(nameof(UsbCPerformanceEditor));
        }
        var admittedPerformancePresets = performancePresets.ToHashSet();
        IReadOnlyList<PerformancePresetOption> pluggedInOptions =
            StableOptionCollection.ReuseEquivalent(
                AvailablePluggedInPerformanceOptions,
                PerformancePresetPresentation.CreateOptions(
                    PowerSource.PluggedIn,
                    admittedPerformancePresets));
        IReadOnlyList<PerformancePresetOption> batteryOptions =
            StableOptionCollection.ReuseEquivalent(
                AvailableBatteryPerformanceOptions,
                PerformancePresetPresentation.CreateOptions(
                    PowerSource.Battery,
                    admittedPerformancePresets));
        IReadOnlyList<PerformancePresetOption> usbCOptions =
            StableOptionCollection.ReuseEquivalent(
                AvailableUsbCPerformanceOptions,
                PerformancePresetPresentation.CreateOptions(
                    PowerSource.UsbC,
                    admittedPerformancePresets));
        bool performanceOptionsChanged = false;
        if (!ReferenceEquals(AvailablePluggedInPerformanceOptions, pluggedInOptions))
        {
            AvailablePluggedInPerformanceOptions = pluggedInOptions;
            performanceOptionsChanged = true;
        }
        if (!ReferenceEquals(AvailableBatteryPerformanceOptions, batteryOptions))
        {
            AvailableBatteryPerformanceOptions = batteryOptions;
            performanceOptionsChanged = true;
        }
        if (!ReferenceEquals(AvailableUsbCPerformanceOptions, usbCOptions))
        {
            AvailableUsbCPerformanceOptions = usbCOptions;
            performanceOptionsChanged = true;
        }
        if (performanceOptionsChanged)
        {
            Notify(nameof(AvailableActivePerformanceOptions));
            Notify(nameof(SelectedPerformanceOption));
            Notify(nameof(PluggedInPerformanceOption));
            Notify(nameof(BatteryPerformanceOption));
            Notify(nameof(UsbCPerformanceOption));
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
            var admittedFanModes = fanModes.ToHashSet();
            availableFanModeOptions = StableOptionCollection.ReuseEquivalent(
                availableFanModeOptions,
                FanModePresentation.CreateOptions(admittedFanModes));
            availableUsbCFanModeOptions = StableOptionCollection.ReuseEquivalent(
                availableUsbCFanModeOptions,
                FanModePresentation.CreateOptions(
                    admittedFanModes,
                    automaticOnly: true));
            Notify(nameof(PluggedInFanModeEditor));
            Notify(nameof(BatteryFanModeEditor));
            Notify(nameof(UsbCFanModeEditor));
            NotifyActiveFanSelectionBinding();
        }
        LightingEffect[] lightingEffects = capabilities.LightingEffects
            .Where(Enum.IsDefined)
            .Order()
            .ToArray();
        if (!AvailableLightingEffects.SequenceEqual(lightingEffects))
        {
            AvailableLightingEffects = lightingEffects;
        }
        IReadOnlyList<LightingEffect> presentedLightingEffects =
            AvailableLightingEffects.Count > 0
                ? AvailableLightingEffects
                : KnownLightingEffects;
        IReadOnlyList<LightingEffectOption> lightingEffectOptions =
            StableOptionCollection.ReuseEquivalent(
                AvailableLightingEffectOptions,
                presentedLightingEffects
                    .Select(CreateLightingEffectOption)
                    .OrderBy(option => option.Effect == LightingEffect.Off ? 0 : 1)
                    .ThenBy(option => option.Label, StringComparer.OrdinalIgnoreCase)
                    .ToArray());
        if (!ReferenceEquals(AvailableLightingEffectOptions, lightingEffectOptions))
        {
            AvailableLightingEffectOptions = lightingEffectOptions;
            Notify(nameof(SelectedLightingEffectOption));
        }
        LightingSettings[] lightingSettings = AvailableLightingEffects
            .Select(CreateLightingChoice)
            .ToArray();
        if (!AvailableLightingSettings.SequenceEqual(lightingSettings))
        {
            AvailableLightingSettings = lightingSettings;
        }
        LogoLightingMode[] admittedLogoLightingModes = capabilities.LogoLightingModes
            .Where(Enum.IsDefined)
            .Order()
            .ToArray();
        IReadOnlyList<LogoLightingMode> presentedLogoLightingModes =
            AvailableLightingEffects.Count == 0 && admittedLogoLightingModes.Length == 0
                ? KnownLogoLightingModes
                : admittedLogoLightingModes;
        IReadOnlyList<LogoLightingModeOption> logoLightingModeOptions =
            StableOptionCollection.ReuseEquivalent(
                AvailableLogoLightingModeOptions,
                presentedLogoLightingModes
                    .Select(mode => new LogoLightingModeOption(mode, mode.ToString()))
                    .ToArray());
        if (!ReferenceEquals(AvailableLogoLightingModeOptions, logoLightingModeOptions))
        {
            AvailableLogoLightingModeOptions = logoLightingModeOptions;
            Notify(nameof(SelectedLogoLightingModeOption));
        }
        Notify(nameof(IsLogoLightingVisible));
        IsPerformanceVisible = AvailablePerformancePresets.Count > 0;
        IsFanControlVisible = AvailableFanModes.Count > 0;
        Notify(nameof(IsProfileControlVisible));
    }

    private static LightingEffectOption CreateLightingEffectOption(LightingEffect effect) =>
        new(
            effect,
            effect switch
            {
                LightingEffect.Spectrum => "Spectrum Cycling",
                LightingEffect.AmbientAwareness => "Ambient Awareness",
                LightingEffect.AudioMeter => "Audio Meter",
                _ => effect.ToString(),
            });

    private void NotifyActiveFanSelectionBinding()
    {
        // WinUI clears SelectedItem whenever the active ItemsSource is replaced.
        // Always publish the list first, then reapply the confirmed selection.
        Notify(nameof(AvailableActiveFanModes));
        Notify(nameof(AvailableActiveFanModeOptions));
        Notify(nameof(SelectedFanMode));
        Notify(nameof(SelectedFanModeOption));
    }

    private void MarkDisconnected() => MarkDisconnected("Service disconnected");

    private void MarkReconnecting(string message)
    {
        if (!IsConnected || latestStatus is null)
        {
            MarkDisconnected(message);
            return;
        }

        subscriptionConnected = false;
        long generation = ++reconnectGeneration;
        ConnectionStatus = message;
        _ = MarkDisconnectedAfterReconnectGraceAsync(generation, message);
    }

    private async Task MarkDisconnectedAfterReconnectGraceAsync(
        long generation,
        string message)
    {
        try
        {
            await Task.Delay(ReconnectVisualGracePeriod, lifetime.Token)
                .ConfigureAwait(false);
            await dispatcher.InvokeAsync(
                () =>
                {
                    if (generation == reconnectGeneration
                        && !subscriptionConnected)
                    {
                        MarkDisconnected(message);
                    }
                },
                lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
    }

    public bool HasEnabledTrayHoverLiveReadings =>
        LiveReadingsEnabled
        && TrayHoverLabelItems.Any(option =>
            option.IsEnabled && ToSensor(option.Kind) != LiveReadingSensors.None);

    private LiveReadingSensors ResolveRequestedLiveReadings(bool includeDashboardReadings)
    {
        if (!LiveReadingsEnabled)
        {
            return LiveReadingSensors.None;
        }

        LiveReadingSensors requested = LiveReadingSensors.None;
        if (includeDashboardReadings)
        {
            foreach (LiveReadingVisibilityOption option in DashboardTelemetryItems)
            {
                if (option.IsEnabled)
                {
                    requested |= ToSensor(option.Kind);
                }
            }
        }

        foreach (TrayHoverLabelItemOption option in TrayHoverLabelItems)
        {
            if (option.IsEnabled)
            {
                requested |= ToSensor(option.Kind);
            }
        }
        return requested;
    }

    private void ApplyLiveReadings(
        ServiceStatusSnapshot status,
        bool publishPresentation = true)
    {
        if (!LiveReadingsEnabled || !status.DeviceVerified)
        {
            SetLiveReadings(null, publishPresentation);
            if (!status.DeviceVerified)
            {
                ClearLiveReadingsRefreshTime(publishPresentation);
            }
        }
        else if (status.LiveReadings is not null)
        {
            SetLiveReadings(status.LiveReadings, publishPresentation);
        }
    }

    private static LiveReadingSensors ToSensor(LiveReadingItemKind kind) => kind switch
    {
        LiveReadingItemKind.BatteryPower => LiveReadingSensors.BatteryPower,
        LiveReadingItemKind.CpuTemperature => LiveReadingSensors.CpuTemperature,
        LiveReadingItemKind.GpuTemperature => LiveReadingSensors.GpuTemperature,
        LiveReadingItemKind.Fan1Speed or LiveReadingItemKind.Fan2Speed =>
            LiveReadingSensors.FanSpeeds,
        _ => LiveReadingSensors.None,
    };

    private static LiveReadingSensors ToSensor(TrayHoverLabelItemKind kind) => kind switch
    {
        TrayHoverLabelItemKind.BatteryPower => LiveReadingSensors.BatteryPower,
        TrayHoverLabelItemKind.CpuTemperature => LiveReadingSensors.CpuTemperature,
        TrayHoverLabelItemKind.GpuTemperature => LiveReadingSensors.GpuTemperature,
        TrayHoverLabelItemKind.Fan1Speed or TrayHoverLabelItemKind.Fan2Speed =>
            LiveReadingSensors.FanSpeeds,
        _ => LiveReadingSensors.None,
    };

    public async Task<bool> ImportProfilesAsync(
        ProfileSet profiles,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        if (!TryGetSupportedProfileDraft(profiles, out ProfileSet validated))
        {
            return false;
        }

        bool accepted = await RunOperationAsync(
            token => serviceClient.UpdateProfilesAsync(validated, token),
            cancellationToken);
        if (!accepted)
        {
            return false;
        }

        await dispatcher.InvokeAsync(
            () =>
            {
                Profiles = validated;
                ClearActiveProfileDraft();
                SetProfileEditorsDirty(false);
                LoadProfileEditors(validated);
            },
            cancellationToken);
        return true;
    }

    private void MarkDisconnected(string message)
    {
        connectionGeneration++;
        reconnectGeneration++;
        subscriptionConnected = false;
        latestAcceptedRevision = -1;
        IsConnected = false;
        TrayIconState = TrayIconState.GenericError;
        IsReadOnly = true;
        CanControlHardware = false;
        ConnectionStatus = message;
        ActivePowerSourceLabel = UnavailableLabel;
        CurrentBatterySettingLabel = UnavailableLabel;
        PerformanceSourceLabel = UnavailableLabel;
        FanSourceLabel = UnavailableLabel;
        LightingSourceLabel = UnavailableLabel;
        BatterySourceLabel = UnavailableLabel;
        LiveReadings = null;
        ClearLiveReadingsRefreshTime();
        ControlAccessStatus = "OpenBlade service isn’t running";
        NotifyCommands();
        NotifyPresentationState();
    }

    private bool ApplyObservedStatus(
        ServiceStatusSnapshot status,
        ObservationRequestStamp stamp,
        bool trayOnly)
    {
        if (stamp.BeganWithConnectedSubscription
            && stamp.Generation == connectionGeneration)
        {
            ApplyStatus(status, trayOnly);
            return true;
        }

        return false;
    }

    private bool TryApplyTrayLiveReadingsOnly(ServiceStatusSnapshot status)
    {
        if (status.Revision < latestAcceptedRevision)
        {
            return true;
        }

        ServiceStatusSnapshot? previousStatus = latestStatus;
        if (!IsConnected
            || previousStatus is null
            || !StatusMetadataEqual(previousStatus, status))
        {
            return false;
        }

        latestAcceptedRevision = status.Revision;
        latestStatus = status;
        ApplyLiveReadings(status, publishPresentation: false);
        return true;
    }

    private void MarkLiveReadingsRefreshed(bool publishPresentation = true)
    {
        lastLiveReadingsRefreshAt = timeProvider.GetUtcNow();
        if (publishPresentation)
        {
            Notify(nameof(LiveReadingsRefreshToolTip));
        }
    }

    private void ClearLiveReadingsRefreshTime(bool publishPresentation = true)
    {
        if (lastLiveReadingsRefreshAt is null)
        {
            return;
        }

        lastLiveReadingsRefreshAt = null;
        if (publishPresentation)
        {
            Notify(nameof(LiveReadingsRefreshToolTip));
        }
    }

    private bool CanSetChargeLimit(int value)
    {
        DeviceCapabilities capabilities = CommandCapabilities;
        ServiceStatusSnapshot? status = latestStatus;
        if (IsChargeLimitOperationInProgress
            || !CanMutate
            || !HasConfirmedPowerSource
            || status?.ConfirmedState?.Battery is not
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
            Notify(nameof(EnableFullChargeOverride));
            Notify(nameof(FullChargeActionLabel));
        }

        FullChargeOverride = active;
    }

    private async Task SetPerformanceAsync(object? parameter, CancellationToken token)
    {
        PerformancePreset requested = (PerformancePreset)parameter!;
        await ApplyPerformanceAsync(
            new PerformanceOverride(
                requested,
                OverrideLifetime.UntilPowerSourceChange,
                requested == PerformancePreset.Custom
                    ? GetActiveProfileCustomPerformance()
                    : null)
            {
                GpuClockTuning = GetActiveProfileGpuClockTuning(),
            },
            token);
    }

    private async Task SetCustomPerformanceAsync(object? parameter, CancellationToken token)
    {
        CustomPerformanceSettings requested = (CustomPerformanceSettings)parameter!;
        GpuClockTuningSettings? gpuClockTuning = GetActiveProfileGpuClockTuning();
        if (requested.GpuClockOffsets is { } legacyOffsets)
        {
            requested = requested with { GpuClockOffsets = null };
            gpuClockTuning ??= new GpuClockTuningSettings(
                Enabled: true,
                legacyOffsets);
        }
        await ApplyPerformanceAsync(
            new PerformanceOverride(
                PerformancePreset.Custom,
                OverrideLifetime.UntilPowerSourceChange,
                requested)
            {
                GpuClockTuning = gpuClockTuning,
            },
            token);
    }

    public Task SetGpuClockTuningEnabledAsync(
        bool enabled,
        CancellationToken token)
    {
        if (!CanToggleGpuClockTuning)
        {
            throw new InvalidOperationException("GPU clock tuning is unavailable.");
        }

        return ApplyGpuClockTuningAsync(
            new GpuClockTuningSettings(enabled, CurrentGpuClockOffsets),
            token);
    }

    public Task SetGpuClockOffsetsAsync(
        GpuClockOffsetSettings offsets,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(offsets);
        if (!CanEditGpuClockTuning
            || GpuClockOffsetCapabilities?.Supports(offsets) != true)
        {
            throw new InvalidOperationException("GPU clock offsets are unavailable.");
        }

        return ApplyGpuClockTuningAsync(
            new GpuClockTuningSettings(Enabled: true, offsets),
            token);
    }

    private Task ApplyGpuClockTuningAsync(
        GpuClockTuningSettings gpuClockTuning,
        CancellationToken token) => ApplyPerformanceAsync(
            new PerformanceOverride(
                SelectedPerformance,
                OverrideLifetime.UntilPowerSourceChange,
                SelectedPerformance == PerformancePreset.Custom
                    ? new CustomPerformanceSettings(
                        SelectedCustomCpuLevel,
                        SelectedCustomGpuLevel)
                    : null)
            {
                GpuClockTuning = gpuClockTuning,
            },
            token);

    private async Task ApplyPerformanceAsync(
        PerformanceOverride requested,
        CancellationToken token)
    {
        PowerSource sourceAtStart = ActivePowerSource;
        PerformancePreset previousPerformance = SelectedPerformance;
        CustomPerformanceSettings previousCustom = new(
            SelectedCustomCpuLevel,
            SelectedCustomGpuLevel);
        GpuClockTuningSettings? previousGpuClockTuning = gpuClockTuningConfigured
            ? new GpuClockTuningSettings(
                IsGpuClockTuningEnabled,
                CurrentGpuClockOffsets)
            : null;
        bool applied = false;
        pendingPerformance = new PendingPerformanceHold(
            requested,
            sourceAtStart,
            ExpiresAt: null);
        IsPerformanceOperationInProgress = true;
        PresentPerformance(requested);
        try
        {
            await RunOperationAsync(
                cancellationToken => serviceClient.SetPerformanceConfigurationOverrideAsync(
                    requested,
                    cancellationToken),
                token,
                result =>
                {
                    if (result.Status is
                        ControlOperationStatus.Applied or ControlOperationStatus.Stored)
                    {
                        applied = true;
                        MarkActiveProfileDraft(
                            sourceAtStart,
                            performance: requested.Value,
                            customPerformance: requested.CustomPerformance,
                            gpuClockTuning: requested.GpuClockTuning);
                    }
                });
        }
        finally
        {
            IsPerformanceOperationInProgress = false;
            if (!applied)
            {
                pendingPerformance = null;
                PresentPerformance(new PerformanceOverride(
                    previousPerformance,
                    OverrideLifetime.UntilPowerSourceChange,
                    previousPerformance == PerformancePreset.Custom
                        ? previousCustom
                        : null)
                {
                    GpuClockTuning = previousGpuClockTuning,
                });
            }
            else
            {
                if (pendingPerformance is { Requested: var pending } hold
                    && pending == requested)
                {
                    pendingPerformance = hold with
                    {
                        ExpiresAt = timeProvider.GetUtcNow()
                            + PerformancePresentationHoldGracePeriod,
                    };
                }

                if (latestStatus is { } status)
                {
                    ReleasePendingPerformanceHold(status);
                }
            }
        }
    }

    private bool ReleasePendingPerformanceHold(ServiceStatusSnapshot status)
    {
        if (pendingPerformance is not { } pending)
        {
            return false;
        }

        bool statusCaughtUp = status.PerformanceOverride == pending.Requested;
        bool sourceChanged = status.ActivePowerSource != pending.PowerSource;
        bool gracePeriodExpired = pending.ExpiresAt is { } expiresAt
            && timeProvider.GetUtcNow() >= expiresAt;
        if (!statusCaughtUp && !sourceChanged && !gracePeriodExpired)
        {
            return false;
        }

        pendingPerformance = null;
        return true;
    }

    private void PresentPerformance(PerformanceOverride value)
    {
        SelectedPerformance = value.Value;
        if (value is
            { Value: PerformancePreset.Custom, CustomPerformance: { } custom })
        {
            SelectedCustomCpuLevel = custom.Cpu;
            SelectedCustomGpuLevel = custom.Gpu;
        }

        gpuClockTuningConfigured = value.GpuClockTuning is not null;
        IsGpuClockTuningEnabled = value.GpuClockTuning?.Enabled == true;
        if (value.GpuClockTuning?.Offsets is { } offsets)
        {
            GpuCoreClockOffsetMegahertz = offsets.CoreMegahertz;
            GpuMemoryClockOffsetMegahertz = offsets.MemoryMegahertz;
        }
    }

    private Task SetFanModeAsync(object? parameter, CancellationToken token)
    {
        FanMode requested = (FanMode)parameter!;
        FanControlSettings settings = new(
            requested,
            requested == FanMode.Maximum
                ? ActiveFanSpeedRange.MaximumRpm
                : FixedFanSpeedRpm,
            CurrentFanControlSettings.ManualCurve);
        return ApplyFanControlAsync(settings, token);
    }

    private Task SetFanControlAsync(object? parameter, CancellationToken token) =>
        ApplyFanControlAsync((FanControlSettings)parameter!, token);

    private async Task ApplyFanControlAsync(
        FanControlSettings requested,
        CancellationToken token)
    {
        PowerSource sourceAtStart = ActivePowerSource;
        FanControlSettings previous = CurrentFanControlSettings;
        bool applied = false;
        pendingFanControl = requested;
        IsFanControlOperationInProgress = true;
        SelectedFanMode = requested.Mode;
        LoadFanControlEditor(requested);
        try
        {
            await RunOperationAsync(
                cancellationToken => serviceClient.SetFanControlOverrideAsync(
                    new ManualOverride<FanMode>(
                        requested.Mode,
                        OverrideLifetime.UntilPowerSourceChange),
                    requested,
                    cancellationToken),
                token,
                result =>
                {
                    if (result.Status == ControlOperationStatus.Applied)
                    {
                        applied = true;
                        SelectedFanMode = requested.Mode;
                        LoadFanControlEditor(requested);
                        MarkActiveProfileDraft(
                            sourceAtStart,
                            fans: requested.Mode,
                            fanControl: requested);
                    }
                });
        }
        finally
        {
            pendingFanControl = null;
            IsFanControlOperationInProgress = false;
            if (!applied)
            {
                SelectedFanMode = previous.Mode;
                LoadFanControlEditor(previous);
            }
        }
    }

    private async Task SetLightingAsync(object? parameter, CancellationToken token)
    {
        LightingSettings requested = (LightingSettings)parameter!;
        PowerSource sourceAtStart = ActivePowerSource;
        long revisionAtStart = latestAcceptedRevision;
        ControlOperationStatus? resultStatus = null;
        IsLightingOperationInProgress = true;
        try
        {
            await RunOperationAsync(
                cancellationToken => serviceClient.SetLightingOverrideAsync(
                    new ManualOverride<LightingSettings>(
                        requested,
                        OverrideLifetime.UntilPowerSourceChange),
                    cancellationToken),
                token,
                result =>
                {
                    resultStatus = result.Status;
                    if (result.Status is ControlOperationStatus.Applied
                        or ControlOperationStatus.Stored)
                    {
                        MarkActiveProfileDraft(sourceAtStart, lighting: requested);
                    }
                });
        }
        finally
        {
            IsLightingOperationInProgress = false;
        }

        if (resultStatus is ControlOperationStatus.Applied
            or ControlOperationStatus.Stored)
        {
            await dispatcher.InvokeAsync(
                () =>
                {
                    if (LightingDraft != requested)
                    {
                        return;
                    }
                    SetLightingEditorDirty(false);
                    if (resultStatus == ControlOperationStatus.Applied
                        && latestStatus is not null
                        && latestAcceptedRevision > revisionAtStart)
                    {
                        LightingSettings? confirmedLighting =
                            latestStatus.ConfirmedState?.Lighting;
                        bool statusReflectsRequest = confirmedLighting is not null
                            || latestStatus.LightingOverride?.Value == requested
                            || latestStatus.DesiredState.Lighting.Value == requested;
                        if (statusReflectsRequest)
                        {
                            LoadLightingEditor(
                                confirmedLighting
                                ?? latestStatus.DesiredState.Lighting.Value);
                        }
                    }
                },
                token);
        }
    }

    private async Task SetChargeLimitAsync(object? parameter, CancellationToken token)
    {
        int requestedLimit = (int)parameter!;
        long revisionAtStart = latestAcceptedRevision;
        ControlOperationStatus? resultStatus = null;
        IsChargeLimitOperationInProgress = true;
        try
        {
            await RunOperationAsync(
                cancellationToken => serviceClient.SetBatteryPolicyAsync(
                    BatteryPolicy.Create(requestedLimit),
                    cancellationToken),
                token,
                result => resultStatus = result.Status);
        }
        finally
        {
            IsChargeLimitOperationInProgress = false;
        }

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

    private async Task ToggleBatteryProtectionAsync(object? parameter, CancellationToken token)
    {
        IsBatteryProtectionOperationInProgress = true;
        try
        {
            await RunOperationAsync(
                cancellationToken => serviceClient.SetBatteryProtectionAsync(
                    !BatteryProtectionEnabled,
                    cancellationToken),
                token);
        }
        finally
        {
            IsBatteryProtectionOperationInProgress = false;
        }
    }

    private async Task ToggleFullChargeAsync(object? parameter, CancellationToken token)
    {
        IsFullChargeOperationInProgress = true;
        try
        {
            await RunOperationAsync(
                cancellationToken => serviceClient.SetFullChargeOverrideAsync(
                    parameter is bool enabled ? enabled : !FullChargeOverride,
                    cancellationToken),
                token);
        }
        finally
        {
            IsFullChargeOperationInProgress = false;
        }
    }

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
        bool failed;
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
            failed = notCleared.Count > 0;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            message = "Manual override clear requests completed; service confirmation is unavailable.";
            failed = true;
        }

        await dispatcher.InvokeAsync(
            () =>
            {
                OperationStatusMessage = message;
                TroubleshootingRecentErrorStatus = failed
                    ? FormatTroubleshootingValue(message)
                    : string.Empty;
            },
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

    private async Task ApplyOneShotLaptopControlAsync(
        object? parameter,
        CancellationToken token)
    {
        ApplyOneShotLaptopControl requested = (ApplyOneShotLaptopControl)parameter!;
        bool? previousStartupAnimation = StartupAnimationEnabled;
        FunctionKeyPrimaryMode? previousFunctionKeyPrimary = FunctionKeyPrimary;
        bool? previousGamingMode = GamingModeEnabled;
        SetOneShotOperationInProgress(requested.Control, true);
        ApplyOneShotLaptopControlOptimistically(requested);
        try
        {
            bool accepted = await RunOperationAsync(
                cancellationToken => serviceClient.ApplyOneShotLaptopControlAsync(
                    requested,
                    cancellationToken),
                token);
            if (!accepted)
            {
                RestoreOneShotLaptopControlPresentation(
                    requested.Control,
                    previousStartupAnimation,
                    previousFunctionKeyPrimary,
                    previousGamingMode);
            }
            else if (ActivePowerSource is PowerSource.PluggedIn
                or PowerSource.Battery
                or PowerSource.UsbC)
            {
                MarkActiveProfileDraft(
                    ActivePowerSource,
                    laptopBehavior: new LaptopBehaviorProfileSettings(
                        StartupAnimationEnabled,
                        FunctionKeyPrimary,
                        GamingModeEnabled)
                    {
                        GamingModeKeyPolicy = GetPresentedProfile(ActivePowerSource)
                            .EffectiveLaptopBehavior
                            .GamingModeKeyPolicy,
                    },
                    previousLaptopBehavior: new LaptopBehaviorProfileSettings(
                        previousStartupAnimation,
                        previousFunctionKeyPrimary,
                        previousGamingMode)
                    {
                        GamingModeKeyPolicy = GetProfile(ActivePowerSource)
                            .EffectiveLaptopBehavior
                            .GamingModeKeyPolicy,
                    });
            }
        }
        finally
        {
            SetOneShotOperationInProgress(requested.Control, false);
        }
    }

    private void ApplyOneShotLaptopControlOptimistically(ApplyOneShotLaptopControl requested)
    {
        switch (requested.Control)
        {
            case OneShotLaptopControl.StartupAnimation when requested.Enabled is { } startupEnabled:
                StartupAnimationEnabled = startupEnabled;
                break;
            case OneShotLaptopControl.FunctionKeyPrimary when requested.FunctionKeyMode is { } functionKeyMode:
                FunctionKeyPrimary = functionKeyMode;
                break;
            case OneShotLaptopControl.GamingMode when requested.Enabled is { } gamingEnabled:
                GamingModeEnabled = gamingEnabled;
                break;
        }
    }

    private void RestoreOneShotLaptopControlPresentation(
        OneShotLaptopControl control,
        bool? previousStartupAnimation,
        FunctionKeyPrimaryMode? previousFunctionKeyPrimary,
        bool? previousGamingMode)
    {
        switch (control)
        {
            case OneShotLaptopControl.StartupAnimation:
                StartupAnimationEnabled = previousStartupAnimation;
                break;
            case OneShotLaptopControl.FunctionKeyPrimary:
                FunctionKeyPrimary = previousFunctionKeyPrimary;
                break;
            case OneShotLaptopControl.GamingMode:
                GamingModeEnabled = previousGamingMode;
                break;
        }
    }

    private void SetOneShotOperationInProgress(OneShotLaptopControl control, bool value)
    {
        switch (control)
        {
            case OneShotLaptopControl.StartupAnimation:
                isStartupAnimationOperationInProgress = value;
                break;
            case OneShotLaptopControl.FunctionKeyPrimary:
                isFunctionKeyOperationInProgress = value;
                break;
            case OneShotLaptopControl.GamingMode:
                isGamingModeOperationInProgress = value;
                break;
            default:
                return;
        }

        NotifyFirmwareFieldLoadingState();
        NotifyCommands();
    }

    private async Task SaveProfilesAsync(object? parameter, CancellationToken token)
    {
        bool savingActiveDraft = HasActiveProfileDraft;
        ProfileSet? candidate = savingActiveDraft
            ? CreateProfileSetWithActiveDraft()
            : parameter as ProfileSet;
        if (!TryGetSupportedProfileDraft(candidate, out ProfileSet draft))
        {
            return;
        }

        bool accepted = await RunOperationAsync(
            cancellationToken => serviceClient.UpdateProfilesAsync(draft, cancellationToken),
            token);
        if (accepted)
        {
            if (savingActiveDraft)
            {
                try
                {
                    await ClearActiveProfileHardwareOverridesAsync(token);
                }
                finally
                {
                    // The profile update is already durable in the service. Keep the
                    // UI in sync even if clearing an equivalent temporary override
                    // fails or the operation is canceled afterward.
                    Profiles = draft;
                    ClearActiveProfileDraft();
                }
            }
            else
            {
                Profiles = draft;
                SetProfileEditorsDirty(false);
                LoadProfileEditors(draft);
            }
        }
    }

    private async Task SaveLightingPowerPolicyAsync(
        object? parameter,
        CancellationToken token)
    {
        if (parameter is not LightingPowerPolicy policy)
        {
            return;
        }

        if (ActivePowerSource is not (
                PowerSource.PluggedIn or PowerSource.Battery or PowerSource.UsbC))
        {
            return;
        }

        await dispatcher.InvokeAsync(
            () =>
            {
                MarkActiveProfileDraft(
                    ActivePowerSource,
                    lightingPowerPolicy: policy);
                if (LightingPowerPolicyDraft == policy)
                {
                    LoadLightingPowerPolicyEditor(policy);
                }
            },
            token).ConfigureAwait(false);
    }

    private bool CanSaveLightingPowerPolicy(object? parameter) =>
        Volatile.Read(ref disposed) == 0
        && IsConnected
        && CanMutate
        && ActivePowerSource is (
            PowerSource.PluggedIn or PowerSource.Battery or PowerSource.UsbC)
        && lightingPowerPolicyEditorDirty
        && parameter is LightingPowerPolicy policy
        && policy.IdleTimeoutMinutes is null or >= 1 and <= 60;

    private bool CanSaveProfiles(object? parameter) =>
        CanMutate
        && (HasActiveProfileDraft
            ? TryGetSupportedProfileDraft(CreateProfileSetWithActiveDraft(), out _)
            : profileEditorsDirty && TryGetSupportedProfileDraft(parameter, out _));

    private bool CanSaveActiveProfileDraft(ProfileDraftGroup group) =>
        CanMutate
        && (group != ProfileDraftGroup.Lighting || CanControlLighting)
        && (HasActiveProfileDraftFor(group)
            && activeProfileDraftSource == ActivePowerSource
            && TryGetSupportedProfileDraft(CreateProfileSetWithActiveDraft(group), out _)
            || HasSaveableUnsetProfileField(group));

    private async Task SaveActiveProfileDraftAsync(
        ProfileDraftGroup group,
        CancellationToken token)
    {
        CaptureSaveableUnsetProfileFields(group);
        if (!CanSaveActiveProfileDraft(group)
            || !TryGetSupportedProfileDraft(
                CreateProfileSetWithActiveDraft(group),
                out ProfileSet draft))
        {
            return;
        }

        bool accepted = await RunOperationAsync(
            cancellationToken => serviceClient.UpdateProfilesAsync(draft, cancellationToken),
            token);
        if (!accepted)
        {
            return;
        }

        try
        {
            await ClearActiveProfileHardwareOverridesAsync(token, group);
        }
        finally
        {
            // The profile update is durable even when clearing an equivalent
            // temporary hardware override fails afterward.
            Profiles = draft;
            ClearActiveProfileDraft(group);
        }
    }

    private async Task DiscardActiveProfileDraftAsync(
        ProfileDraftGroup group,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!HasProfileDifferenceFor(group)
            || ActivePowerSource is not (
                PowerSource.PluggedIn or PowerSource.Battery or PowerSource.UsbC))
        {
            return;
        }

        PowerSource source = ActivePowerSource;

        bool restored;
        if (group == ProfileDraftGroup.Display)
        {
            DisplayProfileSettings savedDisplay =
                GetProfile(source).Display ?? DisplayProfileSettings.Unconfigured();
            DisplayProfileSettings originalDisplay =
                activeProfileOriginalDisplay ?? DisplayProfileSettings.Unconfigured();
            DisplayProfileSettings displayToRestore = new(
                savedDisplay.RefreshRateKey ?? originalDisplay.RefreshRateKey,
                savedDisplay.HdrEnabled ?? originalDisplay.HdrEnabled,
                savedDisplay.ColorProfileKey ?? originalDisplay.ColorProfileKey);
            restored = await RestoreSavedDisplayProfileAsync(
                source,
                displayToRestore,
                token);
        }
        else if (group == ProfileDraftGroup.Laptop)
        {
            bool controlsRestored = !HasThermalProfileDifference
                || await ClearActiveProfileHardwareOverridesAsync(
                    token,
                    group,
                    force: true);
            LaptopBehaviorProfileSettings saved = GetProfile(source).EffectiveLaptopBehavior;
            LaptopBehaviorProfileSettings original = activeProfileOriginalLaptopBehavior
                ?? LaptopBehaviorProfileSettings.Unconfigured();
            var behaviorToRestore = new LaptopBehaviorProfileSettings(
                saved.StartupAnimationEnabled ?? original.StartupAnimationEnabled,
                saved.FunctionKeyPrimary ?? original.FunctionKeyPrimary,
                saved.GamingModeEnabled ?? original.GamingModeEnabled)
            {
                GamingModeKeyPolicy =
                    saved.GamingModeKeyPolicy ?? original.GamingModeKeyPolicy,
            };
            bool behaviorRestored = !HasLaptopBehaviorProfileDifference
                || await RestoreSavedLaptopBehaviorAsync(
                    source,
                    behaviorToRestore,
                    token);
            restored = controlsRestored && behaviorRestored;
        }
        else if (group == ProfileDraftGroup.Lighting)
        {
            restored = !HasLightingHardwareProfileDifference
                || await ClearActiveProfileHardwareOverridesAsync(
                    token,
                    group,
                    force: true);
        }
        else
        {
            restored = await ClearActiveProfileHardwareOverridesAsync(
                token,
                group,
                force: true);
        }

        if (restored && ActivePowerSource == source)
        {
            ClearActiveProfileDraft(group);
            if (group == ProfileDraftGroup.Lighting)
            {
                LightingSettings savedLighting = GetProfile(source).Lighting;
                LoadLightingEditor(
                    savedLighting.LogoMode is null
                        ? savedLighting with { LogoMode = LogoLightingModeEditor }
                        : savedLighting);
                LoadLightingPowerPolicyEditor(
                    GetProfile(source).EffectiveLightingPowerPolicy);
            }
        }
        else if (ActivePowerSource != source)
        {
            await ReportProfileRestoreSourceChangedAsync(token);
        }
    }

    private async Task DiscardProfileDraftAsync(object? parameter, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (activeProfileDraftSource is not { } source)
        {
            return;
        }

        if (ActivePowerSource != source)
        {
            await ReportProfileRestoreSourceChangedAsync(token);
            return;
        }

        DisplayProfileSettings savedDisplay =
            GetProfile(source).Display ?? DisplayProfileSettings.Unconfigured();
        DisplayProfileSettings originalDisplay =
            activeProfileOriginalDisplay ?? DisplayProfileSettings.Unconfigured();
        DisplayProfileSettings displayToRestore = new(
            savedDisplay.RefreshRateKey ?? originalDisplay.RefreshRateKey,
            savedDisplay.HdrEnabled ?? originalDisplay.HdrEnabled,
            savedDisplay.ColorProfileKey ?? originalDisplay.ColorProfileKey);
        bool controlsRestored = await ClearActiveProfileHardwareOverridesAsync(token);
        bool displayRestored = activeProfileDraftDisplay is null
            || await RestoreSavedDisplayProfileAsync(source, displayToRestore, token);
        LaptopBehaviorProfileSettings savedLaptop = GetProfile(source).EffectiveLaptopBehavior;
        LaptopBehaviorProfileSettings originalLaptop = activeProfileOriginalLaptopBehavior
            ?? LaptopBehaviorProfileSettings.Unconfigured();
        bool laptopRestored = activeProfileDraftLaptopBehavior is null
            || await RestoreSavedLaptopBehaviorAsync(
                source,
                new LaptopBehaviorProfileSettings(
                    savedLaptop.StartupAnimationEnabled
                        ?? originalLaptop.StartupAnimationEnabled,
                    savedLaptop.FunctionKeyPrimary ?? originalLaptop.FunctionKeyPrimary,
                    savedLaptop.GamingModeEnabled ?? originalLaptop.GamingModeEnabled)
                {
                    GamingModeKeyPolicy =
                        savedLaptop.GamingModeKeyPolicy
                        ?? originalLaptop.GamingModeKeyPolicy,
                },
                token);
        if (controlsRestored
            && displayRestored
            && laptopRestored
            && ActivePowerSource == source)
        {
            ClearActiveProfileDraft();
            LoadLightingPowerPolicyEditor(
                GetProfile(source).EffectiveLightingPowerPolicy);
        }
        else if (ActivePowerSource != source)
        {
            await ReportProfileRestoreSourceChangedAsync(token);
        }
    }

    private async Task<bool> ClearActiveProfileHardwareOverridesAsync(
        CancellationToken token,
        ProfileDraftGroup? group = null,
        bool force = false)
    {
        bool restored = true;
        if ((group is null or ProfileDraftGroup.Thermal or ProfileDraftGroup.Laptop)
            && (force || activeProfileDraftPerformance is not null))
        {
            restored &= await RunOperationAsync(
                cancellationToken => serviceClient.SetPerformanceOverrideAsync(
                    null,
                    cancellationToken),
                token);
        }
        if ((group is null or ProfileDraftGroup.Thermal or ProfileDraftGroup.Laptop)
            && (force || activeProfileDraftFans is not null))
        {
            restored &= await RunOperationAsync(
                cancellationToken => serviceClient.SetFanOverrideAsync(
                    null,
                    cancellationToken),
                token);
        }
        if ((group is null or ProfileDraftGroup.Lighting)
            && (force || activeProfileDraftLighting is not null))
        {
            restored &= await RunOperationAsync(
                cancellationToken => serviceClient.SetLightingOverrideAsync(
                    null,
                    cancellationToken),
                token);
        }
        return restored;
    }

    private async Task<bool> RestoreSavedDisplayProfileAsync(
        PowerSource expectedPowerSource,
        DisplayProfileSettings settings,
        CancellationToken token)
    {
        Delegate[] handlers = SavedDisplayProfileRestoreRequested?.GetInvocationList() ?? [];
        if (handlers.Length == 0)
        {
            await dispatcher.InvokeAsync(
                () => OperationStatusMessage =
                    "Could not restore the saved display profile because the display controller is unavailable.",
                token);
            return false;
        }

        try
        {
            foreach (Delegate handler in handlers)
            {
                if (ActivePowerSource != expectedPowerSource)
                {
                    await ReportProfileRestoreSourceChangedAsync(token);
                    return false;
                }

                bool restored = await ((Func<PowerSource, DisplayProfileSettings, CancellationToken, Task<bool>>)handler)(
                    expectedPowerSource,
                    settings,
                    token);
                if (!restored)
                {
                    await dispatcher.InvokeAsync(
                        () => OperationStatusMessage =
                            "Could not restore the saved display profile because Windows did not confirm every setting.",
                        token);
                    return false;
                }
            }
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            string message = $"Could not restore the saved display profile: {exception.Message}";
            await dispatcher.InvokeAsync(
                () =>
                {
                    OperationStatusMessage = message;
                    TroubleshootingRecentErrorStatus = FormatTroubleshootingValue(message);
                },
                token);
            return false;
        }
    }

    private Task ReportProfileRestoreSourceChangedAsync(CancellationToken token) =>
        dispatcher.InvokeAsync(
            () => OperationStatusMessage =
                "Could not restore the saved profile settings because the power source changed.",
            token);

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

            candidate = CreateLegacyProfileEditorDraft();
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
        && (draft.Performance != PerformancePreset.Custom
            || CustomPerformanceSettings.IsWellFormed(draft.CustomPerformance))
        && (draft.CustomPerformance is null
            || CustomPerformanceSettings.IsWellFormed(draft.CustomPerformance))
        && ThermalCombinationSupported(draft.Performance, draft.Fans)
        && (LightingSupported(draft.Lighting, capabilities)
            || draft.Lighting == current.Lighting);

    private static bool ThermalCombinationSupported(
        PerformancePreset performance,
        FanMode fans) =>
        fans == FanMode.Automatic
        || (performance == PerformancePreset.Balanced
            && fans is FanMode.Fixed or FanMode.ManualCurve or FanMode.Maximum);

    private static bool FanModeAvailable(PowerSource powerSource, FanMode fans) =>
        powerSource switch
        {
            PowerSource.PluggedIn => fans is FanMode.Automatic
                or FanMode.Fixed
                or FanMode.ManualCurve
                or FanMode.Maximum,
            PowerSource.Battery or PowerSource.UsbC => fans == FanMode.Automatic,
            PowerSource.ExternalUnknown => false,
            _ => false,
        };

    private static bool LightingSupported(
        LightingSettings lighting,
        DeviceCapabilities capabilities)
    {
        if (!Enum.IsDefined(lighting.Effect)
            || !capabilities.LightingEffects.Contains(lighting.Effect)
            || !LightingSettingsValidation.IsWellFormed(lighting))
        {
            return false;
        }

        return true;
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
                    TroubleshootingRecentErrorStatus = result.Status == ControlOperationStatus.Failed
                        ? FormatTroubleshootingValue(result.Message)
                        : string.Empty;
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
            string message = $"Operation failed: {exception.Message}";
            await dispatcher.InvokeAsync(
                () =>
                {
                    OperationStatusMessage = message;
                    TroubleshootingRecentErrorStatus = FormatTroubleshootingValue(message);
                },
                token);
            return false;
        }
    }

    private async Task ExportDiagnosticsAsync(object? parameter, CancellationToken token)
    {
        try
        {
            await dispatcher.InvokeAsync(
                () =>
                {
                    OperationStatusMessage = "Preparing diagnostic export...";
                    TroubleshootingRecentErrorStatus = string.Empty;
                },
                token);
            DiagnosticArchive archive = await serviceClient.ExportDiagnosticsAsync(
                LiveReadingsEnabled,
                token);
            DiagnosticArchiveSaveResult result = await diagnosticArchiveSaver.SaveAsync(
                archive,
                token);
            await dispatcher.InvokeAsync(
                () =>
                {
                    OperationStatusMessage = result.Status switch
                    {
                        DiagnosticArchiveSaveStatus.Canceled => "Diagnostic export canceled.",
                        DiagnosticArchiveSaveStatus.Saved
                            when !string.IsNullOrWhiteSpace(result.DestinationPath) =>
                            $"Diagnostics exported to {result.DestinationPath}.",
                        _ => throw new InvalidDataException(
                            "The diagnostic saver returned an invalid result."),
                    };
                    TroubleshootingRecentErrorStatus = string.Empty;
                },
                token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            string message = $"Diagnostic export failed: {exception.Message}";
            await dispatcher.InvokeAsync(
                () =>
                {
                    OperationStatusMessage = message;
                    TroubleshootingRecentErrorStatus = FormatTroubleshootingValue(message);
                },
                token);
        }
    }

    private void LoadLightingEditor(LightingSettings value)
    {
        int brightness = value.Effect == LightingEffect.Off
            ? 0
            : value.Brightness == 0
                ? DefaultLightingBrightness
                : value.Brightness;
        SetField(ref lightingEffectEditor, value.Effect, nameof(LightingEffectEditor));
        SetField(ref lightingBrightnessEditor, brightness, nameof(LightingBrightnessEditor));
        SetField(ref lightingRedEditor, (int)value.Color.Red, nameof(LightingRedEditor));
        SetField(ref lightingGreenEditor, (int)value.Color.Green, nameof(LightingGreenEditor));
        SetField(ref lightingBlueEditor, (int)value.Color.Blue, nameof(LightingBlueEditor));
        RgbColor secondaryColor = value.Parameters switch
        {
            BreathingLightingParameters { SecondaryColor: { } color } => color,
            TidalLightingParameters tidal => tidal.SecondaryColor,
            StarlightLightingParameters { SecondaryColor: { } color } => color,
            _ => value.Color,
        };
        SetField(
            ref lightingSecondaryRedEditor,
            (int)secondaryColor.Red,
            nameof(LightingSecondaryRedEditor));
        SetField(
            ref lightingSecondaryGreenEditor,
            (int)secondaryColor.Green,
            nameof(LightingSecondaryGreenEditor));
        SetField(
            ref lightingSecondaryBlueEditor,
            (int)secondaryColor.Blue,
            nameof(LightingSecondaryBlueEditor));
        Notify(nameof(LightingPrimaryColorEditor));
        Notify(nameof(LightingSecondaryColorEditor));
        BreathingColorMode breathingColorMode =
            value.Parameters is BreathingLightingParameters breathing
                ? breathing.ColorMode
                : BreathingColorMode.Single;
        SetField(
            ref lightingBreathingColorModeEditor,
            breathingColorMode,
            nameof(LightingBreathingColorModeEditor));
        ReactiveLightingDuration reactiveDuration =
            value.Parameters is ReactiveLightingParameters reactive
                ? reactive.Duration
                : ReactiveLightingDuration.Medium;
        SetField(
            ref lightingReactiveDurationEditor,
            reactiveDuration,
            nameof(LightingReactiveDurationEditor));
        StarlightColorMode starlightColorMode =
            value.Parameters is StarlightLightingParameters starlight
                ? starlight.ColorMode
                : StarlightColorMode.Single;
        SetField(
            ref lightingStarlightColorModeEditor,
            starlightColorMode,
            nameof(LightingStarlightColorModeEditor));
        StarlightLightingDuration starlightDuration =
            value.Parameters is StarlightLightingParameters starlightParameters
                ? starlightParameters.Duration
                : StarlightLightingDuration.Medium;
        SetField(
            ref lightingStarlightDurationEditor,
            starlightDuration,
            nameof(LightingStarlightDurationEditor));
        bool randomColors = value.Parameters is TidalLightingParameters
        {
            RandomColors: true,
        };
        SetField(
            ref lightingRandomColorsEditor,
            randomColors,
            nameof(LightingRandomColorsEditor));
        LightingDirection direction = value.Parameters switch
        {
            TidalLightingParameters tidalParameters => tidalParameters.Direction,
            WaveLightingParameters waveParameters => waveParameters.Direction,
            _ => LightingDirection.Rightward,
        };
        SetField(
            ref lightingDirectionEditor,
            direction,
            nameof(LightingDirectionEditor));
        LightingRotationDirection rotationDirection =
            value.Parameters is WheelLightingParameters wheelParameters
                ? wheelParameters.Direction
                : LightingRotationDirection.Clockwise;
        SetField(
            ref lightingRotationDirectionEditor,
            rotationDirection,
            nameof(LightingRotationDirectionEditor));
        AmbientAwarenessRegion ambientAwarenessRegion =
            value.Parameters is AmbientAwarenessLightingParameters ambientAwareness
                ? ambientAwareness.Region
                : AmbientAwarenessRegion.Full;
        SetField(
            ref lightingAmbientAwarenessRegionEditor,
            ambientAwarenessRegion,
            nameof(LightingAmbientAwarenessRegionEditor));
        double audioMeterColorBoost =
            value.Parameters is AudioMeterLightingParameters audioMeter
                ? audioMeter.ColorBoost
                : 1;
        SetField(
            ref lightingAudioMeterColorBoostEditor,
            audioMeterColorBoost,
            nameof(LightingAudioMeterColorBoostEditor));
        int audioMeterDecayMilliseconds =
            value.Parameters is AudioMeterLightingParameters audioMeterDecay
                ? audioMeterDecay.DecayMilliseconds
                : LightingSettingsFactory.DefaultAudioMeterDecayMilliseconds;
        SetField(
            ref lightingAudioMeterDecayMillisecondsEditor,
            audioMeterDecayMilliseconds,
            nameof(LightingAudioMeterDecayMillisecondsEditor));
        Notify(nameof(LightingAudioMeterDecayValue));
        SetField(
            ref logoLightingModeEditor,
            value.LogoMode,
            nameof(LogoLightingModeEditor));
        NotifyLightingParameterPresentation();
        Notify(nameof(SelectedLightingEffectOption));
        Notify(nameof(SelectedLogoLightingModeOption));
        Notify(nameof(SelectedAmbientAwarenessRegionOption));
        Notify(nameof(LightingDraft));
    }

    private void SetKeyboardEffectUnconfirmed(bool value)
    {
        if (keyboardEffectUnconfirmed == value)
        {
            return;
        }

        keyboardEffectUnconfirmed = value;
        Notify(nameof(IsKeyboardEffectUnconfirmed));
        Notify(nameof(KeyboardEffectDescription));
        Notify(nameof(SelectedLightingEffectOption));
    }

    private void LoadLightingPowerPolicyEditor(LightingPowerPolicy policy)
    {
        SetField(
            ref lightingTurnOffWithDisplayEditor,
            policy.TurnOffWhenDisplayTurnsOff,
            nameof(LightingTurnOffWithDisplayEditor));
        SetField(
            ref lightingIdleTimeoutEnabledEditor,
            policy.IdleTimeoutMinutes.HasValue,
            nameof(LightingIdleTimeoutEnabledEditor));
        SetField(
            ref lightingIdleTimeoutMinutesEditor,
            policy.IdleTimeoutMinutes ?? 30,
            nameof(LightingIdleTimeoutMinutesEditor));
        lightingPowerPolicyEditorDirty = false;
        Notify(nameof(HasUnsavedLightingPowerPolicy));
        Notify(nameof(LightingPowerPolicyDraft));
        NotifyCommands();
    }

    private void PrepareLightingEditorForPowerSource(PowerSource powerSource)
    {
        if (powerSource is not (PowerSource.PluggedIn or PowerSource.Battery or PowerSource.UsbC))
        {
            return;
        }

        if (lastConfirmedLightingEditorPowerSource is { } previousPowerSource
            && previousPowerSource != powerSource)
        {
            SetLightingEditorDirty(false);
        }

        lastConfirmedLightingEditorPowerSource = powerSource;
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
        Notify(nameof(PluggedInPerformanceOption));
        Notify(nameof(BatteryPerformanceOption));
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
        Notify(nameof(UsbCPerformanceOption));
        SetField(
            ref profileChargeLimitEditor,
            value.BatteryPolicy.ChargeLimitPercent,
            nameof(ProfileChargeLimitEditor));
        SetProfileEditorsDirty(false);
        NotifyCommands();
    }

    private PowerProfile GetProfile(PowerSource source) => source switch
    {
        PowerSource.PluggedIn => Profiles.PluggedIn,
        PowerSource.Battery => Profiles.Battery,
        PowerSource.UsbC => Profiles.UsbC,
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    private CustomPerformanceSettings GetActiveProfileCustomPerformance() =>
        ActivePowerSource is PowerSource.PluggedIn
            ? GetPresentedProfile(PowerSource.PluggedIn).CustomPerformance
                ?? CustomPerformanceSettings.Default
            : CustomPerformanceSettings.Default;

    private GpuClockTuningSettings? GetActiveProfileGpuClockTuning()
    {
        if (ActivePowerSource is not PowerSource.PluggedIn)
        {
            return null;
        }

        PowerProfile profile = GetPresentedProfile(ActivePowerSource);
        if (profile.GpuClockTuning is not null)
        {
            return profile.GpuClockTuning;
        }

        return profile.Performance == PerformancePreset.Custom
            && profile.CustomPerformance?.GpuClockOffsets is { } legacyOffsets
                ? new GpuClockTuningSettings(Enabled: true, legacyOffsets)
                : null;
    }

    private PowerProfile GetPresentedProfile(PowerSource source) =>
        activeProfileDraftSource == source
            ? GetActiveProfileDraft() ?? GetProfile(source)
            : GetProfile(source);

    private PowerProfile? GetActiveProfileDraft()
    {
        if (activeProfileDraftSource is not { } source)
        {
            return null;
        }

        PowerProfile profile = GetProfile(source);
        return profile with
        {
            Performance = activeProfileDraftPerformance ?? profile.Performance,
            CustomPerformance = activeProfileDraftCustomPerformance
                ?? profile.CustomPerformance,
            GpuClockTuning = activeProfileDraftGpuClockTuning
                ?? profile.GpuClockTuning,
            Fans = activeProfileDraftFans ?? profile.Fans,
            FanControl = activeProfileDraftFanControl ?? profile.FanControl,
            Lighting = activeProfileDraftLighting ?? profile.Lighting,
            LightingPowerPolicy = activeProfileDraftLightingPowerPolicy
                ?? profile.LightingPowerPolicy,
            Display = activeProfileDraftDisplay ?? profile.Display,
            LaptopBehavior = activeProfileDraftLaptopBehavior
                ?? profile.LaptopBehavior,
        };
    }

    private ProfileSet CreateProfileSetWithActiveDraft()
    {
        PowerSource source = activeProfileDraftSource
            ?? throw new InvalidOperationException("No active profile draft exists.");
        PowerProfile draft = GetActiveProfileDraft()
            ?? throw new InvalidOperationException("No active profile draft exists.");
        return source switch
        {
            PowerSource.PluggedIn => Profiles with { PluggedIn = draft },
            PowerSource.Battery => Profiles with { Battery = draft },
            PowerSource.UsbC => Profiles with { UsbC = draft },
            _ => throw new ArgumentOutOfRangeException(nameof(source)),
        };
    }

    private ProfileSet CreateProfileSetWithActiveDraft(ProfileDraftGroup group)
    {
        PowerSource source = activeProfileDraftSource
            ?? throw new InvalidOperationException("No active profile draft exists.");
        PowerProfile saved = GetProfile(source);
        PowerProfile draft = group switch
        {
            ProfileDraftGroup.Thermal => saved with
            {
                Performance = activeProfileDraftPerformance ?? saved.Performance,
                CustomPerformance = activeProfileDraftCustomPerformance
                    ?? saved.CustomPerformance,
                GpuClockTuning = activeProfileDraftGpuClockTuning
                    ?? saved.GpuClockTuning,
                Fans = activeProfileDraftFans ?? saved.Fans,
                FanControl = activeProfileDraftFanControl ?? saved.FanControl,
            },
            ProfileDraftGroup.Lighting => saved with
            {
                Lighting = activeProfileDraftLighting ?? saved.Lighting,
                LightingPowerPolicy = activeProfileDraftLightingPowerPolicy
                    ?? saved.LightingPowerPolicy,
            },
            ProfileDraftGroup.Display => saved with
            {
                Display = activeProfileDraftDisplay ?? saved.Display,
            },
            ProfileDraftGroup.Laptop => saved with
            {
                Performance = activeProfileDraftPerformance ?? saved.Performance,
                CustomPerformance = activeProfileDraftCustomPerformance
                    ?? saved.CustomPerformance,
                GpuClockTuning = activeProfileDraftGpuClockTuning
                    ?? saved.GpuClockTuning,
                Fans = activeProfileDraftFans ?? saved.Fans,
                FanControl = activeProfileDraftFanControl ?? saved.FanControl,
                LaptopBehavior = activeProfileDraftLaptopBehavior
                    ?? saved.LaptopBehavior,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(group)),
        };
        return source switch
        {
            PowerSource.PluggedIn => Profiles with { PluggedIn = draft },
            PowerSource.Battery => Profiles with { Battery = draft },
            PowerSource.UsbC => Profiles with { UsbC = draft },
            _ => throw new ArgumentOutOfRangeException(nameof(source)),
        };
    }

    private ProfileSet CreateLegacyProfileEditorDraft() => new(
        Profiles.PluggedIn with
        {
            Performance = PluggedInPerformanceEditor,
            CustomPerformance = Profiles.PluggedIn.CustomPerformance
                ?? CustomPerformanceSettings.Default,
            Fans = PluggedInFanModeEditor,
            Lighting = PluggedInLightingEditor,
        },
        Profiles.Battery with
        {
            Performance = BatteryPerformanceEditor,
            Fans = BatteryFanModeEditor,
            Lighting = BatteryLightingEditor,
        },
        Profiles.UsbC with
        {
            Performance = UsbCPerformanceEditor,
            Fans = UsbCFanModeEditor,
            Lighting = UsbCLightingEditor,
        },
        BatteryPolicy.Create(ProfileChargeLimitEditor));

    private void MarkActiveProfileDraft(
        PowerSource source,
        PerformancePreset? performance = null,
        CustomPerformanceSettings? customPerformance = null,
        GpuClockTuningSettings? gpuClockTuning = null,
        FanMode? fans = null,
        FanControlSettings? fanControl = null,
        LightingSettings? lighting = null,
        LightingPowerPolicy? lightingPowerPolicy = null,
        DisplayProfileSettings? display = null,
        DisplayProfileSettings? previousDisplay = null,
        LaptopBehaviorProfileSettings? laptopBehavior = null,
        LaptopBehaviorProfileSettings? previousLaptopBehavior = null)
    {
        if (source != ActivePowerSource
            || source is not (PowerSource.PluggedIn or PowerSource.Battery or PowerSource.UsbC))
        {
            return;
        }

        if (activeProfileDraftSource != source)
        {
            activeProfileDraftSource = source;
            activeProfileDraftPerformance = null;
            activeProfileDraftCustomPerformance = null;
            activeProfileDraftGpuClockTuning = null;
            activeProfileDraftFans = null;
            activeProfileDraftFanControl = null;
            activeProfileDraftLighting = null;
            activeProfileDraftLightingPowerPolicy = null;
            activeProfileDraftDisplay = null;
            activeProfileDraftLaptopBehavior = null;
            activeProfileOriginalDisplay = previousDisplay;
            activeProfileOriginalLaptopBehavior = previousLaptopBehavior;
        }
        else if (display is not null && activeProfileDraftDisplay is null)
        {
            activeProfileOriginalDisplay = previousDisplay;
        }
        else if (laptopBehavior is not null
            && activeProfileDraftLaptopBehavior is null)
        {
            activeProfileOriginalLaptopBehavior = previousLaptopBehavior;
        }

        activeProfileDraftPerformance = performance ?? activeProfileDraftPerformance;
        activeProfileDraftCustomPerformance = customPerformance
            ?? activeProfileDraftCustomPerformance;
        activeProfileDraftGpuClockTuning = gpuClockTuning
            ?? activeProfileDraftGpuClockTuning;
        activeProfileDraftFans = fans ?? activeProfileDraftFans;
        activeProfileDraftFanControl = fanControl ?? activeProfileDraftFanControl;
        activeProfileDraftLighting = lighting ?? activeProfileDraftLighting;
        activeProfileDraftLightingPowerPolicy = lightingPowerPolicy
            ?? activeProfileDraftLightingPowerPolicy;
        activeProfileDraftDisplay = display ?? activeProfileDraftDisplay;
        activeProfileDraftLaptopBehavior = laptopBehavior
            ?? activeProfileDraftLaptopBehavior;

        PowerProfile saved = GetProfile(source);
        if (activeProfileDraftPerformance == saved.Performance)
        {
            activeProfileDraftPerformance = null;
        }
        if (activeProfileDraftCustomPerformance == saved.CustomPerformance)
        {
            activeProfileDraftCustomPerformance = null;
        }
        if (activeProfileDraftGpuClockTuning == saved.GpuClockTuning)
        {
            activeProfileDraftGpuClockTuning = null;
        }
        if (activeProfileDraftFans == saved.Fans)
        {
            activeProfileDraftFans = null;
        }
        if (activeProfileDraftFanControl == saved.FanControl)
        {
            activeProfileDraftFanControl = null;
        }
        if (activeProfileDraftLighting == saved.Lighting)
        {
            activeProfileDraftLighting = null;
        }
        if (activeProfileDraftLightingPowerPolicy
            == saved.EffectiveLightingPowerPolicy)
        {
            activeProfileDraftLightingPowerPolicy = null;
        }
        DisplayProfileSettings savedDisplay =
            saved.Display ?? DisplayProfileSettings.Unconfigured();
        DisplayProfileSettings displayBaseline = savedDisplay.IsConfigured
            ? savedDisplay
            : activeProfileOriginalDisplay ?? savedDisplay;
        if (activeProfileDraftDisplay == displayBaseline)
        {
            activeProfileDraftDisplay = null;
            activeProfileOriginalDisplay = null;
        }
        LaptopBehaviorProfileSettings savedLaptopBehavior =
            saved.EffectiveLaptopBehavior;
        LaptopBehaviorProfileSettings laptopBehaviorBaseline =
            savedLaptopBehavior.IsConfigured
                ? savedLaptopBehavior
                : activeProfileOriginalLaptopBehavior ?? savedLaptopBehavior;
        if (activeProfileDraftLaptopBehavior == laptopBehaviorBaseline)
        {
            activeProfileDraftLaptopBehavior = null;
            activeProfileOriginalLaptopBehavior = null;
        }
        if (activeProfileDraftPerformance is null
            && activeProfileDraftCustomPerformance is null
            && activeProfileDraftGpuClockTuning is null
            && activeProfileDraftFans is null
            && activeProfileDraftFanControl is null
            && activeProfileDraftLighting is null
            && activeProfileDraftLightingPowerPolicy is null
            && activeProfileDraftDisplay is null
            && activeProfileDraftLaptopBehavior is null)
        {
            activeProfileDraftSource = null;
        }

        NotifyActiveProfileDraft();
    }

    private void ClearActiveProfileDraft()
    {
        if (!HasActiveProfileDraft)
        {
            return;
        }

        activeProfileDraftSource = null;
        activeProfileDraftPerformance = null;
        activeProfileDraftCustomPerformance = null;
        activeProfileDraftGpuClockTuning = null;
        activeProfileDraftFans = null;
        activeProfileDraftFanControl = null;
        activeProfileDraftLighting = null;
        activeProfileDraftLightingPowerPolicy = null;
        activeProfileDraftDisplay = null;
        activeProfileDraftLaptopBehavior = null;
        activeProfileOriginalDisplay = null;
        activeProfileOriginalLaptopBehavior = null;
        NotifyActiveProfileDraft();
    }

    private void ClearActiveProfileDraft(ProfileDraftGroup group)
    {
        switch (group)
        {
            case ProfileDraftGroup.Thermal:
                activeProfileDraftPerformance = null;
                activeProfileDraftCustomPerformance = null;
                activeProfileDraftGpuClockTuning = null;
                activeProfileDraftFans = null;
                activeProfileDraftFanControl = null;
                break;
            case ProfileDraftGroup.Lighting:
                activeProfileDraftLighting = null;
                activeProfileDraftLightingPowerPolicy = null;
                break;
            case ProfileDraftGroup.Display:
                activeProfileDraftDisplay = null;
                activeProfileOriginalDisplay = null;
                break;
            case ProfileDraftGroup.Laptop:
                activeProfileDraftPerformance = null;
                activeProfileDraftCustomPerformance = null;
                activeProfileDraftGpuClockTuning = null;
                activeProfileDraftFans = null;
                activeProfileDraftFanControl = null;
                activeProfileDraftLaptopBehavior = null;
                activeProfileOriginalLaptopBehavior = null;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(group));
        }

        if (!HasThermalProfileDraft
            && !HasLightingProfileDraft
            && !HasDisplayProfileDraft
            && !HasLaptopProfileDraft)
        {
            activeProfileDraftSource = null;
        }
        NotifyActiveProfileDraft();
    }

    private bool HasActiveProfileDraftFor(ProfileDraftGroup group) => group switch
    {
        ProfileDraftGroup.Thermal => HasThermalProfileDraft,
        ProfileDraftGroup.Lighting => HasLightingProfileDraft,
        ProfileDraftGroup.Display => HasDisplayProfileDraft,
        ProfileDraftGroup.Laptop => HasLaptopPageProfileDraft,
        _ => false,
    };

    private bool HasProfileDifferenceFor(ProfileDraftGroup group) => group switch
    {
        ProfileDraftGroup.Thermal => HasThermalProfileDifference,
        ProfileDraftGroup.Lighting => HasLightingProfileDifference,
        ProfileDraftGroup.Display => HasDisplayProfileDraft,
        ProfileDraftGroup.Laptop => HasLaptopPageProfileDifference,
        _ => false,
    };

    private bool HasThermalProfileDifferenceCore
    {
        get
        {
            PowerProfile saved = GetProfile(ActivePowerSource);
            PerformancePreset currentPerformance =
                activeProfileDraftPerformance ?? SelectedPerformance;
            if (saved.Performance != currentPerformance)
            {
                return true;
            }

            if (saved.Performance == PerformancePreset.Custom)
            {
                CustomPerformanceSettings savedCustom =
                    saved.CustomPerformance ?? CustomPerformanceSettings.Default;
                CustomPerformanceSettings currentCustom =
                    activeProfileDraftCustomPerformance
                    ?? new CustomPerformanceSettings(
                        SelectedCustomCpuLevel,
                        SelectedCustomGpuLevel);
                if (savedCustom.Cpu != currentCustom.Cpu
                    || savedCustom.Gpu != currentCustom.Gpu)
                {
                    return true;
                }
            }

            GpuClockTuningSettings? savedGpuClockTuning = saved.GpuClockTuning;
            if (savedGpuClockTuning is null
                && saved.Performance == PerformancePreset.Custom
                && saved.CustomPerformance?.GpuClockOffsets is { } legacyOffsets)
            {
                savedGpuClockTuning = new GpuClockTuningSettings(
                    Enabled: true,
                    legacyOffsets);
            }
            GpuClockTuningSettings? currentGpuClockTuning =
                activeProfileDraftGpuClockTuning
                ?? (gpuClockTuningConfigured
                    ? new GpuClockTuningSettings(
                        IsGpuClockTuningEnabled,
                        CurrentGpuClockOffsets)
                    : null);
            if (savedGpuClockTuning != currentGpuClockTuning)
            {
                return true;
            }

            FanControlSettings savedFans =
                saved.FanControl ?? FanControlSettings.FromLegacy(saved.Fans);
            FanControlSettings currentFans = activeProfileDraftFanControl
                ?? CurrentFanControlSettings;
            return !FanControlSettingsMatch(savedFans, currentFans);
        }
    }

    private bool HasLaptopBehaviorProfileDifferenceCore
    {
        get
        {
            LaptopBehaviorProfileSettings saved =
                GetProfile(ActivePowerSource).EffectiveLaptopBehavior;
            LaptopBehaviorProfileSettings current =
                activeProfileDraftLaptopBehavior
                ?? new LaptopBehaviorProfileSettings(
                    StartupAnimationEnabled,
                    FunctionKeyPrimary,
                    GamingModeEnabled)
                {
                    GamingModeKeyPolicy = saved.GamingModeKeyPolicy,
                };
            return ConfiguredValueDiffers(
                    saved.StartupAnimationEnabled,
                    current.StartupAnimationEnabled)
                || ConfiguredValueDiffers(
                    saved.FunctionKeyPrimary,
                    current.FunctionKeyPrimary)
                || ConfiguredValueDiffers(
                    saved.GamingModeEnabled,
                    current.GamingModeEnabled)
                || saved.GamingModeKeyPolicy != current.GamingModeKeyPolicy;
        }
    }

    private bool HasLightingHardwareProfileDifference
    {
        get
        {
            PowerProfile saved = GetProfile(ActivePowerSource);
            LightingSettings current = activeProfileDraftLighting ?? LightingDraft;
            bool keyboardDiffers = (activeProfileDraftLighting is not null
                    || !IsKeyboardEffectUnconfirmed)
                && LightingSettingsFactory.NormalizeLegacyOff(
                    saved.Lighting with { LogoMode = null })
                    != LightingSettingsFactory.NormalizeLegacyOff(
                        current with { LogoMode = null });
            bool logoDiffers = saved.Lighting.LogoMode is { } savedLogo
                && current.LogoMode is { } currentLogo
                && savedLogo != currentLogo;
            bool unreadableEffectChangedOutside = IsKeyboardEffectUnconfirmed
                && latestStatus?.ExternalChanges.Contains(DeviceSubsystem.Lighting) == true;
            return keyboardDiffers || logoDiffers || unreadableEffectChangedOutside;
        }
    }

    private static bool ConfiguredValueDiffers<T>(T? saved, T? current)
        where T : struct =>
        saved.HasValue && current.HasValue && !saved.Value.Equals(current.Value);

    private static bool FanControlSettingsMatch(
        FanControlSettings saved,
        FanControlSettings current)
    {
        if (saved.Mode != current.Mode)
        {
            return false;
        }

        return saved.Mode switch
        {
            FanMode.Fixed => saved.FixedSpeedRpm == current.FixedSpeedRpm,
            FanMode.ManualCurve => saved.ManualCurve.Equals(current.ManualCurve),
            _ => true,
        };
    }

    private bool HasSaveableUnsetProfileField(ProfileDraftGroup group) => group switch
    {
        ProfileDraftGroup.Lighting => HasSaveableUnsetLightingProfileField,
        ProfileDraftGroup.Laptop => HasSaveableUnsetLaptopProfileField,
        _ => false,
    };

    private bool HasSaveableUnsetLightingProfileField
    {
        get
        {
            if (!HasConfirmedPowerSource
                || LogoLightingModeEditor is not { } logoMode
                || !Capabilities.LogoLightingModes.Contains(logoMode))
            {
                return false;
            }

            return GetProfile(ActivePowerSource).Lighting.LogoMode is null;
        }
    }

    private bool HasSaveableUnsetLaptopProfileField
    {
        get
        {
            if (!HasConfirmedPowerSource)
            {
                return false;
            }

            LaptopBehaviorProfileSettings saved =
                GetProfile(ActivePowerSource).EffectiveLaptopBehavior;
            return saved.StartupAnimationEnabled is null
                    && SupportsStartupAnimation
                    && StartupAnimationEnabled.HasValue
                || saved.FunctionKeyPrimary is null
                    && SupportsFunctionKeyPrimary
                    && FunctionKeyPrimary.HasValue
                || saved.GamingModeEnabled is null
                    && SupportsGamingMode
                    && GamingModeEnabled.HasValue;
        }
    }

    private void CaptureSaveableUnsetProfileFields(ProfileDraftGroup group)
    {
        if (!HasConfirmedPowerSource)
        {
            return;
        }

        PowerProfile saved = GetProfile(ActivePowerSource);
        if (group == ProfileDraftGroup.Lighting
            && saved.Lighting.LogoMode is null
            && LogoLightingModeEditor is { } logoMode
            && Capabilities.LogoLightingModes.Contains(logoMode))
        {
            LightingSettings draft = activeProfileDraftLighting ?? saved.Lighting;
            MarkActiveProfileDraft(
                ActivePowerSource,
                lighting: draft with { LogoMode = logoMode });
        }

        if (group != ProfileDraftGroup.Laptop)
        {
            return;
        }

        LaptopBehaviorProfileSettings baseline = activeProfileDraftLaptopBehavior
            ?? saved.EffectiveLaptopBehavior;
        var draftBehavior = new LaptopBehaviorProfileSettings(
            baseline.StartupAnimationEnabled
                ?? (SupportsStartupAnimation ? StartupAnimationEnabled : null),
            baseline.FunctionKeyPrimary
                ?? (SupportsFunctionKeyPrimary ? FunctionKeyPrimary : null),
            baseline.GamingModeEnabled
                ?? (SupportsGamingMode ? GamingModeEnabled : null))
        {
            GamingModeKeyPolicy = baseline.GamingModeKeyPolicy,
        };
        if (draftBehavior != baseline)
        {
            MarkActiveProfileDraft(
                ActivePowerSource,
                laptopBehavior: draftBehavior);
        }
    }

    private void NotifyActiveProfileDraft()
    {
        Notify(nameof(HasActiveProfileDraft));
        Notify(nameof(HasThermalProfileDraft));
        Notify(nameof(HasLightingProfileDraft));
        Notify(nameof(HasLightingProfileSaveOpportunity));
        Notify(nameof(HasLightingProfileActionOpportunity));
        Notify(nameof(HasDisplayProfileDraft));
        Notify(nameof(HasLaptopProfileDraft));
        Notify(nameof(HasLaptopPageProfileDraft));
        Notify(nameof(HasLaptopPageProfileSaveOpportunity));
        Notify(nameof(HasLaptopPageProfileActionOpportunity));
        Notify(nameof(ThermalProfileRelationshipStatus));
        Notify(nameof(LightingProfileRelationshipStatus));
        Notify(nameof(IsPluggedInProfileDraft));
        Notify(nameof(IsBatteryProfileDraft));
        Notify(nameof(IsUsbCProfileDraft));
        Notify(nameof(HasUnsavedProfileChanges));
        Notify(nameof(ActiveProfileDraftName));
        Notify(nameof(SavedActiveProfileSummary));
        Notify(nameof(PendingActiveProfileSummary));
        NotifyGamingModeKeyPolicy();
        NotifyProfileSummaries();
        NotifyCommands();
    }

    private void NotifyGamingModeKeyPolicy()
    {
        Notify(nameof(BlockWindowsKeyInGamingMode));
        Notify(nameof(BlockAltTabInGamingMode));
        Notify(nameof(CanEditGamingModeKeyPolicy));
    }

    private void NotifyProfileSummaries()
    {
        Notify(nameof(PluggedInProfileThermalSummary));
        Notify(nameof(PluggedInProfileLightingSummary));
        Notify(nameof(BatteryProfileThermalSummary));
        Notify(nameof(BatteryProfileLightingSummary));
        Notify(nameof(UsbCProfileThermalSummary));
        Notify(nameof(UsbCProfileLightingSummary));
        Notify(nameof(PluggedInProfileDisplaySummary));
        Notify(nameof(BatteryProfileDisplaySummary));
        Notify(nameof(UsbCProfileDisplaySummary));
        Notify(nameof(ActiveSavedDisplayProfile));
        Notify(nameof(SavedActiveProfileSummary));
        Notify(nameof(PendingActiveProfileSummary));
        Notify(nameof(PluggedInProfilePresentation));
        Notify(nameof(UsbCProfilePresentation));
        Notify(nameof(BatteryProfilePresentation));
        Notify(nameof(LightingDisplayOffPolicyPresentation));
        Notify(nameof(LightingIdlePolicyPresentation));
        Notify(nameof(HasLightingProfileSaveOpportunity));
        Notify(nameof(HasLaptopPageProfileSaveOpportunity));
        NotifyProfileDifferenceState();
        NotifySelectedProfilePresentation();
    }

    private void NotifySelectedProfilePresentation()
    {
        Notify(nameof(SelectedProfilePresentation));
        Notify(nameof(IsSelectedProfileActive));
        Notify(nameof(IsSelectedProfileDraft));
    }

    private static string FormatThermalProfile(PowerProfile profile) =>
        $"{FormatPerformanceProfile(profile)} | {FanModePresentation.Label(profile.Fans)} fans";

    private static string FormatPowerProfile(PowerProfile profile) =>
        $"Power mode: {FormatPerformanceProfile(profile)}{Environment.NewLine}"
        + $"Fans: {FanModePresentation.Label(profile.Fans)}{Environment.NewLine}"
        + $"Lighting: {FormatLightingProfile(profile.Lighting)}{Environment.NewLine}"
        + $"Display: {FormatDisplayProfile(profile.Display)}";

    private static string SubsystemLabel(DeviceSubsystem subsystem) => subsystem switch
    {
        DeviceSubsystem.Performance => "Power mode",
        DeviceSubsystem.Fans => "Fans",
        DeviceSubsystem.Battery => "Battery protection",
        DeviceSubsystem.Lighting => "Keyboard lighting",
        _ => subsystem.ToString(),
    };

    private string ProfileRelationshipStatus(bool hasDraft, bool changedOutside) =>
        !IsConnected || latestStatus?.DeviceVerified != true
            ? UnavailableLabel
            : !HasConfirmedPowerSource
                ? "Waiting for power source"
                : hasDraft
                    ? "Temporary change not saved to this profile"
                    : changedOutside
                        ? "Changed outside OpenBlade"
                        : "Matches saved profile";

    private static string FormatLightingProfile(LightingSettings lighting)
    {
        if (lighting.Effect == LightingEffect.Off)
        {
            return "Off";
        }

        string effect = lighting.Effect switch
        {
            LightingEffect.Spectrum => "Spectrum Cycling",
            LightingEffect.AmbientAwareness => "Ambient Awareness",
            LightingEffect.AudioMeter => "Audio Meter",
            _ => lighting.Effect.ToString(),
        };
        string details = lighting.Parameters switch
        {
            BreathingLightingParameters
            {
                ColorMode: BreathingColorMode.Dual,
                SecondaryColor: { } secondary,
            } =>
                $"two colors, {FormatColor(lighting.Color)} and "
                + FormatColor(secondary),
            BreathingLightingParameters { ColorMode: BreathingColorMode.Random } => "random colors",
            BreathingLightingParameters => FormatColor(lighting.Color),
            ReactiveLightingParameters reactive =>
                $"{reactive.Duration.ToString().ToLowerInvariant()}, {FormatColor(lighting.Color)}",
            RippleLightingParameters => FormatColor(lighting.Color),
            TidalLightingParameters { RandomColors: true } tidal =>
                $"random colors, {tidal.Direction.ToString().ToLowerInvariant()}",
            TidalLightingParameters tidal =>
                $"{FormatColor(tidal.PrimaryColor)} and {FormatColor(tidal.SecondaryColor)}, "
                + tidal.Direction.ToString().ToLowerInvariant(),
            StarlightLightingParameters
            {
                ColorMode: StarlightColorMode.Random,
            } starlight =>
                $"random colors, {starlight.Duration.ToString().ToLowerInvariant()}",
            StarlightLightingParameters
            {
                ColorMode: StarlightColorMode.Dual,
                SecondaryColor: { } secondary,
            } starlight =>
                $"{FormatColor(lighting.Color)} and {FormatColor(secondary)}, "
                + starlight.Duration.ToString().ToLowerInvariant(),
            StarlightLightingParameters starlight =>
                $"{FormatColor(lighting.Color)}, "
                + starlight.Duration.ToString().ToLowerInvariant(),
            WaveLightingParameters wave =>
                wave.Direction.ToString().ToLowerInvariant(),
            WheelLightingParameters wheel =>
                FormatRotationDirection(wheel.Direction),
            AmbientAwarenessLightingParameters ambient =>
                ambient.Region == AmbientAwarenessRegion.Full
                    ? "full screen"
                    : ambient.Region.ToString().ToLowerInvariant(),
            AudioMeterLightingParameters audioMeter =>
                $"color boost {audioMeter.ColorBoost}, smoothing: "
                    + FormatAudioMeterDecay(audioMeter.DecayMilliseconds),
            _ when lighting.Effect == LightingEffect.Static => FormatColor(lighting.Color),
            _ => string.Empty,
        };
        return string.IsNullOrEmpty(details)
            ? $"{effect} | {lighting.Brightness}%"
            : $"{effect} | {lighting.Brightness}% | {details}";
    }

    private static string FormatColor(RgbColor color) =>
        $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}";

    private static string FormatAudioMeterDecay(int milliseconds) => milliseconds switch
    {
        0 => "Off",
        1000 => "Up to 1 second",
        _ => $"Up to {milliseconds / 1000d:0.##} seconds",
    };

    private static string FormatDisplayProfile(DisplayProfileSettings? display)
    {
        DisplayProfileSettings value = display ?? DisplayProfileSettings.Unconfigured();
        List<string> settings = [];
        if (!string.IsNullOrWhiteSpace(value.RefreshRateKey))
        {
            settings.Add(value.RefreshRateKey);
        }
        if (value.HdrEnabled is bool hdrEnabled)
        {
            settings.Add(hdrEnabled ? "HDR on" : "HDR off");
        }
        if (!string.IsNullOrWhiteSpace(value.ColorProfileKey))
        {
            settings.Add(value.ColorProfileKey);
        }
        return settings.Count == 0 ? "Not set" : string.Join(" | ", settings);
    }

    private ProfilePresentation CreateProfilePresentation(PowerSource source)
    {
        string profileName = ComparisonOptions
            .First(option => option.Source == source)
            .Label;
        PowerProfile savedProfile = GetProfile(source);
        ProfilePresentation saved = CreateProfilePresentation(savedProfile, profileName);
        if (activeProfileDraftSource != source || GetActiveProfileDraft() is not { } draft)
        {
            return saved;
        }

        ProfilePresentation pending = CreateProfilePresentation(draft, profileName);
        return new ProfilePresentation(
            MarkPending(saved.PowerMode, pending.PowerMode),
            MarkPending(saved.GpuTuning, pending.GpuTuning),
            MarkPending(
                saved.Fans,
                pending.Fans,
                !FanControlSettingsMatch(
                    savedProfile.FanControl
                        ?? FanControlSettings.FromLegacy(savedProfile.Fans),
                    draft.FanControl ?? FanControlSettings.FromLegacy(draft.Fans))),
            MarkPending(saved.LightingEffect, pending.LightingEffect),
            MarkPending(saved.LightingBrightness, pending.LightingBrightness),
            MarkPending(saved.LightingColor, pending.LightingColor),
            MarkPending(saved.LogoLighting, pending.LogoLighting),
            MarkPending(saved.LightingDisplayOff, pending.LightingDisplayOff),
            MarkPending(saved.LightingIdle, pending.LightingIdle),
            MarkPending(saved.DisplayRefreshRate, pending.DisplayRefreshRate),
            MarkPending(saved.DisplayHdr, pending.DisplayHdr),
            MarkPending(saved.DisplayColorProfile, pending.DisplayColorProfile),
            MarkPending(saved.FunctionKeyBehavior, pending.FunctionKeyBehavior),
            MarkPending(saved.GamingMode, pending.GamingMode),
            MarkPending(saved.GamingModeWindowsKey, pending.GamingModeWindowsKey),
            MarkPending(saved.GamingModeAltTab, pending.GamingModeAltTab),
            MarkPending(saved.StartupAnimation, pending.StartupAnimation));
    }

    private static ProfileValuePresentation MarkPending(
        ProfileValuePresentation saved,
        ProfileValuePresentation pending,
        bool isPending = false) =>
        saved with
        {
            IsPending = isPending
                || !string.Equals(saved.Text, pending.Text, StringComparison.Ordinal),
            PendingText = pending.Text,
        };

    private static ProfilePresentation CreateProfilePresentation(
        PowerProfile profile,
        string profileName)
    {
        ProfileValuePresentation Value(string settingName, string text) =>
            new(text, settingName, profileName);

        DisplayProfileSettings display = profile.Display ?? DisplayProfileSettings.Unconfigured();
        return new ProfilePresentation(
            Value("Power mode", FormatPerformanceProfile(profile)),
            Value("GPU tuning", FormatGpuClockOffsets(profile)),
            Value("Fans", FormatFanControl(profile)),
            Value("Lighting effect", FormatLightingEffect(profile.Lighting)),
            Value("Lighting brightness", profile.Lighting.Effect == LightingEffect.Off
                ? "Not applicable"
                : $"{profile.Lighting.Brightness}%"),
            Value("Lighting color", FormatLightingColor(profile.Lighting)),
            Value("Laptop lid logo", profile.Lighting.LogoMode switch
            {
                LogoLightingMode.Static => "Static",
                LogoLightingMode.Breathing => "Breathing",
                LogoLightingMode.Off => "Off",
                null => "Not set",
                _ => "Not set",
            }),
            Value("Display turns off", profile.EffectiveLightingPowerPolicy.TurnOffWhenDisplayTurnsOff
                ? "Turn lighting off"
                : "Keep lighting on"),
            Value("Inactivity timeout", profile.EffectiveLightingPowerPolicy.IdleTimeoutMinutes is int minutes
                ? $"{minutes.ToString(CultureInfo.CurrentCulture)} minutes"
                : "Never"),
            Value("Refresh rate", FormatDisplayRefreshRate(display.RefreshRateKey)),
            Value("HDR", display.HdrEnabled switch
            {
                true => "On",
                false => "Off",
                null => "Not set",
            }),
            Value("Color profile", FormatDisplayColorProfile(display.ColorProfileKey)),
            Value("Function keys", profile.EffectiveLaptopBehavior.FunctionKeyPrimary switch
            {
                FunctionKeyPrimaryMode.FunctionKeys => "Function keys",
                FunctionKeyPrimaryMode.MultimediaKeys => "Media keys",
                _ => "Not set",
            }),
            Value("Gaming mode", FormatOptionalToggle(profile.EffectiveLaptopBehavior.GamingModeEnabled)),
            Value("Block Windows key", FormatOptionalToggle(
                profile.EffectiveLaptopBehavior
                    .EffectiveGamingModeKeyPolicy
                    .BlockWindowsKey)),
            Value("Block Alt+Tab", FormatOptionalToggle(
                profile.EffectiveLaptopBehavior
                    .EffectiveGamingModeKeyPolicy
                    .BlockAltTab)),
            Value(
                "Startup animation",
                FormatOptionalToggle(profile.EffectiveLaptopBehavior.StartupAnimationEnabled)));
    }

    private static string FormatOptionalToggle(bool? value) => value switch
    {
        true => "On",
        false => "Off",
        null => "Not set",
    };

    private static string FormatFanControl(PowerProfile profile)
    {
        FanControlSettings settings = profile.FanControl
            ?? FanControlSettings.FromLegacy(profile.Fans);
        return settings.Mode switch
        {
            FanMode.Automatic => "Automatic",
            FanMode.Fixed =>
                $"Fixed, {settings.FixedSpeedRpm.ToString("N0", CultureInfo.CurrentCulture)} RPM",
            FanMode.ManualCurve => "Manual fan curve",
            FanMode.Maximum => "Maximum",
            _ => settings.Mode.ToString(),
        };
    }

    private static string FormatPerformanceProfile(PowerProfile profile) =>
        profile is
        {
            Performance: PerformancePreset.Custom,
            CustomPerformance: { } custom,
        }
            ? $"Custom, CPU {custom.Cpu}, GPU {custom.Gpu}"
            : PerformancePresetPresentation.Label(profile.Performance);

    private static string FormatGpuClockOffsets(PowerProfile profile) =>
        ResolveProfileGpuClockTuning(profile) switch
        {
            { Enabled: true, Offsets: { } offsets } =>
                $"Core {FormatSignedMegahertz(offsets.CoreMegahertz)}, "
                + $"memory {FormatSignedMegahertz(offsets.MemoryMegahertz)}",
            { Enabled: false } => "Off",
            _ => "Not set",
        };

    private static GpuClockTuningSettings? ResolveProfileGpuClockTuning(
        PowerProfile profile) => profile.GpuClockTuning
        ?? (profile.Performance == PerformancePreset.Custom
            && profile.CustomPerformance?.GpuClockOffsets is { } legacyOffsets
                ? new GpuClockTuningSettings(Enabled: true, legacyOffsets)
                : null);

    private static string FormatSignedMegahertz(int value) =>
        $"{value.ToString("+0;-0;0", CultureInfo.CurrentCulture)} MHz";

    private static string FormatLightingEffect(LightingSettings lighting) => lighting switch
    {
        { Effect: LightingEffect.Off } => "Off",
        { Effect: LightingEffect.Spectrum } => "Spectrum Cycling",
        {
            Effect: LightingEffect.AudioMeter,
            Parameters: AudioMeterLightingParameters audioMeter,
        } => $"Audio Meter, color boost {audioMeter.ColorBoost}, smoothing: "
            + FormatAudioMeterDecay(audioMeter.DecayMilliseconds),
        {
            Effect: LightingEffect.AmbientAwareness,
            Parameters: AmbientAwarenessLightingParameters ambient,
        } => ambient.Region == AmbientAwarenessRegion.Full
            ? "Ambient Awareness, full screen"
            : $"Ambient Awareness, {ambient.Region.ToString().ToLowerInvariant()}",
        {
            Effect: LightingEffect.Breathing,
            Parameters: BreathingLightingParameters { ColorMode: BreathingColorMode.Dual },
        } => "Breathing, two colors",
        {
            Effect: LightingEffect.Breathing,
            Parameters: BreathingLightingParameters { ColorMode: BreathingColorMode.Random },
        } => "Breathing, random colors",
        { Effect: LightingEffect.Breathing } => "Breathing, one color",
        {
            Effect: LightingEffect.Reactive,
            Parameters: ReactiveLightingParameters reactive,
        } => $"Reactive, {reactive.Duration.ToString().ToLowerInvariant()}",
        { Effect: LightingEffect.Ripple } => "Ripple",
        {
            Effect: LightingEffect.Tidal,
            Parameters: TidalLightingParameters tidal,
        } => tidal.Direction == LightingDirection.Leftward
            ? "Tidal, outward"
            : "Tidal, inward",
        {
            Effect: LightingEffect.Starlight,
            Parameters: StarlightLightingParameters
            {
                ColorMode: StarlightColorMode.Dual,
            } starlight,
        } => $"Starlight, two colors, {starlight.Duration.ToString().ToLowerInvariant()}",
        {
            Effect: LightingEffect.Starlight,
            Parameters: StarlightLightingParameters
            {
                ColorMode: StarlightColorMode.Random,
            } starlight,
        } => $"Starlight, random colors, {starlight.Duration.ToString().ToLowerInvariant()}",
        {
            Effect: LightingEffect.Starlight,
            Parameters: StarlightLightingParameters starlight,
        } => $"Starlight, one color, {starlight.Duration.ToString().ToLowerInvariant()}",
        {
            Effect: LightingEffect.Wave,
            Parameters: WaveLightingParameters wave,
        } => $"Wave, {wave.Direction.ToString().ToLowerInvariant()}",
        {
            Effect: LightingEffect.Wheel,
            Parameters: WheelLightingParameters wheel,
        } => $"Wheel, {FormatRotationDirection(wheel.Direction)}",
        _ => lighting.Effect.ToString(),
    };

    private static string FormatLightingColor(LightingSettings lighting) => lighting switch
    {
        { Effect: LightingEffect.Off } => "Not applicable",
        { Effect: LightingEffect.Spectrum } => "Automatic",
        {
            Effect: LightingEffect.Wave or LightingEffect.Wheel
                or LightingEffect.AmbientAwareness
                or LightingEffect.AudioMeter
                or LightingEffect.Fire,
        } => "Automatic",
        {
            Parameters: BreathingLightingParameters { ColorMode: BreathingColorMode.Random },
        } => "Random",
        {
            Parameters: TidalLightingParameters { RandomColors: true },
        } => "Random",
        {
            Parameters: TidalLightingParameters tidal,
        } => $"{FormatColor(tidal.PrimaryColor)} and {FormatColor(tidal.SecondaryColor)}",
        {
            Parameters: StarlightLightingParameters { ColorMode: StarlightColorMode.Random },
        } => "Random",
        {
            Parameters: StarlightLightingParameters
            {
                ColorMode: StarlightColorMode.Dual,
                SecondaryColor: { } secondary,
            },
        } => $"{FormatColor(lighting.Color)} and {FormatColor(secondary)}",
        {
            Parameters: BreathingLightingParameters
            {
                ColorMode: BreathingColorMode.Dual,
                SecondaryColor: { } secondary,
            },
        } => $"{FormatColor(lighting.Color)} and {FormatColor(secondary)}",
        _ => FormatColor(lighting.Color),
    };

    private static string FormatRotationDirection(LightingRotationDirection direction) =>
        direction == LightingRotationDirection.Counterclockwise
            ? "counterclockwise"
            : "clockwise";

    private static string FormatDisplayRefreshRate(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return "Not set";
        }

        string[] parts = key.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2
            && string.Equals(parts[0], "fixed", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(parts[1], CultureInfo.InvariantCulture, out int fixedHertz))
        {
            return $"{fixedHertz} Hz";
        }
        if (parts.Length == 3
            && (string.Equals(parts[0], "dynamic", StringComparison.OrdinalIgnoreCase)
                || string.Equals(parts[0], "drr", StringComparison.OrdinalIgnoreCase))
            && int.TryParse(parts[1], CultureInfo.InvariantCulture, out int minimumHertz)
            && int.TryParse(parts[2], CultureInfo.InvariantCulture, out int maximumHertz))
        {
            return $"Dynamic {minimumHertz}–{maximumHertz} Hz";
        }

        return key;
    }

    private static string FormatDisplayColorProfile(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return "Not set";
        }

        string fileName = Path.GetFileNameWithoutExtension(key);
        string normalized = fileName.Replace("_", " ", StringComparison.Ordinal)
            .Replace("-", " ", StringComparison.Ordinal)
            .Trim();
        return normalized.ToLowerInvariant() switch
        {
            "srgb" => "sRGB",
            "dci p3" or "dcip3" => "DCI-P3",
            "adobe rgb" or "adobergb" => "Adobe RGB",
            "rec 709" or "rec709" => "Rec. 709",
            "native" => "Native",
            _ => string.IsNullOrWhiteSpace(normalized) ? key : normalized,
        };
    }

    private LightingSettings CreateLightingDraft()
    {
        byte brightness = LightingEffectEditor != LightingEffect.Off
            && LightingBrightnessEditor == 0
                ? DefaultLightingBrightness
                : (byte)LightingBrightnessEditor;
        RgbColor primaryColor = new(
            (byte)LightingRedEditor,
            (byte)LightingGreenEditor,
            (byte)LightingBlueEditor);
        RgbColor secondaryColor = new(
            (byte)LightingSecondaryRedEditor,
            (byte)LightingSecondaryGreenEditor,
            (byte)LightingSecondaryBlueEditor);
        LightingSettings keyboard = LightingEffectEditor switch
        {
            LightingEffect.Off => LightingSettingsFactory.Off(),
            LightingEffect.Static => LightingSettingsFactory.Static(primaryColor, brightness),
            LightingEffect.Spectrum => LightingSettingsFactory.SpectrumCycling(brightness),
            LightingEffect.Breathing when
                LightingBreathingColorModeEditor == BreathingColorMode.Single =>
                LightingSettingsFactory.BreathingSingle(primaryColor, brightness),
            LightingEffect.Breathing when
                LightingBreathingColorModeEditor == BreathingColorMode.Dual =>
                LightingSettingsFactory.BreathingDual(
                    primaryColor,
                    secondaryColor,
                    brightness),
            LightingEffect.Breathing when
                LightingBreathingColorModeEditor == BreathingColorMode.Random =>
                LightingSettingsFactory.BreathingRandom(brightness),
            LightingEffect.Reactive => LightingSettingsFactory.Reactive(
                primaryColor,
                brightness,
                LightingReactiveDurationEditor),
            LightingEffect.Ripple => LightingSettingsFactory.Ripple(primaryColor, brightness),
            LightingEffect.Tidal => LightingSettingsFactory.Tidal(
                primaryColor,
                secondaryColor,
                LightingRandomColorsEditor,
                LightingDirectionEditor,
                brightness),
            LightingEffect.Starlight when
                LightingStarlightColorModeEditor == StarlightColorMode.Single =>
                LightingSettingsFactory.StarlightSingle(
                    primaryColor,
                    brightness,
                    LightingStarlightDurationEditor),
            LightingEffect.Starlight when
                LightingStarlightColorModeEditor == StarlightColorMode.Dual =>
                LightingSettingsFactory.StarlightDual(
                    primaryColor,
                    secondaryColor,
                    brightness,
                    LightingStarlightDurationEditor),
            LightingEffect.Starlight when
                LightingStarlightColorModeEditor == StarlightColorMode.Random =>
                LightingSettingsFactory.StarlightRandom(
                    brightness,
                    LightingStarlightDurationEditor),
            LightingEffect.Wave => LightingSettingsFactory.Wave(
                LightingDirectionEditor,
                brightness),
            LightingEffect.Wheel => LightingSettingsFactory.Wheel(
                LightingRotationDirectionEditor,
                brightness),
            LightingEffect.AmbientAwareness => LightingSettingsFactory.AmbientAwareness(
                LightingAmbientAwarenessRegionEditor,
                brightness),
            LightingEffect.AudioMeter => LightingSettingsFactory.AudioMeter(
                LightingAudioMeterColorBoostEditor,
                brightness,
                LightingAudioMeterDecayMillisecondsEditor),
            LightingEffect.Fire => LightingSettingsFactory.Fire(brightness),
            _ => new LightingSettings(LightingEffectEditor, default, brightness),
        };
        return keyboard with { LogoMode = LogoLightingModeEditor };
    }

    private void SeedVisibleLightingDefaults(
        LightingEffect previous,
        LightingEffect selected)
    {
        if (selected == LightingEffect.Off)
        {
            return;
        }

        if (LightingBrightnessEditor == 0)
        {
            SetLightingEditorField(
                ref lightingBrightnessEditor,
                (int)DefaultLightingBrightness,
                nameof(LightingBrightnessEditor));
        }

        bool selectedUsesColor = selected is LightingEffect.Static
            or LightingEffect.Breathing
            or LightingEffect.Reactive
            or LightingEffect.Ripple
            or LightingEffect.Tidal
            or LightingEffect.Starlight;
        bool previousHadNoEditableColor = previous is LightingEffect.Off
            or LightingEffect.Spectrum
            || (previous == LightingEffect.Breathing
                && LightingBreathingColorModeEditor == BreathingColorMode.Random);
        if (selectedUsesColor
            && previousHadNoEditableColor
            && LightingPrimaryColorEditor == default)
        {
            SetLightingColorEditor(
                LightingSettingsFactory.DefaultVisibleColor,
                secondary: false);
        }

        if (selected == LightingEffect.Breathing)
        {
            SeedBreathingColorDefaults(LightingBreathingColorModeEditor);
        }

        if (selected == LightingEffect.Tidal)
        {
            SeedTidalColorDefaults();
        }

        if (selected == LightingEffect.Starlight)
        {
            SeedStarlightColorDefaults(LightingStarlightColorModeEditor);
        }
    }

    private void SeedBreathingColorDefaults(BreathingColorMode selected)
    {
        if (selected is BreathingColorMode.Single or BreathingColorMode.Dual
            && LightingPrimaryColorEditor == default)
        {
            SetLightingColorEditor(
                LightingSettingsFactory.DefaultVisibleColor,
                secondary: false);
        }

        if (selected == BreathingColorMode.Dual
            && LightingSecondaryColorEditor == default)
        {
            SetLightingColorEditor(new RgbColor(255, 0, 170), secondary: true);
        }
    }

    private void SeedTidalColorDefaults()
    {
        if (LightingPrimaryColorEditor == default)
        {
            SetLightingColorEditor(
                LightingSettingsFactory.DefaultVisibleColor,
                secondary: false);
        }
        if (LightingSecondaryColorEditor == default)
        {
            SetLightingColorEditor(new RgbColor(0, 0, 255), secondary: true);
        }
    }

    private void SeedStarlightColorDefaults(StarlightColorMode selected)
    {
        if (selected is StarlightColorMode.Single or StarlightColorMode.Dual
            && LightingPrimaryColorEditor == default)
        {
            SetLightingColorEditor(
                LightingSettingsFactory.DefaultVisibleColor,
                secondary: false);
        }

        if (selected == StarlightColorMode.Dual
            && LightingSecondaryColorEditor == default)
        {
            SetLightingColorEditor(new RgbColor(255, 0, 170), secondary: true);
        }
    }

    private LightingSettings CreateLightingChoice(LightingEffect effect)
    {
        byte brightness = SelectedLighting.Brightness == 0
            ? DefaultLightingBrightness
            : SelectedLighting.Brightness;
        RgbColor color = SelectedLighting.Color == default
            ? LightingSettingsFactory.DefaultVisibleColor
            : SelectedLighting.Color;
        return effect switch
        {
            LightingEffect.Off => LightingSettingsFactory.Off(),
            LightingEffect.Static => LightingSettingsFactory.Static(color, brightness),
            LightingEffect.Spectrum => LightingSettingsFactory.SpectrumCycling(brightness),
            LightingEffect.Breathing => LightingSettingsFactory.BreathingSingle(
                color,
                brightness),
            LightingEffect.Reactive => LightingSettingsFactory.Reactive(
                color,
                brightness,
                ReactiveLightingDuration.Medium),
            LightingEffect.Ripple => LightingSettingsFactory.Ripple(color, brightness),
            LightingEffect.Tidal => LightingSettingsFactory.Tidal(
                color,
                new RgbColor(0, 0, 255),
                randomColors: false,
                LightingDirection.Rightward,
                brightness),
            LightingEffect.Starlight => LightingSettingsFactory.StarlightSingle(
                color,
                brightness,
                StarlightLightingDuration.Medium),
            LightingEffect.Wave => LightingSettingsFactory.Wave(
                LightingDirection.Rightward,
                brightness),
            LightingEffect.Wheel => LightingSettingsFactory.Wheel(
                LightingRotationDirection.Clockwise,
                brightness),
            LightingEffect.AmbientAwareness => LightingSettingsFactory.AmbientAwareness(
                AmbientAwarenessRegion.Full,
                brightness),
            LightingEffect.AudioMeter => LightingSettingsFactory.AudioMeter(
                colorBoost: 1,
                brightness),
            LightingEffect.Fire => LightingSettingsFactory.Fire(brightness),
            _ => new LightingSettings(effect, color, brightness),
        };
    }

    private void NotifyCommands()
    {
        foreach (AsyncRelayCommand command in commands)
        {
            command.NotifyCanExecuteChanged();
        }

        Notify(nameof(CanAdjustChargeLimit));
        Notify(nameof(CanChangeBatteryProtection));
        if (!suppressProfileDifferenceNotifications)
        {
            NotifyProfileDifferenceState();
        }
    }

    private void NotifyProfileDifferenceState()
    {
        Notify(nameof(HasThermalProfileDifference));
        Notify(nameof(HasLaptopBehaviorProfileDifference));
        Notify(nameof(HasLaptopPageProfileDifference));
        Notify(nameof(HasLaptopPageProfileActionOpportunity));
        Notify(nameof(HasLightingProfileDifference));
        Notify(nameof(HasLightingProfileActionOpportunity));
    }

    private string SourceLabel(
        DeviceSubsystem subsystem,
        bool confirmed,
        ServiceStatusSnapshot status,
        IReadOnlyCollection<DeviceSubsystem> externalChanges) =>
        IsCheckingLaptopConnection(status)
            ? "Checking laptop"
            : !status.DeviceVerified
            ? UnavailableLaptopReason
            : externalChanges.Contains(subsystem)
            ? "Changed outside OpenBlade"
            : confirmed
                ? "Current setting"
                : "Controls unavailable";

    private string FormatBatterySource(
        ConfirmedBatteryState? battery,
        bool changedOutside,
        ServiceStatusSnapshot status)
    {
        if (IsCheckingLaptopConnection(status))
        {
            return "Checking laptop";
        }
        if (!status.DeviceVerified)
        {
            return UnavailableLaptopReason;
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
            ? "Changed outside OpenBlade"
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

    private static string FormatTemperatureValue(int? celsius) => celsius is int value
        ? $"{value.ToString(CultureInfo.CurrentCulture)} °C"
        : "Unavailable";

    private string LiveReadingValue(LiveReadingItemKind kind) => kind switch
    {
        LiveReadingItemKind.BatteryPower => BatteryPowerFlowLabel,
        LiveReadingItemKind.CpuTemperature => CpuTemperatureValue,
        LiveReadingItemKind.GpuTemperature => GpuTemperatureValue,
        LiveReadingItemKind.Fan1Speed => Fan1RpmValue,
        LiveReadingItemKind.Fan2Speed => Fan2RpmValue,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private bool DashboardTelemetryEnabled(LiveReadingItemKind kind) =>
        DashboardTelemetryItems.Any(option => option.Kind == kind && option.IsEnabled);

    private static string FormatDashboardTemperatures(
        bool showCpu,
        string cpuValue,
        bool showGpu,
        string gpuValue)
    {
        if (!showCpu && !showGpu)
        {
            return string.Empty;
        }

        if (showCpu
            && showGpu
            && string.Equals(cpuValue, UnavailableLabel, StringComparison.Ordinal)
            && string.Equals(gpuValue, UnavailableLabel, StringComparison.Ordinal))
        {
            return UnavailableLabel;
        }

        string[] values =
        [
            showCpu ? $"CPU {cpuValue}" : string.Empty,
            showGpu ? $"GPU {gpuValue}" : string.Empty,
        ];
        return string.Join(" / ", values.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private async Task<bool> RestoreSavedLaptopBehaviorAsync(
        PowerSource expectedPowerSource,
        LaptopBehaviorProfileSettings settings,
        CancellationToken token)
    {
        bool restored = true;
        if (settings.FunctionKeyPrimary is { } functionKeyMode)
        {
            restored &= await RunOperationAsync(
                cancellationToken => serviceClient.ApplyOneShotLaptopControlAsync(
                    new ApplyOneShotLaptopControl(
                        OneShotLaptopControl.FunctionKeyPrimary,
                        FunctionKeyMode: functionKeyMode),
                    cancellationToken),
                token);
        }
        if (expectedPowerSource != ActivePowerSource)
        {
            return false;
        }
        if (settings.GamingModeEnabled is { } gamingModeEnabled)
        {
            restored &= await RunOperationAsync(
                cancellationToken => serviceClient.ApplyOneShotLaptopControlAsync(
                    new ApplyOneShotLaptopControl(
                        OneShotLaptopControl.GamingMode,
                        Enabled: gamingModeEnabled),
                    cancellationToken),
                token);
        }
        if (expectedPowerSource != ActivePowerSource)
        {
            return false;
        }
        if (settings.StartupAnimationEnabled is { } startupAnimationEnabled)
        {
            restored &= await RunOperationAsync(
                cancellationToken => serviceClient.ApplyOneShotLaptopControlAsync(
                    new ApplyOneShotLaptopControl(
                        OneShotLaptopControl.StartupAnimation,
                        Enabled: startupAnimationEnabled),
                    cancellationToken),
                token);
        }
        return restored && expectedPowerSource == ActivePowerSource;
    }

    private static string LiveReadingLabel(LiveReadingItemKind kind) => kind switch
    {
        LiveReadingItemKind.BatteryPower => "Battery",
        LiveReadingItemKind.CpuTemperature => "CPU temperature",
        LiveReadingItemKind.GpuTemperature => "GPU temperature",
        LiveReadingItemKind.Fan1Speed => "Fan 1",
        LiveReadingItemKind.Fan2Speed => "Fan 2",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string TrayHoverLabelItemLabel(TrayHoverLabelItemKind kind) => kind switch
    {
        TrayHoverLabelItemKind.ActiveProfile => "Active profile",
        TrayHoverLabelItemKind.AppName => "OpenBlade app name",
        TrayHoverLabelItemKind.PowerSource => "Power source",
        TrayHoverLabelItemKind.PowerMode => "Power mode",
        TrayHoverLabelItemKind.BatteryPower => "Battery power",
        TrayHoverLabelItemKind.CpuTemperature => "CPU temperature",
        TrayHoverLabelItemKind.GpuTemperature => "GPU temperature",
        TrayHoverLabelItemKind.Fan1Speed => "Fan 1 speed",
        TrayHoverLabelItemKind.Fan2Speed => "Fan 2 speed",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private void SubscribeTelemetryOptions(
        IEnumerable<LiveReadingVisibilityOption> options)
    {
        foreach (LiveReadingVisibilityOption option in options)
        {
            option.PropertyChanged += OnDashboardTelemetryOptionChanged;
        }
    }

    private void SubscribeTrayHoverOptions(IEnumerable<TrayHoverLabelItemOption> options)
    {
        foreach (TrayHoverLabelItemOption option in options)
        {
            option.PropertyChanged += OnTrayHoverLabelOptionChanged;
        }
    }

    private void UnsubscribeTrayHoverOptions(IEnumerable<TrayHoverLabelItemOption> options)
    {
        foreach (TrayHoverLabelItemOption option in options)
        {
            option.PropertyChanged -= OnTrayHoverLabelOptionChanged;
        }
    }

    private void OnDashboardTelemetryItemsChanged(
        object? sender,
        NotifyCollectionChangedEventArgs args)
    {
        if (args.OldItems is not null)
        {
            foreach (LiveReadingVisibilityOption option in args.OldItems)
            {
                option.PropertyChanged -= OnDashboardTelemetryOptionChanged;
            }
        }
        if (args.NewItems is not null)
        {
            foreach (LiveReadingVisibilityOption option in args.NewItems)
            {
                option.PropertyChanged += OnDashboardTelemetryOptionChanged;
            }
        }
        NotifyDashboardTelemetryPreferencesChanged();
    }

    private void OnTrayHoverLabelItemsChanged(
        object? sender,
        NotifyCollectionChangedEventArgs args)
    {
        if (args.OldItems is not null)
        {
            foreach (TrayHoverLabelItemOption option in args.OldItems)
            {
                option.PropertyChanged -= OnTrayHoverLabelOptionChanged;
            }
        }
        if (args.NewItems is not null)
        {
            foreach (TrayHoverLabelItemOption option in args.NewItems)
            {
                option.PropertyChanged += OnTrayHoverLabelOptionChanged;
            }
        }
        SyncLegacyTrayHoverLabelMode();
        NotifyTrayHoverPreferencesChanged();
    }

    private void OnDashboardTelemetryOptionChanged(
        object? sender,
        PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(LiveReadingVisibilityOption.IsEnabled))
        {
            NotifyDashboardTelemetryPreferencesChanged();
        }
    }

    private void OnTrayHoverLabelOptionChanged(
        object? sender,
        PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(TrayHoverLabelItemOption.IsEnabled))
        {
            SyncLegacyTrayHoverLabelMode();
            NotifyTrayHoverPreferencesChanged();
        }
    }

    private void ApplyLegacyTrayHoverLabelMode(TrayHoverLabelMode mode)
    {
        changingTelemetryPreferences = true;
        try
        {
            Dictionary<TrayHoverLabelItemKind, bool> enabled =
                TelemetryPreferenceDefaults.FromLegacyMode(mode)
                    .ToDictionary(item => item.Kind, item => item.IsEnabled);
            foreach (TrayHoverLabelItemOption option in TrayHoverLabelItems)
            {
                option.IsEnabled = enabled.GetValueOrDefault(option.Kind);
            }
        }
        finally
        {
            changingTelemetryPreferences = false;
        }
        NotifyTrayHoverPreferencesChanged();
    }

    private void SyncLegacyTrayHoverLabelMode()
    {
        if (changingTelemetryPreferences)
        {
            return;
        }

        HashSet<TrayHoverLabelItemKind> enabled = TrayHoverLabelItems
            .Where(option => option.IsEnabled)
            .Select(option => option.Kind)
            .Where(kind => kind != TrayHoverLabelItemKind.AppName)
            .ToHashSet();
        TrayHoverLabelMode mode = enabled.SetEquals(
                [
                    TrayHoverLabelItemKind.PowerSource,
                    TrayHoverLabelItemKind.PowerMode,
                    TrayHoverLabelItemKind.BatteryPower,
                ])
            ? TrayHoverLabelMode.ProfileAndPowerFlow
            : enabled.SetEquals(
                [TrayHoverLabelItemKind.PowerSource, TrayHoverLabelItemKind.PowerMode])
                ? TrayHoverLabelMode.ActiveProfile
                : enabled.SetEquals([TrayHoverLabelItemKind.BatteryPower])
                    ? TrayHoverLabelMode.BatteryPowerFlow
                    : TrayHoverLabelMode.OpenBlade;
        if (trayHoverLabelMode != mode)
        {
            SetField(ref trayHoverLabelMode, mode, nameof(TrayHoverLabelMode));
            Notify(nameof(SelectedTrayHoverLabelOption));
        }
    }

    private string FormatControlAccessStatus(ServiceStatusSnapshot status)
    {
        if (status.ControlAccess != SessionControlAccess.Active)
        {
            return status.ControlAccess == SessionControlAccess.InactiveSession
                ? "Another user is controlling OpenBlade"
                : "Interactive session required";
        }

        if (IsCheckingLaptopConnection(status))
        {
            return "Checking laptop";
        }

        if (!status.DeviceVerified)
        {
            return UnavailableLaptopReason;
        }

        if (status.ActivePowerSource == PowerSource.ExternalUnknown)
        {
            return "Checking power source";
        }

        if (IsRetainedPowerSourceRecheck(status))
        {
            return "Ready";
        }

        return status.WritesBlocked
            ? "Controls unavailable"
            : "Ready";
    }

    private static bool IsRetainedPowerSourceRecheck(ServiceStatusSnapshot? status) =>
        status is
        {
            DeviceVerified: true,
            WritesBlocked: true,
            ActivePowerSource: PowerSource.PluggedIn or PowerSource.UsbC,
        }
        && status.Message.Contains(
            "last confirmed power source is retained",
            StringComparison.OrdinalIgnoreCase);

    private static bool IsCheckingLaptopConnection(ServiceStatusSnapshot? status) =>
        status is not null
        && IsTemporaryDeviceAccessMessage(status.Message);

    private static bool IsTemporaryDeviceAccessMessage(string message) =>
        message.StartsWith("Checking", StringComparison.OrdinalIgnoreCase)
            && message.Contains("laptop connection", StringComparison.OrdinalIgnoreCase)
        || message.StartsWith(
            "Refreshing laptop controls after a temporary device-access failure",
            StringComparison.OrdinalIgnoreCase);

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

    private static string FirmwareValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "Unavailable" : value.Trim();

    private static string FormatBatteryHealth(BatteryHealthStatus? health)
    {
        if (health is null)
        {
            return UnavailableLabel;
        }

        int percentage = checked((int)Math.Round(
            health.FullChargeCapacityMilliwattHours
                * 100d
                / health.DesignCapacityMilliwattHours,
            MidpointRounding.AwayFromZero));
        string fullChargeCapacity = (
            health.FullChargeCapacityMilliwattHours / 1000d)
            .ToString("0.#", CultureInfo.CurrentCulture);
        string designCapacity = (
            health.DesignCapacityMilliwattHours / 1000d)
            .ToString("0.#", CultureInfo.CurrentCulture);
        return $"{percentage}% ({fullChargeCapacity} Wh of {designCapacity} Wh)";
    }

    private static bool HasConcreteFirmwareValue(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && !string.Equals(
            value.Trim(),
            UnavailableLabel,
            StringComparison.OrdinalIgnoreCase);

    private static string FormatTestedPlatformWarning(
        TestedPlatformAssessment assessment)
    {
        if (!assessment.IsEvaluated || assessment.Mismatches.Count == 0)
        {
            return string.Empty;
        }

        string versions = string.Join(
            ", ",
            assessment.Mismatches.Select(FormatTestedPlatformMismatch));
        bool hasUnavailableValue = assessment.Mismatches.Any(mismatch =>
            string.Equals(
                mismatch.CurrentValue,
                UnavailableLabel,
                StringComparison.OrdinalIgnoreCase));
        return hasUnavailableValue
            ? $"OpenBlade could not verify this configuration: {versions}."
            : $"OpenBlade has not tested this configuration: {versions}.";
    }

    private static string FormatTestedPlatformMismatch(
        TestedPlatformVersionMismatch mismatch)
    {
        string currentValue = string.Equals(
            mismatch.Component,
            "MCU",
            StringComparison.OrdinalIgnoreCase)
            ? McuFirmwareVersionPresentation.Format(mismatch.CurrentValue)
            : mismatch.CurrentValue;
        return $"{mismatch.Component} {currentValue}";
    }

    private static string FormatOperationResult(ControlOperationResult result) => result.Status switch
    {
        ControlOperationStatus.Applied or ControlOperationStatus.Stored => string.Empty,
        ControlOperationStatus.Blocked => "OpenBlade saved the change, but the laptop has not applied it.",
        _ => "OpenBlade couldn’t complete that action. Check Troubleshooting for details.",
    };

    private void UpdateOneShotReadbacks(ServiceStatusSnapshot status)
    {
        bool startupPending = SupportsStartupAnimation && status.StartupAnimationEnabled is null;
        bool functionKeyPending = SupportsFunctionKeyPrimary && status.FunctionKeyPrimary is null;
        bool gamingPending = SupportsGamingMode && status.GamingModeEnabled is null;
        bool pendingChanged =
            startupAnimationReadPending != startupPending
            || functionKeyReadPending != functionKeyPending
            || gamingModeReadPending != gamingPending;

        startupAnimationReadPending = startupPending;
        functionKeyReadPending = functionKeyPending;
        gamingModeReadPending = gamingPending;

        if (!SupportsStartupAnimation || status.StartupAnimationEnabled is not null
            || StartupAnimationEnabled is null)
        {
            StartupAnimationEnabled = status.StartupAnimationEnabled;
        }

        if (!SupportsFunctionKeyPrimary || status.FunctionKeyPrimary is not null
            || FunctionKeyPrimary is null)
        {
            FunctionKeyPrimary = status.FunctionKeyPrimary;
        }

        if (!SupportsGamingMode || status.GamingModeEnabled is not null
            || GamingModeEnabled is null)
        {
            GamingModeEnabled = status.GamingModeEnabled;
        }

        if (pendingChanged)
        {
            NotifyFirmwareFieldLoadingState();
            NotifyCommands();
        }
    }

    private bool ShouldPreserveCapabilityPresentationDuringRefresh(
        ServiceStatusSnapshot status,
        ServiceStatusSnapshot? previousStatus,
        DeviceCapabilities incomingCapabilities) =>
        previousStatus is not null
        && (IsCheckingLaptopConnection(status)
            || IsRetainedPowerSourceRecheck(status))
        && HasAnyCapability(Capabilities)
        && !HasAnyCapability(incomingCapabilities);

    private static bool HasAnyCapability(DeviceCapabilities capabilities) =>
        capabilities.PerformancePresets.Count > 0
        || capabilities.FanModes.Count > 0
        || capabilities.LightingEffects.Count > 0
        || capabilities.ChargeLimits.Count > 0
        || capabilities.SupportsBatteryProtectionOff
        || capabilities.SupportsTemporaryFullCharge
        || capabilities.LogoLightingModes.Count > 0
        || capabilities.OneShotLaptopControls.Count > 0;

    private void NotifyPresentationState()
    {
        Notify(nameof(IsServiceUnavailable));
        Notify(nameof(ControlAccessStatus));
        Notify(nameof(CanRequestHardwareControl));
        Notify(nameof(CanReapplyChangedSettings));
        Notify(nameof(CanEditPerformance));
        Notify(nameof(CanToggleGpuClockTuning));
        Notify(nameof(CanEditGpuClockTuning));
        Notify(nameof(CanEditFanControl));
        NotifyGamingModeKeyPolicy();
        NotifyDashboardTelemetryValuesChanged();
        Notify(nameof(LiveReadingsRefreshToolTip));
        Notify(nameof(ActivePerformanceLabel));
        Notify(nameof(ActiveFanModeLabel));
        Notify(nameof(ProfileStateLabel));
        Notify(nameof(IsReadinessReady));
        Notify(nameof(IsReadinessChecking));
        Notify(nameof(IsReadinessWarning));
        Notify(nameof(IsReadinessError));
        Notify(nameof(HasLightingCapability));
        Notify(nameof(CanControlLighting));
        Notify(nameof(IsLightingControlUnavailable));
        Notify(nameof(IsLightingControlUnavailableNoticeVisible));
        Notify(nameof(LightingControlUnavailableMessage));
        Notify(nameof(ShowOpenBladeKeyboardLightingControls));
        Notify(nameof(HasActiveChanges));
        Notify(nameof(ActiveChangesText));
        Notify(nameof(HasAttention));
        Notify(nameof(AttentionSummary));
        Notify(nameof(AttentionBadgeLabel));
        Notify(nameof(AttentionDetails));
        Notify(nameof(TroubleshootingServiceStatus));
        Notify(nameof(TroubleshootingPowerSourceStatus));
        Notify(nameof(TroubleshootingControlsStatus));
        Notify(nameof(TroubleshootingKeyboardDriverModeStatus));
        Notify(nameof(TroubleshootingUsbDeviceStatus));
        Notify(nameof(TroubleshootingOtherRazerSoftwareStatus));
        Notify(nameof(AboutServiceVersionStatus));
        Notify(nameof(AboutDetectedLaptopStatus));
        Notify(nameof(DetectedFirmware));
        Notify(nameof(AboutBiosVersionStatus));
        Notify(nameof(AboutEcVersionStatus));
        Notify(nameof(AboutMcuVersionStatus));
        Notify(nameof(AboutVbiosVersionStatus));
        Notify(nameof(AboutBatteryHealthStatus));
        Notify(nameof(HasUntestedPlatformVersion));
        Notify(nameof(HasIncompatibleFirmware));
        Notify(nameof(PlatformCompatibilityWarningTitle));
        Notify(nameof(DashboardTestedPlatformWarningMessage));
        Notify(nameof(AboutTestedPlatformWarningMessage));
    }

    private void NotifyFirmwareFieldLoadingState()
    {
        Notify(nameof(IsStartupAnimationFieldLoading));
        Notify(nameof(CanEditStartupAnimation));
        Notify(nameof(IsFunctionKeyFieldLoading));
        Notify(nameof(CanEditFunctionKeyPrimary));
        Notify(nameof(IsGamingModeFieldLoading));
        Notify(nameof(CanEditGamingMode));
    }

    private TrayHoverLabelFormatResult BuildTrayHoverLabel() =>
        TrayHoverLabelFormatter.FormatDetailed(
            TrayHoverLabelItems
                .Where(option => option.IsEnabled)
                .Select(option => option.Kind),
            IsConnected,
            ActivePowerSource,
            SelectedPerformance,
            BatteryPowerFlowLabel,
            LiveReadings?.CpuTemperatureCelsius,
            LiveReadings?.GpuTemperatureCelsius,
            LiveReadings?.Fan1Rpm,
            LiveReadings?.Fan2Rpm);

    private void NotifyTrayHoverLabel()
    {
        Notify(nameof(TrayHoverLabelText));
        Notify(nameof(HasTrayHoverLabelOverflow));
        Notify(nameof(TrayHoverLabelOverflowMessage));
    }

    private void Notify(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

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
        Notify(propertyName);
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
        Notify(propertyName);
        Notify(nameof(ManualOverrideStatus));
        Notify(nameof(HasActiveChanges));
        Notify(nameof(ActiveChangesText));
        Notify(nameof(ProfileStateLabel));
    }

    private void SetOverrideField(
        ref PerformanceOverride? field,
        PerformanceOverride? value,
        [CallerMemberName] string? propertyName = null)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        Notify(propertyName);
        Notify(nameof(ManualOverrideStatus));
        Notify(nameof(HasActiveChanges));
        Notify(nameof(ActiveChangesText));
        Notify(nameof(ProfileStateLabel));
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

    private static void AddOverrideStatus(
        ICollection<string> status,
        string subsystem,
        PerformanceOverride? value,
        Func<PerformancePreset, string>? formatValue = null)
    {
        if (value is null)
        {
            return;
        }

        string lifetime = value.Lifetime == OverrideLifetime.Persistent
            ? "until reset"
            : "until the power source changes";
        string displayValue = formatValue?.Invoke(value.Value) ?? value.Value.ToString();
        if (value.CustomPerformance is { } custom)
        {
            displayValue += $" (CPU {custom.Cpu}, GPU {custom.Gpu})";
        }
        status.Add($"{subsystem}: {displayValue} {lifetime}");
    }

    private static PerformancePresetOption? FindPerformanceOption(
        IReadOnlyList<PerformancePresetOption> options,
        PerformancePreset value) =>
        options.FirstOrDefault(option => option.Value == value);

    private static PerformancePreset ResolvePresentedPerformance(
        ServiceStatusSnapshot status,
        ConfirmedDeviceState? confirmed,
        DeviceCapabilities capabilities)
    {
        if (status.PerformanceShortcutPreview is { } preview
            && capabilities.PerformancePresets.Contains(preview.Preset)
            && PerformancePresetPresentation.IsAvailable(
                status.ActivePowerSource,
                preview.Preset))
        {
            return preview.Preset;
        }

        PerformancePreset desired = status.DesiredState.Performance.Value;
        if (confirmed?.Performance is not { } observed)
        {
            return desired;
        }

        return capabilities.PerformancePresets.Contains(observed)
            && PerformancePresetPresentation.IsAvailable(status.ActivePowerSource, observed)
                ? observed
                : desired;
    }

    private static FanMode ResolvePresentedFanMode(
        ServiceStatusSnapshot status,
        ConfirmedDeviceState? confirmed,
        DeviceCapabilities capabilities)
    {
        FanMode desired = status.DesiredState.Fans.Value;
        if (confirmed?.Fans is not { } observed)
        {
            return desired;
        }

        if (desired is FanMode.Fixed or FanMode.ManualCurve or FanMode.Maximum
            && observed is FanMode.Fixed or FanMode.ManualCurve or FanMode.Maximum)
        {
            return desired;
        }

        bool sourceAllowsObserved = status.ActivePowerSource != PowerSource.UsbC
            || observed == FanMode.Automatic;
        return capabilities.FanModes.Contains(observed) && sourceAllowsObserved
            ? observed
            : desired;
    }

    private void LoadFanControlEditor(FanControlSettings value)
    {
        FanSpeedRange range = ActiveFanSpeedRange;
        SetField(
            ref fixedFanSpeedRpm,
            range.Quantize(value.FixedSpeedRpm),
            nameof(FixedFanSpeedRpm));
        SetField(
            ref useHigherFanTemperature,
            value.ManualCurve.UseHigherTemperatureForBoth,
            nameof(UseHigherFanTemperature));
        cpuFanCurveEditorPoints = value.ManualCurve.Cpu
            .Select(point => new FanCurveEditorPoint(new FanCurvePoint(
                point.TemperatureCelsius,
                range.Quantize(point.SpeedRpm))))
            .ToArray();
        gpuFanCurveEditorPoints = value.ManualCurve.Gpu
            .Select(point => new FanCurveEditorPoint(new FanCurvePoint(
                point.TemperatureCelsius,
                range.Quantize(point.SpeedRpm))))
            .ToArray();
        Notify(nameof(CpuFanCurveEditorPoints));
        Notify(nameof(GpuFanCurveEditorPoints));
        Notify(nameof(CurrentFanControlSettings));
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

        SetProfileEditorsDirty(true);
        SetField(ref field, value, propertyName);
        NotifyCommands();
    }

    private void SetLightingPowerPolicyEditorField<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        SetField(ref field, value, propertyName);
        lightingPowerPolicyEditorDirty = true;
        Notify(nameof(HasUnsavedLightingPowerPolicy));
        Notify(nameof(LightingPowerPolicyDraft));
        NotifyCommands();
    }

    private void SetProfileEditorsDirty(bool value)
    {
        if (profileEditorsDirty == value)
        {
            return;
        }

        profileEditorsDirty = value;
        Notify(nameof(HasProfileEditorChanges));
        Notify(nameof(HasUnsavedProfileChanges));
    }

    private bool SetLightingColorField(
        ref int field,
        int value,
        [CallerMemberName] string? propertyName = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 255);
        return SetLightingEditorField(ref field, value, propertyName);
    }

    private void SetLightingColorEditor(RgbColor value, bool secondary)
    {
        ref int red = ref (secondary
            ? ref lightingSecondaryRedEditor
            : ref lightingRedEditor);
        ref int green = ref (secondary
            ? ref lightingSecondaryGreenEditor
            : ref lightingGreenEditor);
        ref int blue = ref (secondary
            ? ref lightingSecondaryBlueEditor
            : ref lightingBlueEditor);
        if (red == value.Red && green == value.Green && blue == value.Blue)
        {
            return;
        }

        SetLightingEditorDirty(true);
        string prefix = secondary ? "LightingSecondary" : "Lighting";
        SetField(ref red, (int)value.Red, $"{prefix}RedEditor");
        SetField(ref green, (int)value.Green, $"{prefix}GreenEditor");
        SetField(ref blue, (int)value.Blue, $"{prefix}BlueEditor");
        Notify(
            secondary
                ? nameof(LightingSecondaryColorEditor)
                : nameof(LightingPrimaryColorEditor));
        Notify(nameof(LightingDraft));
        NotifyCommands();
    }

    private bool SetLightingEditorField<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        SetLightingEditorDirty(true);
        SetField(ref field, value, propertyName);
        Notify(nameof(LightingDraft));
        NotifyCommands();
        return true;
    }

    private void SetLightingEditorDirty(bool value)
    {
        if (lightingEditorDirty == value)
        {
            return;
        }

        lightingEditorDirty = value;
        Notify(nameof(IsLightingApplyPending));
    }

    private void NotifyLightingParameterPresentation()
    {
        Notify(nameof(LightingShowsBrightness));
        Notify(nameof(LightingShowsBreathingColorMode));
        Notify(nameof(LightingShowsStarlightColorMode));
        Notify(nameof(LightingShowsPrimaryColor));
        Notify(nameof(LightingShowsSecondaryColor));
        Notify(nameof(LightingPrimaryColorLabel));
        Notify(nameof(LightingPrimaryColorDescription));
        Notify(nameof(LightingShowsReactiveDuration));
        Notify(nameof(LightingShowsStarlightDuration));
        Notify(nameof(LightingShowsRandomColors));
        Notify(nameof(LightingShowsDirection));
        Notify(nameof(LightingDirectionOptions));
        Notify(nameof(SelectedLightingDirectionOption));
        Notify(nameof(LightingShowsRotationDirection));
        Notify(nameof(LightingShowsAmbientAwarenessRegion));
        Notify(nameof(LightingShowsAudioMeterControls));
    }

}

public sealed record LightingEffectOption(LightingEffect Effect, string Label);

public sealed record AmbientAwarenessRegionOption(
    AmbientAwarenessRegion Region,
    string Label);

public sealed record LogoLightingModeOption(LogoLightingMode? Mode, string Label);
