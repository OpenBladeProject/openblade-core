using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Threading;
using OpenSynapse.Contracts;

namespace OpenSynapse.Tray;

public enum ThemePreference
{
    FollowWindows,
    Light,
    Dark,
}

public interface IUiDispatcher
{
    Task InvokeAsync(Action action, CancellationToken token);
}

public sealed class MainViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly IServiceClient serviceClient;
    private readonly IUiDispatcher dispatcher;
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
    private string activePowerSourceLabel = "Power source unavailable";
    private string performanceSourceLabel = "Unavailable";
    private string fanSourceLabel = "Unavailable";
    private string lightingSourceLabel = "Unavailable";
    private IReadOnlyList<string> conflictNames = [];
    private IReadOnlyList<PerformancePreset> availablePerformancePresets = [];
    private IReadOnlyList<FanMode> availableFanModes = [];
    private IReadOnlyList<LightingEffect> availableLightingEffects = [];
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
    private ThemePreference theme = ThemePreference.FollowWindows;
    private int disposed;

    public MainViewModel(IServiceClient serviceClient, Dispatcher dispatcher)
        : this(serviceClient, new WpfUiDispatcher(dispatcher))
    {
    }

    public MainViewModel(IServiceClient serviceClient, IUiDispatcher dispatcher)
    {
        this.serviceClient = serviceClient ?? throw new ArgumentNullException(nameof(serviceClient));
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

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
                && latestStatus!.Capabilities!.LightingEffects.Contains(value.Effect));
        SetChargeLimitCommand = CreateCommand(
            SetChargeLimitAsync,
            parameter => parameter is int value && CanSetChargeLimit(value));
        ToggleFullChargeCommand = CreateCommand(
            ToggleFullChargeAsync,
            parameter => CanMutate && parameter is null or bool);
        SaveProfilesCommand = CreateCommand(
            SaveProfilesAsync,
            parameter => CanMutate && parameter is null or ProfileSet);
        commands =
        [
            (AsyncRelayCommand)SetPerformanceCommand,
            (AsyncRelayCommand)SetFanModeCommand,
            (AsyncRelayCommand)SetLightingCommand,
            (AsyncRelayCommand)SetChargeLimitCommand,
            (AsyncRelayCommand)ToggleFullChargeCommand,
            (AsyncRelayCommand)SaveProfilesCommand,
        ];
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
        private set => SetField(ref fullChargeOverride, value);
    }

    public DeviceCapabilities Capabilities
    {
        get => capabilities;
        private set => SetField(ref capabilities, value);
    }

    public TelemetrySnapshot? Telemetry
    {
        get => telemetry;
        private set => SetField(ref telemetry, value);
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
            await foreach (ServiceStatusSnapshot status in
                serviceClient.WatchStatusAsync(token))
            {
                await dispatcher.InvokeAsync(() => ApplyStatus(status), token);
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
        ConflictNames = status.Conflicts.ToArray();
        Profiles = status.Profiles;
        SelectedPerformance = status.DesiredState.Performance.Value;
        SelectedFanMode = status.DesiredState.Fans.Value;
        SelectedLighting = status.DesiredState.Lighting.Value;
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
        IsPerformanceVisible = AvailablePerformancePresets.Count > 0;
        IsFanControlVisible = AvailableFanModes.Count > 0;
        IsLightingVisible = AvailableLightingEffects.Count > 0;
        NotifyCommands();
    }

    private void MarkDisconnected()
    {
        IsConnected = false;
        IsReadOnly = true;
        CanControlHardware = false;
        ConnectionStatus = "Service disconnected";
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

    private Task SaveProfilesAsync(object? parameter, CancellationToken token) =>
        RunOperationAsync(
            cancellationToken => serviceClient.UpdateProfilesAsync(
                parameter as ProfileSet ?? Profiles,
                cancellationToken),
            token);

    private async Task RunOperationAsync(
        Func<CancellationToken, Task<ControlOperationResult>> operation,
        CancellationToken token)
    {
        try
        {
            ControlOperationResult result = await operation(token);
            await dispatcher.InvokeAsync(
                () => OperationStatusMessage = result.Message,
                token);
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
        }
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

    private sealed class WpfUiDispatcher(Dispatcher dispatcher) : IUiDispatcher
    {
        private readonly Dispatcher dispatcher = dispatcher
            ?? throw new ArgumentNullException(nameof(dispatcher));

        public Task InvokeAsync(Action action, CancellationToken token)
        {
            if (dispatcher.CheckAccess())
            {
                token.ThrowIfCancellationRequested();
                action();
                return Task.CompletedTask;
            }

            return dispatcher.InvokeAsync(
                action,
                DispatcherPriority.DataBind,
                token).Task;
        }
    }
}
