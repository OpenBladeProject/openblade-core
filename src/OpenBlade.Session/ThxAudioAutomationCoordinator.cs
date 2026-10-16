using System.Text.Json;
using OpenBlade.Contracts;

namespace OpenBlade.Session;

public sealed record ThxForegroundObservation(string? ExecutableName, ThxAudioEndpoint? Endpoint = null)
{
    public static ThxForegroundObservation Unknown { get; } = new((string?)null);
}

public interface IThxForegroundSource
{
    IAsyncEnumerable<ThxForegroundObservation> WatchAsync(CancellationToken cancellationToken);
}

public sealed record ThxAudioAutomationTarget(
    ThxAudioProfileSettings Settings,
    ThxApplicationAudioRule? ApplicationRule,
    ThxAudioEndpoint Endpoint,
    string ExecutableName);

public interface IThxAudioAutomationController
{
    Task<ThxAudioOperationStatus> ApplyAsync(
        ThxAudioAutomationTarget target,
        CancellationToken cancellationToken);
}

/// <summary>
/// One in-flight application and one replaceable destination. Foreground changes
/// never queue a history of writes, and uncertain submissions are never retried.
/// </summary>
public sealed class ThxAudioAutomationCoordinator(IThxAudioAutomationController controller)
    : IAsyncDisposable
{
    private readonly IThxAudioAutomationController controller = controller
        ?? throw new ArgumentNullException(nameof(controller));
    private readonly object sync = new();
    private ThxAudioProfileSettings? settings;
    private string? executable;
    private ThxAudioEndpoint? endpoint;
    private long manualEpoch;
    private ThxAudioAutomationTarget? desired;
    private string? desiredKey;
    private string? attemptedKey;
    private CancellationTokenSource? activeCancellation;
    private Task pump = Task.CompletedTask;
    private bool running;
    private bool paused;
    private bool integrationEnabled;
    private bool disposed;

    public bool IsPaused
    {
        get { lock (sync) { return paused; } }
    }

    public void SetIntegrationEnabled(bool enabled)
    {
        lock (sync)
        {
            if (disposed)
            {
                return;
            }
            integrationEnabled = enabled;
            ReconcileCore();
        }
    }

    public void Observe(ServiceStatusSnapshot status)
    {
        ArgumentNullException.ThrowIfNull(status);
        lock (sync)
        {
            if (disposed)
            {
                return;
            }
            settings = status.DeviceVerified && !status.WritesBlocked
                && status.ControlAccess == SessionControlAccess.Active
                && status.PotentialControllers.Count == 0
                && string.IsNullOrEmpty(status.ControllerDetectionWarning)
                && IsAdmittedThxAudio(status)
                && status.ActivePowerSource is (PowerSource.PluggedIn or PowerSource.Battery or PowerSource.UsbC)
                ? status.Profiles.ForPowerSource(status.ActivePowerSource).ThxAudio
                : null;
            ReconcileCore();
        }
    }

    private static bool IsAdmittedThxAudio(ServiceStatusSnapshot status) =>
        status.Capabilities?.ThxAudio is { Access: DeviceFeatureAccess.ReadWrite } capability
        && (status.Firmware?.ModelNumber, capability.ProductId) is
            ("RZ09-0581", ThxAudioProductIds.Rznb12)
            or ("RZ09-0528", ThxAudioProductIds.Rznb11)
            or ("RZ09-0530", ThxAudioProductIds.Rznb11);

    public void ObserveForeground(ThxForegroundObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        lock (sync)
        {
            if (disposed)
            {
                return;
            }
            endpoint = observation.Endpoint;
            try
            {
                executable = observation.ExecutableName is { } name
                    ? ThxApplicationAudioRule.NormalizeExecutableName(name)
                    : null;
            }
            catch (ArgumentException)
            {
                executable = null;
            }
            ReconcileCore();
        }
    }

    public void MarkUnavailable()
    {
        lock (sync)
        {
            settings = null;
            ReconcileCore();
        }
    }

    public void PauseForManualOperation()
    {
        lock (sync)
        {
            manualEpoch++;
            paused = true;
            activeCancellation?.Cancel();
        }
    }

    // Wait for the completed native batch's caller too: its late Indeterminate
    // result must not resurrect a pause after the explicit read reconciled it.
    public async Task ResetAfterExplicitReadAsync()
    {
        Task preceding;
        long epoch;
        lock (sync)
        {
            preceding = pump;
            epoch = manualEpoch;
        }
        await preceding.ConfigureAwait(false);
        lock (sync)
        {
            if (disposed || epoch != manualEpoch)
            {
                return;
            }
            paused = false;
            // Reading is reconciliation, not consent to replay a destination.
            attemptedKey = desiredKey;
        }
    }

    private void ReconcileCore()
    {
        if (disposed || !integrationEnabled || settings is not { ApplicationRoutingEnabled: true }
            || !ThxAudioProfileSettings.IsWellFormed(settings) || executable is null
            || endpoint is null || !Enum.IsDefined(endpoint.Value))
        {
            desired = null;
            desiredKey = null;
            attemptedKey = null;
            activeCancellation?.Cancel();
            return;
        }
        var target = new ThxAudioAutomationTarget(settings,
            settings.ApplicationRules.FirstOrDefault(rule => string.Equals(
                rule.ExecutableName, executable, StringComparison.OrdinalIgnoreCase)), endpoint.Value, executable);
        string key = JsonSerializer.Serialize(target);
        if (key != desiredKey)
        {
            // Own a snapshot: status/profile collections must not change a
            // destination while its operation is awaiting native confirmation.
            desired = JsonSerializer.Deserialize<ThxAudioAutomationTarget>(key)!;
            desiredKey = key;
        }
        if (!paused && !running && desiredKey != attemptedKey)
        {
            running = true;
            pump = Task.Run(RunAsync);
        }
    }

    private async Task RunAsync()
    {
        while (true)
        {
            ThxAudioAutomationTarget target;
            CancellationTokenSource cancellation;
            lock (sync)
            {
                if (disposed || paused || desired is null || desiredKey == attemptedKey)
                {
                    running = false;
                    return;
                }
                target = desired;
                attemptedKey = desiredKey;
                cancellation = new CancellationTokenSource();
                activeCancellation = cancellation;
            }
            ThxAudioOperationStatus result;
            try
            {
                result = await controller.ApplyAsync(target, cancellation.Token).ConfigureAwait(false);
            }
            catch
            {
                // Controller entry may have submitted a write. Cancellation and
                // unexpected failures require explicit read-only reconciliation.
                result = ThxAudioOperationStatus.Indeterminate;
            }
            lock (sync)
            {
                activeCancellation = null;
                if (result == ThxAudioOperationStatus.Indeterminate)
                {
                    paused = true;
                }
            }
            cancellation.Dispose();
        }
    }

    public async Task WhenIdleAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task observed;
            lock (sync) { observed = pump; }
            await observed.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (sync)
            {
                if (ReferenceEquals(pump, observed))
                {
                    return;
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (sync)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            desired = null;
            activeCancellation?.Cancel();
        }
        await WhenIdleAsync().ConfigureAwait(false);
    }
}
