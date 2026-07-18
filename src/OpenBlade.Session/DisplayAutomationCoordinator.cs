using OpenBlade.Contracts;

namespace OpenBlade.Session;

public enum DisplayAutomationObservation
{
    IgnoredUnconfirmed,
    BaselineEstablished,
    SameSource,
    TransitionScheduled,
}

public sealed record DisplayTransitionResult(
    long Generation,
    PowerSource PowerSource,
    SessionDisplayExecutionResult Result);

public sealed class DisplayAutomationCoordinator : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly DisplayPowerContextState powerContext;
    private readonly IDisplayProfileApplier applier;
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private CancellationTokenSource? transitionCancellation;
    private readonly List<Task> transitionTasks = [];
    private PowerSource? lastConfirmedPowerSource;
    private PowerSource? pendingTransitionPowerSource;
    private DisplayTransitionResult? lastTransitionResult;
    private long generation;
    private int disposed;

    public DisplayAutomationCoordinator(
        DisplayPowerContextState powerContext,
        IDisplayProfileApplier applier)
    {
        this.powerContext = powerContext ?? throw new ArgumentNullException(nameof(powerContext));
        this.applier = applier ?? throw new ArgumentNullException(nameof(applier));
    }

    public PowerSource? LastConfirmedPowerSource
    {
        get
        {
            lock (sync)
            {
                return lastConfirmedPowerSource;
            }
        }
    }

    public DisplayTransitionResult? LastTransitionResult
    {
        get
        {
            lock (sync)
            {
                return lastTransitionResult;
            }
        }
    }

    public DisplayAutomationObservation Observe(ServiceStatusSnapshot status)
    {
        ArgumentNullException.ThrowIfNull(status);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (!Enum.IsDefined(status.ActivePowerSource))
        {
            throw new ArgumentOutOfRangeException(nameof(status), "Power source is undefined.");
        }
        ArgumentNullException.ThrowIfNull(status.Profiles);

        lock (sync)
        {
            PowerSource source = status.ActivePowerSource;
            if (source == PowerSource.ExternalUnknown)
            {
                return MarkUnavailableCore();
            }
            if (source is not (
                PowerSource.PluggedIn or PowerSource.Battery or PowerSource.UsbC))
            {
                throw new ArgumentOutOfRangeException(nameof(status), "Power source is not confirmed.");
            }

            if (lastConfirmedPowerSource is null)
            {
                lastConfirmedPowerSource = source;
                powerContext.Update(source, NextGeneration());
                return DisplayAutomationObservation.BaselineEstablished;
            }

            if (lastConfirmedPowerSource == source)
            {
                DisplayPowerContext current = powerContext.Capture();
                if (current.PowerSource != source)
                {
                    long recoveredGeneration = NextGeneration();
                    powerContext.Update(source, recoveredGeneration);
                    if (pendingTransitionPowerSource == source)
                    {
                        ScheduleTransition(
                            status,
                            source,
                            recoveredGeneration);
                        return DisplayAutomationObservation.TransitionScheduled;
                    }
                }
                return DisplayAutomationObservation.SameSource;
            }

            lastConfirmedPowerSource = source;
            long transitionGeneration = NextGeneration();
            powerContext.Update(source, transitionGeneration);
            CancelActiveTransition();
            ScheduleTransition(status, source, transitionGeneration);
            return DisplayAutomationObservation.TransitionScheduled;
        }
    }

    /// <summary>
    /// Suspends writes while the privileged-service subscription is unavailable,
    /// without forgetting the last confirmed source baseline.
    /// </summary>
    public DisplayAutomationObservation MarkUnavailable()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        lock (sync)
        {
            return MarkUnavailableCore();
        }
    }

    public async Task WhenIdleAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task[] observed;
            lock (sync)
            {
                observed = transitionTasks.ToArray();
            }

            await Task.WhenAll(observed).WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (sync)
            {
                if (transitionTasks.Count == observed.Length
                    && transitionTasks.All(task => task.IsCompleted))
                {
                    return;
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        lifetimeCancellation.Cancel();
        CancellationTokenSource? transition;
        Task[] active;
        lock (sync)
        {
            transition = transitionCancellation;
            transition?.Cancel();
            active = transitionTasks.ToArray();
        }
        try
        {
            await Task.WhenAll(active).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        transition?.Dispose();
        lifetimeCancellation.Dispose();
    }

    private async Task RunTransitionAsync(
        long transitionGeneration,
        PowerSource source,
        DisplayProfileSettings settings,
        CancellationTokenSource ownedCancellation)
    {
        try
        {
            SessionDisplayExecutionResult result =
                await applier.ApplyProfileForTransitionAsync(
                    source,
                    transitionGeneration,
                    settings,
                    ownedCancellation.Token).ConfigureAwait(false);
            lock (sync)
            {
                if (generation == transitionGeneration
                    && !ownedCancellation.IsCancellationRequested)
                {
                    pendingTransitionPowerSource = null;
                    lastTransitionResult = new DisplayTransitionResult(
                        transitionGeneration,
                        source,
                        result);
                }
            }
        }
        catch (OperationCanceledException) when (ownedCancellation.IsCancellationRequested)
        {
        }
        finally
        {
            lock (sync)
            {
                if (ReferenceEquals(transitionCancellation, ownedCancellation))
                {
                    transitionCancellation = null;
                }
            }
            ownedCancellation.Dispose();
        }
    }

    private void CancelActiveTransition()
    {
        transitionCancellation?.Cancel();
        transitionCancellation = null;
    }

    private long NextGeneration() => checked(++generation);

    private DisplayAutomationObservation MarkUnavailableCore()
    {
        if (powerContext.Capture().PowerSource != PowerSource.ExternalUnknown)
        {
            powerContext.Update(PowerSource.ExternalUnknown, NextGeneration());
            CancelActiveTransition();
        }
        return DisplayAutomationObservation.IgnoredUnconfirmed;
    }

    private void ScheduleTransition(
        ServiceStatusSnapshot status,
        PowerSource source,
        long transitionGeneration)
    {
        CancelActiveTransition();
        pendingTransitionPowerSource = source;
        transitionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            lifetimeCancellation.Token);
        DisplayProfileSettings settings = SelectProfile(status.Profiles, source).Display
            ?? DisplayProfileSettings.Unconfigured();
        CancellationTokenSource ownedCancellation = transitionCancellation;
        transitionTasks.RemoveAll(task => task.IsCompleted);
        Task transition = RunTransitionAsync(
            transitionGeneration,
            source,
            settings,
            ownedCancellation);
        transitionTasks.Add(transition);
    }

    private static PowerProfile SelectProfile(ProfileSet profiles, PowerSource source) =>
        source switch
        {
            PowerSource.PluggedIn => profiles.PluggedIn,
            PowerSource.Battery => profiles.Battery,
            PowerSource.UsbC => profiles.UsbC,
            _ => throw new ArgumentOutOfRangeException(nameof(source)),
        };
}
