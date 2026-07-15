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
    private readonly CancellationTokenSource lifetime = new();
    private readonly object startSync = new();
    private readonly AsyncRelayCommand[] commands;
    private ServiceStatusSnapshot? latestStatus;
    private Task? watchTask;
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
    private IReadOnlyList<string> conflictNames = [];
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
    private bool fullChargeOverride;
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
    private int disposed;

    public MainViewModel(IServiceClient serviceClient, IUiDispatcher dispatcher)
        : this(serviceClient, dispatcher, new UnavailableDiagnosticArchiveSaver())
    {
    }

    public MainViewModel(
        IServiceClient serviceClient,
        IUiDispatcher dispatcher,
        IDiagnosticArchiveSaver diagnosticArchiveSaver)
    {
        this.serviceClient = serviceClient ?? throw new ArgumentNullException(nameof(serviceClient));
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        this.diagnosticArchiveSaver = diagnosticArchiveSaver
            ?? throw new ArgumentNullException(nameof(diagnosticArchiveSaver));

        SetPerformanceCommand = CreateCommand(
            SetPerformanceAsync,
            parameter => parameter is PerformancePreset value
                && CanMutate
                && latestStatus!.Capabilities!.PerformancePresets.Contains(value));
        SetFanModeCommand = CreateCommand(
            SetFanModeAsync,
            parameter => parameter is FanMode value
                && CanMutate
                && latestStatus!.Capabilities!.FanModes.Contains(value));
        SetLightingCommand = CreateCommand(
            SetLightingAsync,
            parameter => parameter is LightingSettings value
                && CanMutate
                && LightingSupported(value, latestStatus!.Capabilities!));
        SetChargeLimitCommand = CreateCommand(
            SetChargeLimitAsync,
            parameter => parameter is int value && CanSetChargeLimit(value));
        ToggleFullChargeCommand = CreateCommand(
            ToggleFullChargeAsync,
            CanSetFullChargeOverride);
        SaveProfilesCommand = CreateCommand(
            SaveProfilesAsync,
            CanSaveProfiles);
        ExportDiagnosticsCommand = CreateCommand(
            ExportDiagnosticsAsync,
            _ => Volatile.Read(ref disposed) == 0 && IsConnected);
        commands =
        [
            (AsyncRelayCommand)SetPerformanceCommand,
            (AsyncRelayCommand)SetFanModeCommand,
            (AsyncRelayCommand)SetLightingCommand,
            (AsyncRelayCommand)SetChargeLimitCommand,
            (AsyncRelayCommand)ToggleFullChargeCommand,
            (AsyncRelayCommand)SaveProfilesCommand,
            (AsyncRelayCommand)ExportDiagnosticsCommand,
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

    public IReadOnlyList<string> ConflictNames
    {
        get => conflictNames;
        private set => SetField(ref conflictNames, value);
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
        private set => SetField(ref chargeLimitPercent, value);
    }

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

    public string FullChargeActionLabel => FullChargeOverride
        ? "Cancel charge to 100%"
        : "Charge to 100% until unplugged";

    public DeviceCapabilities Capabilities
    {
        get => capabilities;
        private set => SetField(ref capabilities, value);
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
        private set => SetField(ref performanceOverride, value);
    }

    public ManualOverride<FanMode>? FanOverride
    {
        get => fanOverride;
        private set => SetField(ref fanOverride, value);
    }

    public ManualOverride<LightingSettings>? LightingOverride
    {
        get => lightingOverride;
        private set => SetField(ref lightingOverride, value);
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

    public ICommand SetPerformanceCommand { get; }
    public ICommand SetFanModeCommand { get; }
    public ICommand SetLightingCommand { get; }
    public ICommand SetChargeLimitCommand { get; }
    public ICommand ToggleFullChargeCommand { get; }
    public ICommand SaveProfilesCommand { get; }
    public ICommand ExportDiagnosticsCommand { get; }

    private bool CanMutate =>
        Volatile.Read(ref disposed) == 0
        && IsConnected
        && latestStatus is
        {
            DeviceVerified: true,
            WritesBlocked: false,
            Conflicts.Count: 0,
            Capabilities: not null,
        };

    private bool CanSetFullChargeOverride(object? parameter)
    {
        if (parameter is not null and not bool)
        {
            return false;
        }

        bool enable = parameter is bool requested
            ? requested
            : !FullChargeOverride;
        if (!enable)
        {
            return Volatile.Read(ref disposed) == 0 && IsConnected;
        }

        return CanMutate && Telemetry?.PowerSource == PowerSource.PluggedIn;
    }

    public Task StartAsync()
    {
        lock (startSync)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            watchTask ??= WatchStatusAsync(lifetime.Token);
        }
        return Task.CompletedTask;
    }

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
        latestStatus = status;
        IsConnected = true;
        bool controllable = status.DeviceVerified
            && !status.WritesBlocked
            && status.Conflicts.Count == 0;
        IsReadOnly = !controllable;
        CanControlHardware = controllable;
        ConnectionStatus = status.Message;
        ControlAccessStatus = FormatControlAccessStatus(status);
        ConflictNames = status.Conflicts.ToArray();
        Profiles = status.Profiles;
        if (!profileEditorsDirty)
        {
            LoadProfileEditors(status.Profiles);
        }
        SelectedPerformance = status.DesiredState.Performance.Value;
        SelectedFanMode = status.DesiredState.Fans.Value;
        SelectedLighting = status.DesiredState.Lighting.Value;
        if (!lightingEditorDirty)
        {
            LoadLightingEditor(SelectedLighting);
        }
        ChargeLimitPercent = status.DesiredState.Battery.ChargeLimitPercent;
        FullChargeOverride = status.FullChargeOverride;
        Telemetry = status.Telemetry;
        PerformanceOverride = status.PerformanceOverride;
        FanOverride = status.FanOverride;
        LightingOverride = status.LightingOverride;
        ActivePowerSourceLabel = status.Telemetry.PowerSource == PowerSource.Battery
            ? "Battery profile"
            : "Plugged-in profile";
        PerformanceSourceLabel = SourceLabel(status.DesiredState.Performance.Source);
        FanSourceLabel = SourceLabel(status.DesiredState.Fans.Source);
        LightingSourceLabel = SourceLabel(status.DesiredState.Lighting.Source);
        TelemetryStatus = FormatTelemetry(status.Telemetry);

        DeviceCapabilities capabilities = status.Capabilities
            ?? DeviceCapabilities.Unavailable();
        Capabilities = capabilities;
        AvailablePerformancePresets = capabilities.PerformancePresets.Order().ToArray();
        AvailableFanModes = capabilities.FanModes.Order().ToArray();
        AvailableLightingEffects = capabilities.LightingEffects.Order().ToArray();
        AvailableLightingSettings = AvailableLightingEffects
            .Select(CreateLightingChoice)
            .ToArray();
        IsPerformanceVisible = AvailablePerformancePresets.Count > 0;
        IsFanControlVisible = AvailableFanModes.Count > 0;
        IsLightingVisible = AvailableLightingEffects.Count > 0;
        NotifyCommands();
    }

    private void MarkDisconnected() => MarkDisconnected("Service disconnected");

    private void MarkDisconnected(string message)
    {
        IsConnected = false;
        IsReadOnly = true;
        CanControlHardware = false;
        ConnectionStatus = message;
        ControlAccessStatus = "Service disconnected · hardware controls disabled";
        NotifyCommands();
    }

    private bool CanSetChargeLimit(int value)
    {
        if (!CanMutate || latestStatus!.Capabilities is not { } capabilities)
        {
            return false;
        }

        return capabilities.ChargeLimitStep > 0
            && value >= capabilities.MinimumChargeLimit
            && value <= capabilities.MaximumChargeLimit
            && (value - capabilities.MinimumChargeLimit) % capabilities.ChargeLimitStep == 0;
    }

    private Task SetPerformanceAsync(object? parameter, CancellationToken token) =>
        RunOperationAsync(
            cancellationToken => serviceClient.SetPerformanceOverrideAsync(
                new ManualOverride<PerformancePreset>(
                    (PerformancePreset)parameter!,
                    OverrideLifetime.UntilPowerSourceChange),
                cancellationToken),
            token);

    private Task SetFanModeAsync(object? parameter, CancellationToken token) =>
        RunOperationAsync(
            cancellationToken => serviceClient.SetFanOverrideAsync(
                new ManualOverride<FanMode>(
                    (FanMode)parameter!,
                    OverrideLifetime.UntilPowerSourceChange),
                cancellationToken),
            token);

    private Task SetLightingAsync(object? parameter, CancellationToken token) =>
        RunOperationAsync(
            cancellationToken => serviceClient.SetLightingOverrideAsync(
                new ManualOverride<LightingSettings>(
                    (LightingSettings)parameter!,
                    OverrideLifetime.UntilPowerSourceChange),
                cancellationToken),
            token);

    private Task SetChargeLimitAsync(object? parameter, CancellationToken token) =>
        RunOperationAsync(
            cancellationToken => serviceClient.SetBatteryPolicyAsync(
                BatteryPolicy.Create((int)parameter!),
                cancellationToken),
            token);

    private Task ToggleFullChargeAsync(object? parameter, CancellationToken token) =>
        RunOperationAsync(
            cancellationToken => serviceClient.SetFullChargeOverrideAsync(
                parameter is bool enabled ? enabled : !FullChargeOverride,
                cancellationToken),
            token);

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

        return ProfileSupported(draft.PluggedIn, capabilities)
            && ProfileSupported(draft.Battery, capabilities)
            && capabilities.ChargeLimitStep > 0
            && draft.BatteryPolicy.ChargeLimitPercent >= capabilities.MinimumChargeLimit
            && draft.BatteryPolicy.ChargeLimitPercent <= capabilities.MaximumChargeLimit
            && (draft.BatteryPolicy.ChargeLimitPercent - capabilities.MinimumChargeLimit)
                % capabilities.ChargeLimitStep == 0
            && !draft.BatteryPolicy.TemporaryFullCharge;
    }

    private static bool ProfileSupported(
        PowerProfile profile,
        DeviceCapabilities capabilities) =>
        capabilities.PerformancePresets.Contains(profile.Performance)
        && capabilities.FanModes.Contains(profile.Fans)
        && LightingSupported(profile.Lighting, capabilities);

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
        CancellationToken token)
    {
        try
        {
            ControlOperationResult result = await operation(token);
            await dispatcher.InvokeAsync(
                () => OperationStatusMessage = result.Message,
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

    private void LoadProfileEditors(ProfileSet value)
    {
        SetField(ref pluggedInPerformanceEditor, value.PluggedIn.Performance, nameof(PluggedInPerformanceEditor));
        SetField(ref pluggedInFanModeEditor, value.PluggedIn.Fans, nameof(PluggedInFanModeEditor));
        SetField(ref pluggedInLightingEditor, value.PluggedIn.Lighting, nameof(PluggedInLightingEditor));
        SetField(ref batteryPerformanceEditor, value.Battery.Performance, nameof(BatteryPerformanceEditor));
        SetField(ref batteryFanModeEditor, value.Battery.Fans, nameof(BatteryFanModeEditor));
        SetField(ref batteryLightingEditor, value.Battery.Lighting, nameof(BatteryLightingEditor));
        SetField(ref profileChargeLimitEditor, value.BatteryPolicy.ChargeLimitPercent, nameof(ProfileChargeLimitEditor));
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

    private static string SourceLabel(SettingValueSource source) => source switch
    {
        SettingValueSource.PersistentOverride => "Persistent override",
        SettingValueSource.TemporaryOverride => "Until power change",
        SettingValueSource.PowerProfile => "Power profile",
        SettingValueSource.ConfirmedFirmwareState => "Confirmed firmware state",
        _ => "Unavailable",
    };

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

        if (status.Conflicts.Count > 0)
        {
            return "Read-only · Razer software conflict detected";
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

    private void SetEditorField<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
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
