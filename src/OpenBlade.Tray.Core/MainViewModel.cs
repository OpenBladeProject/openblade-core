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

public sealed record ProfilePresentation(
    string PowerMode,
    string Fans,
    string LightingEffect,
    string LightingBrightness,
    string LightingColor,
    string DisplayRefreshRate,
    string DisplayHdr,
    string DisplayColorProfile);

public sealed class MainViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private const string UnavailableLabel = "Unavailable";

    private enum ProfileDraftGroup
    {
        Thermal,
        Lighting,
        Display,
    }

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
            .ToArray();

    private static readonly IReadOnlyList<LogoLightingModeOption> KnownLogoLightingModeOptions =
    [
        new(null, "Leave unchanged"),
        .. KnownLogoLightingModes.Select(
            mode => new LogoLightingModeOption(mode, mode.ToString())),
    ];

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
    private readonly string uiVersion;
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
    private IReadOnlyList<PerformancePreset> availablePerformancePresets = [];
    private IReadOnlyList<PerformancePresetOption> availablePluggedInPerformanceOptions = [];
    private IReadOnlyList<PerformancePresetOption> availableBatteryPerformanceOptions = [];
    private IReadOnlyList<PerformancePresetOption> availableUsbCPerformanceOptions = [];
    private IReadOnlyList<FanMode> availableFanModes = [];
    private IReadOnlyList<FanMode> availableUsbCFanModes = [];
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
    private FanMode selectedFanMode = FanMode.Automatic;
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
    private FanMode? activeProfileDraftFans;
    private LightingSettings? activeProfileDraftLighting;
    private DisplayProfileSettings? activeProfileDraftDisplay;
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
        TimeProvider? timeProvider = null,
        string? uiVersion = null)
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

        SetPerformanceCommand = CreateCommand(
            SetPerformanceAsync,
            CanSetPerformance);
        SetCustomPerformanceCommand = CreateCommand(
            SetCustomPerformanceAsync,
            CanSetCustomPerformance);
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
        DiscardProfileDraftCommand = CreateCommand(
            DiscardProfileDraftAsync,
            _ => HasActiveProfileDraft);
        SaveThermalProfileDraftCommand = CreateCommand(
            (_, token) => SaveActiveProfileDraftAsync(ProfileDraftGroup.Thermal, token),
            _ => CanSaveActiveProfileDraft(ProfileDraftGroup.Thermal));
        DiscardThermalProfileDraftCommand = CreateCommand(
            (_, token) => DiscardActiveProfileDraftAsync(ProfileDraftGroup.Thermal, token),
            _ => HasThermalProfileDraft);
        SaveLightingProfileDraftCommand = CreateCommand(
            (_, token) => SaveActiveProfileDraftAsync(ProfileDraftGroup.Lighting, token),
            _ => CanSaveActiveProfileDraft(ProfileDraftGroup.Lighting));
        DiscardLightingProfileDraftCommand = CreateCommand(
            (_, token) => DiscardActiveProfileDraftAsync(ProfileDraftGroup.Lighting, token),
            _ => HasLightingProfileDraft);
        SaveLightingPowerPolicyCommand = CreateCommand(
            SaveLightingPowerPolicyAsync,
            CanSaveLightingPowerPolicy);
        SaveDisplayProfileDraftCommand = CreateCommand(
            (_, token) => SaveActiveProfileDraftAsync(ProfileDraftGroup.Display, token),
            _ => CanSaveActiveProfileDraft(ProfileDraftGroup.Display));
        DiscardDisplayProfileDraftCommand = CreateCommand(
            (_, token) => DiscardActiveProfileDraftAsync(ProfileDraftGroup.Display, token),
            _ => HasDisplayProfileDraft);
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
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasOperationFeedback)));
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
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(HasTroubleshootingRecentError)));
        }
    }

    public bool HasTroubleshootingRecentError => !string.IsNullOrWhiteSpace(
        TroubleshootingRecentErrorStatus);

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
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(IsCustomPerformanceVisible)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(ActiveSavedDisplayProfile)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(SaveToActiveProfileLabel)));
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
        ? $"{SelectedFanMode} fans"
        : UnavailableLabel;

    public string ProfileStateLabel => !IsConnected
        ? "Service unavailable"
        : latestStatus?.DeviceVerified == false
            ? "Laptop not found"
            : ActivePowerSource == PowerSource.ExternalUnknown
                ? "Checking power source"
                : CanControlHardware || IsRetainedPowerSourceRecheck(latestStatus)
                    ? "Ready"
                    : "Controls unavailable";

    public bool IsReadinessReady => IsConnected
        && latestStatus?.DeviceVerified == true
        && (CanControlHardware || IsRetainedPowerSourceRecheck(latestStatus));

    public bool IsReadinessChecking => IsConnected
        && latestStatus?.DeviceVerified == true
        && ActivePowerSource == PowerSource.ExternalUnknown;

    public bool IsReadinessWarning => IsConnected
        && latestStatus?.DeviceVerified == true
        && ActivePowerSource != PowerSource.ExternalUnknown
        && !IsRetainedPowerSourceRecheck(latestStatus)
        && !CanControlHardware;

    public bool IsReadinessError => !IsConnected
        || latestStatus?.DeviceVerified != true;

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
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(ThermalProfileRelationshipStatus)));
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
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(ThermalProfileRelationshipStatus)));
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
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(LightingProfileRelationshipStatus)));
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

    public bool HasAttention => IsConnected
        && (HasControllerAdvisory
            || HasControllerDetectionWarning
            || HasExternalChanges);

    public string AttentionSummary
    {
        get
        {
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

    public string AttentionBadgeLabel => HasExternalChanges
        ? "Changed outside OpenBlade"
        : HasControllerDetectionWarning
            ? "Check status"
            : "Razer app active";

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

    public string AboutUiVersionStatus => uiVersion;

    public string AboutServiceVersionStatus =>
        !IsConnected || string.IsNullOrWhiteSpace(latestStatus?.ServiceVersion)
            ? "Unavailable"
            : latestStatus.ServiceVersion.Trim();

    public string AboutDetectedLaptopStatus => !IsConnected
        ? "Unavailable"
        : latestStatus?.DeviceVerified == true
            ? TargetLaptopLabel
            : "Not detected";

    public string AboutBiosVersionStatus => FirmwareValue(latestStatus?.Firmware?.BiosVersion);

    public string AboutEcVersionStatus => FirmwareValue(latestStatus?.Firmware?.EcVersion);

    public string AboutMcuVersionStatus => FirmwareValue(latestStatus?.Firmware?.McuVersion);

    public string AboutVbiosVersionStatus => FirmwareValue(latestStatus?.Firmware?.VbiosVersion);

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

    public bool CanControlLighting => CanControlHardware && HasLightingCapability;

    public bool IsLightingControlUnavailable => !CanControlLighting;

    public string LightingControlUnavailableMessage
    {
        get
        {
            if (!IsConnected)
            {
                return "OpenBlade can’t connect to its service, so lighting controls are unavailable.";
            }

            if (latestStatus?.DeviceVerified != true)
            {
                return "OpenBlade can’t find a supported laptop, so lighting controls are unavailable.";
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
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActivePerformanceLabel)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedPerformanceOption)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSilentSelected)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsBalancedSelected)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPerformanceSelected)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(IsCustomPerformanceVisible)));
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
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(AvailableCustomGpuLevels)));
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
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(AvailableCustomCpuLevels)));
        }
    }

    public IReadOnlyList<CustomPerformanceLevel> AvailableCustomCpuLevels =>
        SelectedCustomGpuLevel == CustomPerformanceLevel.High
            ? [CustomPerformanceLevel.Low, CustomPerformanceLevel.Medium]
            : Enum.GetValues<CustomPerformanceLevel>();

    public IReadOnlyList<CustomPerformanceLevel> AvailableCustomGpuLevels =>
        SelectedCustomCpuLevel == CustomPerformanceLevel.High
            ? [CustomPerformanceLevel.Low, CustomPerformanceLevel.Medium]
            : Enum.GetValues<CustomPerformanceLevel>();

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
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(HasOneShotLaptopControls)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(SupportsStartupAnimation)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(SupportsFunctionKeyPrimary)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(SupportsGamingMode)));
        }
    }

    public bool HasOneShotLaptopControls => Capabilities.OneShotLaptopControls.Count > 0;

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
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(StartupAnimationEnabled)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(StartupAnimationToggleIsOn)));
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
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(FunctionKeyPrimary)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(FunctionKeyPrimaryStateLabel)));
        }
    }

    public string FunctionKeyPrimaryStateLabel => FunctionKeyPrimary switch
    {
        FunctionKeyPrimaryMode.FunctionKeys => "Function keys",
        FunctionKeyPrimaryMode.MultimediaKeys => "Media keys",
        null => "Setâ€¦",
        _ => "Setâ€¦",
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
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(GamingModeEnabled)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(GamingModeToggleIsOn)));
        }
    }

    public bool GamingModeToggleIsOn => GamingModeEnabled is true;

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

    public bool HasUnsavedProfileChanges => profileEditorsDirty || HasActiveProfileDraft;

    public bool HasActiveProfileDraft => activeProfileDraftSource is not null;

    public bool HasThermalProfileDraft =>
        activeProfileDraftPerformance is not null
        || activeProfileDraftCustomPerformance is not null
        || activeProfileDraftFans is not null;

    public bool HasLightingProfileDraft => activeProfileDraftLighting is not null;

    public bool HasDisplayProfileDraft => activeProfileDraftDisplay is not null;

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
        CreateProfilePresentation(GetPresentedProfile(PowerSource.PluggedIn));

    public ProfilePresentation UsbCProfilePresentation =>
        CreateProfilePresentation(GetPresentedProfile(PowerSource.UsbC));

    public ProfilePresentation BatteryProfilePresentation =>
        CreateProfilePresentation(GetPresentedProfile(PowerSource.Battery));

    public ProfilePresentation SelectedProfilePresentation =>
        CreateProfilePresentation(GetPresentedProfile(SelectedProfileComparisonOption.Source));

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

    public LightingEffect LightingEffectEditor
    {
        get => lightingEffectEditor;
        set
        {
            LightingEffect previous = lightingEffectEditor;
            if (!Enum.IsDefined(value)
                || !SetLightingEditorField(ref lightingEffectEditor, value))
            {
                return;
            }

            SeedVisibleLightingDefaults(previous, value);

            NotifyLightingParameterPresentation();
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(SelectedLightingEffectOption)));
        }
    }

    public LightingEffectOption? SelectedLightingEffectOption
    {
        get => AvailableLightingEffectOptions.FirstOrDefault(
            option => option.Effect == LightingEffectEditor);
        set
        {
            if (value is not null)
            {
                LightingEffectEditor = value.Effect;
            }
        }
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
        set
        {
            if (SetLightingColorField(ref lightingRedEditor, value))
            {
                PropertyChanged?.Invoke(
                    this,
                    new PropertyChangedEventArgs(nameof(LightingPrimaryColorEditor)));
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
                PropertyChanged?.Invoke(
                    this,
                    new PropertyChangedEventArgs(nameof(LightingPrimaryColorEditor)));
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
                PropertyChanged?.Invoke(
                    this,
                    new PropertyChangedEventArgs(nameof(LightingPrimaryColorEditor)));
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
                PropertyChanged?.Invoke(
                    this,
                    new PropertyChangedEventArgs(nameof(LightingSecondaryColorEditor)));
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
                PropertyChanged?.Invoke(
                    this,
                    new PropertyChangedEventArgs(nameof(LightingSecondaryColorEditor)));
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
                PropertyChanged?.Invoke(
                    this,
                    new PropertyChangedEventArgs(nameof(LightingSecondaryColorEditor)));
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

    public LightingDirection LightingDirectionEditor
    {
        get => lightingDirectionEditor;
        set
        {
            if (!Enum.IsDefined(value))
            {
                return;
            }

            SetLightingEditorField(ref lightingDirectionEditor, value);
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
                PropertyChanged?.Invoke(
                    this,
                    new PropertyChangedEventArgs(nameof(SelectedLogoLightingModeOption)));
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

    public bool IsLogoLightingVisible => AvailableLogoLightingModeOptions.Count > 1;

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

    public LightingSettings LightingDraft => CreateLightingDraft();

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
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(StartupDescription)));
        }
    }

    public string StartupDescription => string.IsNullOrWhiteSpace(StartupStatusMessage)
        ? "Launch OpenBlade automatically when you sign in."
        : StartupStatusMessage;

    public ICommand SetPerformanceCommand { get; }
    public ICommand SetCustomPerformanceCommand { get; }
    public ICommand SetFanModeCommand { get; }
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

    private bool CanSetCustomPerformance(object? parameter) =>
        parameter is CustomPerformanceSettings value
        && CustomPerformanceSettings.IsWellFormed(value)
        && IsCustomPerformanceVisible
        && CanMutate
        && HasConfirmedPowerSource
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

    private bool CanApplyOneShotLaptopControl(object? parameter) =>
        parameter is ApplyOneShotLaptopControl value
        && value.IsWellFormed()
        && CanMutate
        && latestStatus!.Capabilities!.OneShotLaptopControls.Contains(value.Control);

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

        ServiceStatusSnapshot? previousStatus = latestStatus;
        latestAcceptedRevision = status.Revision;
        latestStatus = status;
        if (IsConnected
            && previousStatus is not null
            && StatusMetadataEqual(previousStatus, status))
        {
            LiveReadings = LiveReadingsEnabled ? status.LiveReadings : null;
            return;
        }

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
            ? $"Changed outside OpenBlade: {string.Join(", ", externalChanges.Select(SubsystemLabel))}. Your saved profiles are unchanged. Automatic profile updates for these controls are paused until you change them in OpenBlade."
            : string.Empty;
        Profiles = status.Profiles;
        if (!lightingPowerPolicyEditorDirty)
        {
            LoadLightingPowerPolicyEditor(status.Profiles.EffectiveLightingPowerPolicy);
        }
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
        StartupAnimationEnabled = status.StartupAnimationEnabled;
        FunctionKeyPrimary = status.FunctionKeyPrimary;
        GamingModeEnabled = status.GamingModeEnabled;
        SelectedPerformance = ResolvePresentedPerformance(status, confirmed, capabilities);
        SelectedFanMode = ResolvePresentedFanMode(status, confirmed, capabilities);
        PrepareLightingEditorForPowerSource(status.ActivePowerSource);
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
        CustomPerformanceSettings customPerformance =
            confirmed is { Performance: PerformancePreset.Custom, CustomPerformance: { } observed }
                ? observed
                : status.DesiredState.CustomPerformance
                    ?? status.Profiles.PluggedIn.CustomPerformance
                    ?? CustomPerformanceSettings.Default;
        SelectedCustomCpuLevel = customPerformance.Cpu;
        SelectedCustomGpuLevel = customPerformance.Gpu;
        TrayIconState = TrayIconStateResolver.Resolve(
            isConnected: true,
            status.DeviceVerified,
            status.ActivePowerSource,
            SelectedPerformance);
        LiveReadings = LiveReadingsEnabled ? status.LiveReadings : null;
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
        IReadOnlyList<LightingEffect> presentedLightingEffects =
            AvailableLightingEffects.Count > 0
                ? AvailableLightingEffects
                : KnownLightingEffects;
        AvailableLightingEffectOptions = presentedLightingEffects
            .Select(CreateLightingEffectOption)
            .ToArray();
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(SelectedLightingEffectOption)));
        AvailableLightingSettings = AvailableLightingEffects
            .Select(CreateLightingChoice)
            .ToArray();
        LogoLightingMode[] admittedLogoLightingModes = capabilities.LogoLightingModes
            .Where(Enum.IsDefined)
            .Order()
            .ToArray();
        IReadOnlyList<LogoLightingMode> presentedLogoLightingModes =
            AvailableLightingEffects.Count == 0 && admittedLogoLightingModes.Length == 0
                ? KnownLogoLightingModes
                : admittedLogoLightingModes;
        AvailableLogoLightingModeOptions =
        [
            new(null, "Leave unchanged"),
            .. presentedLogoLightingModes
                .Select(mode => new LogoLightingModeOption(mode, mode.ToString())),
        ];
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(SelectedLogoLightingModeOption)));
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(IsLogoLightingVisible)));
        IsPerformanceVisible = AvailablePerformancePresets.Count > 0;
        IsFanControlVisible = AvailableFanModes.Count > 0;
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(IsProfileControlVisible)));
        NotifyCommands();
        NotifyPresentationState();
    }

    private static LightingEffectOption CreateLightingEffectOption(LightingEffect effect) =>
        new(
            effect,
            effect == LightingEffect.Spectrum ? "Spectrum Cycling" : effect.ToString());

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

    private Task SetPerformanceAsync(object? parameter, CancellationToken token)
    {
        PerformancePreset requested = (PerformancePreset)parameter!;
        PowerSource sourceAtStart = ActivePowerSource;
        return RunOperationAsync(
            cancellationToken => serviceClient.SetPerformanceConfigurationOverrideAsync(
                new PerformanceOverride(
                    requested,
                    OverrideLifetime.UntilPowerSourceChange,
                    requested == PerformancePreset.Custom
                        ? GetActiveProfileCustomPerformance()
                        : null),
                cancellationToken),
            token,
            result =>
            {
                if (result.Status == ControlOperationStatus.Applied)
                {
                    MarkActiveProfileDraft(sourceAtStart, performance: requested);
                }
            });
    }

    private Task SetCustomPerformanceAsync(object? parameter, CancellationToken token)
    {
        CustomPerformanceSettings requested = (CustomPerformanceSettings)parameter!;
        PowerSource sourceAtStart = ActivePowerSource;
        return RunOperationAsync(
            cancellationToken => serviceClient.SetPerformanceConfigurationOverrideAsync(
                new PerformanceOverride(
                    PerformancePreset.Custom,
                    OverrideLifetime.UntilPowerSourceChange,
                    requested),
                cancellationToken),
            token,
            result =>
            {
                if (result.Status == ControlOperationStatus.Applied)
                {
                    MarkActiveProfileDraft(
                        sourceAtStart,
                        performance: PerformancePreset.Custom,
                        customPerformance: requested);
                }
            });
    }

    private Task SetFanModeAsync(object? parameter, CancellationToken token)
    {
        FanMode requested = (FanMode)parameter!;
        PowerSource sourceAtStart = ActivePowerSource;
        return RunOperationAsync(
            cancellationToken => serviceClient.SetFanOverrideAsync(
                new ManualOverride<FanMode>(
                    requested,
                    OverrideLifetime.UntilPowerSourceChange),
                cancellationToken),
            token,
            result =>
            {
                if (result.Status == ControlOperationStatus.Applied)
                {
                    MarkActiveProfileDraft(sourceAtStart, fans: requested);
                }
            });
    }

    private async Task SetLightingAsync(object? parameter, CancellationToken token)
    {
        LightingSettings requested = (LightingSettings)parameter!;
        PowerSource sourceAtStart = ActivePowerSource;
        long revisionAtStart = latestAcceptedRevision;
        ControlOperationStatus? resultStatus = null;
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
                if (result.Status == ControlOperationStatus.Applied)
                {
                    MarkActiveProfileDraft(sourceAtStart, lighting: requested);
                }
            });

        if (resultStatus == ControlOperationStatus.Applied)
        {
            await dispatcher.InvokeAsync(
                () =>
                {
                    lightingEditorDirty = false;
                    if (latestStatus is not null
                        && latestAcceptedRevision > revisionAtStart)
                    {
                        LoadLightingEditor(
                            latestStatus.ConfirmedState?.Lighting
                            ?? latestStatus.DesiredState.Lighting.Value);
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

    private Task ApplyOneShotLaptopControlAsync(
        object? parameter,
        CancellationToken token) => RunOperationAsync(
            cancellationToken => serviceClient.ApplyOneShotLaptopControlAsync(
                (ApplyOneShotLaptopControl)parameter!,
                cancellationToken),
            token);

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

        ProfileSet draft = Profiles with { LightingPowerPolicy = policy };
        bool accepted = await RunOperationAsync(
            cancellationToken => serviceClient.UpdateProfilesAsync(draft, cancellationToken),
            token).ConfigureAwait(false);
        if (!accepted)
        {
            return;
        }

        await dispatcher.InvokeAsync(
            () =>
            {
                Profiles = draft;
                LoadLightingPowerPolicyEditor(policy);
            },
            token).ConfigureAwait(false);
    }

    private bool CanSaveLightingPowerPolicy(object? parameter) =>
        Volatile.Read(ref disposed) == 0
        && IsConnected
        && CanControlLighting
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
        && HasActiveProfileDraftFor(group)
        && activeProfileDraftSource == ActivePowerSource
        && TryGetSupportedProfileDraft(CreateProfileSetWithActiveDraft(group), out _);

    private async Task SaveActiveProfileDraftAsync(
        ProfileDraftGroup group,
        CancellationToken token)
    {
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
        if (!HasActiveProfileDraftFor(group)
            || activeProfileDraftSource is not { } source)
        {
            return;
        }

        if (ActivePowerSource != source)
        {
            await ReportDisplayRestoreSourceChangedAsync(token);
            return;
        }

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
        else
        {
            restored = await ClearActiveProfileHardwareOverridesAsync(token, group);
        }

        if (restored && ActivePowerSource == source)
        {
            ClearActiveProfileDraft(group);
        }
        else if (ActivePowerSource != source)
        {
            await ReportDisplayRestoreSourceChangedAsync(token);
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
            await ReportDisplayRestoreSourceChangedAsync(token);
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
        if (controlsRestored && displayRestored && ActivePowerSource == source)
        {
            ClearActiveProfileDraft();
        }
        else if (ActivePowerSource != source)
        {
            await ReportDisplayRestoreSourceChangedAsync(token);
        }
    }

    private async Task<bool> ClearActiveProfileHardwareOverridesAsync(
        CancellationToken token,
        ProfileDraftGroup? group = null)
    {
        bool restored = true;
        if ((group is null or ProfileDraftGroup.Thermal)
            && activeProfileDraftPerformance is not null)
        {
            restored &= await RunOperationAsync(
                cancellationToken => serviceClient.SetPerformanceOverrideAsync(
                    null,
                    cancellationToken),
                token);
        }
        if ((group is null or ProfileDraftGroup.Thermal)
            && activeProfileDraftFans is not null)
        {
            restored &= await RunOperationAsync(
                cancellationToken => serviceClient.SetFanOverrideAsync(
                    null,
                    cancellationToken),
                token);
        }
        if ((group is null or ProfileDraftGroup.Lighting)
            && activeProfileDraftLighting is not null)
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
                    await ReportDisplayRestoreSourceChangedAsync(token);
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

    private Task ReportDisplayRestoreSourceChangedAsync(CancellationToken token) =>
        dispatcher.InvokeAsync(
            () => OperationStatusMessage =
                "Could not restore the saved display profile because the power source changed.",
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
        SetField(ref lightingEffectEditor, value.Effect, nameof(LightingEffectEditor));
        SetField(ref lightingBrightnessEditor, value.Brightness, nameof(LightingBrightnessEditor));
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
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(LightingPrimaryColorEditor)));
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(LightingSecondaryColorEditor)));
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
        SetField(
            ref logoLightingModeEditor,
            value.LogoMode,
            nameof(LogoLightingModeEditor));
        NotifyLightingParameterPresentation();
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(SelectedLightingEffectOption)));
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(SelectedLogoLightingModeOption)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LightingDraft)));
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
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(HasUnsavedLightingPowerPolicy)));
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(LightingPowerPolicyDraft)));
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
            lightingEditorDirty = false;
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
            Fans = activeProfileDraftFans ?? profile.Fans,
            Lighting = activeProfileDraftLighting ?? profile.Lighting,
            Display = activeProfileDraftDisplay ?? profile.Display,
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
                Fans = activeProfileDraftFans ?? saved.Fans,
            },
            ProfileDraftGroup.Lighting => saved with
            {
                Lighting = activeProfileDraftLighting ?? saved.Lighting,
            },
            ProfileDraftGroup.Display => saved with
            {
                Display = activeProfileDraftDisplay ?? saved.Display,
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
        FanMode? fans = null,
        LightingSettings? lighting = null,
        DisplayProfileSettings? display = null,
        DisplayProfileSettings? previousDisplay = null)
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
            activeProfileDraftFans = null;
            activeProfileDraftLighting = null;
            activeProfileDraftDisplay = null;
            activeProfileOriginalDisplay = previousDisplay;
        }
        else if (display is not null && activeProfileDraftDisplay is null)
        {
            activeProfileOriginalDisplay = previousDisplay;
        }

        activeProfileDraftPerformance = performance ?? activeProfileDraftPerformance;
        activeProfileDraftCustomPerformance = customPerformance
            ?? activeProfileDraftCustomPerformance;
        activeProfileDraftFans = fans ?? activeProfileDraftFans;
        activeProfileDraftLighting = lighting ?? activeProfileDraftLighting;
        activeProfileDraftDisplay = display ?? activeProfileDraftDisplay;

        PowerProfile saved = GetProfile(source);
        if (activeProfileDraftPerformance == saved.Performance)
        {
            activeProfileDraftPerformance = null;
        }
        if (activeProfileDraftCustomPerformance == saved.CustomPerformance)
        {
            activeProfileDraftCustomPerformance = null;
        }
        if (activeProfileDraftFans == saved.Fans)
        {
            activeProfileDraftFans = null;
        }
        if (activeProfileDraftLighting == saved.Lighting)
        {
            activeProfileDraftLighting = null;
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
        if (activeProfileDraftPerformance is null
            && activeProfileDraftCustomPerformance is null
            && activeProfileDraftFans is null
            && activeProfileDraftLighting is null
            && activeProfileDraftDisplay is null)
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
        activeProfileDraftFans = null;
        activeProfileDraftLighting = null;
        activeProfileDraftDisplay = null;
        activeProfileOriginalDisplay = null;
        NotifyActiveProfileDraft();
    }

    private void ClearActiveProfileDraft(ProfileDraftGroup group)
    {
        switch (group)
        {
            case ProfileDraftGroup.Thermal:
                activeProfileDraftPerformance = null;
                activeProfileDraftCustomPerformance = null;
                activeProfileDraftFans = null;
                break;
            case ProfileDraftGroup.Lighting:
                activeProfileDraftLighting = null;
                break;
            case ProfileDraftGroup.Display:
                activeProfileDraftDisplay = null;
                activeProfileOriginalDisplay = null;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(group));
        }

        if (!HasThermalProfileDraft
            && !HasLightingProfileDraft
            && !HasDisplayProfileDraft)
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
        _ => false,
    };

    private void NotifyActiveProfileDraft()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasActiveProfileDraft)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasThermalProfileDraft)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasLightingProfileDraft)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasDisplayProfileDraft)));
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(ThermalProfileRelationshipStatus)));
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(LightingProfileRelationshipStatus)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPluggedInProfileDraft)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsBatteryProfileDraft)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsUsbCProfileDraft)));
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(HasUnsavedProfileChanges)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActiveProfileDraftName)));
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(SavedActiveProfileSummary)));
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(PendingActiveProfileSummary)));
        NotifyProfileSummaries();
        NotifyCommands();
    }

    private void NotifyProfileSummaries()
    {
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(PluggedInProfileThermalSummary)));
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(PluggedInProfileLightingSummary)));
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(BatteryProfileThermalSummary)));
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(BatteryProfileLightingSummary)));
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(UsbCProfileThermalSummary)));
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(UsbCProfileLightingSummary)));
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(PluggedInProfileDisplaySummary)));
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(BatteryProfileDisplaySummary)));
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(UsbCProfileDisplaySummary)));
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(ActiveSavedDisplayProfile)));
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(SavedActiveProfileSummary)));
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(PendingActiveProfileSummary)));
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(PluggedInProfilePresentation)));
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(UsbCProfilePresentation)));
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(BatteryProfilePresentation)));
        NotifySelectedProfilePresentation();
    }

    private void NotifySelectedProfilePresentation()
    {
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(SelectedProfilePresentation)));
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(IsSelectedProfileActive)));
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(IsSelectedProfileDraft)));
    }

    private static string FormatThermalProfile(PowerProfile profile) =>
        $"{FormatPerformanceProfile(profile)} | {profile.Fans} fans";

    private static string FormatPowerProfile(PowerProfile profile) =>
        $"Power mode: {FormatPerformanceProfile(profile)}{Environment.NewLine}"
        + $"Fans: {profile.Fans}{Environment.NewLine}"
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

        string effect = lighting.Effect == LightingEffect.Spectrum
            ? "Spectrum Cycling"
            : lighting.Effect.ToString();
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
            _ when lighting.Effect == LightingEffect.Static => FormatColor(lighting.Color),
            _ => string.Empty,
        };
        return string.IsNullOrEmpty(details)
            ? $"{effect} | {lighting.Brightness}%"
            : $"{effect} | {lighting.Brightness}% | {details}";
    }

    private static string FormatColor(RgbColor color) =>
        $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}";

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
        return settings.Count == 0 ? "Use current settings" : string.Join(" | ", settings);
    }

    private static ProfilePresentation CreateProfilePresentation(PowerProfile profile)
    {
        DisplayProfileSettings display = profile.Display ?? DisplayProfileSettings.Unconfigured();
        return new ProfilePresentation(
            FormatPerformanceProfile(profile),
            profile.Fans switch
            {
                FanMode.Automatic => "Automatic",
                FanMode.Maximum => "Maximum",
                _ => profile.Fans.ToString(),
            },
            FormatLightingEffect(profile.Lighting),
            profile.Lighting.Effect == LightingEffect.Off
                ? "Not applicable"
                : $"{profile.Lighting.Brightness}%",
            FormatLightingColor(profile.Lighting),
            FormatDisplayRefreshRate(display.RefreshRateKey),
            display.HdrEnabled switch
            {
                true => "On",
                false => "Off",
                null => "Use current",
            },
            FormatDisplayColorProfile(display.ColorProfileKey));
    }

    private static string FormatPerformanceProfile(PowerProfile profile) =>
        profile is
        {
            Performance: PerformancePreset.Custom,
            CustomPerformance: { } custom,
        }
            ? $"Custom, CPU {custom.Cpu}, GPU {custom.Gpu}"
            : PerformancePresetPresentation.Label(profile.Performance);

    private static string FormatLightingEffect(LightingSettings lighting) => lighting switch
    {
        { Effect: LightingEffect.Off } => "Off",
        { Effect: LightingEffect.Spectrum } => "Spectrum Cycling",
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
        } => $"Tidal, {tidal.Direction.ToString().ToLowerInvariant()}",
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
        { Effect: LightingEffect.Wave or LightingEffect.Wheel } => "Automatic",
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
            return "Use current";
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
            return "Use current";
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
        byte brightness = (byte)LightingBrightnessEditor;
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

        if (previous == LightingEffect.Off && LightingBrightnessEditor == 0)
        {
            SetLightingEditorField(
                ref lightingBrightnessEditor,
                (int)LightingSettingsFactory.DefaultVisibleBrightness);
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
            SetLightingColorEditor(new RgbColor(0, 255, 0), secondary: false);
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
            ? LightingSettingsFactory.DefaultVisibleBrightness
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
            _ => new LightingSettings(effect, color, brightness),
        };
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
            ? "Changed outside OpenBlade"
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

    private static bool StatusMetadataEqual(
        ServiceStatusSnapshot left,
        ServiceStatusSnapshot right) =>
        left.DeviceVerified == right.DeviceVerified
        && left.WritesBlocked == right.WritesBlocked
        && left.PotentialControllers.SequenceEqual(
            right.PotentialControllers,
            StringComparer.Ordinal)
        && string.Equals(
            left.ControllerDetectionWarning,
            right.ControllerDetectionWarning,
            StringComparison.Ordinal)
        && left.ExternalChanges.SequenceEqual(right.ExternalChanges)
        && string.Equals(left.Message, right.Message, StringComparison.Ordinal)
        && CapabilitiesEqual(left.Capabilities, right.Capabilities)
        && left.ConfirmedState == right.ConfirmedState
        && left.Profiles == right.Profiles
        && left.DesiredState == right.DesiredState
        && left.ActivePowerSource == right.ActivePowerSource
        && left.PerformanceOverride == right.PerformanceOverride
        && left.FanOverride == right.FanOverride
        && left.LightingOverride == right.LightingOverride
        && left.FullChargeOverride == right.FullChargeOverride
        && left.StartupAnimationEnabled == right.StartupAnimationEnabled
        && left.FunctionKeyPrimary == right.FunctionKeyPrimary
        && left.GamingModeEnabled == right.GamingModeEnabled
        && (left.LightingSuppressions ?? []).SequenceEqual(
            right.LightingSuppressions ?? [])
        && left.Firmware == right.Firmware
        && string.Equals(left.ServiceVersion, right.ServiceVersion, StringComparison.Ordinal);

    private static bool CapabilitiesEqual(
        DeviceCapabilities? left,
        DeviceCapabilities? right) =>
        ReferenceEquals(left, right)
        || (left is not null && right is not null
            && left.PerformancePresets.SetEquals(right.PerformancePresets)
            && left.FanModes.SetEquals(right.FanModes)
            && left.LightingEffects.SetEquals(right.LightingEffects)
            && left.LogoLightingModes.SetEquals(right.LogoLightingModes)
            && left.OneShotLaptopControls.SetEquals(right.OneShotLaptopControls)
            && left.ChargeLimits.SetEquals(right.ChargeLimits)
            && left.SupportsBatteryProtectionOff == right.SupportsBatteryProtectionOff
            && left.SupportsTemporaryFullCharge == right.SupportsTemporaryFullCharge);

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

    private static string FormatOperationResult(ControlOperationResult result) => result.Status switch
    {
        ControlOperationStatus.Applied or ControlOperationStatus.Stored => string.Empty,
        ControlOperationStatus.Blocked => "OpenBlade saved the change, but the laptop has not applied it.",
        _ => "OpenBlade couldn’t complete that action. Check Troubleshooting for details.",
    };

    private void NotifyPresentationState()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsServiceUnavailable)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BatteryPowerFlowLabel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Fan1RpmLabel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Fan2RpmLabel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LiveReadingsRefreshToolTip)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActivePerformanceLabel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActiveFanModeLabel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProfileStateLabel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsReadinessReady)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsReadinessChecking)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsReadinessWarning)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsReadinessError)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasLightingCapability)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanControlLighting)));
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(IsLightingControlUnavailable)));
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(LightingControlUnavailableMessage)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasActiveChanges)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActiveChangesText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasAttention)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AttentionSummary)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AttentionBadgeLabel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AttentionDetails)));
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(TroubleshootingServiceStatus)));
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
            new PropertyChangedEventArgs(nameof(AboutServiceVersionStatus)));
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(AboutDetectedLaptopStatus)));
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(AboutBiosVersionStatus)));
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(AboutEcVersionStatus)));
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(AboutMcuVersionStatus)));
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(AboutVbiosVersionStatus)));
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

        bool sourceAllowsObserved = status.ActivePowerSource != PowerSource.UsbC
            || observed == FanMode.Automatic;
        return capabilities.FanModes.Contains(observed) && sourceAllowsObserved
            ? observed
            : desired;
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
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(HasUnsavedLightingPowerPolicy)));
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(LightingPowerPolicyDraft)));
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

    private bool SetLightingColorField(ref int field, int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 255);
        return SetLightingEditorField(ref field, value);
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

        lightingEditorDirty = true;
        string prefix = secondary ? "LightingSecondary" : "Lighting";
        SetField(ref red, (int)value.Red, $"{prefix}RedEditor");
        SetField(ref green, (int)value.Green, $"{prefix}GreenEditor");
        SetField(ref blue, (int)value.Blue, $"{prefix}BlueEditor");
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(
                secondary
                    ? nameof(LightingSecondaryColorEditor)
                    : nameof(LightingPrimaryColorEditor)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LightingDraft)));
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

        lightingEditorDirty = true;
        SetField(ref field, value, propertyName);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LightingDraft)));
        NotifyCommands();
        return true;
    }

    private void NotifyLightingParameterPresentation()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LightingShowsBrightness)));
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(LightingShowsBreathingColorMode)));
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(LightingShowsStarlightColorMode)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LightingShowsPrimaryColor)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LightingShowsSecondaryColor)));
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(LightingShowsReactiveDuration)));
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(LightingShowsStarlightDuration)));
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(LightingShowsRandomColors)));
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(LightingShowsDirection)));
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(LightingShowsRotationDirection)));
    }

}

public sealed record LightingEffectOption(LightingEffect Effect, string Label);

public sealed record LogoLightingModeOption(LogoLightingMode? Mode, string Label);
