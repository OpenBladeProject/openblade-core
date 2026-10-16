using System.ComponentModel;
using OpenBlade.Session;

namespace OpenBlade.Tray;

public sealed record ThxAudioEndpointOption(ThxAudioEndpoint Endpoint, string Label);

public interface IThxAudioAdapter
{
    Task<SessionAudioResponse> DiscoverAsync(CancellationToken cancellationToken);
    Task<SessionAudioResponse> ReadAsync(ThxAudioEndpoint endpoint, CancellationToken cancellationToken);
    Task<SessionAudioResponse> SetSpatialAsync(ThxAudioEndpoint endpoint, bool enabled, CancellationToken cancellationToken);
    Task<SessionAudioResponse> SetEqualizerAsync(ThxAudioEndpoint endpoint, bool enabled, CancellationToken cancellationToken);
    Task<SessionAudioResponse> SelectPresetAsync(ThxAudioEndpoint endpoint, string presetId, CancellationToken cancellationToken);
}

/// <summary>
/// User-requested audio operations only. Restoring the preference, discovering
/// components, switching endpoints, and disposing never change audio settings.
/// </summary>
public sealed class ThxAudioViewModel(IThxAudioAdapter adapter) : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly IThxAudioAdapter adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
    private readonly CancellationTokenSource lifetime = new();
    private ThxAudioState? state;
    private ThxAudioEndpoint endpoint;
    private ThxAudioAvailability availability = ThxAudioAvailability.Disabled;
    private string statusKey = "THX audio integration is off.";
    private bool enabled;
    private bool busy;
    private bool disposed;
    private Task pendingOperation = Task.CompletedTask;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsEnabled => enabled;
    public bool IsBusy => busy;
    public bool CanToggleIntegration => !busy && !disposed;
    public bool CanRead => enabled && !busy && !disposed;
    public bool CanEdit => CanRead && state is not null && availability == ThxAudioAvailability.Available;
    public bool SpatialEnabled => state?.SpatialEnabled ?? false;
    public bool EqualizerEnabled => state?.EqualizerEnabled ?? false;
    public ThxAudioEndpoint Endpoint => endpoint;
    public IReadOnlyList<ThxAudioEndpointOption> Endpoints { get; } =
    [
        new(ThxAudioEndpoint.Speakers, TrayText.Get("Speakers")),
        new(ThxAudioEndpoint.Headphones, TrayText.Get("Headphones")),
    ];
    public ThxAudioEndpointOption SelectedEndpoint => Endpoints.Single(option => option.Endpoint == endpoint);
    public IReadOnlyList<ThxAudioPreset> Presets => state?.Presets ?? [];
    public ThxAudioPreset? SelectedPreset => Presets.FirstOrDefault(preset => preset.Id == state?.SelectedPresetId);
    public string StatusMessage => TrayText.Get(statusKey);

    public void ApplyPreference(bool value)
    {
        if (disposed || busy)
        {
            return;
        }
        enabled = value;
        state = null;
        availability = value ? ThxAudioAvailability.ComponentsUnavailable : ThxAudioAvailability.Disabled;
        statusKey = value ? "Read the current THX settings to connect." : "THX audio integration is off.";
        NotifyAll();
    }

    public Task SetEnabledAsync(bool value)
    {
        if (!CanToggleIntegration || enabled == value)
        {
            return Task.CompletedTask;
        }
        ApplyPreference(value);
        // Enabling discovers installed files only. Native registration is
        // reserved for the separate, explicit Read settings action.
        return value ? StartAsync(adapter.DiscoverAsync, isMutation: false, isDiscovery: true) : Task.CompletedTask;
    }

    public void SelectEndpoint(ThxAudioEndpoint value)
    {
        if (!CanRead || !Enum.IsDefined(value) || endpoint == value)
        {
            return;
        }
        endpoint = value;
        state = null;
        statusKey = "Read the current THX settings to connect.";
        NotifyAll();
    }

    public Task RefreshAsync() => !CanRead ? Task.CompletedTask : StartAsync(async token =>
    {
        SessionAudioResponse discovery = await adapter.DiscoverAsync(token);
        return discovery.Status == ThxAudioOperationStatus.Succeeded
            && discovery.Availability == ThxAudioAvailability.Available
            ? await adapter.ReadAsync(endpoint, token)
            : discovery;
    }, isMutation: false);

    public Task SetSpatialAsync(bool value) => !CanEdit || SpatialEnabled == value
        ? Task.CompletedTask
        : StartAsync(token => adapter.SetSpatialAsync(endpoint, value, token), isMutation: true);

    public Task SetEqualizerAsync(bool value) => !CanEdit || EqualizerEnabled == value
        ? Task.CompletedTask
        : StartAsync(token => adapter.SetEqualizerAsync(endpoint, value, token), isMutation: true);

    public Task SelectPresetAsync(ThxAudioPreset preset) => !CanEdit
        || !Presets.Contains(preset) || SelectedPreset?.Id == preset.Id
        ? Task.CompletedTask
        : StartAsync(token => adapter.SelectPresetAsync(endpoint, preset.Id, token), isMutation: true);

    private Task StartAsync(
        Func<CancellationToken, Task<SessionAudioResponse>> action,
        bool isMutation,
        bool isDiscovery = false)
    {
        if (disposed || busy)
        {
            return Task.CompletedTask;
        }
        busy = true;
        NotifyAll();
        pendingOperation = RunAsync(action, isMutation, isDiscovery);
        return pendingOperation;
    }

    private async Task RunAsync(
        Func<CancellationToken, Task<SessionAudioResponse>> action,
        bool isMutation,
        bool isDiscovery)
    {
        try
        {
            // These awaits deliberately capture the UI context: confirmed
            // state and the finally block both update bound properties.
            SessionAudioResponse response = await action(lifetime.Token);
            if (disposed)
            {
                return;
            }
            availability = response.Availability;
            if (response.Status == ThxAudioOperationStatus.Succeeded
                && response.Availability == ThxAudioAvailability.Available)
            {
                state = response.State is { } confirmed && confirmed.Endpoint == endpoint
                    ? confirmed : null;
                statusKey = state is not null
                    ? "THX settings are up to date."
                    : isDiscovery ? "THX components found. Read the current settings to connect."
                    : "THX settings could not be confirmed. Read them again before making changes.";
            }
            else
            {
                state = null;
                statusKey = response.Status == ThxAudioOperationStatus.Indeterminate
                    ? "The audio change could not be confirmed. Read the current settings before trying again."
                    : response.Availability == ThxAudioAvailability.ControllerActive
                    ? "Close Synapse before connecting to THX audio."
                    : response.Availability == ThxAudioAvailability.UnsupportedModel
                    ? "THX audio controls are available only on RZ09-0581."
                    : response.Availability == ThxAudioAvailability.ComponentsUnavailable
                    ? "The supported THX components are unavailable."
                    : "THX settings could not be confirmed. Read them again before making changes.";
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException
            or OperationCanceledException or UnauthorizedAccessException)
        {
            state = null;
            statusKey = isMutation
                ? "The audio change could not be confirmed. Read the current settings before trying again."
                : "The Session Agent could not read THX settings.";
        }
        finally
        {
            busy = false;
            if (!disposed)
            {
                NotifyAll();
            }
        }
    }

    private void NotifyAll()
    {
        foreach (string property in new[]
        {
            nameof(IsEnabled), nameof(IsBusy), nameof(CanToggleIntegration), nameof(CanRead),
            nameof(CanEdit), nameof(SpatialEnabled), nameof(EqualizerEnabled), nameof(Endpoint),
            nameof(SelectedEndpoint), nameof(Presets), nameof(SelectedPreset), nameof(StatusMessage),
        })
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        await lifetime.CancelAsync();
        await pendingOperation;
        lifetime.Dispose();
        // Do not reset EQ, spatial processing, or the selected preset on exit.
    }
}
