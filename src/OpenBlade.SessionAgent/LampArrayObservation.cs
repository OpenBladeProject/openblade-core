using System.Threading.Channels;
using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

internal static class Rz090528LampArrayGeometry
{
    internal const int LampCount = 85;
    internal const string Kind = "Keyboard";
    internal const string Sha256 =
        "C82E71D69DCAADDCB8530C0744B7B3240CC2E9C8FEF6CDAF1AB16B76F7A6A450";
}

internal sealed record LampArrayObservation(
    int EnumeratedDeviceCount,
    int ExactDeviceCount,
    string? Kind,
    int? LampCount,
    bool? IsConnected,
    bool? IsEnabled,
    bool? IsAvailable,
    bool? SupportsVirtualKeys,
    long? MinimumUpdateIntervalMicroseconds,
    string? GeometrySha256)
{
    internal bool MatchesApprovedGeometry =>
        ExactDeviceCount == 1
        && string.Equals(Kind, Rz090528LampArrayGeometry.Kind, StringComparison.Ordinal)
        && LampCount == Rz090528LampArrayGeometry.LampCount
        && SupportsVirtualKeys == true
        && string.Equals(
            GeometrySha256,
            Rz090528LampArrayGeometry.Sha256,
            StringComparison.Ordinal);

    internal string ToSanitizedSummary() => ExactDeviceCount switch
    {
        0 => "The exact RZ09-0528 LampArray is not currently present.",
        > 1 => "More than one exact RZ09-0528 LampArray is present; control remains disabled.",
        _ when !MatchesApprovedGeometry =>
            "The exact RZ09-0528 LampArray geometry does not match approved evidence; control remains disabled.",
        _ =>
            $"The exact 85-lamp keyboard was observed (connected={IsConnected}, " +
            $"enabled={IsEnabled}, available={IsAvailable}); this path is read-only.",
    };
}

internal interface ILampArrayObservationSource
{
    /// <summary>
    /// Performs one bounded, read-only observation. Implementations must never
    /// request LampArray priority or call a LampArray mutation API.
    /// </summary>
    Task<LampArrayObservation> ObserveAsync(CancellationToken cancellationToken);
}

internal sealed record LampArraySessionEligibility(DeviceFeatureAccess Access)
{
    internal static LampArraySessionEligibility? FromStatus(
        ServiceStatusSnapshot status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (!status.DeviceVerified
            || status.Capabilities?.LampArrayLightingAccess is not
                (DeviceFeatureAccess.ObservedOnly or DeviceFeatureAccess.ReadWrite)
            || RazerBladeModelCatalog.Find(status.Firmware?.ModelNumber)?.ModelNumber
                != RazerBladeModelCatalog.Rz090528ModelNumber
            || !TestedPlatformCatalog
                .Assess(status.Firmware?.ModelNumber, status.Firmware)
                .MatchesTestedConfiguration)
        {
            return null;
        }

        return new(status.Capabilities.LampArrayLightingAccess.Value);
    }
}

/// <summary>
/// Activates read-only Windows LampArray observation only while the service has
/// verified the exact tested model and explicitly advertises the OS-native
/// interface. This loop has no mutation dependency by design.
/// </summary>
internal sealed class ServiceLampArrayObservationLoop
{
    private static readonly TimeSpan ObservationInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FailureRetryInterval = TimeSpan.FromSeconds(5);

    private readonly object sync = new();
    private readonly IServiceStatusWatcher statusWatcher;
    private readonly ILampArrayObservationSource observationSource;
    private readonly Action<LampArrayObservation>? observationHandler;
    private readonly Action<Exception>? errorHandler;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly Channel<byte> eligibilityChanges = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });
    private LampArraySessionEligibility? eligibility;
    private LampArrayObservation? lastObservation;
    private long eligibilityGeneration;

    internal ServiceLampArrayObservationLoop(
        IServiceStatusWatcher statusWatcher,
        ILampArrayObservationSource observationSource,
        Action<LampArrayObservation>? observationHandler = null,
        Action<Exception>? errorHandler = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        this.statusWatcher = statusWatcher
            ?? throw new ArgumentNullException(nameof(statusWatcher));
        this.observationSource = observationSource
            ?? throw new ArgumentNullException(nameof(observationSource));
        this.observationHandler = observationHandler;
        this.errorHandler = errorHandler;
        this.delay = delay ?? Task.Delay;
    }

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        Task watch = WatchStatusAsync(linked.Token);
        Task observe = ObserveAsync(linked.Token);
        Task completed = await Task.WhenAny(watch, observe).ConfigureAwait(false);
        await linked.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(watch, observe).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }

        if (completed.IsFaulted)
        {
            await completed.ConfigureAwait(false);
        }
    }

    private async Task WatchStatusAsync(CancellationToken cancellationToken)
    {
        await foreach (ServiceWatchUpdate update in statusWatcher
            .WatchStatusAsync(cancellationToken)
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            LampArraySessionEligibility? next = update switch
            {
                ServiceWatchUpdate.Connected connected =>
                    LampArraySessionEligibility.FromStatus(connected.Status),
                ServiceWatchUpdate.Reconnecting => null,
                _ => null,
            };
            SetEligibility(next);
        }

        throw new IOException(
            "The LampArray observation status subscription ended unexpectedly.");
    }

    private async Task ObserveAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            DrainEligibilityChanges();
            (LampArraySessionEligibility? current, long generation) = ReadEligibility();
            if (current is null)
            {
                _ = await eligibilityChanges.Reader
                    .ReadAsync(cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            TimeSpan nextDelay = ObservationInterval;
            try
            {
                LampArrayObservation observation = await observationSource
                    .ObserveAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (ReadEligibility().Generation == generation)
                {
                    ReportObservation(observation);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                nextDelay = FailureRetryInterval;
                ReportFailure(exception);
            }

            await WaitForDelayOrEligibilityChangeAsync(
                    nextDelay,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private void SetEligibility(LampArraySessionEligibility? value)
    {
        lock (sync)
        {
            if (eligibility == value)
            {
                return;
            }

            eligibility = value;
            eligibilityGeneration++;
            lastObservation = null;
        }
        eligibilityChanges.Writer.TryWrite(0);
    }

    private (LampArraySessionEligibility? Eligibility, long Generation)
        ReadEligibility()
    {
        lock (sync)
        {
            return (eligibility, eligibilityGeneration);
        }
    }

    private void ReportObservation(LampArrayObservation observation)
    {
        bool changed;
        lock (sync)
        {
            changed = lastObservation != observation;
            lastObservation = observation;
        }
        if (!changed)
        {
            return;
        }

        try
        {
            observationHandler?.Invoke(observation);
        }
        catch
        {
        }
    }

    private void ReportFailure(Exception exception)
    {
        try
        {
            errorHandler?.Invoke(exception);
        }
        catch
        {
        }
    }

    private async Task WaitForDelayOrEligibilityChangeAsync(
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        Task intervalTask = delay(interval, waitCancellation.Token);
        Task<bool> changedTask = eligibilityChanges.Reader
            .WaitToReadAsync(waitCancellation.Token)
            .AsTask();
        _ = await Task.WhenAny(intervalTask, changedTask).ConfigureAwait(false);
        await waitCancellation.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(intervalTask, changedTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (waitCancellation.IsCancellationRequested)
        {
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private void DrainEligibilityChanges()
    {
        while (eligibilityChanges.Reader.TryRead(out _))
        {
        }
    }
}
