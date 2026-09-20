using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public readonly record struct ChargeLimitCommitRequest(int Value, long EditorRevision);

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

public sealed record ThermalControlPolicyOption(
    ThermalControlPolicy Policy,
    string Label);

public sealed record ProfileComparisonOption(
    PowerSource Source,
    string Label);

public sealed record WindowsPowerModeOption(
    WindowsPowerMode? Value,
    string Label,
    string Description);

public sealed record FunctionKeyPrimaryOption(
    FunctionKeyPrimaryMode Value,
    string Label,
    ApplyOneShotLaptopControl Action);

public sealed record LightingDirectionOption(
    LightingDirection Value,
    string Label);

public sealed partial class MainViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private static string UnavailableLabel => TrayText.Get("Unavailable");
    private static readonly TimeSpan ReconnectVisualGracePeriod =
        TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan ControlPresentationHoldGracePeriod =
        TimeSpan.FromSeconds(2);
    private static readonly HashSet<string> CompetingLightingControllers = new(
        ["RazerAppEngine", "Razer Synapse Service"],
        StringComparer.OrdinalIgnoreCase);

    private sealed record PendingPerformanceHold(
        PerformanceOverride Requested,
        PowerSource PowerSource,
        DateTimeOffset? ExpiresAt);

    private sealed record PendingFanControlHold(
        FanControlSettings Requested,
        PowerSource PowerSource,
        DateTimeOffset? ExpiresAt);

    private sealed record PendingLightingHold(
        LightingSettings SubmittedIntent,
        PowerSource PowerSource,
        long StartingRevision,
        DateTimeOffset ExpiresAt);

    private sealed record LocalizedOptionCatalog(
        IReadOnlyList<ProfileComparisonOption> Comparison,
        IReadOnlyList<ThermalControlPolicyOption> ThermalControlPolicies,
        IReadOnlyList<WindowsPowerModeOption> WindowsPowerModes,
        IReadOnlyList<FunctionKeyPrimaryOption> FunctionKeyPrimaryModes,
        IReadOnlyList<LightingEffectOption> LightingEffects,
        IReadOnlyList<LogoLightingModeOption> LogoLightingModes,
        IReadOnlyList<LightingDirectionOption> StandardLightingDirections,
        IReadOnlyList<LightingDirectionOption> TidalLightingDirections);

    private static readonly ConcurrentDictionary<string, LocalizedOptionCatalog>
        LocalizedOptionCatalogs = new(StringComparer.OrdinalIgnoreCase);

    private static LocalizedOptionCatalog CurrentLocalizedOptions =>
        LocalizedOptionCatalogs.GetOrAdd(
            TrayLocalization.ResourceCulture.Name,
            static cultureName =>
            {
                using IDisposable temporary =
                    TrayLocalization.BeginTemporaryLanguageOverride(cultureName);
                return CreateLocalizedOptionCatalog();
            });

    private static IReadOnlyList<ProfileComparisonOption> ComparisonOptions =>
        CurrentLocalizedOptions.Comparison;

    private static IReadOnlyList<ThermalControlPolicyOption> ThermalControlPolicyOptions =>
        CurrentLocalizedOptions.ThermalControlPolicies;

    private static IReadOnlyList<WindowsPowerModeOption> WindowsPowerModeOptions =>
        CurrentLocalizedOptions.WindowsPowerModes;

    private static IReadOnlyList<FunctionKeyPrimaryOption> FunctionKeyPrimaryOptions =>
        CurrentLocalizedOptions.FunctionKeyPrimaryModes;

    private static readonly IReadOnlyList<LightingEffect> KnownLightingEffects =
        Enum.GetValues<LightingEffect>()
            .Where(effect => Enum.IsDefined(effect) && effect != LightingEffect.Sequence)
            .Order()
            .ToArray();

    private static readonly IReadOnlyList<LogoLightingMode> KnownLogoLightingModes =
        Enum.GetValues<LogoLightingMode>()
            .Where(Enum.IsDefined)
            .Order()
            .ToArray();

    private static IReadOnlyList<LightingEffectOption> KnownLightingEffectOptions =>
        CurrentLocalizedOptions.LightingEffects;

    private static IReadOnlyList<LogoLightingModeOption> KnownLogoLightingModeOptions =>
        CurrentLocalizedOptions.LogoLightingModes;

    private static readonly IReadOnlyList<CustomPerformanceLevel> AllCustomPerformanceLevels =
        Enum.GetValues<CustomPerformanceLevel>();

    private static IReadOnlyList<LightingDirectionOption> StandardLightingDirectionOptions =>
        CurrentLocalizedOptions.StandardLightingDirections;

    private static IReadOnlyList<LightingDirectionOption> TidalLightingDirectionOptions =>
        CurrentLocalizedOptions.TidalLightingDirections;

    private static LocalizedOptionCatalog CreateLocalizedOptionCatalog() => new(
        Comparison:
        [
            new(PowerSource.PluggedIn, "AC"),
            new(PowerSource.UsbC, "USB-C"),
            new(PowerSource.Battery, TrayText.Get("On battery")),
        ],
        ThermalControlPolicies:
        [
            new(
                ThermalControlPolicy.MaintainDesiredState,
                TrayText.Get("Restore them")),
            new(
                ThermalControlPolicy.Strict,
                TrayText.Get("Notify me")),
        ],
        WindowsPowerModes:
        [
            new(
                null,
                ProfilePresentationFactory.FormatWindowsPowerMode(null),
                TrayText.Get("Keeps the current Windows setting.")),
            new(
                WindowsPowerMode.BestPowerEfficiency,
                ProfilePresentationFactory.FormatWindowsPowerMode(WindowsPowerMode.BestPowerEfficiency),
                TrayText.Get("Favors lower energy use.")),
            new(
                WindowsPowerMode.Balanced,
                ProfilePresentationFactory.FormatWindowsPowerMode(WindowsPowerMode.Balanced),
                TrayText.Get("Balances performance and energy use.")),
            new(
                WindowsPowerMode.BestPerformance,
                ProfilePresentationFactory.FormatWindowsPowerMode(WindowsPowerMode.BestPerformance),
                TrayText.Get("Favors responsiveness over energy use.")),
        ],
        FunctionKeyPrimaryModes:
        [
            new(
                FunctionKeyPrimaryMode.FunctionKeys,
                TrayText.Get("Function keys"),
                new ApplyOneShotLaptopControl(
                    OneShotLaptopControl.FunctionKeyPrimary,
                    FunctionKeyMode: FunctionKeyPrimaryMode.FunctionKeys)),
            new(
                FunctionKeyPrimaryMode.MultimediaKeys,
                TrayText.Get("Media keys"),
                new ApplyOneShotLaptopControl(
                    OneShotLaptopControl.FunctionKeyPrimary,
                    FunctionKeyMode: FunctionKeyPrimaryMode.MultimediaKeys)),
        ],
        LightingEffects: KnownLightingEffects
            .Select(CreateLightingEffectOption)
            .OrderBy(option => LightingEffectDisplayOrder(option.Effect))
            .ToArray(),
        LogoLightingModes: KnownLogoLightingModes
            .Select(mode => new LogoLightingModeOption(
                mode,
                LightingPresentation.LogoLightingModeLabel(mode)))
            .ToArray(),
        StandardLightingDirections:
        [
            new(LightingDirection.Leftward, TrayText.Get("Leftward")),
            new(LightingDirection.Rightward, TrayText.Get("Rightward")),
        ],
        TidalLightingDirections:
        [
            new(LightingDirection.Leftward, TrayText.Get("Outward")),
            new(LightingDirection.Rightward, TrayText.Get("Inward")),
        ]);

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
    private bool hasAppliedServiceState;
    private bool isReadOnly = true;
    private bool canControlHardware;
    private string connectionStatus = TrayText.Get("Service unavailable");
    private string operationStatusMessage = string.Empty;
    private string operationFeedbackMessage = string.Empty;
    private string troubleshootingRecentErrorStatus = string.Empty;
    private string controlAccessStatus = TrayText.Get("OpenBlade service isn’t running");
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
    private bool hasUnconfirmedWrites;
    private string unconfirmedWritesText = string.Empty;
    private bool hasPendingHyperBoostRestore;
    private ThermalConvergenceStatus? thermalConvergence;
    private bool isThermalPolicyOperationInProgress;
    private bool isThermalDisclosureAcknowledgementInProgress;
    private bool alwaysOverrideExternalLightingChanges;
    private LightingRefreshRate lightingRefreshRate = LightingRefreshRate.ThirtyHz;
    private bool allowUntestedFirmware;
    private KeyboardLightingControlMode keyboardLightingControlMode;
    private IReadOnlyList<PerformancePreset> availablePerformancePresets = [];
    private IReadOnlyList<PerformancePresetOption> availablePluggedInPerformanceOptions = [];
    private IReadOnlyList<PerformancePresetOption> availableBatteryPerformanceOptions = [];
    private IReadOnlyList<PerformancePresetOption> availableUsbCPerformanceOptions = [];
    private IReadOnlyList<LaptopPerformanceOption> availableLaptopPerformanceOptions = [];
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
    private bool isGpuAppCleanupVisible;
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
    private PendingFanControlHold? pendingFanControl;
    private bool isFanControlOperationInProgress;
    private bool isPerformanceOperationInProgress;
    private bool isHyperBoostOperationInProgress;
    private bool hyperBoostActive;
    private PendingLightingHold? pendingLighting;
    private bool isLightingOperationInProgress;
    private bool isChargeLimitOperationInProgress;
    private bool isBatteryProtectionOperationInProgress;
    private bool isFullChargeOperationInProgress;
    private bool isReapplyChangedSettingsOperationInProgress;
    private bool isHyperBoostRestoreRecoveryInProgress;
    private bool isCheckCurrentStateOperationInProgress;
    private bool isStartupAnimationOperationInProgress;
    private bool isFunctionKeyOperationInProgress;
    private bool isGamingModeOperationInProgress;
    private readonly FanControlEditorState fanControlEditor = new();
    private IReadOnlyList<LiveReadingGroupDisplayItem> visibleDashboardTelemetryGroups = [];
    private LightingSettings selectedLighting = ProfileDefaults.CreateSafeDefaults().PluggedIn.Lighting;
    private int chargeLimitPercent = 80;
    private long chargeLimitEditorRevision;
    private string currentBatterySettingLabel = UnavailableLabel;
    private bool chargeLimitEditorDirty;
    private IReadOnlyList<int> chargeLimitOptions = [];
    private bool batteryProtectionEnabled = true;
    private bool fullChargeOverride;
    private bool temporaryFullChargeStateKnown;
    private DeviceObservationState observation = new();
    private PowerSource activePowerSource = PowerSource.PluggedIn;
    private LiveReadings? liveReadings;
    private bool dashboardTelemetryPresentationActive = true;
    private bool trayDashboardTelemetryPresentationActive;
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
    private readonly LightingEditorState lightingEditor = new();
    private bool lightingTurnOffWithDisplayEditor;
    private bool lightingIdleTimeoutEnabledEditor;
    private int lightingIdleTimeoutMinutesEditor = 30;
    private bool lightingPowerPolicyEditorDirty;
    private readonly ActiveProfileDraftState activeProfileDraft = new();
    private BatteryPolicy? pendingBatteryPolicy;
    private ThemePreference theme = ThemePreference.FollowWindows;
    private UiLanguagePreference languagePreference = UiLanguagePreference.FollowSystem;
    private bool labFeaturesEnabled;
    private LiveReadingsCadence liveReadingsRefreshCadence =
        LiveReadingsCadence.EveryTwoSeconds;
    private TrayHoverLabelMode trayHoverLabelMode =
        TrayHoverLabelMode.ProfileAndPowerFlow;
    private bool startAtSignIn;
    private bool startupStateKnown;
    private string startupStatusMessage = string.Empty;
    private TrayIconState trayIconState = TrayIconState.GenericError;
    private int disposed;
    private long latestAcceptedRevision = -1;
    private long connectionGeneration;
    private long reconnectGeneration;
    private bool subscriptionConnected;
    private bool changingTelemetryPreferences;
    private bool suppressProfileDifferenceNotifications;
    private readonly Dictionary<string, object?> publishedComputedValues =
        new(StringComparer.Ordinal);

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
        lightingEditor.Changed += OnLightingEditorChanged;
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
                && ProfileAdmissionPolicy.LightingSupported(value, CommandCapabilities));
        SetChargeLimitCommand = CreateCommand(
            SetChargeLimitAsync,
            parameter => parameter switch
            {
                int value => CanSetChargeLimit(value),
                ChargeLimitCommitRequest request => CanSetChargeLimit(request.Value),
                _ => false,
            });
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
            _ => HasActiveProfileDraft
                || HasDashboardProfileDifference
                || HasGlobalBatteryPolicyDraft);
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
        SetThermalControlPolicyCommand = CreateCommand(
            async (parameter, token) =>
            {
                await SetThermalControlPolicyAsync(
                    ((ThermalControlPolicyOption)parameter!).Policy,
                    token);
            },
            CanSetThermalControlPolicy);
        AcknowledgeThermalPolicyUpgradeDisclosureCommand = CreateCommand(
            async (_, token) =>
            {
                await AcknowledgeThermalPolicyUpgradeDisclosureAsync(token);
            },
            _ => CanAcknowledgeThermalPolicyUpgradeDisclosure);
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
            (AsyncRelayCommand)SetThermalControlPolicyCommand,
            (AsyncRelayCommand)AcknowledgeThermalPolicyUpgradeDisclosureCommand,
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

    public bool HasAppliedServiceState
    {
        get => hasAppliedServiceState;
        private set => SetField(ref hasAppliedServiceState, value);
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
        ? FormatFanRpm(TrayText.Get("Fan 1"), LiveReadings?.Fan1Rpm)
        : UnavailableLabel;

    public string Fan2RpmLabel => IsConnected
        ? FormatFanRpm(TrayText.Get("Fan 2"), LiveReadings?.Fan2Rpm)
        : UnavailableLabel;

    public string Fan1RpmValue => FormatFanRpmValue(LiveReadings?.Fan1Rpm);

    public string Fan2RpmValue => FormatFanRpmValue(LiveReadings?.Fan2Rpm);

    public string CpuTemperatureValue =>
        FormatTemperatureValue(LiveReadings?.CpuTemperatureCelsius);

    public string GpuTemperatureValue =>
        FormatTemperatureValue(LiveReadings?.GpuTemperatureCelsius);

    public string TrayDashboardBatteryPowerFlowLabel =>
        TrayDashboardStatusPresentation.BatteryPower(
            IsConnected,
            LiveReadings?.BatteryPowerFlowMilliwatts,
            ActivePowerSource);

    public string TrayDashboardTemperatureLabel =>
        TrayDashboardStatusPresentation.Temperatures(
            IsConnected,
            LiveReadings?.CpuTemperatureCelsius,
            LiveReadings?.GpuTemperatureCelsius);

    public string TrayDashboardPowerSourceLabel =>
        TrayDashboardStatusPresentation.PowerSourceText(IsConnected, ActivePowerSource);

    public string TrayDashboardFanSpeedLabel =>
        TrayDashboardStatusPresentation.FanSpeed(
            IsConnected,
            LiveReadings?.Fan1Rpm,
            LiveReadings?.Fan2Rpm);

    public string TrayDashboardFanSpeedToolTip =>
        IsConnected
            ? TrayText.Format("{0}; {1}", Fan1RpmLabel, Fan2RpmLabel)
            : UnavailableLabel;

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
                    TrayText.Get("Battery"),
                    BatteryPowerFlowLabel));
            }

            string temperatureValue = FormatDashboardTemperatures(
                DashboardTelemetryEnabled(LiveReadingItemKind.CpuTemperature),
                LiveReadings?.CpuTemperatureCelsius,
                DashboardTelemetryEnabled(LiveReadingItemKind.GpuTemperature)
                    && LiveReadings?.GpuTemperatureCelsius is not null,
                LiveReadings?.GpuTemperatureCelsius);
            if (temperatureValue.Length > 0)
            {
                groups.Add(new LiveReadingGroupDisplayItem(
                    TrayText.Get("Temperatures"),
                    temperatureValue));
            }

            bool showFan1 = DashboardTelemetryEnabled(LiveReadingItemKind.Fan1Speed);
            bool showFan2 = DashboardTelemetryEnabled(LiveReadingItemKind.Fan2Speed);
            string fanValue = (showFan1, showFan2) switch
            {
                (true, true) when LiveReadings?.Fan1Rpm is null
                    && LiveReadings?.Fan2Rpm is null => UnavailableLabel,
                (true, true) =>
                    TrayText.Format("{0} / {1}", Fan1RpmValue, Fan2RpmValue),
                (true, false) => Fan1RpmValue,
                (false, true) => Fan2RpmValue,
                _ => string.Empty,
            };
            if (fanValue.Length > 0)
            {
                string label = (showFan1, showFan2) switch
                {
                    (true, true) => TrayText.Get("Fans"),
                    (true, false) => TrayText.Get("Fan 1"),
                    _ => TrayText.Get("Fan 2"),
                };
                groups.Add(new LiveReadingGroupDisplayItem(label, fanValue));
            }
            // Reuse the published list while its items are equal so an ItemsSource
            // bound to it is neither re-set nor re-templated on every refresh.
            visibleDashboardTelemetryGroups = StableOptionCollection.ReuseEquivalent(
                visibleDashboardTelemetryGroups,
                groups);
            return visibleDashboardTelemetryGroups;
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
        ? TrayText.Get(
            "Live readings are unavailable while the OpenBlade service is not running.")
        : lastLiveReadingsRefreshAt is { } refreshedAt
            ? TrayText.Format(
                "Last checked at {0:T}.{1}Values change only when Windows or firmware reports new data.",
                refreshedAt.ToLocalTime(),
                Environment.NewLine)
            : TrayText.Get("Waiting for the first live-reading check.");

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

            // Update the backing state before draft notifications so every
            // dependent property observes one coherent power-source snapshot.
            activePowerSource = value;

            if (activeProfileDraft.ChangePowerSource(value))
            {
                NotifyActiveProfileDraft();
            }

            Notify(nameof(ActivePowerSource));
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
            RefreshLaptopPerformanceOptions();
            Notify(nameof(SelectedWindowsPowerModeOption));
            Notify(nameof(SelectedWindowsPowerModeDescription));
            Notify(nameof(CanEditWindowsPowerMode));
            Notify(nameof(IsCustomPerformanceVisible));
            Notify(nameof(IsGpuClockTuningVisible));
            Notify(nameof(TrayDashboardGpuTuningLabel));
            Notify(nameof(IsGpuClockTuningAvailable));
            Notify(nameof(CanToggleGpuClockTuning));
            Notify(nameof(CanEditGpuClockTuning));
            Notify(nameof(CanToggleHyperBoost));
            Notify(nameof(HyperBoostDescription));
            NotifyHyperBoostAvailability();
            Notify(nameof(ActiveSavedDisplayProfile));
            Notify(nameof(SaveToActiveProfileLabel));
            NotifyGamingModeKeyPolicy();
            NotifyActiveFanSelectionBinding();
            Notify(nameof(BatteryPowerFlowLabel));
            Notify(nameof(TrayDashboardBatteryPowerFlowLabel));
            Notify(nameof(TrayDashboardPowerSourceLabel));
            NotifyTrayHoverLabel();
            NotifyCommands();
        }
    }

    public bool IsPluggedInProfileActive => ActivePowerSource == PowerSource.PluggedIn;

    public bool IsBatteryProfileActive => ActivePowerSource == PowerSource.Battery;

    public bool IsUsbCProfileActive => ActivePowerSource == PowerSource.UsbC;

    public string SaveToActiveProfileLabel => ActivePowerSource switch
    {
        PowerSource.PluggedIn => TrayText.Get("Save to AC profile"),
        PowerSource.Battery => TrayText.Get("Save to battery profile"),
        PowerSource.UsbC => TrayText.Get("Save to USB-C profile"),
        _ => TrayText.Get("Save to profile"),
    };

    public string DashboardProfileSaveLabel => HasActiveProfileDraft
        ? SaveToActiveProfileLabel
        : TrayText.Get("Save profile changes");

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
        ? HyperBoostActive
            ? TrayText.Get("HyperBoost")
            : PerformancePresetPresentation.Label(SelectedPerformance)
        : UnavailableLabel;

    public string ActiveFanModeLabel => IsConnected
        ? TrayText.Format("{0} fans", FanModePresentation.Label(SelectedFanMode))
        : UnavailableLabel;

    public string ProfileStateLabel => !IsConnected
        ? TrayText.Get("Service unavailable")
        : IsCheckingLaptopConnection(latestStatus)
            ? TrayText.Get("Checking laptop")
            : latestStatus?.DeviceVerified == false
                ? UnavailableLaptopReason
                : ActivePowerSource == PowerSource.ExternalUnknown
                    ? PowerSourcePresentation.Label(ActivePowerSource)
                    : CanControlHardware || IsRetainedPowerSourceRecheck(latestStatus)
                        ? TrayText.Get("Ready")
                        : TrayText.Get("Controls unavailable");

    public bool IsReadinessReady => IsConnected
        && latestStatus?.DeviceVerified == true
        && !IsCheckingLaptopConnection(latestStatus)
        && (CanControlHardware || IsRetainedPowerSourceRecheck(latestStatus));

    public bool IsReadinessChecking => IsConnected
        && IsCheckingLaptopConnection(latestStatus);

    public bool IsReadinessWarning => IsConnected
        && (IsFirmwareAdmissionBlocked
            || latestStatus?.DeviceVerified == true
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

    public bool HasUnconfirmedWrites
    {
        get => hasUnconfirmedWrites;
        private set
        {
            if (hasUnconfirmedWrites == value)
            {
                return;
            }
            SetField(ref hasUnconfirmedWrites, value);
            NotifyPresentationState();
        }
    }

    public string UnconfirmedWritesText
    {
        get => unconfirmedWritesText;
        private set => SetField(ref unconfirmedWritesText, value);
    }

    public bool IsCheckCurrentStateOperationInProgress
    {
        get => isCheckCurrentStateOperationInProgress;
        private set
        {
            if (isCheckCurrentStateOperationInProgress == value)
            {
                return;
            }
            SetField(ref isCheckCurrentStateOperationInProgress, value);
            Notify(nameof(CanCheckCurrentState));
        }
    }

    public bool CanCheckCurrentState => IsConnected
        && HasUnconfirmedWrites
        && !IsCheckCurrentStateOperationInProgress;

    public bool HasPendingHyperBoostRestore
    {
        get => hasPendingHyperBoostRestore;
        private set
        {
            if (hasPendingHyperBoostRestore == value)
            {
                return;
            }

            SetField(ref hasPendingHyperBoostRestore, value);
            NotifyPresentationState();
        }
    }

    public bool IsHyperBoostRestoreRecoveryInProgress
    {
        get => isHyperBoostRestoreRecoveryInProgress;
        private set
        {
            if (isHyperBoostRestoreRecoveryInProgress == value)
            {
                return;
            }

            SetField(ref isHyperBoostRestoreRecoveryInProgress, value);
            Notify(nameof(CanRecoverHyperBoostRestore));
        }
    }

    public bool CanRecoverHyperBoostRestore => IsConnected
        && HasPendingHyperBoostRestore
        && !HasUnconfirmedWrites
        && latestStatus is
        {
            ControlAccess: SessionControlAccess.Active,
            DeviceVerified: true,
            ActivePowerSource: PowerSource.PluggedIn,
            ControllerDetectionWarning: null,
        }
        && !HasBlockingHyperBoostController(latestStatus)
        && !IsHyperBoostRestoreRecoveryInProgress;

    public string HyperBoostRestoreRecoveryText => TrayText.Get(
        "HyperBoost is off, but the saved performance and fan setting was not confirmed. Retry restores that exact saved setting once; OpenBlade will not retry automatically.");

    public IReadOnlyList<ThermalControlPolicyOption> AvailableThermalControlPolicyOptions =>
        ThermalControlPolicyOptions;

    public ThermalControlPolicyOption SelectedThermalControlPolicyOption =>
        ThermalControlPolicyOptions.First(option => option.Policy ==
            (thermalConvergence?.Policy ?? ThermalControlPolicy.MaintainDesiredState));

    public bool IsThermalControlPolicyAvailable => thermalConvergence is not null;

    public bool IsThermalPolicyOperationInProgress
    {
        get => isThermalPolicyOperationInProgress;
        private set
        {
            if (isThermalPolicyOperationInProgress == value)
            {
                return;
            }

            SetField(ref isThermalPolicyOperationInProgress, value);
            Notify(nameof(CanChangeThermalControlPolicy));
            NotifyCommands();
        }
    }

    public bool CanChangeThermalControlPolicy => IsThermalControlPolicyAvailable
        && IsConnected
        && latestStatus?.ControlAccess == SessionControlAccess.Active
        && !IsThermalPolicyOperationInProgress;

    public bool HasThermalPolicyUpgradeDisclosure => thermalConvergence?.Disclosure
        == ThermalPolicyUpgradeDisclosureState.UpgradeDisclosurePending;

    public bool ShouldForceWindowVisibleForThermalDisclosure =>
        HasThermalPolicyUpgradeDisclosure;

    public bool IsThermalDisclosureAcknowledgementInProgress
    {
        get => isThermalDisclosureAcknowledgementInProgress;
        private set
        {
            if (isThermalDisclosureAcknowledgementInProgress == value)
            {
                return;
            }

            SetField(ref isThermalDisclosureAcknowledgementInProgress, value);
            Notify(nameof(CanAcknowledgeThermalPolicyUpgradeDisclosure));
            NotifyCommands();
        }
    }

    public bool CanAcknowledgeThermalPolicyUpgradeDisclosure =>
        HasThermalPolicyUpgradeDisclosure
        && IsConnected
        && latestStatus?.ControlAccess == SessionControlAccess.Active
        && !IsThermalDisclosureAcknowledgementInProgress;

    public string ThermalControlPolicyHeading =>
        TrayText.Get("If settings change outside OpenBlade");

    public string ThermalControlPolicyCommonNote => TrayText.Get(
        "Fan speed and curve changes cannot be detected.");

    public string ThermalAffectedComponentsLabel => TrayText.Get("Affected controls:");

    public string ThermalPolicyUpgradeDisclosureTitle => TrayText.Get(
        "OpenBlade now restores settings changed elsewhere");

    public string ThermalPolicyUpgradeDisclosureMessage => TrayText.Get(
        "If your performance mode or fan setting changes outside OpenBlade, it is now restored after safety checks. Restoration stays paused until you select Got it. Choose Notify me to have changes reported without restoring them. HyperBoost is never turned on automatically.");

    public string ThermalPolicyUpgradeDisclosureActionLabel => TrayText.Get("Got it");

    public string ThermalReapplyActionLabel => TrayText.Get("Reapply");

    public string ThermalConvergenceStateLabel => thermalConvergence?.State switch
    {
        ThermalConvergenceState.Matched => TrayText.Get("Up to date"),
        ThermalConvergenceState.AwaitingObservation => TrayText.Get("Checking current state"),
        ThermalConvergenceState.Converging => TrayText.Get("Restoring saved settings"),
        ThermalConvergenceState.Paused => TrayText.Get("Restoration paused"),
        ThermalConvergenceState.Suppressed => TrayText.Get("Needs you to reapply"),
        ThermalConvergenceState.External => TrayText.Get("Changed outside OpenBlade"),
        ThermalConvergenceState.RestorePending => TrayText.Get("Restore pending"),
        ThermalConvergenceState.ReconciliationUnavailable => TrayText.Get(
            "Cannot confirm the change"),
        _ => TrayText.Get("Unavailable"),
    };

    public string ThermalConvergenceDetail => thermalConvergence?.Reason switch
    {
        ThermalConvergenceReason.None when thermalConvergence.State ==
            ThermalConvergenceState.Converging => TrayText.Get(
            "OpenBlade is restoring your saved settings and will confirm the result."),
        ThermalConvergenceReason.None => TrayText.Get(
            "Your saved settings match the laptop."),
        ThermalConvergenceReason.StrictPolicy => TrayText.Get(
            "Changes made elsewhere are reported, not restored. Fan curve safety maintenance continues."),
        ThermalConvergenceReason.UpgradeDisclosurePending => TrayText.Get(
            "Review the new default before OpenBlade starts restoring settings in the background."),
        ThermalConvergenceReason.ConfigurationUnavailable => TrayText.Get(
            "The saved performance and fan settings are unavailable."),
        ThermalConvergenceReason.ConfigurationQuarantined => TrayText.Get(
            "The saved performance and fan settings could not be trusted. Changes made elsewhere are only reported."),
        ThermalConvergenceReason.InactiveSession => TrayText.Get(
            "Restoration waits for this Windows session to become active."),
        ThermalConvergenceReason.ProtocolCompatibilityClient => TrayText.Get(
            "An older OpenBlade component is connected. Update or repair OpenBlade to resume restoring settings."),
        ThermalConvergenceReason.AwaitingFreshObservation => TrayText.Get(
            "OpenBlade is waiting for a fresh, complete read of the laptop state."),
        ThermalConvergenceReason.DeviceUnavailable => TrayText.Get(
            "The laptop control device is temporarily unavailable."),
        ThermalConvergenceReason.DeviceIdentityBlocked => TrayText.Get(
            "OpenBlade could not verify which laptop this is."),
        ThermalConvergenceReason.PowerUnsettled => TrayText.Get(
            "Restoration waits until the power source is confirmed and stable."),
        ThermalConvergenceReason.LowPower => TrayText.Get(
            "Restoration is paused while Windows enters or leaves low power."),
        ThermalConvergenceReason.CompetingControllerPresent => TrayText.Get(
            "Another Razer controller is active, so OpenBlade will not compete for these settings."),
        ThermalConvergenceReason.CompetingControllerUncertain => TrayText.Get(
            "OpenBlade could not confirm whether another Razer controller is active."),
        ThermalConvergenceReason.HyperBoostActive => TrayText.Get(
            "HyperBoost controls performance and fans right now."),
        ThermalConvergenceReason.HyperBoostPending => TrayText.Get(
            "OpenBlade is finishing the HyperBoost change before restoring other settings."),
        ThermalConvergenceReason.ManualFanReadbackAmbiguous => TrayText.Get(
            "The laptop reports manual fan control without the exact curve, so OpenBlade will not guess it."),
        ThermalConvergenceReason.PendingReconciliation => TrayText.Get(
            "A change that may have been sent must be confirmed before OpenBlade tries again."),
        ThermalConvergenceReason.BudgetExhausted => TrayText.Get(
            "Automatic restoration stopped after its one attempt. Select Reapply to try again."),
        ThermalConvergenceReason.Tombstoned => TrayText.Get(
            "This change is recorded as unresolved. Check the laptop, then select Reapply."),
        ThermalConvergenceReason.Shutdown => TrayText.Get(
            "Restoration is closed while OpenBlade shuts down."),
        ThermalConvergenceReason.InternalStateUnavailable => TrayText.Get(
            "OpenBlade cannot safely save or restore this state right now."),
        _ => TrayText.Get("The performance and fan status is unavailable."),
    };

    public string ThermalConvergenceAffectedComponents =>
        thermalConvergence?.AffectedComponents?.Distinct().Order().ToArray() switch
        {
            [ThermalComponent.Performance] => TrayText.Get("Performance"),
            [ThermalComponent.Fans] => TrayText.Get("Fans"),
            [ThermalComponent.Performance, ThermalComponent.Fans] => TrayText.Get(
                "Performance and fans"),
            _ => string.Empty,
        };

    public bool HasThermalConvergenceAffectedComponents =>
        !string.IsNullOrEmpty(ThermalConvergenceAffectedComponents);

    /// <summary>
    /// Gets a value indicating whether the thermal state row has something to
    /// report. A matched state is the ordinary case and stays hidden so the row
    /// is not permanent furniture that reads "up to date" and trains the user to
    /// ignore it.
    /// </summary>
    public bool HasThermalConvergenceAttention =>
        thermalConvergence is not null
        && thermalConvergence.State != ThermalConvergenceState.Matched;

    public bool ThermalConvergenceExplicitReapplyRequired =>
        thermalConvergence?.ExplicitReapplyRequired == true;

    public bool ShowReapplyChangedSettings => (HasExternalChanges
        || ThermalConvergenceExplicitReapplyRequired)
        && !HasUnconfirmedWrites;

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

    public bool CanReapplyChangedSettings => (HasExternalChanges
        || ThermalConvergenceExplicitReapplyRequired)
        && !HasUnconfirmedWrites
        && CanControlHardware
        && (HasReapplicableExternalChanges || ThermalConvergenceExplicitReapplyRequired)
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

    public LightingRefreshRate LightingRefreshRate
    {
        get => lightingRefreshRate;
        private set
        {
            if (lightingRefreshRate != value)
            {
                SetField(ref lightingRefreshRate, value);
                Notify(nameof(IsSixtyHzLightingEnabled));
            }
        }
    }

    public bool IsSixtyHzLightingEnabled =>
        LightingRefreshRate == LightingRefreshRate.SixtyHz;

    public bool SupportsSixtyHzLighting =>
        Capabilities.SupportedLightingRefreshRates.Contains(
            LightingRefreshRate.SixtyHz);

    public bool CanSetLightingRefreshRate =>
        SupportsSixtyHzLighting
        && CanControlLighting
        && !UseWindowsDynamicLighting;

    public string LightingRefreshRateAvailabilityText => SupportsSixtyHzLighting
        ? TrayText.Get("Render supported animated effects at 60 Hz. Uses more CPU and power.")
        : TrayText.Get("60 Hz lighting is not available for this keyboard.");

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
                Notify(nameof(CanSetLightingRefreshRate));
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

    public async Task<bool> SetThermalControlPolicyAsync(
        ThermalControlPolicy policy,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(policy)
            || !CanChangeThermalControlPolicy
            || thermalConvergence?.Policy == policy)
        {
            return false;
        }

        IsThermalPolicyOperationInProgress = true;
        try
        {
            return await RunOperationAsync(
                token => serviceClient.SetThermalControlPolicyAsync(policy, token),
                cancellationToken);
        }
        finally
        {
            IsThermalPolicyOperationInProgress = false;
        }
    }

    public async Task<bool> AcknowledgeThermalPolicyUpgradeDisclosureAsync(
        CancellationToken cancellationToken)
    {
        if (!CanAcknowledgeThermalPolicyUpgradeDisclosure)
        {
            return false;
        }

        IsThermalDisclosureAcknowledgementInProgress = true;
        try
        {
            return await RunOperationAsync(
                serviceClient.AcknowledgeThermalPolicyUpgradeDisclosureAsync,
                cancellationToken);
        }
        finally
        {
            IsThermalDisclosureAcknowledgementInProgress = false;
        }
    }

    public async Task<bool> SetLightingRefreshRateAsync(
        LightingRefreshRate refreshRate,
        CancellationToken cancellationToken)
    {
        if (!LabFeaturesEnabled
            || !Capabilities.SupportedLightingRefreshRates.Contains(refreshRate)
            || !CanControlLighting
            || UseWindowsDynamicLighting)
        {
            return false;
        }

        bool accepted = await RunOperationAsync(
            token => serviceClient.SetLightingRefreshRateAsync(refreshRate, token),
            cancellationToken);
        if (accepted)
        {
            await dispatcher.InvokeAsync(
                () => LightingRefreshRate = refreshRate,
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

    public async Task<bool> RecoverHyperBoostRestoreAsync(
        CancellationToken cancellationToken)
    {
        if (!CanRecoverHyperBoostRestore)
        {
            return false;
        }

        IsHyperBoostRestoreRecoveryInProgress = true;
        try
        {
            return await RunOperationAsync(
                serviceClient.RecoverHyperBoostRestoreAsync,
                cancellationToken);
        }
        finally
        {
            IsHyperBoostRestoreRecoveryInProgress = false;
        }
    }

    public async Task<bool> CheckCurrentStateAsync(CancellationToken cancellationToken)
    {
        if (!CanCheckCurrentState)
        {
            return false;
        }

        IsCheckCurrentStateOperationInProgress = true;
        try
        {
            ObservationRequestStamp stamp = new(
                connectionGeneration,
                subscriptionConnected);
            ServiceStatusSnapshot status = await serviceClient
                .ObserveStateAsync(LiveReadingSensors.None, cancellationToken);
            bool accepted = false;
            await dispatcher.InvokeAsync(
                () => accepted = ApplyObservedStatus(status, stamp, trayOnly: false),
                cancellationToken);
            return accepted;
        }
        finally
        {
            IsCheckCurrentStateOperationInProgress = false;
        }
    }

    public bool HasAttention => IsConnected
        && (HasControllerAdvisory
            || HasControllerDetectionWarning
            || HasExternalChanges
            || HasUnconfirmedWrites
            || HasPendingHyperBoostRestore
            || HasThermalPolicyUpgradeDisclosure);

    public string AttentionSummary
    {
        get
        {
            if (!HasAttention)
            {
                return string.Empty;
            }
            if (HasUnconfirmedWrites)
            {
                return TrayText.Get("State not confirmed");
            }
            if (HasThermalPolicyUpgradeDisclosure)
            {
                return TrayText.Get("Review thermal control policy");
            }
            if (HasPendingHyperBoostRestore)
            {
                return TrayText.Get("HyperBoost restore needs confirmation");
            }
            if (HasExternalChanges)
            {
                return TrayText.Get("Laptop controls changed outside OpenBlade");
            }
            if (HasControllerDetectionWarning)
            {
                return TrayText.Get("Controller detection needs attention");
            }
            return TrayText.Get("Another Razer app is running");
        }
    }

    public string AttentionBadgeLabel => !HasAttention
        ? string.Empty
        : HasUnconfirmedWrites
            ? TrayText.Get("State not confirmed")
            : HasThermalPolicyUpgradeDisclosure
            ? TrayText.Get("Review policy")
            : HasPendingHyperBoostRestore
            ? TrayText.Get("Recovery needed")
            : HasExternalChanges
            ? TrayText.Get("Changed outside OpenBlade")
            : HasControllerDetectionWarning
                ? TrayText.Get("Check status")
                : TrayText.Get("Razer app active");

    public string AttentionDetails
    {
        get
        {
            if (!HasAttention)
            {
                return string.Empty;
            }
            List<string> details = [];
            if (HasUnconfirmedWrites)
            {
                details.Add(UnconfirmedWritesText);
            }
            if (HasPendingHyperBoostRestore)
            {
                details.Add(HyperBoostRestoreRecoveryText);
            }
            if (HasThermalPolicyUpgradeDisclosure)
            {
                details.Add(TrayText.Get(
                    "Review the new default. Restoration stays paused until you select Got it."));
            }
            if (HasExternalChanges)
            {
                details.Add(ExternalChangesText);
            }
            if (HasControllerAdvisory)
            {
                details.Add(TrayText.Get(
                    "Another Razer app can also change these settings. OpenBlade will show changes instead of changing them back."));
            }
            if (HasControllerDetectionWarning)
            {
                details.Add(TrayText.Get(
                    "OpenBlade could not check every Razer controller. Controls remain available only when the laptop is verified."));
            }
            return string.Join("\n", details);
        }
    }

    public string TroubleshootingServiceStatus => IsConnected
        ? TrayText.Get("Running")
        : TrayText.Get("Unavailable");

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
                return TrayText.Get("Unavailable");
            }

            string? modelNumber = latestStatus?.Firmware?.ModelNumber;
            if (HasConcreteFirmwareValue(modelNumber))
            {
                RazerBladeModelInfo? model = RazerBladeModelCatalog.Find(modelNumber);
                return model is null
                    ? FirmwareValue(modelNumber)
                    : TrayText.Format("{0}, {1}", model.DisplayName, model.ModelNumber);
            }

            return latestStatus?.DeviceVerified == true
                ? TrayText.Get("Verified Razer Blade")
                : TrayText.Get("Not detected");
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
                : TrayText.Format(
                    "VID {0} · PID {1}",
                    model.UsbVendorId,
                    model.UsbProductId);
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

    public string AboutDisplayBrightnessDriverStatus => !IsConnected
        ? UnavailableLabel
        : latestStatus?.DisplayBrightnessHidAvailability switch
        {
            DisplayBrightnessHidAvailability.Available =>
                TrayText.Get("Available · Windows OSD"),
            DisplayBrightnessHidAvailability.Unavailable =>
                TrayText.Get("Not available · OpenBlade OSD"),
            _ => TrayText.Get("Unknown"),
        };

    public string AboutDisplayBrightnessDriverToolTip =>
        TrayText.Get(
            "The exact admitted Razer driver lets Windows show its native brightness flyout. OpenBlade revalidates the exact device path for every key press. When the native path is unavailable, OpenBlade uses its own flyout.");

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
        ? TrayText.Get("Incompatible firmware")
        : TrayText.Get("Laptop not found");

    private bool IsFirmwareAdmissionBlocked =>
        latestStatus?.FirmwareCompatibility
            == FirmwareCompatibilityStatus.UntestedBlocked;

    public string PlatformCompatibilityWarningTitle =>
        CurrentTestedPlatformAssessment.Mismatches.Any(mismatch =>
            string.Equals(
                mismatch.Component,
                "Model",
                StringComparison.OrdinalIgnoreCase))
            ? TrayText.Get("Incompatible hardware")
            : HasIncompatibleFirmware
                ? TrayText.Get("Incompatible firmware")
                : TrayText.Get("Untested platform configuration");

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
                    ? TrayText.Format(
                        AllowUntestedFirmware
                            ? "{0} This is a compatibility notice, not a hardware failure. The firmware override is enabled, and OpenBlade is checking control access."
                            : "{0} This is a compatibility notice, not a hardware failure. Controls remain blocked unless you explicitly enable the firmware override.",
                        warning)
                    : latestStatus?.DeviceVerified != true
                        ? TrayText.Format(
                            "{0} {1}",
                            warning,
                            TrayText.Get(
                                "Laptop controls are currently unavailable. Check Troubleshooting for details."))
                    : TrayText.Format(
                        "{0} This is a compatibility notice, not a hardware failure. Controls remain available, but behavior may differ.",
                        warning);
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
        string.IsNullOrWhiteSpace(value) ? TrayText.Get("Unavailable") : value.Trim();

    public string TroubleshootingPowerSourceStatus => !IsConnected
        ? TrayText.Get("Unavailable")
        : PowerSourcePresentation.TroubleshootingLabel(ActivePowerSource);

    public string TroubleshootingControlsStatus => IsConnected
        ? ControlAccessStatus
        : TrayText.Get("Unavailable");

    public string TroubleshootingKeyboardDriverModeStatus => !IsConnected
        ? TrayText.Get("Unavailable")
        : latestStatus?.KeyboardDriverModeActive switch
        {
            true => TrayText.Get("Loaded"),
            false => TrayText.Get("Not loaded"),
            _ => TrayText.Get("Unknown"),
        };

    public string TroubleshootingOtherRazerSoftwareStatus
    {
        get
        {
            string running = string.Join(", ", latestStatus?.PotentialControllers ?? []);
            if (!HasControllerDetectionWarning)
            {
                return string.IsNullOrWhiteSpace(running)
                    ? TrayText.Get("None")
                    : running;
            }

            string warning = FormatTroubleshootingValue(ControllerDetectionWarningText);
            return string.IsNullOrWhiteSpace(running)
                ? TrayText.Format("Could not check: {0}", warning)
                : TrayText.Format("{0}; check incomplete: {1}", running, warning);
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

    public IReadOnlyList<LaptopPerformanceOption> AvailableLaptopPerformanceOptions
    {
        get => availableLaptopPerformanceOptions;
        private set => SetField(ref availableLaptopPerformanceOptions, value);
    }

    public PerformancePresetOption? SelectedPerformanceOption =>
        FindPerformanceOption(AvailableActivePerformanceOptions, SelectedPerformance);

    public LaptopPerformanceOption? SelectedLaptopPerformanceOption =>
        HyperBoostActive
            ? AvailableLaptopPerformanceOptions.FirstOrDefault(option => option.IsHyperBoost)
            : AvailableLaptopPerformanceOptions.FirstOrDefault(
                option => option.Value == SelectedPerformance);

    public void RestoreLaptopPerformanceSelection() =>
        Notify(nameof(SelectedLaptopPerformanceOption));

    public void RestoreFanModeSelection() =>
        Notify(nameof(SelectedFanModeOption));

    public IReadOnlyList<WindowsPowerModeOption> AvailableWindowsPowerModeOptions =>
        WindowsPowerModeOptions;

    private WindowsPowerModeAssociations PresentedWindowsPowerModes =>
        activeProfileDraft.WindowsPowerModes ?? Profiles.WindowsPowerModes;

    public WindowsPowerModeOption SelectedWindowsPowerModeOption
    {
        get
        {
            WindowsPowerMode? selected =
                PresentedWindowsPowerModes.ForPerformance(SelectedPerformance);
            return WindowsPowerModeOptions.First(option => option.Value == selected);
        }
    }

    public string SelectedWindowsPowerModeDescription =>
        SelectedWindowsPowerModeOption.Value is null
            ? TrayText.Format(
                "Leaves the Windows setting unchanged whenever {0} is selected.",
                PerformancePresetPresentation.Label(SelectedPerformance))
            : TrayText.Format(
                "Used whenever {0} is selected. {1}",
                PerformancePresetPresentation.Label(SelectedPerformance),
                SelectedWindowsPowerModeOption.Description);

    public bool CanEditWindowsPowerMode =>
        CanMutate && HasConfirmedPowerSource;

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

    public bool IsGpuAppCleanupVisible
    {
        get => isGpuAppCleanupVisible;
        private set => SetField(ref isGpuAppCleanupVisible, value);
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
                return TrayText.Get("Windows Dynamic Lighting owns the keyboard. OpenBlade will not change keyboard brightness, effects, or lighting power policies.");
            }

            if (!IsConnected)
            {
                return TrayText.Get("OpenBlade can’t connect to its service, so lighting controls are unavailable.");
            }

            if (latestStatus?.DeviceVerified != true)
            {
                return IsFirmwareAdmissionBlocked
                    ? TrayText.Get("OpenBlade found a supported laptop with incompatible firmware, so lighting controls are unavailable unless you enable the firmware override.")
                    : TrayText.Get("OpenBlade can’t find a supported laptop, so lighting controls are unavailable.");
            }

            if (!HasLightingCapability && latestStatus.PotentialControllers.Count > 0)
            {
                return TrayText.Get("Another Razer app may be using the laptop’s lighting interface. OpenBlade will enable these controls when it becomes available.");
            }

            if (!HasLightingCapability && HasControllerDetectionWarning)
            {
                return TrayText.Get("OpenBlade couldn’t confirm whether another Razer app is using the laptop’s lighting interface. Controls will remain disabled until access is available.");
            }

            if (!HasLightingCapability)
            {
                return TrayText.Get("OpenBlade couldn’t access the laptop’s lighting interface. Another lighting app may be using it.");
            }

            if (ActivePowerSource == PowerSource.ExternalUnknown)
            {
                return TrayText.Get("OpenBlade is checking the laptop before enabling lighting controls.");
            }

            return TrayText.Get("Laptop controls are currently unavailable. Check Troubleshooting for details.");
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
            RefreshLaptopPerformanceOptions();
            Notify(nameof(SelectedLaptopPerformanceOption));
            Notify(nameof(SelectedWindowsPowerModeOption));
            Notify(nameof(SelectedWindowsPowerModeDescription));
            Notify(nameof(IsSilentSelected));
            Notify(nameof(IsBalancedSelected));
            Notify(nameof(IsPerformanceSelected));
            Notify(nameof(IsCustomPerformanceVisible));
            Notify(nameof(IsGpuClockTuningVisible));
            Notify(nameof(TrayDashboardGpuTuningLabel));
            Notify(nameof(IsGpuClockTuningAvailable));
            Notify(nameof(CanToggleGpuClockTuning));
            Notify(nameof(CanEditGpuClockTuning));
            Notify(nameof(CanToggleHyperBoost));
            Notify(nameof(HyperBoostDescription));
            NotifyHyperBoostAvailability();
            Notify(nameof(CanEditFanControl));
            Notify(nameof(CanChangeFanMode));
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
            Notify(nameof(TrayDashboardGpuTuningLabel));
        }
    }

    public string TrayDashboardGpuTuningLabel => !IsGpuClockTuningVisible
        ? UnavailableLabel
        : IsGpuClockTuningEnabled
            ? TrayText.Get("On")
            : TrayText.Get("Off");

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
            // The mode is an input to CurrentFanControlSettings, and the editor
            // load announces that property only when a curve or speed changed.
            Notify(nameof(CurrentFanControlSettings));
            Notify(nameof(ActiveFanModeLabel));
            Notify(nameof(SelectedFanModeOption));
            Notify(nameof(CanChangeFanMode));
            Notify(nameof(IsFixedFanSpeedVisible));
            Notify(nameof(IsManualFanCurveVisible));
            Notify(nameof(CanToggleHyperBoost));
            Notify(nameof(HyperBoostDescription));
            NotifyHyperBoostAvailability();
        }
    }

    public int FixedFanSpeedRpm
    {
        get => fanControlEditor.FixedSpeedRpm;
        set => NotifyFanControlEditorChanges(
            fanControlEditor.SetFixedSpeed(value, ActiveFanSpeedRange));
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
        get => fanControlEditor.UseHigherTemperature;
        set => NotifyFanControlEditorChanges(fanControlEditor.SetUseHigherTemperature(value));
    }

    public IReadOnlyList<FanCurveEditorPoint> CpuFanCurveEditorPoints =>
        fanControlEditor.CpuPoints;

    public IReadOnlyList<FanCurveEditorPoint> GpuFanCurveEditorPoints =>
        fanControlEditor.GpuPoints;

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
            Notify(nameof(CanChangeFanMode));
        }
    }

    public bool CanEditFanControl =>
        CanRequestHardwareControl
        && !IsFanControlOperationInProgress
        && !HyperBoostActive;

    public bool CanChangeFanMode =>
        CanEditFanControl
        && (AvailableActiveFanModeOptions.Count > 1
            || SelectedFanModeOption is null);

    public bool CanEditPerformance =>
        CanRequestHardwareControl
        && !IsPerformanceOperationInProgress
        && !HyperBoostActive;

    public bool CanSelectLaptopPerformance =>
        !IsPerformanceOperationInProgress
        && !IsHyperBoostOperationInProgress
        && (HyperBoostActive ? CanToggleHyperBoost : CanEditPerformance);

    public bool IsLaptopPerformanceOperationInProgress =>
        IsPerformanceOperationInProgress || IsHyperBoostOperationInProgress;

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
                Notify(nameof(CanSelectLaptopPerformance));
                Notify(nameof(IsLaptopPerformanceOperationInProgress));
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
            observation.StartupAnimationReadPending);

    public bool IsFunctionKeyFieldLoading =>
        IsOneShotFirmwareFieldLoading(
            OneShotLaptopControl.FunctionKeyPrimary,
            isFunctionKeyOperationInProgress,
            observation.FunctionKeyReadPending);

    public bool IsGamingModeFieldLoading =>
        IsOneShotFirmwareFieldLoading(
            OneShotLaptopControl.GamingMode,
            isGamingModeOperationInProgress,
            observation.GamingModeReadPending);

    public bool CanEditStartupAnimation =>
        CanMutate && SupportsStartupAnimation && !IsStartupAnimationFieldLoading;

    public bool CanEditFunctionKeyPrimary =>
        CanMutate && SupportsFunctionKeyPrimary && !IsFunctionKeyFieldLoading;

    public bool CanEditGamingMode =>
        CanMutate && SupportsGamingMode && !IsGamingModeFieldLoading;

    public FanControlSettings CurrentFanControlSettings =>
        fanControlEditor.ToSettings(SelectedFanMode);

    public void UpdateFanCurvePoint(FanCurveEditorPoint point, int speedRpm) =>
        NotifyFanControlEditorChanges(
            fanControlEditor.UpdatePoint(point, speedRpm, ActiveFanSpeedRange));

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
            chargeLimitEditorRevision++;
            SetField(ref chargeLimitPercent, value);
            Notify(nameof(TrayDashboardChargeLimitLabel));
            NotifyCommands();
        }
    }

    public bool CanAdjustChargeLimit => CanSetChargeLimit(ChargeLimitPercent);

    public long ChargeLimitEditorRevision => chargeLimitEditorRevision;

    public void RevertUnappliedChargeLimitEdit(
        int requestedLimit,
        long editorRevision)
    {
        if (chargeLimitPercent != requestedLimit
            || chargeLimitEditorRevision != editorRevision)
        {
            return;
        }

        chargeLimitEditorDirty = false;
        if (latestStatus is not null)
        {
            LoadChargeLimitEditor(latestStatus);
        }
        NotifyCommands();
    }

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
            Notify(nameof(TrayDashboardChargeLimitLabel));
        }
    }

    public string BatteryProtectionActionLabel => BatteryProtectionEnabled
        ? TrayText.Get("Turn off battery protection")
        : TrayText.Get("Restore charge limit");

    public string TrayDashboardChargeLimitLabel => BatteryProtectionEnabled
        ? TrayText.Format("{0} %", ChargeLimitPercent)
        : TrayText.Get("Off");

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
            ? TrayText.Get("Unavailable")
            : FullChargeOverride
                ? TrayText.Get("Cancel charge to 100%")
                : TrayText.Get("Charge to 100% until unplugged");

    public DeviceCapabilities Capabilities
    {
        get => observation.Capabilities;
        private set
        {
            if (CapabilitiesEqual(observation.Capabilities, value))
            {
                return;
            }

            observation = observation with { Capabilities = value };
            Notify(nameof(Capabilities));
            RefreshCustomPerformanceOptions();
            if (fanControlEditor.SetFixedSpeed(FixedFanSpeedRpm, ActiveFanSpeedRange)
                != FanControlEditorChanges.None)
            {
                Notify(nameof(FixedFanSpeedRpm));
            }
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
            Notify(nameof(TrayDashboardGpuTuningLabel));
            Notify(nameof(IsGpuClockTuningAvailable));
            Notify(nameof(CanToggleGpuClockTuning));
            Notify(nameof(CanEditGpuClockTuning));
            Notify(nameof(IsHyperBoostVisible));
            Notify(nameof(IsHyperBoostWritable));
            Notify(nameof(CanToggleHyperBoost));
            Notify(nameof(HyperBoostDescription));
            NotifyHyperBoostAvailability();
            Notify(nameof(IsSpecialKeyBindingsEditable));
            Notify(nameof(IsWindowsDynamicLightingAvailable));
            Notify(nameof(CanControlLighting));
            Notify(nameof(SupportsSixtyHzLighting));
            Notify(nameof(CanSetLightingRefreshRate));
            Notify(nameof(LightingRefreshRateAvailabilityText));
            NotifyGamingModeKeyPolicy();
            NotifyFirmwareFieldLoadingState();
        }
    }

    public bool HasOneShotLaptopControls => Capabilities.OneShotLaptopControls.Count > 0;

    public bool IsHyperBoostVisible =>
        Capabilities.HyperBoostAccess is not null
        || Capabilities.HyperBoostWithoutCoolingPadAccess is not null;

    public bool IsHyperBoostWritable =>
        Capabilities.HyperBoostWithoutCoolingPadAccess == DeviceFeatureAccess.ReadWrite;

    public bool HyperBoostActive
    {
        get => hyperBoostActive;
        private set
        {
            if (hyperBoostActive == value)
            {
                return;
            }

            SetField(ref hyperBoostActive, value);
            RefreshLaptopPerformanceOptions();
            Notify(nameof(CanToggleHyperBoost));
            Notify(nameof(HyperBoostDescription));
            NotifyHyperBoostAvailability();
            Notify(nameof(CanEditPerformance));
            Notify(nameof(CanSelectLaptopPerformance));
            Notify(nameof(SelectedLaptopPerformanceOption));
            Notify(nameof(ActivePerformanceLabel));
            NotifyTrayHoverLabel();
            Notify(nameof(CanEditFanControl));
            Notify(nameof(CanChangeFanMode));
            NotifyCommands();
        }
    }

    public bool IsHyperBoostOperationInProgress
    {
        get => isHyperBoostOperationInProgress;
        private set
        {
            if (isHyperBoostOperationInProgress == value)
            {
                return;
            }

            SetField(ref isHyperBoostOperationInProgress, value);
            Notify(nameof(CanToggleHyperBoost));
            NotifyHyperBoostAvailability();
            Notify(nameof(CanSelectLaptopPerformance));
            Notify(nameof(IsLaptopPerformanceOperationInProgress));
            RefreshLaptopPerformanceOptions();
        }
    }

    public bool CanToggleHyperBoost =>
        CanMutate
        && !IsRetainedPowerSourceRecheck(latestStatus)
        && IsHyperBoostWritable
        && !IsHyperBoostOperationInProgress
        && !HasControllerDetectionWarning
        && !HasBlockingHyperBoostController(latestStatus)
        && (HyperBoostActive
            || ActivePowerSource == PowerSource.PluggedIn
                && PerformancePresetPresentation.IsAvailable(
                    PowerSource.PluggedIn,
                    SelectedPerformance)
                && Capabilities.PerformancePresets.Contains(SelectedPerformance)
                && Capabilities.FanModes.Contains(SelectedFanMode)
                && latestStatus?.PadlessHyperBoostEligibility
                    == PadlessHyperBoostEligibilityState.CoolingPadAbsent);

    public bool HasHyperBoostUnavailableReason =>
        !string.IsNullOrEmpty(HyperBoostUnavailableReason);

    public string HyperBoostUnavailableTitle => TrayText.Get(
        "HyperBoost controls unavailable");

    public string HyperBoostUnavailableReason
    {
        get
        {
            if (!IsHyperBoostVisible
                || CanToggleHyperBoost
                || IsHyperBoostOperationInProgress)
            {
                return string.Empty;
            }

            if (!IsConnected)
            {
                return TrayText.Get(
                    "HyperBoost is unavailable while the OpenBlade service is disconnected.");
            }

            if (latestStatus is not { } status || !status.DeviceVerified)
            {
                return TrayText.Get("OpenBlade could not verify which laptop this is.");
            }

            if (status.ControlAccess == SessionControlAccess.InactiveSession)
            {
                return TrayText.Get("Another user is controlling OpenBlade");
            }

            if (status.ControlAccess == SessionControlAccess.NotInteractive)
            {
                return TrayText.Get("Interactive session required");
            }

            if (IsCheckingLaptopConnection(status))
            {
                return TrayText.Get(
                    "OpenBlade is waiting for a fresh, complete read of the laptop state.");
            }

            if (status.HyperBoostRestorePending)
            {
                return HyperBoostRestoreRecoveryText;
            }

            if (IsRetainedPowerSourceRecheck(status))
            {
                return TrayText.Get(
                    "HyperBoost is unavailable while OpenBlade reconfirms the retained power source.");
            }

            if (status.UnconfirmedWrites.Count > 0)
            {
                return TrayText.Get(
                    "A change that may have been sent must be confirmed before OpenBlade tries again.");
            }

            if (HasControllerDetectionWarning)
            {
                return TrayText.Get(
                    "OpenBlade could not confirm whether another Razer controller is active. Refresh the laptop state. If this continues, exit Razer laptop-control software such as Synapse and refresh again.");
            }

            string[] blockingControllers = status.PotentialControllers
                .Where(RazerThermalControllerPolicy.IsBlocking)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (blockingControllers.Length > 0)
            {
                return TrayText.Format(
                    "These Razer components may also control the laptop: {0}. Exit them to change HyperBoost, then refresh the laptop state.",
                    string.Join(", ", blockingControllers));
            }

            if (status.WritesBlocked)
            {
                return TrayText.Get(
                    "Laptop controls are currently unavailable. Check Troubleshooting for details.");
            }

            if (!HyperBoostActive && status.ActivePowerSource != PowerSource.PluggedIn)
            {
                return TrayText.Get(
                    "Connect the full-power AC adapter to use HyperBoost.");
            }

            if (!HyperBoostActive && status.PadlessHyperBoostEligibility
                == PadlessHyperBoostEligibilityState.CoolingPadPresent)
            {
                return TrayText.Get(
                    "Disconnect the cooling pad before enabling HyperBoost.");
            }

            if (!HyperBoostActive && status.PadlessHyperBoostEligibility
                != PadlessHyperBoostEligibilityState.CoolingPadAbsent)
            {
                return TrayText.Get(
                    "Cooling-pad absence could not be confirmed. Refresh the laptop state before enabling HyperBoost.");
            }

            if (!IsHyperBoostWritable)
            {
                return HyperBoostDescription;
            }

            return TrayText.Get(
                "HyperBoost is unavailable with the current performance and fan state. Refresh the laptop state.");
        }
    }

    public string HyperBoostDescription => IsHyperBoostWritable
        ? HyperBoostActive
            ? TrayText.Get(
                "Improves thermal performance so key components can sustain higher power limits.")
            : latestStatus?.PadlessHyperBoostEligibility switch
            {
                PadlessHyperBoostEligibilityState.CoolingPadAbsent => TrayText.Get(
                    "Improves thermal performance so key components can sustain higher power limits."),
                PadlessHyperBoostEligibilityState.CoolingPadPresent => TrayText.Get(
                    "Disconnect the cooling pad before enabling HyperBoost."),
                _ => TrayText.Get(
                    "Cooling-pad absence could not be confirmed. Refresh the laptop state before enabling HyperBoost."),
            }
        : Capabilities.HyperBoostWithoutCoolingPadAccess
            == DeviceFeatureAccess.ObservedOnly
            ? TrayText.Get(
                "Pad-less HyperBoost is detected but is not available with the current admission evidence.")
            : TrayText.Get(
                "Detected on this laptop, but OpenBlade cannot safely change it with the current evidence.");

    public bool HasKeyboardLaptopControls =>
        SupportsFunctionKeyPrimary || SupportsGamingMode;

    public bool SupportsStartupAnimation => Capabilities.OneShotLaptopControls.Contains(
        OneShotLaptopControl.StartupAnimation);

    public bool? StartupAnimationEnabled
    {
        get => observation.StartupAnimationEnabled;
        private set
        {
            if (observation.StartupAnimationEnabled == value)
            {
                return;
            }

            observation = observation with { StartupAnimationEnabled = value };
            Notify(nameof(StartupAnimationEnabled));
            Notify(nameof(StartupAnimationToggleIsOn));
            Notify(nameof(IsStartupAnimationFieldLoading));
            NotifyLaptopBehaviorProfilePresentations();
        }
    }

    public bool StartupAnimationToggleIsOn => StartupAnimationEnabled is true;

    public bool SupportsFunctionKeyPrimary => Capabilities.OneShotLaptopControls.Contains(
        OneShotLaptopControl.FunctionKeyPrimary);

    public FunctionKeyPrimaryMode? FunctionKeyPrimary
    {
        get => observation.FunctionKeyPrimary;
        private set
        {
            if (observation.FunctionKeyPrimary == value)
            {
                return;
            }

            observation = observation with { FunctionKeyPrimary = value };
            Notify(nameof(FunctionKeyPrimary));
            Notify(nameof(FunctionKeyPrimaryStateLabel));
            Notify(nameof(SelectedFunctionKeyPrimaryOption));
            Notify(nameof(IsFunctionKeyFieldLoading));
            NotifyLaptopBehaviorProfilePresentations();
        }
    }

    public string FunctionKeyPrimaryStateLabel => FunctionKeyPrimary switch
    {
        FunctionKeyPrimaryMode.FunctionKeys => TrayText.Get("Function keys"),
        FunctionKeyPrimaryMode.MultimediaKeys => TrayText.Get("Media keys"),
        null => TrayText.Get("Checking"),
        _ => TrayText.Get("Checking"),
    };

    public IReadOnlyList<FunctionKeyPrimaryOption> AvailableFunctionKeyPrimaryOptions =>
        FunctionKeyPrimaryOptions;

    public FunctionKeyPrimaryOption? SelectedFunctionKeyPrimaryOption =>
        FunctionKeyPrimaryOptions.FirstOrDefault(option => option.Value == FunctionKeyPrimary);

    public bool SupportsGamingMode => Capabilities.OneShotLaptopControls.Contains(
        OneShotLaptopControl.GamingMode);

    public bool? GamingModeEnabled
    {
        get => observation.GamingModeEnabled;
        private set
        {
            if (observation.GamingModeEnabled == value)
            {
                return;
            }

            observation = observation with { GamingModeEnabled = value };
            Notify(nameof(GamingModeEnabled));
            Notify(nameof(GamingModeToggleIsOn));
            Notify(nameof(TrayDashboardGamingModeLabel));
            Notify(nameof(IsGamingModeFieldLoading));
            NotifyLaptopBehaviorProfilePresentations();
        }
    }

    public bool GamingModeToggleIsOn => GamingModeEnabled is true;

    public string TrayDashboardGamingModeLabel => GamingModeEnabled switch
    {
        true => TrayText.Get("On"),
        false => TrayText.Get("Off"),
        null => TrayText.Get("Checking"),
    };

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
                    activeProfileDraft.Source == source
                        ? activeProfileDraft.LaptopBehavior
                        : null;
                Profiles = updatedProfiles;
                if (rebasedDraft is not null)
                {
                    rebasedDraft = activeProfileDraft.RebaseGamingModeKeyPolicy(rebasedDraft, policy);
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
            ? (activeProfileDraft.LaptopBehavior
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
        // when each dashboard surface reactivates its presentation.
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
            NotifyMainDashboardTelemetryValuesChanged();
            Notify(nameof(ShowDashboardLiveReadings));
            Notify(nameof(LiveReadingsRefreshToolTip));
        }
    }

    public void SetTrayDashboardTelemetryPresentationActive(bool active)
    {
        if (trayDashboardTelemetryPresentationActive == active)
        {
            return;
        }

        trayDashboardTelemetryPresentationActive = active;
        if (active)
        {
            NotifyTrayDashboardTelemetryValuesChanged();
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

    public BatteryPolicy? BatteryPolicyOverride => latestStatus?.BatteryPolicyOverride;

    public bool? BatteryProtectionOverride => latestStatus?.BatteryProtectionOverride;

    public string ManualOverrideStatus
    {
        get
        {
            List<string> active = [];
            AddOverrideStatus(
                active,
                TrayText.Get("Power mode"),
                PerformanceOverride,
                PerformancePresetPresentation.Label);
            AddOverrideStatus(active, TrayText.Get("Fans"), FanOverride);
            AddOverrideStatus(
                active,
                TrayText.Get("Lighting"),
                LightingOverride,
                LightingPresentation.FormatLightingProfile);
            AddBatteryOverrideStatus(active);
            return active.Count == 0
                ? TrayText.Get("No manual overrides active.")
                : string.Join(TrayText.Get(" | "), active);
        }
    }

    public bool HasActiveChanges => PerformanceOverride is not null
        || FanOverride is not null
        || (HasLightingCapability && LightingOverride is not null)
        || BatteryPolicyOverride is not null
        || BatteryProtectionOverride is not null;

    public string ActiveChangesText
    {
        get
        {
            List<string> active = [];
            AddOverrideStatus(
                active,
                TrayText.Get("Power mode"),
                PerformanceOverride,
                PerformancePresetPresentation.Label);
            AddOverrideStatus(active, TrayText.Get("Fans"), FanOverride);
            if (HasLightingCapability)
            {
                AddOverrideStatus(
                    active,
                    TrayText.Get("Keyboard lighting"),
                    LightingOverride,
                    LightingPresentation.FormatLightingProfile);
            }
            AddBatteryOverrideStatus(active);
            return string.Join(TrayText.Get(" · "), active);
        }
    }

    private void AddBatteryOverrideStatus(List<string> active)
    {
        if (BatteryProtectionOverride is { } protectionEnabled)
        {
            active.Add(TrayText.Format(
                "Battery protection: {0} (not saved to profile)",
                protectionEnabled ? TrayText.Get("On") : TrayText.Get("Off")));
        }
        if (BatteryPolicyOverride is { } policy)
        {
            active.Add(TrayText.Format(
                "Charge limit: {0}% not saved to profile",
                policy.ChargeLimitPercent));
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
            Notify(nameof(SelectedWindowsPowerModeOption));
            Notify(nameof(SelectedWindowsPowerModeDescription));
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

    public bool HasUnsavedProfileChanges =>
        profileEditorsDirty || HasActiveProfileDraft || HasGlobalBatteryPolicyDraft;

    public bool HasProfileEditorChanges => profileEditorsDirty;

    public bool HasActiveProfileDraft => activeProfileDraft.HasActiveProfileDraft;

    public bool HasThermalProfileDraft => activeProfileDraft.HasThermalProfileDraft;

    public bool HasLightingProfileDraft => activeProfileDraft.HasLightingProfileDraft;

    public bool HasLightingProfileSaveOpportunity =>
        HasLightingProfileDraft || HasSaveableUnsetLightingProfileField;

    public bool HasLightingProfileDifference =>
        HasConfirmedPowerSource
        && (HasLightingHardwareProfileDifference
            || GetProfile(ActivePowerSource).EffectiveLightingPowerPolicy
                != (activeProfileDraft.LightingPowerPolicy ?? LightingPowerPolicyDraft));

    public bool HasLightingProfileActionOpportunity =>
        HasLightingProfileSaveOpportunity || HasLightingProfileDifference;

    public bool HasDisplayProfileDraft => activeProfileDraft.HasDisplayProfileDraft;

    public bool HasLaptopProfileDraft => activeProfileDraft.HasLaptopProfileDraft;

    public bool HasBatteryProtectionProfileDraft => activeProfileDraft.HasBatteryProtectionProfileDraft;

    public void StageWindowsPowerMode(WindowsPowerMode? mode)
    {
        if (mode is { } defined && !Enum.IsDefined(defined))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }
        if (!CanEditWindowsPowerMode)
        {
            return;
        }

        WindowsPowerModeAssociations updated = PresentedWindowsPowerModes
            .WithPerformance(SelectedPerformance, mode);
        MarkActiveProfileDraft(ActivePowerSource, windowsPowerModes: updated);
    }

    public bool HasGlobalBatteryPolicyDraft => pendingBatteryPolicy is not null;

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

    public bool HasBatteryProtectionProfileDifference =>
        HasConfirmedPowerSource
        && GetProfile(ActivePowerSource).EffectiveBatteryProtectionEnabled
            != BatteryProtectionEnabled;

    public bool HasGlobalBatteryPolicyDifference =>
        latestStatus is not null
        && Profiles.BatteryPolicy
            != latestStatus.DesiredState.Battery.ConfiguredPolicy;

    public bool HasDashboardProfileDraft =>
        HasThermalProfileDraft
        || HasLaptopProfileDraft
        || HasBatteryProtectionProfileDraft
        || HasGlobalBatteryPolicyDraft;

    public bool HasDashboardProfileSaveOpportunity =>
        HasDashboardProfileDraft || HasSaveableUnsetLaptopProfileField;

    public bool HasDashboardProfileDifference =>
        HasThermalProfileDifference
        || HasLaptopBehaviorProfileDifference
        || HasBatteryProtectionProfileDifference
        || HasGlobalBatteryPolicyDifference;

    public bool HasDashboardProfileActionOpportunity =>
        HasDashboardProfileSaveOpportunity || HasDashboardProfileDifference;

    public bool HasDashboardProfileRestoreOpportunity =>
        HasDashboardProfileDraft || HasDashboardProfileDifference;

    public string ThermalProfileRelationshipStatus => ProfileRelationshipStatus(
        HasThermalProfileDraft,
        latestStatus?.ExternalChanges.Contains(DeviceSubsystem.Performance) == true
            || latestStatus?.ExternalChanges.Contains(DeviceSubsystem.Fans) == true);

    public string LightingProfileRelationshipStatus => ProfileRelationshipStatus(
        HasLightingProfileDraft,
        latestStatus?.ExternalChanges.Contains(DeviceSubsystem.Lighting) == true);

    public bool IsPluggedInProfileDraft =>
        activeProfileDraft.Source == PowerSource.PluggedIn;

    public bool IsBatteryProfileDraft =>
        activeProfileDraft.Source == PowerSource.Battery;

    public bool IsUsbCProfileDraft =>
        activeProfileDraft.Source == PowerSource.UsbC;

    public string PluggedInProfileThermalSummary =>
        ProfilePresentationFactory.FormatThermalProfile(GetPresentedProfile(PowerSource.PluggedIn));

    public string PluggedInProfileLightingSummary =>
        LightingPresentation.FormatLightingProfile(GetPresentedProfile(PowerSource.PluggedIn).Lighting);

    public string PluggedInProfileDisplaySummary =>
        ProfilePresentationFactory.FormatDisplayProfile(GetPresentedProfile(PowerSource.PluggedIn).Display);

    public string BatteryProfileThermalSummary =>
        ProfilePresentationFactory.FormatThermalProfile(GetPresentedProfile(PowerSource.Battery));

    public string BatteryProfileLightingSummary =>
        LightingPresentation.FormatLightingProfile(GetPresentedProfile(PowerSource.Battery).Lighting);

    public string BatteryProfileDisplaySummary =>
        ProfilePresentationFactory.FormatDisplayProfile(GetPresentedProfile(PowerSource.Battery).Display);

    public string UsbCProfileThermalSummary =>
        ProfilePresentationFactory.FormatThermalProfile(GetPresentedProfile(PowerSource.UsbC));

    public string UsbCProfileLightingSummary =>
        LightingPresentation.FormatLightingProfile(GetPresentedProfile(PowerSource.UsbC).Lighting);

    public string UsbCProfileDisplaySummary =>
        ProfilePresentationFactory.FormatDisplayProfile(GetPresentedProfile(PowerSource.UsbC).Display);

    public ProfilePresentation PluggedInProfilePresentation =>
        CreateProfilePresentation(PowerSource.PluggedIn);

    public ProfilePresentation UsbCProfilePresentation =>
        CreateProfilePresentation(PowerSource.UsbC);

    public ProfilePresentation BatteryProfilePresentation =>
        CreateProfilePresentation(PowerSource.Battery);

    public ProfilePresentation SelectedProfilePresentation =>
        CreateProfilePresentation(SelectedProfileComparisonOption.Source);

    public ProfileValuePresentation BatteryPolicyPresentation
    {
        get
        {
            var saved = new ProfileValuePresentation(
                TrayText.Format("{0}%", Profiles.BatteryPolicy.ChargeLimitPercent),
                TrayText.Get("Charge limit"),
                TrayText.Get("All profiles"));
            return pendingBatteryPolicy is { } pending
                ? ProfilePresentationFactory.MarkPending(
                    saved,
                    saved with { Text = TrayText.Format("{0}%", pending.ChargeLimitPercent) })
                : saved;
        }
    }

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

    public string ActiveProfileDraftName => activeProfileDraft.Source switch
    {
        PowerSource.PluggedIn => "AC",
        PowerSource.Battery => TrayText.Get("On battery"),
        PowerSource.UsbC => "USB-C",
        _ => TrayText.Get("Current profile"),
    };

    public string SavedActiveProfileSummary => activeProfileDraft.Source is { } source
        ? ProfilePresentationFactory.FormatPowerProfile(GetProfile(source), Profiles.WindowsPowerModes)
        : string.Empty;

    public string PendingActiveProfileSummary => GetActiveProfileDraft() is { } draft
        ? ProfilePresentationFactory.FormatPowerProfile(draft, PresentedWindowsPowerModes)
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
        get => lightingEditor.Effect;
        set => lightingEditor.Effect = value;
    }

    public LightingEffectOption? SelectedLightingEffectOption
    {
        get => lightingEditor.IsEffectUnconfirmed
            ? null
            : AvailableLightingEffectOptions.FirstOrDefault(
                option => option.Effect == LightingEffectEditor);
        set
        {
            if (value is not null)
            {
                lightingEditor.SelectEffect(value.Effect);
            }
        }
    }

    public bool IsKeyboardEffectUnconfirmed => lightingEditor.IsEffectUnconfirmed;

    public string KeyboardEffectDescription => lightingEditor.IsEffectUnconfirmed
        ? TrayText.Get("The active effect was set outside OpenBlade and cannot be read from firmware.")
        : TrayText.Get("Choose a supported keyboard lighting effect.");

    public int LightingBrightnessEditor
    {
        get => lightingEditor.Brightness;
        set => lightingEditor.Brightness = value;
    }

    public int LightingRedEditor
    {
        get => lightingEditor.Red;
        set => lightingEditor.Red = value;
    }

    public int LightingGreenEditor
    {
        get => lightingEditor.Green;
        set => lightingEditor.Green = value;
    }

    public int LightingBlueEditor
    {
        get => lightingEditor.Blue;
        set => lightingEditor.Blue = value;
    }

    public RgbColor LightingPrimaryColorEditor
    {
        get => lightingEditor.PrimaryColor;
        set => lightingEditor.PrimaryColor = value;
    }

    public int LightingSecondaryRedEditor
    {
        get => lightingEditor.SecondaryRed;
        set => lightingEditor.SecondaryRed = value;
    }

    public int LightingSecondaryGreenEditor
    {
        get => lightingEditor.SecondaryGreen;
        set => lightingEditor.SecondaryGreen = value;
    }

    public int LightingSecondaryBlueEditor
    {
        get => lightingEditor.SecondaryBlue;
        set => lightingEditor.SecondaryBlue = value;
    }

    public RgbColor LightingSecondaryColorEditor
    {
        get => lightingEditor.SecondaryColor;
        set => lightingEditor.SecondaryColor = value;
    }

    public IReadOnlyList<BreathingColorMode> LightingBreathingColorModes { get; } =
        Enum.GetValues<BreathingColorMode>();

    public BreathingColorMode LightingBreathingColorModeEditor
    {
        get => lightingEditor.BreathingColorMode;
        set => lightingEditor.BreathingColorMode = value;
    }

    public IReadOnlyList<ReactiveLightingDuration> LightingReactiveDurations { get; } =
        Enum.GetValues<ReactiveLightingDuration>();

    public ReactiveLightingDuration LightingReactiveDurationEditor
    {
        get => lightingEditor.ReactiveDuration;
        set => lightingEditor.ReactiveDuration = value;
    }

    public IReadOnlyList<StarlightColorMode> LightingStarlightColorModes { get; } =
        Enum.GetValues<StarlightColorMode>();

    public StarlightColorMode LightingStarlightColorModeEditor
    {
        get => lightingEditor.StarlightColorMode;
        set => lightingEditor.StarlightColorMode = value;
    }

    public IReadOnlyList<StarlightLightingDuration> LightingStarlightDurations { get; } =
        Enum.GetValues<StarlightLightingDuration>();

    public StarlightLightingDuration LightingStarlightDurationEditor
    {
        get => lightingEditor.StarlightDuration;
        set => lightingEditor.StarlightDuration = value;
    }

    public bool LightingRandomColorsEditor
    {
        get => lightingEditor.RandomColors;
        set => lightingEditor.RandomColors = value;
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
        get => lightingEditor.Direction;
        set => lightingEditor.Direction = value;
    }

    public IReadOnlyList<LightingRotationDirection> LightingRotationDirections { get; } =
        Enum.GetValues<LightingRotationDirection>();

    public LightingRotationDirection LightingRotationDirectionEditor
    {
        get => lightingEditor.RotationDirection;
        set => lightingEditor.RotationDirection = value;
    }

    public IReadOnlyList<AmbientAwarenessRegionOption>
        AvailableAmbientAwarenessRegionOptions
    { get; } =
        Enum.GetValues<AmbientAwarenessRegion>()
            .Select(region => new AmbientAwarenessRegionOption(
                region,
                LightingPresentation.AmbientAwarenessRegionLabel(region)))
            .ToArray();

    public AmbientAwarenessRegion LightingAmbientAwarenessRegionEditor
    {
        get => lightingEditor.AmbientAwarenessRegion;
        set => lightingEditor.AmbientAwarenessRegion = value;
    }

    public double LightingAudioMeterColorBoostEditor
    {
        get => lightingEditor.AudioMeterColorBoost;
        set => lightingEditor.AudioMeterColorBoost = value;
    }

    public int LightingAudioMeterDecayMillisecondsEditor
    {
        get => lightingEditor.AudioMeterDecayMilliseconds;
        set => lightingEditor.AudioMeterDecayMilliseconds = value;
    }

    public string LightingAudioMeterDecayValue => LightingPresentation.FormatAudioMeterDecay(
        lightingEditor.AudioMeterDecayMilliseconds);

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
        get => lightingEditor.LogoMode;
        set => lightingEditor.LogoMode = value;
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

    public bool LightingShowsPrimaryColor => lightingEditor.UsesPrimaryColor;

    public bool LightingShowsSecondaryColor =>
        LightingEffectEditor == LightingEffect.Breathing
            && LightingBreathingColorModeEditor == BreathingColorMode.Dual
        || LightingEffectEditor == LightingEffect.Tidal
            && !LightingRandomColorsEditor
        || LightingEffectEditor == LightingEffect.Starlight
            && LightingStarlightColorModeEditor == StarlightColorMode.Dual;

    public string LightingPrimaryColorLabel =>
        LightingShowsSecondaryColor
            ? TrayText.Get("Primary color")
            : TrayText.Get("Color");

    public string LightingPrimaryColorDescription =>
        LightingShowsSecondaryColor
            ? TrayText.Get("Choose the effect's first color.")
            : TrayText.Get("Choose the keyboard color.");

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

    public LightingSettings LightingDraft => lightingEditor.ToSettings();

    public bool IsLightingApplyPending => lightingEditor.IsDirty;

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

    public bool LabFeaturesEnabled
    {
        get => labFeaturesEnabled;
        set => SetField(ref labFeaturesEnabled, value);
    }

    public IReadOnlyList<ThemePreference> ThemeOptions { get; } =
        Enum.GetValues<ThemePreference>();

    public IReadOnlyList<ThemePreferenceOption> ThemeSelectionOptions { get; } =
    [
        new(ThemePreference.FollowWindows, TrayText.Get("Follow Windows")),
        new(ThemePreference.Light, TrayText.Get("Light")),
        new(ThemePreference.Dark, TrayText.Get("Dark")),
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

    public UiLanguagePreference LanguagePreference
    {
        get => languagePreference;
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            if (languagePreference == value)
            {
                return;
            }

            SetField(ref languagePreference, value);
            Notify(nameof(SelectedLanguageOption));
        }
    }

    public IReadOnlyList<UiLanguagePreferenceOption> LanguageSelectionOptions { get; } =
    [
        new(UiLanguagePreference.FollowSystem, TrayText.Get("Follow Windows")),
        new(UiLanguagePreference.English, "English"),
        new(UiLanguagePreference.SimplifiedChinese, "简体中文"),
        new(UiLanguagePreference.Russian, "Русский"),
    ];

    public UiLanguagePreferenceOption SelectedLanguageOption
    {
        get => LanguageSelectionOptions.Single(option => option.Preference == LanguagePreference);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            LanguagePreference = value.Preference;
        }
    }

    public IReadOnlyList<LiveReadingsRefreshOption> LiveReadingsRefreshOptions { get; } =
    [
        new(LiveReadingsCadence.Off, TrayText.Get("Off")),
        new(LiveReadingsCadence.EverySecond, TrayText.Get("Every second")),
        new(LiveReadingsCadence.EveryTwoSeconds, TrayText.Get("Every 2 seconds")),
        new(LiveReadingsCadence.EveryFiveSeconds, TrayText.Get("Every 5 seconds")),
        new(LiveReadingsCadence.EveryTenSeconds, TrayText.Get("Every 10 seconds")),
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
        new(TrayHoverLabelMode.OpenBlade, TrayText.Get("OpenBlade only")),
        new(TrayHoverLabelMode.ActiveProfile, TrayText.Get("Active profile")),
        new(TrayHoverLabelMode.BatteryPowerFlow, TrayText.Get("Battery power flow")),
        new(TrayHoverLabelMode.ProfileAndPowerFlow, TrayText.Get("Profile and battery power")),
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
            return TrayText.Format(
                "Not shown: {0}. Windows limits tray hover text to {1} characters.",
                omitted,
                TrayHoverLabelFormatter.MaximumLength);
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
        ? TrayText.Get("Launch OpenBlade automatically when you sign in.")
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
    public ICommand SetThermalControlPolicyCommand { get; }
    public ICommand AcknowledgeThermalPolicyUpgradeDisclosureCommand { get; }
    public ICommand ApplyOneShotLaptopControlCommand { get; }

    public ApplyOneShotLaptopControl StartupAnimationOnAction { get; } = new(
        OneShotLaptopControl.StartupAnimation,
        Enabled: true);

    public ApplyOneShotLaptopControl StartupAnimationOffAction { get; } = new(
        OneShotLaptopControl.StartupAnimation,
        Enabled: false);

    public ApplyOneShotLaptopControl FunctionKeysPrimaryAction { get; } =
        FunctionKeyPrimaryOptions[0].Action;

    public ApplyOneShotLaptopControl MultimediaKeysPrimaryAction { get; } =
        FunctionKeyPrimaryOptions[1].Action;

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

    private static bool HasBlockingHyperBoostController(ServiceStatusSnapshot? status) =>
        status?.PotentialControllers.Any(RazerThermalControllerPolicy.IsBlocking) == true;

    private bool CanSetThermalControlPolicy(object? parameter) =>
        parameter is ThermalControlPolicyOption option
        && Enum.IsDefined(option.Policy)
        && option.Policy != thermalConvergence?.Policy
        && CanChangeThermalControlPolicy;

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
        && !HyperBoostActive
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
        && !HyperBoostActive
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

        string suffix = string.IsNullOrEmpty(unit)
            ? string.Empty
            : TrayText.Format(" {0}", unit);
        string step = range.Step.HasValue
            ? TrayText.Format(", {0}{1} steps", range.Step.Value, suffix)
            : TrayText.Get(", step not yet validated");
        return TrayText.Format(
            "{0}–{1}{2}{3}",
            range.Minimum,
            range.Maximum,
            suffix,
            step);
    }

    private static string FormatFeatureAccess(
        DeviceFeatureAccess? access,
        string observedOnlyText) => access switch
        {
            DeviceFeatureAccess.ReadWrite => TrayText.Get("Available"),
            DeviceFeatureAccess.ReadOnly => TrayText.Get("Read-only"),
            DeviceFeatureAccess.ObservedOnly => observedOnlyText,
            _ => UnavailableLabel,
        };

    private bool CanSetFanMode(object? parameter)
    {
        if (parameter is not FanMode value
            || !Enum.IsDefined(value)
            || IsFanControlOperationInProgress
            || HyperBoostActive
            || !CanMutate
            || !HasConfirmedPowerSource
            || !CommandCapabilities.FanModes.Contains(value))
        {
            return false;
        }

        return ProfileAdmissionPolicy.FanModeAvailable(ActivePowerSource, value)
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
            || !ProfileAdmissionPolicy.HasBatteryCapability(capabilities)
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
            || !ProfileAdmissionPolicy.HasBatteryCapability(capabilities)
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
                    StartupStatusMessage = TrayText.Format(
                        "Start at sign-in is unavailable: {0}",
                        exception.Message);
                    StartupStateKnown = false;
                },
                cancellationToken).ConfigureAwait(false);
        }
    }

    public Task ObserveStateAsync(CancellationToken cancellationToken) =>
        ObserveStateAsync(
            includeMainDashboardReadings: true,
            additionalLiveReadings: LiveReadingSensors.None,
            trayOnly: false,
            cancellationToken);

    public Task ObserveTrayStateAsync(CancellationToken cancellationToken) =>
        ObserveStateAsync(
            includeMainDashboardReadings: false,
            additionalLiveReadings: LiveReadingSensors.None,
            trayOnly: true,
            cancellationToken);

    public Task ObserveTrayDashboardStateAsync(
        bool includeMainDashboardReadings,
        LiveReadingSensors dashboardLiveReadings,
        CancellationToken cancellationToken) =>
        ObserveStateAsync(
            includeMainDashboardReadings,
            additionalLiveReadings: dashboardLiveReadings,
            trayOnly: false,
            cancellationToken);

    public Task SetGpuAppCleanupVisibleAsync(
        bool visible,
        CancellationToken cancellationToken) =>
        dispatcher.InvokeAsync(
            () => IsGpuAppCleanupVisible = visible,
            cancellationToken);

    private async Task ObserveStateAsync(
        bool includeMainDashboardReadings,
        LiveReadingSensors additionalLiveReadings,
        bool trayOnly,
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
                    includeMainDashboardReadings);
                if (LiveReadingsEnabled)
                {
                    requestedLiveReadings |= additionalLiveReadings;
                }
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
                    trayOnly),
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
                        trayOnly);
                    if (accepted
                        && requestedLiveReadings != LiveReadingSensors.None
                        && LiveReadingsEnabled
                        && status.LiveReadings is not null)
                    {
                        MarkLiveReadingsRefreshed(
                            publishPresentation: !trayOnly);
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
                                MarkReconnecting(TrayText.Get("Reconnecting to service"));
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
        catch (IpcProtocolVersionMismatchException) when (!token.IsCancellationRequested)
        {
            await dispatcher.InvokeAsync(
                () => MarkDisconnected(TrayText.Get(
                    "OpenBlade components have different versions. Update or repair OpenBlade to restore control.")),
                token);
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
        bool presentationApplied;
        try
        {
            presentationApplied = ApplyStatusCore(status);
        }
        finally
        {
            suppressProfileDifferenceNotifications = wasSuppressed;
        }

        // A snapshot whose metadata is unchanged only refreshes live readings,
        // which no profile-difference property depends on.
        if (presentationApplied && !suppressProfileDifferenceNotifications)
        {
            NotifyProfileDifferenceState();
        }

        if (presentationApplied)
        {
            // Connection state changes before the rest of the snapshot. Consumers
            // may seed observation only after every presentation field is applied.
            HasAppliedServiceState = true;
        }
    }

    private bool ApplyStatusCore(ServiceStatusSnapshot status)
    {
        if (status.Revision < latestAcceptedRevision)
        {
            return false;
        }

        bool performanceHoldReleased = ReleasePendingPerformanceHold(status);
        bool fanControlHoldReleased = ReleasePendingFanControlHold(status);
        bool lightingHoldReleased = ReleasePendingLightingHold(status);
        ServiceStatusSnapshot? previousStatus = latestStatus;
        latestAcceptedRevision = status.Revision;
        latestStatus = status;
        if (IsConnected
            && previousStatus is not null
            && !performanceHoldReleased
            && !fanControlHoldReleased
            && !lightingHoldReleased
            && StatusMetadataEqual(previousStatus, status))
        {
            ApplyLiveReadings(status);
            return false;
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
        return true;
    }

    private void ApplyDeviceStatePresentation(
        ServiceStatusSnapshot status,
        ConfirmedDeviceState? confirmed,
        DeviceCapabilities capabilities,
        bool checkingLaptopConnection)
    {
        UpdateOneShotReadbacks(status);
        HyperBoostActive = confirmed?.HyperBoostActive is true;
        SelectedPerformance = pendingPerformance?.Requested.Value
            ?? ResolvePresentedPerformance(status, confirmed, capabilities);
        SelectedFanMode = pendingFanControl?.Requested.Mode
            ?? ResolvePresentedFanMode(status, confirmed, capabilities);
        LoadFanControlEditor(
            pendingFanControl?.Requested
            ?? status.FanControlOverride
            ?? status.DesiredState.FanControl
            ?? FanControlSettings.FromLegacy(SelectedFanMode));
        lightingEditor.PrepareForPowerSource(status.ActivePowerSource);
        if (!checkingLaptopConnection
            && !lightingEditor.IsDirty
            && !IsLightingOperationInProgress
            && pendingLighting is null)
        {
            lightingEditor.SetEffectUnconfirmed(
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
        if (!lightingEditor.IsDirty && !checkingLaptopConnection && pendingLighting is null)
        {
            LoadLightingEditor(
                SelectedLighting,
                startUnconfirmedEffectAtDefaultBrightness: true);
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
        PerformancePreset gpuTuningPerformance = pendingPerformance?.Requested.Value
            ?? status.DesiredState.Performance.Value;
        if (status.ActivePowerSource == PowerSource.PluggedIn
            && gpuTuningPerformance == PerformancePreset.Custom
            && gpuClockTuning is null
            && customPerformance.GpuClockOffsets is { } legacyOffsets)
        {
            gpuClockTuning = new GpuClockTuningSettings(
                Enabled: true,
                legacyOffsets);
        }
        GpuClockOffsetCapabilities = confirmed?.GpuClockOffsets;
        gpuClockTuningConfigured = gpuClockTuning is not null;
        IsGpuClockTuningEnabled = gpuClockTuning?.Enabled == true;
        // Keep dormant legacy offsets available to the editor without treating
        // remembered Custom settings as active GPU tuning.
        GpuClockOffsetSettings presentedGpuOffsets =
            gpuClockTuning?.Offsets
            ?? customPerformance.GpuClockOffsets
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
        ActivePowerSourceLabel = PowerSourcePresentation.ProfileLabel(
            status.ActivePowerSource);
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
        bool competingLightingController = status.PotentialControllers.Any(
            CompetingLightingControllers.Contains);
        ControllerAdvisoryText = HasControllerAdvisory
            ? TrayText.Format(
                competingLightingController
                    ? "Another controller may change laptop settings: {0}. OpenBlade yields automatic keyboard lighting to Synapse while external lighting override is off. Confirmed external changes pause automatic profile updates; manual actions still apply."
                    : "Another controller may change laptop settings: {0}. Confirmed external changes pause automatic profile updates; manual actions still apply.",
                string.Join(", ", status.PotentialControllers))
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
            ? TrayText.Format(
                "Changed outside OpenBlade: {0}. Your saved profiles are unchanged. Automatic profile updates for these controls are paused until you change them in OpenBlade.",
                string.Join(", ", externalChanges.Select(SubsystemLabel)))
            : string.Empty;
        DeviceSubsystem[] unconfirmedWrites = status.UnconfirmedWrites
            .Distinct()
            .OrderBy(value => value.ToString(), StringComparer.Ordinal)
            .ToArray();
        HasUnconfirmedWrites = unconfirmedWrites.Length > 0;
        UnconfirmedWritesText = HasUnconfirmedWrites
            ? TrayText.Format(
                "OpenBlade could not confirm {0} after a write. The last confirmed values remain shown. Checking the current state is read-only; OpenBlade will not retry the write automatically.",
                string.Join(", ", unconfirmedWrites.Select(SubsystemLabel)))
            : string.Empty;
        HasPendingHyperBoostRestore = status.HyperBoostRestorePending;
        ApplyThermalConvergence(status.ThermalConvergence);
        AlwaysOverrideExternalLightingChanges =
            status.AlwaysOverrideExternalLightingChanges;
        LightingRefreshRate = status.LightingRefreshRate;
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

    private void ApplyThermalConvergence(ThermalConvergenceStatus? value)
    {
        if (Equals(thermalConvergence, value))
        {
            return;
        }

        thermalConvergence = value;
        Notify(nameof(AvailableThermalControlPolicyOptions));
        Notify(nameof(SelectedThermalControlPolicyOption));
        Notify(nameof(IsThermalControlPolicyAvailable));
        Notify(nameof(CanChangeThermalControlPolicy));
        Notify(nameof(HasThermalPolicyUpgradeDisclosure));
        Notify(nameof(ShouldForceWindowVisibleForThermalDisclosure));
        Notify(nameof(CanAcknowledgeThermalPolicyUpgradeDisclosure));
        Notify(nameof(HasThermalConvergenceAttention));
        Notify(nameof(ThermalConvergenceStateLabel));
        Notify(nameof(ThermalConvergenceDetail));
        Notify(nameof(ThermalConvergenceAffectedComponents));
        Notify(nameof(HasThermalConvergenceAffectedComponents));
        Notify(nameof(ThermalConvergenceExplicitReapplyRequired));
        Notify(nameof(CanReapplyChangedSettings));
        Notify(nameof(ShowReapplyChangedSettings));
        Notify(nameof(HasAttention));
        Notify(nameof(AttentionSummary));
        Notify(nameof(AttentionBadgeLabel));
        Notify(nameof(AttentionDetails));
        NotifyCommands();
    }

    private DeviceCapabilities ApplyCapabilityPresentation(
        ServiceStatusSnapshot status,
        ServiceStatusSnapshot? previousStatus)
    {
        DeviceCapabilities capabilities = observation.ResolveCapabilities(
            status,
            hasPreviousStatus: previousStatus is not null);
        Capabilities = capabilities;
        int[] chargeLimits = ProfileAdmissionPolicy.HasBatteryCapability(capabilities)
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
            Notify(nameof(TrayDashboardGpuTuningLabel));
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
        RefreshLaptopPerformanceOptions();
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
            .Where(effect => Enum.IsDefined(effect) && effect != LightingEffect.Sequence)
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
        if (LightingEffectEditor == LightingEffect.Sequence
            && !presentedLightingEffects.Contains(LightingEffect.Sequence))
        {
            presentedLightingEffects =
            [
                .. presentedLightingEffects,
                LightingEffect.Sequence,
            ];
        }
        IReadOnlyList<LightingEffectOption> lightingEffectOptions =
            StableOptionCollection.ReuseEquivalent(
                AvailableLightingEffectOptions,
                presentedLightingEffects
                    .Select(CreateLightingEffectOption)
                    .OrderBy(option => LightingEffectDisplayOrder(option.Effect))
                    .ToArray());
        if (!ReferenceEquals(AvailableLightingEffectOptions, lightingEffectOptions))
        {
            AvailableLightingEffectOptions = lightingEffectOptions;
            Notify(nameof(SelectedLightingEffectOption));
        }
        LightingSettings[] lightingSettings = AvailableLightingEffects
            .Select(effect => LightingEditorState.CreateChoice(effect, SelectedLighting))
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
                    .Select(mode => new LogoLightingModeOption(
                        mode,
                        LightingPresentation.LogoLightingModeLabel(mode)))
                    .ToArray());
        if (!ReferenceEquals(AvailableLogoLightingModeOptions, logoLightingModeOptions))
        {
            AvailableLogoLightingModeOptions = logoLightingModeOptions;
            Notify(nameof(SelectedLogoLightingModeOption));
        }
        NotifyComputed(nameof(IsLogoLightingVisible), IsLogoLightingVisible);
        IsPerformanceVisible = AvailablePerformancePresets.Count > 0;
        IsFanControlVisible = AvailableFanModes.Count > 0;
        NotifyComputed(nameof(IsProfileControlVisible), IsProfileControlVisible);
    }

    private static LightingEffectOption CreateLightingEffectOption(LightingEffect effect) =>
        new(effect, LightingPresentation.LightingEffectLabel(effect));

    // Keep Off first, then follow the en-US label order so translations never reorder the picker.
    private static int LightingEffectDisplayOrder(LightingEffect effect) => effect switch
    {
        LightingEffect.Off => 0,
        LightingEffect.AmbientAwareness => 1,
        LightingEffect.AudioMeter => 2,
        LightingEffect.Breathing => 3,
        LightingEffect.Fire => 4,
        LightingEffect.Sequence => 5,
        LightingEffect.Reactive => 6,
        LightingEffect.Ripple => 7,
        LightingEffect.Spectrum => 8,
        LightingEffect.Starlight => 9,
        LightingEffect.Static => 10,
        LightingEffect.Tidal => 11,
        LightingEffect.Wave => 12,
        LightingEffect.Wheel => 13,
        _ => int.MaxValue,
    };

    private void NotifyActiveFanSelectionBinding()
    {
        // WinUI clears SelectedItem whenever the active ItemsSource is replaced.
        // Always publish the list first, then reapply the confirmed selection.
        Notify(nameof(AvailableActiveFanModes));
        Notify(nameof(AvailableActiveFanModeOptions));
        Notify(nameof(SelectedFanMode));
        Notify(nameof(SelectedFanModeOption));
        Notify(nameof(CanChangeFanMode));
    }

    private void MarkDisconnected() =>
        MarkDisconnected(TrayText.Get("Service disconnected"));

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

    private void RefreshLaptopPerformanceOptions()
    {
        IReadOnlyList<PerformancePresetOption> presets = ActivePowerSource switch
        {
            PowerSource.PluggedIn => AvailablePluggedInPerformanceOptions,
            PowerSource.Battery => AvailableBatteryPerformanceOptions,
            PowerSource.UsbC => AvailableUsbCPerformanceOptions,
            PowerSource.ExternalUnknown => [],
            _ => [],
        };
        bool includeHyperBoost = IsHyperBoostVisible
            && (HyperBoostActive
                || IsHyperBoostOperationInProgress
                || CanToggleHyperBoost);
        IReadOnlyList<LaptopPerformanceOption> options =
            StableOptionCollection.ReuseEquivalent(
                AvailableLaptopPerformanceOptions,
                PerformancePresetPresentation.CreateLaptopOptions(
                    presets,
                    includeHyperBoost));
        if (ReferenceEquals(AvailableLaptopPerformanceOptions, options))
        {
            return;
        }

        AvailableLaptopPerformanceOptions = options;
        Notify(nameof(SelectedLaptopPerformanceOption));
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

    public bool CanImportKeyboardLighting(KeyboardLightingPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        return LabFeaturesEnabled
            && CanControlLighting
            && LightingSettingsValidation.IsWellFormed(preset.Lighting)
            && preset.Lighting.LogoMode is null
            && Capabilities.LightingEffects.Contains(preset.Lighting.Effect)
            && (preset.Lighting.Parameters is not SequenceLightingParameters sequence
                || string.Equals(
                    sequence.LayoutId,
                    Capabilities.KeyboardLightingLayoutId,
                    StringComparison.Ordinal));
    }

    public bool TryImportKeyboardLighting(KeyboardLightingPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (!CanImportKeyboardLighting(preset))
        {
            return false;
        }

        lightingEditor.SetEffectUnconfirmed(false);
        EnsureImportedSequenceOption(preset.Lighting);
        LoadLightingEditor(preset.Lighting with { LogoMode = LogoLightingModeEditor });
        lightingEditor.SetDirty(true);
        Notify(nameof(LightingDraft));
        NotifyCommands();
        return true;
    }

    private void MarkDisconnected(string message)
    {
        pendingLighting = null;
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
        ControlAccessStatus = TrayText.Get("OpenBlade service isn’t running");
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
            NotifyComputed(nameof(LiveReadingsRefreshToolTip), LiveReadingsRefreshToolTip);
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
            NotifyComputed(nameof(LiveReadingsRefreshToolTip), LiveReadingsRefreshToolTip);
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

        return ProfileAdmissionPolicy.HasBatteryCapability(capabilities)
            && capabilities.ChargeLimits.Contains(currentLimit)
            && capabilities.ChargeLimits.Contains(value);
    }

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
            CreatePerformanceOverride(
                requested,
                OverrideLifetime.UntilPowerSourceChange,
                requested == PerformancePreset.Custom
                    ? GetActiveProfileCustomPerformance()
                    : null,
                GetActiveProfileGpuClockTuning()),
            token);
    }

    private async Task SetCustomPerformanceAsync(object? parameter, CancellationToken token)
    {
        CustomPerformanceSettings requested = (CustomPerformanceSettings)parameter!;
        await ApplyPerformanceAsync(
            CreatePerformanceOverride(
                PerformancePreset.Custom,
                OverrideLifetime.UntilPowerSourceChange,
                requested,
                GetActiveProfileGpuClockTuning()),
            token);
    }

    private static PerformanceOverride CreatePerformanceOverride(
        PerformancePreset performance,
        OverrideLifetime lifetime,
        CustomPerformanceSettings? customPerformance,
        GpuClockTuningSettings? gpuClockTuning)
    {
        var normalized = ActiveProfileDraftState.NormalizeLegacyGpuClockTuning(
            customPerformance,
            gpuClockTuning);

        return new PerformanceOverride(performance, lifetime, normalized.CustomPerformance)
        {
            GpuClockTuning = normalized.GpuClockTuning,
        };
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

    public async Task SetLaptopPerformanceOptionAsync(
        LaptopPerformanceOption option,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(option);
        if (!AvailableLaptopPerformanceOptions.Contains(option))
        {
            throw new InvalidOperationException("Laptop performance option is unavailable.");
        }

        if (option.IsHyperBoost)
        {
            await SetHyperBoostEnabledAsync(enabled: true, token);
            return;
        }

        if (option.Value is not { } requested)
        {
            throw new InvalidOperationException("Laptop performance option is invalid.");
        }

        if (HyperBoostActive)
        {
            PerformancePreset? restoredPerformance =
                await SetHyperBoostEnabledCoreAsync(enabled: false, token);
            if (HyperBoostActive || HasPendingHyperBoostRestore || HasUnconfirmedWrites)
            {
                return;
            }

            if (restoredPerformance is { } restored && requested == restored)
            {
                return;
            }
        }
        else if (requested == SelectedPerformance)
        {
            return;
        }
        if (!CanSetPerformance(requested))
        {
            return;
        }

        await SetPerformanceAsync(requested, token);
    }

    public Task SetHyperBoostEnabledAsync(
        bool enabled,
        CancellationToken token) => SetHyperBoostEnabledCoreAsync(enabled, token);

    private async Task<PerformancePreset?> SetHyperBoostEnabledCoreAsync(
        bool enabled,
        CancellationToken token)
    {
        if (!CanToggleHyperBoost || enabled == HyperBoostActive)
        {
            throw new InvalidOperationException("HyperBoost control is unavailable.");
        }

        PerformancePreset? restoredPerformance = null;
        ServiceStatusSnapshot? statusAtStart = latestStatus;
        IsHyperBoostOperationInProgress = true;
        try
        {
            await RunOperationAsync(
                cancellationToken => serviceClient.SetHyperBoostAsync(
                    enabled,
                    cancellationToken),
                token,
                result =>
                {
                    if (result.Status == ControlOperationStatus.Applied)
                    {
                        restoredPerformance = result.HyperBoostRestoredPerformance;
                        bool statusSupersedesResult =
                            !ReferenceEquals(latestStatus, statusAtStart)
                            && (latestStatus is not { } currentStatus
                                || statusAtStart is not { } originalStatus
                                || currentStatus.ActivePowerSource
                                    != originalStatus.ActivePowerSource
                                || currentStatus.ConfirmedState?.HyperBoostActive == enabled);
                        if (statusSupersedesResult)
                        {
                            restoredPerformance = enabled ? null : SelectedPerformance;
                            return;
                        }
                        if (!enabled && restoredPerformance is { } restored)
                        {
                            SelectedPerformance = restored;
                        }
                        HyperBoostActive = enabled;
                    }
                });
        }
        finally
        {
            IsHyperBoostOperationInProgress = false;
        }

        return restoredPerformance;
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
            CreatePerformanceOverride(
                SelectedPerformance,
                OverrideLifetime.UntilPowerSourceChange,
                SelectedPerformance == PerformancePreset.Custom
                    ? new CustomPerformanceSettings(
                        SelectedCustomCpuLevel,
                        SelectedCustomGpuLevel)
                    : null,
                gpuClockTuning),
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
                            + ControlPresentationHoldGracePeriod,
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
        pendingFanControl = new PendingFanControlHold(
            requested,
            sourceAtStart,
            ExpiresAt: null);
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
            IsFanControlOperationInProgress = false;
            if (!applied)
            {
                pendingFanControl = null;
                SelectedFanMode = previous.Mode;
                LoadFanControlEditor(previous);
            }
            else
            {
                if (pendingFanControl is { Requested: var pending } hold
                    && pending == requested)
                {
                    pendingFanControl = hold with
                    {
                        ExpiresAt = timeProvider.GetUtcNow()
                            + ControlPresentationHoldGracePeriod,
                    };
                }

                if (latestStatus is { } status)
                {
                    ReleasePendingFanControlHold(status);
                }
            }
        }
    }

    private bool ReleasePendingFanControlHold(ServiceStatusSnapshot status)
    {
        if (pendingFanControl is not { } pending)
        {
            return false;
        }

        bool statusCaughtUp = status.FanControlOverride == pending.Requested;
        bool sourceChanged = status.ActivePowerSource != pending.PowerSource;
        bool gracePeriodExpired = pending.ExpiresAt is { } expiresAt
            && timeProvider.GetUtcNow() >= expiresAt;
        if (!statusCaughtUp && !sourceChanged && !gracePeriodExpired)
        {
            return false;
        }

        pendingFanControl = null;
        return true;
    }

    private async Task SetLightingAsync(object? parameter, CancellationToken token)
    {
        LightingSettings requested = (LightingSettings)parameter!;
        PowerSource sourceAtStart = ActivePowerSource;
        long revisionAtStart = latestAcceptedRevision;
        long connectionAtStart = connectionGeneration;
        pendingLighting = null;
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
                    if (!lightingEditor.ClearDirtyIfUnchanged(requested))
                    {
                        return;
                    }
                    if (resultStatus == ControlOperationStatus.Applied
                        && IsConnected
                        && connectionGeneration == connectionAtStart)
                    {
                        LightingSettings submittedIntent =
                            LightingSettingsFactory.NormalizeLegacyOff(requested);
                        // Applied can overtake an ownership-only snapshot containing old intent.
                        // Clear dirty state to stop retries, but hold presentation until correlation.
                        pendingLighting = new PendingLightingHold(
                            submittedIntent,
                            sourceAtStart,
                            revisionAtStart,
                            timeProvider.GetUtcNow() + ControlPresentationHoldGracePeriod);
                        if (latestStatus is { } status)
                        {
                            ReleasePendingLightingHold(status);
                            if (status.Revision > revisionAtStart
                                && LightingStatusReflectsRequest(status, submittedIntent))
                            {
                                LoadLightingEditor(
                                    status.ConfirmedState?.Lighting
                                    ?? status.DesiredState.Lighting.Value);
                            }
                        }
                    }
                },
                token);
        }
    }

    private bool ReleasePendingLightingHold(ServiceStatusSnapshot status)
    {
        if (pendingLighting is not { } pending)
        {
            return false;
        }

        bool statusCaughtUp = status.Revision > pending.StartingRevision
            && LightingStatusReflectsRequest(status, pending.SubmittedIntent);
        bool sourceChanged = status.ActivePowerSource is
                (PowerSource.PluggedIn or PowerSource.Battery or PowerSource.UsbC)
            && status.ActivePowerSource != pending.PowerSource;
        bool gracePeriodExpired = timeProvider.GetUtcNow() >= pending.ExpiresAt;
        if (!statusCaughtUp && !sourceChanged && !gracePeriodExpired)
        {
            return false;
        }

        pendingLighting = null;
        return true;
    }

    private static bool LightingStatusReflectsRequest(
        ServiceStatusSnapshot status,
        LightingSettings submittedIntent) =>
        status.LightingOverride?.Value == submittedIntent
        || status.DesiredState.Lighting.Value == submittedIntent;

    private async Task SetChargeLimitAsync(object? parameter, CancellationToken token)
    {
        ChargeLimitCommitRequest request = parameter switch
        {
            ChargeLimitCommitRequest commit => commit,
            int value => new(value, chargeLimitEditorRevision),
            _ => throw new ArgumentException("A charge-limit request is required.", nameof(parameter)),
        };
        int requestedLimit = request.Value;
        long editorRevisionAtStart = request.EditorRevision;
        BatteryPolicy requestedPolicy = BatteryPolicy.Create(requestedLimit);
        long revisionAtStart = latestAcceptedRevision;
        ControlOperationStatus? resultStatus = null;
        IsChargeLimitOperationInProgress = true;
        try
        {
            await RunOperationAsync(
                cancellationToken => serviceClient.SetBatteryPolicyAsync(
                    requestedPolicy,
                    cancellationToken),
                token,
                result => resultStatus = result.Status);
        }
        finally
        {
            IsChargeLimitOperationInProgress = false;
        }

        await dispatcher.InvokeAsync(
            () =>
            {
                if (resultStatus != ControlOperationStatus.Applied)
                {
                    RevertUnappliedChargeLimitEdit(
                        requestedLimit,
                        editorRevisionAtStart);
                    return;
                }

                bool editorStillShowsRequest = chargeLimitPercent == requestedLimit
                    && chargeLimitEditorRevision == editorRevisionAtStart;
                if (editorStillShowsRequest)
                {
                    chargeLimitEditorDirty = false;
                }
                pendingBatteryPolicy = requestedPolicy == Profiles.BatteryPolicy
                    ? null
                    : requestedPolicy;
                if (editorStillShowsRequest
                    && latestStatus is not null
                    && latestAcceptedRevision > revisionAtStart)
                {
                    LoadChargeLimitEditor(latestStatus);
                }
                NotifyBatteryProfileDraft();
            },
            token);
    }

    private async Task ToggleBatteryProtectionAsync(object? parameter, CancellationToken token)
    {
        PowerSource sourceAtStart = ActivePowerSource;
        bool requestedEnabled = !BatteryProtectionEnabled;
        ControlOperationStatus? resultStatus = null;
        IsBatteryProtectionOperationInProgress = true;
        try
        {
            await RunOperationAsync(
                cancellationToken => serviceClient.SetBatteryProtectionAsync(
                    requestedEnabled,
                    cancellationToken),
                token,
                result => resultStatus = result.Status);
        }
        finally
        {
            IsBatteryProtectionOperationInProgress = false;
        }

        if (resultStatus == ControlOperationStatus.Applied)
        {
            await dispatcher.InvokeAsync(
                () => MarkActiveProfileDraft(
                    sourceAtStart,
                    batteryProtectionEnabled: requestedEnabled),
                token);
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
        pendingLighting = null;
        List<string> hardwareNotApplied = [];
        if (FanOverride is not null)
        {
            await ClearOverrideAsync(
                TrayText.Get("Fragment.Fans", "fans"),
                cancellationToken => serviceClient.SetFanOverrideAsync(null, cancellationToken),
                hardwareNotApplied,
                token).ConfigureAwait(false);
        }
        if (PerformanceOverride is not null)
        {
            await ClearOverrideAsync(
                TrayText.Get("Fragment.Performance", "performance"),
                cancellationToken => serviceClient.SetPerformanceOverrideAsync(null, cancellationToken),
                hardwareNotApplied,
                token).ConfigureAwait(false);
        }
        if (LightingOverride is not null)
        {
            await ClearOverrideAsync(
                TrayText.Get("Fragment.Lighting", "lighting"),
                cancellationToken => serviceClient.SetLightingOverrideAsync(null, cancellationToken),
                hardwareNotApplied,
                token).ConfigureAwait(false);
        }
        if (BatteryProtectionOverride is not null && HasConfirmedPowerSource)
        {
            await ClearOverrideAsync(
                TrayText.Get("Fragment.BatteryProtection", "battery protection"),
                cancellationToken => serviceClient.SetBatteryProtectionAsync(
                    GetProfile(ActivePowerSource).EffectiveBatteryProtectionEnabled,
                    cancellationToken),
                hardwareNotApplied,
                token).ConfigureAwait(false);
        }
        if (BatteryPolicyOverride is not null)
        {
            await ClearOverrideAsync(
                TrayText.Get("Fragment.ChargeLimit", "charge limit"),
                cancellationToken => serviceClient.SetBatteryPolicyAsync(
                    Profiles.BatteryPolicy,
                    cancellationToken),
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
                notCleared.Add(TrayText.Get("Fragment.Performance", "performance"));
            }
            if (status.FanOverride is not null)
            {
                notCleared.Add(TrayText.Get("Fragment.Fans", "fans"));
            }
            if (status.LightingOverride is not null)
            {
                notCleared.Add(TrayText.Get("Fragment.Lighting", "lighting"));
            }
            if (status.BatteryProtectionOverride is not null)
            {
                notCleared.Add(TrayText.Get("Fragment.BatteryProtection", "battery protection"));
            }
            if (status.BatteryPolicyOverride is not null)
            {
                notCleared.Add(TrayText.Get("Fragment.ChargeLimit", "charge limit"));
            }

            message = notCleared.Count > 0
                ? TrayText.Format(
                    "Could not clear {0} overrides.",
                    string.Join(", ", notCleared))
                : hardwareNotApplied.Count == 0
                    ? TrayText.Get("Manual overrides cleared.")
                    : TrayText.Format(
                        "Manual overrides cleared; hardware restoration was not applied for {0}.",
                        string.Join(", ", hardwareNotApplied));
            failed = notCleared.Count > 0;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            message = TrayText.Get(
                "Manual override clear requests completed; service confirmation is unavailable.");
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
                            ? TrayText.Get("OpenBlade will start when you sign in.")
                            : TrayText.Get("OpenBlade will not start when you sign in.")
                        : TrayText.Get(
                            "Windows did not confirm the requested start-at-sign-in setting.");
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
                    StartupStatusMessage = TrayText.Format(
                        "Could not change start at sign-in: {0}",
                        exception.Message);
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
        if (parameter is null
            && !HasActiveProfileDraft
            && HasSaveableUnsetLaptopProfileField)
        {
            CaptureSaveableUnsetProfileFields(ProfileDraftGroup.Laptop);
        }

        bool savingDashboardDraft = HasActiveProfileDraft || HasGlobalBatteryPolicyDraft;
        ProfileSet? candidate = savingDashboardDraft
            ? CreateProfileSetWithDashboardDraft()
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
            if (savingDashboardDraft)
            {
                try
                {
                    if (HasActiveProfileDraft)
                    {
                        await ClearActiveProfileHardwareOverridesAsync(token);
                    }
                }
                finally
                {
                    // The profile update is already durable in the service. Keep the
                    // UI in sync even if clearing an equivalent temporary override
                    // fails or the operation is canceled afterward.
                    Profiles = draft;
                    ClearActiveProfileDraft();
                    ClearPendingBatteryPolicy();
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
        && ((parameter is null && HasSaveableUnsetLaptopProfileField
            || HasActiveProfileDraft
            || HasGlobalBatteryPolicyDraft)
            ? TryGetSupportedProfileDraft(CreateProfileSetWithDashboardDraft(), out _)
            : profileEditorsDirty && TryGetSupportedProfileDraft(parameter, out _));

    private bool CanSaveActiveProfileDraft(ProfileDraftGroup group) =>
        CanMutate
        && (group != ProfileDraftGroup.Lighting || CanControlLighting)
        && (HasActiveProfileDraftFor(group)
            && activeProfileDraft.Source == ActivePowerSource
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
                activeProfileDraft.OriginalDisplay ?? DisplayProfileSettings.Unconfigured();
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
            LaptopBehaviorProfileSettings original = activeProfileDraft.OriginalLaptopBehavior
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
        PowerSource? source = activeProfileDraft.Source
            ?? (HasDashboardProfileDifference && HasConfirmedPowerSource
                ? ActivePowerSource
                : null);
        if (source is null && pendingBatteryPolicy is null
            && !HasDashboardProfileDifference)
        {
            return;
        }

        if (source is not null && ActivePowerSource != source)
        {
            await ReportProfileRestoreSourceChangedAsync(token);
            return;
        }

        bool batteryProtectionRestored = source is null
            || activeProfileDraft.BatteryProtectionEnabled is null
                && !HasBatteryProtectionProfileDifference
            || await RunOperationAsync(
                cancellationToken => serviceClient.SetBatteryProtectionAsync(
                    GetProfile(source.Value).EffectiveBatteryProtectionEnabled,
                    cancellationToken),
                token);
        bool batteryPolicyRestored = pendingBatteryPolicy is null
                && !HasGlobalBatteryPolicyDifference
            || await RunOperationAsync(
                cancellationToken => serviceClient.SetBatteryPolicyAsync(
                    Profiles.BatteryPolicy,
                    cancellationToken),
                token);

        if (source is null)
        {
            if (batteryPolicyRestored)
            {
                ClearPendingBatteryPolicy();
            }
            return;
        }

        DisplayProfileSettings savedDisplay =
            GetProfile(source.Value).Display ?? DisplayProfileSettings.Unconfigured();
        DisplayProfileSettings originalDisplay =
            activeProfileDraft.OriginalDisplay ?? DisplayProfileSettings.Unconfigured();
        DisplayProfileSettings displayToRestore = new(
            savedDisplay.RefreshRateKey ?? originalDisplay.RefreshRateKey,
            savedDisplay.HdrEnabled ?? originalDisplay.HdrEnabled,
            savedDisplay.ColorProfileKey ?? originalDisplay.ColorProfileKey);
        bool thermalDifference = HasThermalProfileDifference;
        bool thermalControlsRestored = !thermalDifference
            || await ClearActiveProfileHardwareOverridesAsync(
                token,
                ProfileDraftGroup.Laptop,
                force: true);
        bool draftControlsRestored = await ClearActiveProfileHardwareOverridesAsync(
            token,
            thermalDifference ? ProfileDraftGroup.Lighting : null);
        bool controlsRestored = thermalControlsRestored && draftControlsRestored;
        bool displayRestored = activeProfileDraft.Display is null
            || await RestoreSavedDisplayProfileAsync(source.Value, displayToRestore, token);
        LaptopBehaviorProfileSettings savedLaptop = GetProfile(source.Value).EffectiveLaptopBehavior;
        LaptopBehaviorProfileSettings originalLaptop = activeProfileDraft.OriginalLaptopBehavior
            ?? LaptopBehaviorProfileSettings.Unconfigured();
        bool laptopRestored = !HasLaptopBehaviorProfileDifference
            || await RestoreSavedLaptopBehaviorAsync(
                source.Value,
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
            && batteryProtectionRestored
            && batteryPolicyRestored
            && displayRestored
            && laptopRestored
            && ActivePowerSource == source.Value)
        {
            ClearActiveProfileDraft();
            ClearPendingBatteryPolicy();
            LoadLightingPowerPolicyEditor(
                GetProfile(source.Value).EffectiveLightingPowerPolicy);
        }
        else if (ActivePowerSource != source.Value)
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
            && (force || activeProfileDraft.Performance is not null))
        {
            restored &= await RunOperationAsync(
                cancellationToken => serviceClient.SetPerformanceOverrideAsync(
                    null,
                    cancellationToken),
                token);
        }
        if ((group is null or ProfileDraftGroup.Thermal or ProfileDraftGroup.Laptop)
            && (force || activeProfileDraft.Fans is not null))
        {
            restored &= await RunOperationAsync(
                cancellationToken => serviceClient.SetFanOverrideAsync(
                    null,
                    cancellationToken),
                token);
        }
        if ((group is null or ProfileDraftGroup.Lighting)
            && (force || activeProfileDraft.Lighting is not null))
        {
            pendingLighting = null;

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
                    TrayText.Get("Could not restore the saved display profile because the display controller is unavailable."),
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
                            TrayText.Get("Could not restore the saved display profile because Windows did not confirm every setting."),
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
            string message = TrayText.Format(
                "Could not restore the saved display profile: {0}",
                exception.Message);
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
                TrayText.Get("Could not restore the saved profile settings because the power source changed."),
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

        return ProfileAdmissionPolicy.Supports(draft, Profiles, latestStatus?.Capabilities);
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
            string message = TrayText.Format("Operation failed: {0}", exception.Message);
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
                    OperationStatusMessage = TrayText.Get("Preparing diagnostic export...");
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
                        DiagnosticArchiveSaveStatus.Canceled =>
                            TrayText.Get("Diagnostic export canceled."),
                        DiagnosticArchiveSaveStatus.Saved
                            when !string.IsNullOrWhiteSpace(result.DestinationPath) =>
                            TrayText.Format(
                                "Diagnostics exported to {0}.",
                                result.DestinationPath),
                        _ => throw new InvalidDataException(
                            TrayText.Get("The diagnostic saver returned an invalid result.")),
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
            string message = TrayText.Format(
                "Diagnostic export failed: {0}",
                exception.Message);
            await dispatcher.InvokeAsync(
                () =>
                {
                    OperationStatusMessage = message;
                    TroubleshootingRecentErrorStatus = FormatTroubleshootingValue(message);
                },
                token);
        }
    }

    private void LoadLightingEditor(
        LightingSettings value,
        bool startUnconfirmedEffectAtDefaultBrightness = false)
    {
        pendingLighting = null;

        EnsureImportedSequenceOption(value);
        lightingEditor.Load(value, startUnconfirmedEffectAtDefaultBrightness);
        NotifyLightingParameterPresentation();
        NotifyComputed(nameof(SelectedLightingEffectOption), SelectedLightingEffectOption);
        NotifyComputed(nameof(SelectedLogoLightingModeOption), SelectedLogoLightingModeOption);
        NotifyComputed(nameof(SelectedAmbientAwarenessRegionOption), SelectedAmbientAwarenessRegionOption);
        NotifyComputed(nameof(LightingDraft), LightingDraft);
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
        NotifyComputed(nameof(HasUnsavedLightingPowerPolicy), HasUnsavedLightingPowerPolicy);
        NotifyComputed(nameof(LightingPowerPolicyDraft), LightingPowerPolicyDraft);
        NotifyCommands();
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
        NotifyComputed(nameof(PluggedInPerformanceOption), PluggedInPerformanceOption);
        NotifyComputed(nameof(BatteryPerformanceOption), BatteryPerformanceOption);
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
        NotifyComputed(nameof(UsbCPerformanceOption), UsbCPerformanceOption);
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

        return ProfileGpuClockTuning.Resolve(GetPresentedProfile(ActivePowerSource));
    }

    private PowerProfile GetPresentedProfile(PowerSource source) =>
        activeProfileDraft.Source == source
            ? GetActiveProfileDraft() ?? GetProfile(source)
            : GetProfile(source);

    private PowerProfile? GetActiveProfileDraft() => activeProfileDraft.GetDraft(Profiles);

    private ProfileSet CreateProfileSetWithDashboardDraft()
    {
        ProfileSet draft = HasActiveProfileDraft
            ? CreateProfileSetWithActiveDraft()
            : Profiles;
        return pendingBatteryPolicy is { } policy
            ? draft with { BatteryPolicy = policy }
            : draft;
    }

    private ProfileSet CreateProfileSetWithActiveDraft() => activeProfileDraft.CreateProfileSet(Profiles);

    private ProfileSet CreateProfileSetWithActiveDraft(ProfileDraftGroup group) =>
        activeProfileDraft.CreateProfileSet(Profiles, group);

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
        BatteryPolicy.Create(ProfileChargeLimitEditor))
    {
        WindowsPowerModes = Profiles.WindowsPowerModes,
    };

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
        LaptopBehaviorProfileSettings? previousLaptopBehavior = null,
        bool? batteryProtectionEnabled = null,
        WindowsPowerModeAssociations? windowsPowerModes = null)
    {
        if (activeProfileDraft.Stage(Profiles, ActivePowerSource, source, performance, customPerformance, gpuClockTuning, fans, fanControl,
            lighting, lightingPowerPolicy, display, previousDisplay, laptopBehavior,
            previousLaptopBehavior, batteryProtectionEnabled, windowsPowerModes))
        {
            NotifyActiveProfileDraft();
        }
    }

    private void ClearActiveProfileDraft()
    {
        if (!HasActiveProfileDraft)
        {
            return;
        }

        ClearActiveProfileDraftCore();
        NotifyActiveProfileDraft();
    }

    private void ClearActiveProfileDraftCore() => activeProfileDraft.Clear();

    private void ClearActiveProfileDraft(ProfileDraftGroup group)
    {
        activeProfileDraft.Clear(group);
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
                activeProfileDraft.Performance ?? SelectedPerformance;
            if (saved.Performance != currentPerformance)
            {
                return true;
            }

            if (saved.Performance == PerformancePreset.Custom)
            {
                CustomPerformanceSettings savedCustom =
                    saved.CustomPerformance ?? CustomPerformanceSettings.Default;
                CustomPerformanceSettings currentCustom =
                    activeProfileDraft.CustomPerformance
                    ?? new CustomPerformanceSettings(
                        SelectedCustomCpuLevel,
                        SelectedCustomGpuLevel);
                if (savedCustom.Cpu != currentCustom.Cpu
                    || savedCustom.Gpu != currentCustom.Gpu)
                {
                    return true;
                }
            }

            GpuClockTuningSettings? savedGpuClockTuning = ProfileGpuClockTuning.Resolve(saved);
            GpuClockTuningSettings? currentGpuClockTuning =
                activeProfileDraft.GpuClockTuning
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
            FanControlSettings currentFans = activeProfileDraft.FanControl
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
                activeProfileDraft.LaptopBehavior
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
            LightingSettings current = activeProfileDraft.Lighting ?? LightingDraft;
            bool keyboardDiffers = (activeProfileDraft.Lighting is not null
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

    private ActiveProfileDraftValues SaveableProfileValues => new(
        LogoLightingModeEditor is { } logoMode && Capabilities.LogoLightingModes.Contains(logoMode)
            ? logoMode : null,
        SupportsStartupAnimation ? StartupAnimationEnabled : null,
        SupportsFunctionKeyPrimary ? FunctionKeyPrimary : null,
        SupportsGamingMode ? GamingModeEnabled : null);

    private bool HasSaveableUnsetProfileField(ProfileDraftGroup group) =>
        HasConfirmedPowerSource && ActiveProfileDraftState.HasSaveableUnsetField(
            group, GetProfile(ActivePowerSource), SaveableProfileValues);

    private bool HasSaveableUnsetLightingProfileField =>
        HasSaveableUnsetProfileField(ProfileDraftGroup.Lighting);

    private bool HasSaveableUnsetLaptopProfileField =>
        HasSaveableUnsetProfileField(ProfileDraftGroup.Laptop);

    private void CaptureSaveableUnsetProfileFields(ProfileDraftGroup group)
    {
        if (HasConfirmedPowerSource && activeProfileDraft.CaptureSaveableUnsetFields(
            Profiles, ActivePowerSource, group, SaveableProfileValues))
        {
            NotifyActiveProfileDraft();
        }
    }

    private void NotifyActiveProfileDraft()
    {
        NotifyComputed(nameof(HasActiveProfileDraft), HasActiveProfileDraft);
        NotifyComputed(nameof(HasThermalProfileDraft), HasThermalProfileDraft);
        NotifyComputed(nameof(HasLightingProfileDraft), HasLightingProfileDraft);
        NotifyComputed(nameof(HasLightingProfileSaveOpportunity), HasLightingProfileSaveOpportunity);
        NotifyComputed(nameof(HasLightingProfileActionOpportunity), HasLightingProfileActionOpportunity);
        NotifyComputed(nameof(HasDisplayProfileDraft), HasDisplayProfileDraft);
        NotifyComputed(nameof(HasLaptopProfileDraft), HasLaptopProfileDraft);
        NotifyComputed(nameof(HasLaptopPageProfileDraft), HasLaptopPageProfileDraft);
        NotifyComputed(nameof(HasLaptopPageProfileSaveOpportunity), HasLaptopPageProfileSaveOpportunity);
        NotifyComputed(nameof(HasLaptopPageProfileActionOpportunity), HasLaptopPageProfileActionOpportunity);
        NotifyComputed(nameof(HasBatteryProtectionProfileDraft), HasBatteryProtectionProfileDraft);
        NotifyComputed(nameof(HasDashboardProfileDraft), HasDashboardProfileDraft);
        NotifyComputed(nameof(HasDashboardProfileSaveOpportunity), HasDashboardProfileSaveOpportunity);
        NotifyComputed(nameof(HasDashboardProfileActionOpportunity), HasDashboardProfileActionOpportunity);
        NotifyComputed(nameof(HasDashboardProfileRestoreOpportunity), HasDashboardProfileRestoreOpportunity);
        NotifyComputed(nameof(DashboardProfileSaveLabel), DashboardProfileSaveLabel);
        NotifyComputed(nameof(ThermalProfileRelationshipStatus), ThermalProfileRelationshipStatus);
        NotifyComputed(nameof(LightingProfileRelationshipStatus), LightingProfileRelationshipStatus);
        NotifyComputed(nameof(IsPluggedInProfileDraft), IsPluggedInProfileDraft);
        NotifyComputed(nameof(IsBatteryProfileDraft), IsBatteryProfileDraft);
        NotifyComputed(nameof(IsUsbCProfileDraft), IsUsbCProfileDraft);
        NotifyComputed(nameof(HasUnsavedProfileChanges), HasUnsavedProfileChanges);
        NotifyComputed(nameof(ActiveProfileDraftName), ActiveProfileDraftName);
        NotifyComputed(nameof(SavedActiveProfileSummary), SavedActiveProfileSummary);
        NotifyComputed(nameof(PendingActiveProfileSummary), PendingActiveProfileSummary);
        NotifyComputed(nameof(SelectedWindowsPowerModeOption), SelectedWindowsPowerModeOption);
        NotifyComputed(nameof(SelectedWindowsPowerModeDescription), SelectedWindowsPowerModeDescription);
        NotifyGamingModeKeyPolicy();
        NotifyProfileSummaries();
        NotifyCommands();
    }

    private void ClearPendingBatteryPolicy()
    {
        if (pendingBatteryPolicy is null)
        {
            return;
        }

        pendingBatteryPolicy = null;
        NotifyBatteryProfileDraft();
    }

    private void NotifyBatteryProfileDraft()
    {
        NotifyComputed(nameof(HasGlobalBatteryPolicyDraft), HasGlobalBatteryPolicyDraft);
        NotifyComputed(nameof(HasDashboardProfileDraft), HasDashboardProfileDraft);
        NotifyComputed(nameof(HasDashboardProfileSaveOpportunity), HasDashboardProfileSaveOpportunity);
        NotifyComputed(nameof(HasDashboardProfileActionOpportunity), HasDashboardProfileActionOpportunity);
        NotifyComputed(nameof(HasDashboardProfileRestoreOpportunity), HasDashboardProfileRestoreOpportunity);
        NotifyComputed(nameof(DashboardProfileSaveLabel), DashboardProfileSaveLabel);
        NotifyComputed(nameof(HasUnsavedProfileChanges), HasUnsavedProfileChanges);
        NotifyComputed(nameof(BatteryPolicyPresentation), BatteryPolicyPresentation);
        NotifyProfileSummaries();
        NotifyCommands();
    }

    private void NotifyGamingModeKeyPolicy()
    {
        NotifyComputed(nameof(BlockWindowsKeyInGamingMode), BlockWindowsKeyInGamingMode);
        NotifyComputed(nameof(BlockAltTabInGamingMode), BlockAltTabInGamingMode);
        NotifyComputed(nameof(CanEditGamingModeKeyPolicy), CanEditGamingModeKeyPolicy);
    }

    private void NotifyProfileSummaries()
    {
        NotifyComputed(nameof(PluggedInProfileThermalSummary), PluggedInProfileThermalSummary);
        NotifyComputed(nameof(PluggedInProfileLightingSummary), PluggedInProfileLightingSummary);
        NotifyComputed(nameof(BatteryProfileThermalSummary), BatteryProfileThermalSummary);
        NotifyComputed(nameof(BatteryProfileLightingSummary), BatteryProfileLightingSummary);
        NotifyComputed(nameof(UsbCProfileThermalSummary), UsbCProfileThermalSummary);
        NotifyComputed(nameof(UsbCProfileLightingSummary), UsbCProfileLightingSummary);
        NotifyComputed(nameof(PluggedInProfileDisplaySummary), PluggedInProfileDisplaySummary);
        NotifyComputed(nameof(BatteryProfileDisplaySummary), BatteryProfileDisplaySummary);
        NotifyComputed(nameof(UsbCProfileDisplaySummary), UsbCProfileDisplaySummary);
        NotifyComputed(nameof(ActiveSavedDisplayProfile), ActiveSavedDisplayProfile);
        NotifyComputed(nameof(SavedActiveProfileSummary), SavedActiveProfileSummary);
        NotifyComputed(nameof(PendingActiveProfileSummary), PendingActiveProfileSummary);
        NotifyComputed(nameof(PluggedInProfilePresentation), PluggedInProfilePresentation);
        NotifyComputed(nameof(UsbCProfilePresentation), UsbCProfilePresentation);
        NotifyComputed(nameof(BatteryProfilePresentation), BatteryProfilePresentation);
        NotifyComputed(nameof(BatteryPolicyPresentation), BatteryPolicyPresentation);
        NotifyComputed(nameof(HasLightingProfileSaveOpportunity), HasLightingProfileSaveOpportunity);
        NotifyComputed(nameof(HasLaptopPageProfileSaveOpportunity), HasLaptopPageProfileSaveOpportunity);
        NotifyProfileDifferenceState();
        NotifySelectedProfilePresentation();
    }

    private void NotifySelectedProfilePresentation()
    {
        NotifyComputed(nameof(SelectedProfilePresentation), SelectedProfilePresentation);
        NotifyComputed(nameof(IsSelectedProfileActive), IsSelectedProfileActive);
        NotifyComputed(nameof(IsSelectedProfileDraft), IsSelectedProfileDraft);
    }

    private void NotifyLaptopBehaviorProfilePresentations()
    {
        NotifyComputed(nameof(PluggedInProfilePresentation), PluggedInProfilePresentation);
        NotifyComputed(nameof(UsbCProfilePresentation), UsbCProfilePresentation);
        NotifyComputed(nameof(BatteryProfilePresentation), BatteryProfilePresentation);
        NotifyComputed(nameof(SelectedProfilePresentation), SelectedProfilePresentation);
    }

    private static string SubsystemLabel(DeviceSubsystem subsystem) => subsystem switch
    {
        DeviceSubsystem.Performance => TrayText.Get("Power mode"),
        DeviceSubsystem.Fans => TrayText.Get("Fans"),
        DeviceSubsystem.Battery => TrayText.Get("Battery protection"),
        DeviceSubsystem.Lighting => TrayText.Get("Keyboard lighting"),
        _ => TrayText.Get(subsystem.ToString()),
    };

    private string ProfileRelationshipStatus(bool hasDraft, bool changedOutside) =>
        !IsConnected || latestStatus?.DeviceVerified != true
            ? UnavailableLabel
            : !HasConfirmedPowerSource
                ? TrayText.Get("Waiting for power source")
                : hasDraft
                    ? TrayText.Get("Temporary change not saved to this profile")
                    : changedOutside
                        ? TrayText.Get("Changed outside OpenBlade")
                        : TrayText.Get("Matches saved profile");

    private ProfilePresentation CreateProfilePresentation(PowerSource source)
    {
        string profileName = ComparisonOptions
            .First(option => option.Source == source)
            .Label;
        PowerProfile savedProfile = GetProfile(source);
        var confirmedLaptopBehavior = new LaptopBehaviorProfileSettings(
            StartupAnimationEnabled,
            FunctionKeyPrimary,
            GamingModeEnabled);
        LaptopBehaviorProfileSettings savedLaptopBehaviorFallback =
            activeProfileDraft.Source == source
                ? activeProfileDraft.OriginalLaptopBehavior ?? confirmedLaptopBehavior
                : confirmedLaptopBehavior;
        ProfilePresentation saved = ProfilePresentationFactory.Create(
            savedProfile,
            Profiles.WindowsPowerModes,
            profileName,
            savedLaptopBehaviorFallback);
        PowerProfile draft = activeProfileDraft.Source == source
            ? GetActiveProfileDraft() ?? savedProfile
            : savedProfile;
        if (activeProfileDraft.Source != source
            && activeProfileDraft.WindowsPowerModes is null)
        {
            return saved;
        }

        ProfilePresentation pending = ProfilePresentationFactory.Create(
            draft,
            PresentedWindowsPowerModes,
            profileName,
            confirmedLaptopBehavior);
        return ProfilePresentationFactory.WithPending(
            saved,
            pending,
            !FanControlSettingsMatch(
                savedProfile.FanControl
                    ?? FanControlSettings.FromLegacy(savedProfile.Fans),
                draft.FanControl ?? FanControlSettings.FromLegacy(draft.Fans)));
    }

    private void EnsureImportedSequenceOption(LightingSettings value)
    {
        if (value.Effect != LightingEffect.Sequence
            || AvailableLightingEffectOptions.Any(
                option => option.Effect == LightingEffect.Sequence))
        {
            return;
        }

        AvailableLightingEffectOptions =
            AvailableLightingEffectOptions
                .Append(CreateLightingEffectOption(LightingEffect.Sequence))
                .OrderBy(option => LightingEffectDisplayOrder(option.Effect))
                .ToArray();
    }

    private void NotifyCommands()
    {
        foreach (AsyncRelayCommand command in commands)
        {
            command.NotifyCanExecuteChanged();
        }

        NotifyComputed(nameof(CanAdjustChargeLimit), CanAdjustChargeLimit);
        NotifyComputed(nameof(CanChangeBatteryProtection), CanChangeBatteryProtection);
        if (!suppressProfileDifferenceNotifications)
        {
            NotifyProfileDifferenceState();
        }
    }

    private void NotifyProfileDifferenceState()
    {
        NotifyComputed(nameof(HasThermalProfileDifference), HasThermalProfileDifference);
        NotifyComputed(nameof(HasLaptopBehaviorProfileDifference), HasLaptopBehaviorProfileDifference);
        NotifyComputed(nameof(HasLaptopPageProfileDifference), HasLaptopPageProfileDifference);
        NotifyComputed(nameof(HasLaptopPageProfileActionOpportunity), HasLaptopPageProfileActionOpportunity);
        NotifyComputed(nameof(HasLightingProfileDifference), HasLightingProfileDifference);
        NotifyComputed(nameof(HasLightingProfileActionOpportunity), HasLightingProfileActionOpportunity);
        NotifyComputed(nameof(HasBatteryProtectionProfileDifference), HasBatteryProtectionProfileDifference);
        NotifyComputed(nameof(HasGlobalBatteryPolicyDifference), HasGlobalBatteryPolicyDifference);
        NotifyComputed(nameof(HasDashboardProfileDifference), HasDashboardProfileDifference);
        NotifyComputed(nameof(HasDashboardProfileActionOpportunity), HasDashboardProfileActionOpportunity);
        NotifyComputed(nameof(HasDashboardProfileRestoreOpportunity), HasDashboardProfileRestoreOpportunity);
    }

    private string SourceLabel(
        DeviceSubsystem subsystem,
        bool confirmed,
        ServiceStatusSnapshot status,
        IReadOnlyCollection<DeviceSubsystem> externalChanges) =>
        IsCheckingLaptopConnection(status)
            ? TrayText.Get("Checking laptop")
            : !status.DeviceVerified
            ? UnavailableLaptopReason
            : externalChanges.Contains(subsystem)
            ? TrayText.Get("Changed outside OpenBlade")
            : subsystem == DeviceSubsystem.Lighting
                && !confirmed
                && !status.AlwaysOverrideExternalLightingChanges
                && status.PotentialControllers.Any(
                    CompetingLightingControllers.Contains)
            ? TrayText.Get("Other controller may be active")
            : confirmed
                ? TrayText.Get("Current setting")
                : TrayText.Get("Controls unavailable");

    private string FormatBatterySource(
        ConfirmedBatteryState? battery,
        bool changedOutside,
        ServiceStatusSnapshot status)
    {
        if (IsCheckingLaptopConnection(status))
        {
            return TrayText.Get("Checking laptop");
        }
        if (!status.DeviceVerified)
        {
            return UnavailableLaptopReason;
        }
        if (battery is null)
        {
            return TrayText.Get("Controls unavailable");
        }

        string state = battery.TemporaryFullCharge switch
        {
            null => TrayText.Get("Temporary full charge state unavailable"),
            true => TrayText.Get("Temporary full charge active"),
            false when !battery.ProtectionEnabled => TrayText.Get("Battery protection off"),
            _ => TrayText.Format("Charge limit {0}%", battery.ChargeLimitPercent),
        };
        string source = changedOutside
            ? TrayText.Get("Changed outside OpenBlade")
            : TrayText.Get("Current setting");
        return TrayText.Format("{0} · {1}", source, state);
    }

    private static string FormatCurrentBatterySetting(ConfirmedBatteryState? battery) =>
        battery switch
        {
            null => TrayText.Get("Battery setting unavailable"),
            { TemporaryFullCharge: true } => TrayText.Get("Charging to 100%"),
            { ProtectionEnabled: false } => TrayText.Get("Battery protection off"),
            _ => TrayText.Format("Charge limit {0}%", battery.ChargeLimitPercent),
        };

    private static string FormatBatteryPowerFlow(
        LiveReadings? readings,
        PowerSource powerSource)
    {
        if (readings?.BatteryPowerFlowMilliwatts is not int milliwatts)
        {
            return TrayText.Get("Battery power unavailable");
        }
        if (milliwatts == 0)
        {
            return powerSource switch
            {
                PowerSource.PluggedIn or PowerSource.UsbC => TrayText.Get("Not charging"),
                PowerSource.Battery or PowerSource.ExternalUnknown =>
                    TrayText.Get("Battery power unavailable"),
                _ => TrayText.Get("Battery power unavailable"),
            };
        }
        double watts = Math.Abs((long)milliwatts) / 1000d;
        string value = watts.ToString("0.0", CultureInfo.CurrentCulture);
        return milliwatts > 0
            ? TrayText.Format("Charging at {0} W", value)
            : TrayText.Format("Using {0} W", value);
    }

    private static string FormatFanRpm(string label, int? rpm) => rpm is int value
        ? TrayText.Format("{0}: {1:N0} RPM", label, value)
        : TrayText.Format("{0}: unavailable", label);

    private static string FormatFanRpmValue(int? rpm) => rpm is int value
        ? TrayText.Format("{0:N0} RPM", value)
        : TrayText.Get("Unavailable");

    private static string FormatTemperatureValue(int? celsius) => celsius is int value
        ? TrayText.Format("{0} °C", value)
        : TrayText.Get("Unavailable");

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
        int? cpuCelsius,
        bool showGpu,
        int? gpuCelsius)
    {
        if (!showCpu && !showGpu)
        {
            return string.Empty;
        }

        if (showCpu
            && showGpu
            && cpuCelsius is null
            && gpuCelsius is null)
        {
            return UnavailableLabel;
        }

        string[] values =
        [
            showCpu ? TrayText.Format("CPU {0}", FormatTemperatureValue(cpuCelsius)) : string.Empty,
            showGpu ? TrayText.Format("GPU {0}", FormatTemperatureValue(gpuCelsius)) : string.Empty,
        ];
        return string.Join(
            TrayText.Get(" / "),
            values.Where(value => !string.IsNullOrWhiteSpace(value)));
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
        LiveReadingItemKind.BatteryPower => TrayText.Get("Battery"),
        LiveReadingItemKind.CpuTemperature => TrayText.Get("CPU temperature"),
        LiveReadingItemKind.GpuTemperature => TrayText.Get("GPU temperature"),
        LiveReadingItemKind.Fan1Speed => TrayText.Get("Fan 1"),
        LiveReadingItemKind.Fan2Speed => TrayText.Get("Fan 2"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string TrayHoverLabelItemLabel(TrayHoverLabelItemKind kind) => kind switch
    {
        TrayHoverLabelItemKind.ActiveProfile => TrayText.Get("Active profile"),
        TrayHoverLabelItemKind.AppName => TrayText.Get("OpenBlade app name"),
        TrayHoverLabelItemKind.PowerSource => TrayText.Get("Power source"),
        TrayHoverLabelItemKind.PowerMode => TrayText.Get("Power mode"),
        TrayHoverLabelItemKind.BatteryPower => TrayText.Get("Battery power"),
        TrayHoverLabelItemKind.CpuTemperature => TrayText.Get("CPU temperature"),
        TrayHoverLabelItemKind.GpuTemperature => TrayText.Get("GPU temperature"),
        TrayHoverLabelItemKind.Fan1Speed => TrayText.Get("Fan 1 speed"),
        TrayHoverLabelItemKind.Fan2Speed => TrayText.Get("Fan 2 speed"),
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
                ? TrayText.Get("Another user is controlling OpenBlade")
                : TrayText.Get("Interactive session required");
        }

        if (IsCheckingLaptopConnection(status))
        {
            return TrayText.Get("Checking laptop");
        }

        if (!status.DeviceVerified)
        {
            return UnavailableLaptopReason;
        }

        if (status.ActivePowerSource == PowerSource.ExternalUnknown)
        {
            return PowerSourcePresentation.Label(status.ActivePowerSource);
        }

        if (IsRetainedPowerSourceRecheck(status))
        {
            return TrayText.Get("Ready");
        }

        return status.WritesBlocked
            ? TrayText.Get("Controls unavailable")
            : TrayText.Get("Ready");
    }

    private static bool IsRetainedPowerSourceRecheck(ServiceStatusSnapshot? status) =>
        DeviceObservationState.IsRetainedPowerSourceRecheck(status);

    private static bool IsCheckingLaptopConnection(ServiceStatusSnapshot? status) =>
        DeviceObservationState.IsCheckingLaptopConnection(status);

    private static string FormatOperationFeedback(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }
        if (message.Contains("without hardware", StringComparison.OrdinalIgnoreCase)
            || message.Contains("not applied", StringComparison.OrdinalIgnoreCase))
        {
            return TrayText.Get(
                "OpenBlade saved the change, but the laptop has not applied it.");
        }
        if (message.Contains("failed", StringComparison.OrdinalIgnoreCase)
            || message.Contains("could not", StringComparison.OrdinalIgnoreCase)
            || message.Contains("unavailable", StringComparison.OrdinalIgnoreCase))
        {
            return TrayText.Get(
                "OpenBlade couldn’t complete that action. Check Troubleshooting for details.");
        }
        return string.Empty;
    }

    private static string FormatTroubleshootingValue(string value) =>
        value.Trim().TrimEnd('.');

    private static string FirmwareValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? TrayText.Get("Unavailable") : value.Trim();

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
        return TrayText.Format(
            "{0}% ({1} Wh of {2} Wh)",
            percentage,
            fullChargeCapacity,
            designCapacity);
    }

    private static bool HasConcreteFirmwareValue(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && !string.Equals(
            value.Trim(),
            "Unavailable",
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
                "Unavailable",
                StringComparison.OrdinalIgnoreCase));
        return hasUnavailableValue
            ? TrayText.Format(
                "OpenBlade could not verify this configuration: {0}.",
                versions)
            : TrayText.Format(
                "OpenBlade has not tested this configuration: {0}.",
                versions);
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
        return TrayText.Format("{0} {1}", mismatch.Component, currentValue);
    }

    private static string FormatOperationResult(ControlOperationResult result) => result.Status switch
    {
        ControlOperationStatus.Applied or ControlOperationStatus.Stored => string.Empty,
        ControlOperationStatus.Blocked => TrayText.Get(
            "OpenBlade saved the change, but the laptop has not applied it."),
        _ => TrayText.Get(
            "OpenBlade couldn’t complete that action. Check Troubleshooting for details."),
    };

    private void UpdateOneShotReadbacks(ServiceStatusSnapshot status)
    {
        DeviceObservationState readbacks = observation.ObserveOneShotReadbacks(status);
        bool pendingChanged =
            observation.StartupAnimationReadPending != readbacks.StartupAnimationReadPending
            || observation.FunctionKeyReadPending != readbacks.FunctionKeyReadPending
            || observation.GamingModeReadPending != readbacks.GamingModeReadPending;

        // Apply pending flags before the bound values, preserving notification order.
        observation = observation with
        {
            StartupAnimationReadPending = readbacks.StartupAnimationReadPending,
            FunctionKeyReadPending = readbacks.FunctionKeyReadPending,
            GamingModeReadPending = readbacks.GamingModeReadPending,
        };
        StartupAnimationEnabled = readbacks.StartupAnimationEnabled;
        FunctionKeyPrimary = readbacks.FunctionKeyPrimary;
        GamingModeEnabled = readbacks.GamingModeEnabled;

        if (pendingChanged)
        {
            NotifyFirmwareFieldLoadingState();
            NotifyCommands();
        }
    }

    private void NotifyPresentationState()
    {
        NotifyComputed(nameof(IsServiceUnavailable), IsServiceUnavailable);
        NotifyComputed(nameof(ControlAccessStatus), ControlAccessStatus);
        NotifyComputed(nameof(CanRequestHardwareControl), CanRequestHardwareControl);
        NotifyComputed(nameof(CanReapplyChangedSettings), CanReapplyChangedSettings);
        NotifyComputed(nameof(CanRecoverHyperBoostRestore), CanRecoverHyperBoostRestore);
        NotifyComputed(nameof(CanCheckCurrentState), CanCheckCurrentState);
        NotifyComputed(nameof(ShowReapplyChangedSettings), ShowReapplyChangedSettings);
        NotifyComputed(nameof(CanChangeThermalControlPolicy), CanChangeThermalControlPolicy);
        NotifyComputed(nameof(CanAcknowledgeThermalPolicyUpgradeDisclosure), CanAcknowledgeThermalPolicyUpgradeDisclosure);
        NotifyComputed(nameof(CanEditPerformance), CanEditPerformance);
        NotifyComputed(nameof(CanSelectLaptopPerformance), CanSelectLaptopPerformance);
        NotifyComputed(nameof(CanEditWindowsPowerMode), CanEditWindowsPowerMode);
        NotifyComputed(nameof(CanToggleGpuClockTuning), CanToggleGpuClockTuning);
        NotifyComputed(nameof(CanEditGpuClockTuning), CanEditGpuClockTuning);
        NotifyComputed(nameof(IsHyperBoostVisible), IsHyperBoostVisible);
        NotifyComputed(nameof(IsHyperBoostWritable), IsHyperBoostWritable);
        NotifyComputed(nameof(CanToggleHyperBoost), CanToggleHyperBoost);
        NotifyComputed(nameof(HyperBoostDescription), HyperBoostDescription);
        NotifyHyperBoostAvailability();
        NotifyComputed(nameof(AvailableLaptopPerformanceOptions), AvailableLaptopPerformanceOptions);
        NotifyComputed(nameof(SelectedLaptopPerformanceOption), SelectedLaptopPerformanceOption);
        NotifyComputed(nameof(CanEditFanControl), CanEditFanControl);
        NotifyComputed(nameof(CanChangeFanMode), CanChangeFanMode);
        NotifyGamingModeKeyPolicy();
        NotifyDashboardTelemetryValuesChanged();
        NotifyComputed(nameof(LiveReadingsRefreshToolTip), LiveReadingsRefreshToolTip);
        NotifyComputed(nameof(ActivePerformanceLabel), ActivePerformanceLabel);
        NotifyComputed(nameof(ActiveFanModeLabel), ActiveFanModeLabel);
        NotifyComputed(nameof(TrayDashboardPowerSourceLabel), TrayDashboardPowerSourceLabel);
        NotifyComputed(nameof(ProfileStateLabel), ProfileStateLabel);
        NotifyComputed(nameof(IsReadinessReady), IsReadinessReady);
        NotifyComputed(nameof(IsReadinessChecking), IsReadinessChecking);
        NotifyComputed(nameof(IsReadinessWarning), IsReadinessWarning);
        NotifyComputed(nameof(IsReadinessError), IsReadinessError);
        NotifyComputed(nameof(HasLightingCapability), HasLightingCapability);
        NotifyComputed(nameof(CanControlLighting), CanControlLighting);
        NotifyComputed(nameof(CanSetLightingRefreshRate), CanSetLightingRefreshRate);
        NotifyComputed(nameof(IsLightingControlUnavailable), IsLightingControlUnavailable);
        NotifyComputed(nameof(IsLightingControlUnavailableNoticeVisible), IsLightingControlUnavailableNoticeVisible);
        NotifyComputed(nameof(LightingControlUnavailableMessage), LightingControlUnavailableMessage);
        NotifyComputed(nameof(ShowOpenBladeKeyboardLightingControls), ShowOpenBladeKeyboardLightingControls);
        NotifyComputed(nameof(BatteryPolicyOverride), BatteryPolicyOverride);
        NotifyComputed(nameof(BatteryProtectionOverride), BatteryProtectionOverride);
        NotifyComputed(nameof(ManualOverrideStatus), ManualOverrideStatus);
        NotifyComputed(nameof(HasActiveChanges), HasActiveChanges);
        NotifyComputed(nameof(ActiveChangesText), ActiveChangesText);
        NotifyComputed(nameof(HasAttention), HasAttention);
        NotifyComputed(nameof(AttentionSummary), AttentionSummary);
        NotifyComputed(nameof(AttentionBadgeLabel), AttentionBadgeLabel);
        NotifyComputed(nameof(AttentionDetails), AttentionDetails);
        NotifyComputed(nameof(TroubleshootingServiceStatus), TroubleshootingServiceStatus);
        NotifyComputed(nameof(TroubleshootingPowerSourceStatus), TroubleshootingPowerSourceStatus);
        NotifyComputed(nameof(TroubleshootingControlsStatus), TroubleshootingControlsStatus);
        NotifyComputed(nameof(TroubleshootingKeyboardDriverModeStatus), TroubleshootingKeyboardDriverModeStatus);
        NotifyComputed(nameof(TroubleshootingUsbDeviceStatus), TroubleshootingUsbDeviceStatus);
        NotifyComputed(nameof(TroubleshootingOtherRazerSoftwareStatus), TroubleshootingOtherRazerSoftwareStatus);
        NotifyComputed(nameof(AboutServiceVersionStatus), AboutServiceVersionStatus);
        NotifyComputed(nameof(AboutDetectedLaptopStatus), AboutDetectedLaptopStatus);
        NotifyComputed(nameof(DetectedFirmware), DetectedFirmware);
        NotifyComputed(nameof(AboutBiosVersionStatus), AboutBiosVersionStatus);
        NotifyComputed(nameof(AboutEcVersionStatus), AboutEcVersionStatus);
        NotifyComputed(nameof(AboutMcuVersionStatus), AboutMcuVersionStatus);
        NotifyComputed(nameof(AboutVbiosVersionStatus), AboutVbiosVersionStatus);
        NotifyComputed(nameof(AboutBatteryHealthStatus), AboutBatteryHealthStatus);
        NotifyComputed(nameof(AboutDisplayBrightnessDriverStatus), AboutDisplayBrightnessDriverStatus);
        NotifyComputed(nameof(AboutDisplayBrightnessDriverToolTip), AboutDisplayBrightnessDriverToolTip);
        NotifyComputed(nameof(HasUntestedPlatformVersion), HasUntestedPlatformVersion);
        NotifyComputed(nameof(HasIncompatibleFirmware), HasIncompatibleFirmware);
        NotifyComputed(nameof(PlatformCompatibilityWarningTitle), PlatformCompatibilityWarningTitle);
        NotifyComputed(nameof(DashboardTestedPlatformWarningMessage), DashboardTestedPlatformWarningMessage);
        NotifyComputed(nameof(AboutTestedPlatformWarningMessage), AboutTestedPlatformWarningMessage);
    }

    private void NotifyHyperBoostAvailability()
    {
        NotifyComputed(
            nameof(HyperBoostUnavailableReason),
            HyperBoostUnavailableReason);
        NotifyComputed(
            nameof(HasHyperBoostUnavailableReason),
            HasHyperBoostUnavailableReason);
    }

    private void NotifyFirmwareFieldLoadingState()
    {
        NotifyComputed(nameof(IsStartupAnimationFieldLoading), IsStartupAnimationFieldLoading);
        NotifyComputed(nameof(CanEditStartupAnimation), CanEditStartupAnimation);
        NotifyComputed(nameof(IsFunctionKeyFieldLoading), IsFunctionKeyFieldLoading);
        NotifyComputed(nameof(CanEditFunctionKeyPrimary), CanEditFunctionKeyPrimary);
        NotifyComputed(nameof(IsGamingModeFieldLoading), IsGamingModeFieldLoading);
        NotifyComputed(nameof(CanEditGamingMode), CanEditGamingMode);
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
            LiveReadings?.Fan2Rpm,
            HyperBoostActive ? TrayText.Get("HyperBoost") : null);

    private void NotifyTrayHoverLabel()
    {
        NotifyComputed(nameof(TrayHoverLabelText), TrayHoverLabelText);
        NotifyComputed(nameof(HasTrayHoverLabelOverflow), HasTrayHoverLabelOverflow);
        NotifyComputed(nameof(TrayHoverLabelOverflowMessage), TrayHoverLabelOverflowMessage);
    }

    private void Notify(string? propertyName)
    {
        // A direct notification forgets the last published value so a later
        // NotifyComputed for the same property can never suppress a real change.
        if (string.IsNullOrEmpty(propertyName))
        {
            publishedComputedValues.Clear();
        }
        else
        {
            publishedComputedValues.Remove(propertyName);
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    // Every PropertyChanged reaches each native XAML {Binding} subscriber through
    // a CsWinRT marshal of the sender, so the batch Notify* methods publish a
    // computed property only when its value differs from the last one published.
    private void NotifyComputed<T>(string propertyName, T value)
    {
        if (publishedComputedValues.TryGetValue(propertyName, out object? published)
            && Equals(published, value))
        {
            return;
        }

        publishedComputedValues[propertyName] = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
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
            ? TrayText.Get("until reset")
            : TrayText.Get("until the power source changes");
        string displayValue = formatValue?.Invoke(value.Value)
            ?? (value.Value is null ? string.Empty : value.Value.ToString())
            ?? string.Empty;
        status.Add(TrayText.Format("{0}: {1} {2}", subsystem, displayValue, lifetime));
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
            ? TrayText.Get("until reset")
            : TrayText.Get("until the power source changes");
        string displayValue = formatValue?.Invoke(value.Value) ?? value.Value.ToString();
        if (value.CustomPerformance is { } custom)
        {
            displayValue += TrayText.Format(
                " (CPU {0}, GPU {1})",
                ProfilePresentationFactory.CustomPerformanceLevelLabel(custom.Cpu),
                ProfilePresentationFactory.CustomPerformanceLevelLabel(custom.Gpu));
        }
        status.Add(TrayText.Format("{0}: {1} {2}", subsystem, displayValue, lifetime));
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

    private void LoadFanControlEditor(FanControlSettings value) =>
        NotifyFanControlEditorChanges(fanControlEditor.Load(value, ActiveFanSpeedRange));

    private void NotifyFanControlEditorChanges(FanControlEditorChanges changes)
    {
        if ((changes & FanControlEditorChanges.FixedSpeed) != 0)
        {
            Notify(nameof(FixedFanSpeedRpm));
        }
        if ((changes & FanControlEditorChanges.UseHigherTemperature) != 0)
        {
            Notify(nameof(UseHigherFanTemperature));
        }
        if ((changes & FanControlEditorChanges.CpuPoints) != 0)
        {
            Notify(nameof(CpuFanCurveEditorPoints));
        }
        if ((changes & FanControlEditorChanges.GpuPoints) != 0)
        {
            Notify(nameof(GpuFanCurveEditorPoints));
        }
        if (changes != FanControlEditorChanges.None)
        {
            Notify(nameof(CurrentFanControlSettings));
        }
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

    private void OnLightingEditorChanged(LightingEditorChange change)
    {
        if (change.Kind == LightingEditorChangeKind.ParametersChanged)
        {
            NotifyLightingParameterPresentation();
            return;
        }
        if (change.Kind == LightingEditorChangeKind.EditCompleted)
        {
            NotifyCommands();
            return;
        }
        if (change.Kind == LightingEditorChangeKind.Refreshed)
        {
            switch (change.Value)
            {
                case LightingEditorValue.PrimaryColor:
                    NotifyComputed(nameof(LightingPrimaryColorEditor), LightingPrimaryColorEditor);
                    break;
                case LightingEditorValue.SecondaryColor:
                    NotifyComputed(nameof(LightingSecondaryColorEditor), LightingSecondaryColorEditor);
                    break;
                case LightingEditorValue.AudioMeterDecayMilliseconds:
                    NotifyComputed(nameof(LightingAudioMeterDecayValue), LightingAudioMeterDecayValue);
                    break;
            }
            return;
        }
        if (change.Kind == LightingEditorChangeKind.TransitionCompleted)
        {
            switch (change.Value)
            {
                case LightingEditorValue.Effect:
                    Notify(nameof(SelectedLightingEffectOption));
                    break;
                case LightingEditorValue.Direction:
                    Notify(nameof(SelectedLightingDirectionOption));
                    break;
                case LightingEditorValue.AmbientAwarenessRegion:
                    Notify(nameof(SelectedAmbientAwarenessRegionOption));
                    break;
                case LightingEditorValue.LogoMode:
                    Notify(nameof(SelectedLogoLightingModeOption));
                    break;
                case LightingEditorValue.AudioMeterDecayMilliseconds:
                    Notify(nameof(LightingAudioMeterDecayValue));
                    break;
            }
            return;
        }
        if (change.Value == LightingEditorValue.EffectUnconfirmed)
        {
            Notify(nameof(IsKeyboardEffectUnconfirmed));
            Notify(nameof(KeyboardEffectDescription));
            Notify(nameof(SelectedLightingEffectOption));
            return;
        }
        Notify(change.Value switch
        {
            LightingEditorValue.Effect => nameof(LightingEffectEditor),
            LightingEditorValue.Brightness => nameof(LightingBrightnessEditor),
            LightingEditorValue.Red => nameof(LightingRedEditor),
            LightingEditorValue.Green => nameof(LightingGreenEditor),
            LightingEditorValue.Blue => nameof(LightingBlueEditor),
            LightingEditorValue.PrimaryColor => nameof(LightingPrimaryColorEditor),
            LightingEditorValue.SecondaryRed => nameof(LightingSecondaryRedEditor),
            LightingEditorValue.SecondaryGreen => nameof(LightingSecondaryGreenEditor),
            LightingEditorValue.SecondaryBlue => nameof(LightingSecondaryBlueEditor),
            LightingEditorValue.SecondaryColor => nameof(LightingSecondaryColorEditor),
            LightingEditorValue.BreathingColorMode => nameof(LightingBreathingColorModeEditor),
            LightingEditorValue.ReactiveDuration => nameof(LightingReactiveDurationEditor),
            LightingEditorValue.StarlightColorMode => nameof(LightingStarlightColorModeEditor),
            LightingEditorValue.StarlightDuration => nameof(LightingStarlightDurationEditor),
            LightingEditorValue.RandomColors => nameof(LightingRandomColorsEditor),
            LightingEditorValue.Direction => nameof(LightingDirectionEditor),
            LightingEditorValue.RotationDirection => nameof(LightingRotationDirectionEditor),
            LightingEditorValue.AmbientAwarenessRegion => nameof(LightingAmbientAwarenessRegionEditor),
            LightingEditorValue.AudioMeterColorBoost => nameof(LightingAudioMeterColorBoostEditor),
            LightingEditorValue.AudioMeterDecayMilliseconds => nameof(LightingAudioMeterDecayMillisecondsEditor),
            LightingEditorValue.LogoMode => nameof(LogoLightingModeEditor),
            LightingEditorValue.Draft => nameof(LightingDraft),
            LightingEditorValue.Dirty => nameof(IsLightingApplyPending),
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        });
    }

    private void NotifyLightingParameterPresentation()
    {
        NotifyComputed(nameof(LightingShowsBrightness), LightingShowsBrightness);
        NotifyComputed(nameof(LightingShowsBreathingColorMode), LightingShowsBreathingColorMode);
        NotifyComputed(nameof(LightingShowsStarlightColorMode), LightingShowsStarlightColorMode);
        NotifyComputed(nameof(LightingShowsPrimaryColor), LightingShowsPrimaryColor);
        NotifyComputed(nameof(LightingShowsSecondaryColor), LightingShowsSecondaryColor);
        NotifyComputed(nameof(LightingPrimaryColorLabel), LightingPrimaryColorLabel);
        NotifyComputed(nameof(LightingPrimaryColorDescription), LightingPrimaryColorDescription);
        NotifyComputed(nameof(LightingShowsReactiveDuration), LightingShowsReactiveDuration);
        NotifyComputed(nameof(LightingShowsStarlightDuration), LightingShowsStarlightDuration);
        NotifyComputed(nameof(LightingShowsRandomColors), LightingShowsRandomColors);
        NotifyComputed(nameof(LightingShowsDirection), LightingShowsDirection);
        NotifyComputed(nameof(LightingDirectionOptions), LightingDirectionOptions);
        NotifyComputed(nameof(SelectedLightingDirectionOption), SelectedLightingDirectionOption);
        NotifyComputed(nameof(LightingShowsRotationDirection), LightingShowsRotationDirection);
        NotifyComputed(nameof(LightingShowsAmbientAwarenessRegion), LightingShowsAmbientAwarenessRegion);
        NotifyComputed(nameof(LightingShowsAudioMeterControls), LightingShowsAudioMeterControls);
    }

}

public sealed record LightingEffectOption(LightingEffect Effect, string Label);

public sealed record AmbientAwarenessRegionOption(
    AmbientAwarenessRegion Region,
    string Label);

public sealed record LogoLightingModeOption(LogoLightingMode? Mode, string Label);
