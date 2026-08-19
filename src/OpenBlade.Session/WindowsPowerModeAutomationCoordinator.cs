using OpenBlade.Contracts;

namespace OpenBlade.Session;

public sealed record WindowsPowerModeApplyResult(
    bool Succeeded,
    bool Changed,
    WindowsPowerMode? ConfiguredMode,
    string? Error);

public interface IWindowsPowerModeController
{
    Task<WindowsPowerModeApplyResult> ApplyAsync(
        PowerSource powerSource,
        WindowsPowerMode mode,
        CancellationToken cancellationToken = default);
}

public enum WindowsPowerModeAutomationObservation
{
    IgnoredUnavailable,
    LeftUnchanged,
    AlreadyObserved,
    ApplyScheduled,
}

public sealed record WindowsPowerModeAutomationResult(
    long Generation,
    PowerSource PowerSource,
    WindowsPowerMode RequestedMode,
    WindowsPowerModeApplyResult Result);

public sealed class WindowsPowerModeAutomationCoordinator : IAsyncDisposable
{
    private const int MaximumApplyAttempts = 2;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    private readonly object sync = new();
    private readonly IWindowsPowerModeController controller;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly Action<WindowsPowerModeAutomationResult>? resultObserver;
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private readonly List<Task> applyTasks = [];
    private CancellationTokenSource? applyCancellation;
    private Target? lastObservedTarget;
    private WindowsPowerModeAutomationResult? lastResult;
    private long generation;
    private int disposed;

    public WindowsPowerModeAutomationCoordinator(
        IWindowsPowerModeController controller,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Action<WindowsPowerModeAutomationResult>? resultObserver = null)
    {
        this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
        this.delay = delay ?? Task.Delay;
        this.resultObserver = resultObserver;
    }

    public WindowsPowerModeAutomationResult? LastResult
    {
        get
        {
            lock (sync)
            {
                return lastResult;
            }
        }
    }

    public WindowsPowerModeAutomationObservation Observe(ServiceStatusSnapshot status)
    {
        ArgumentNullException.ThrowIfNull(status);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(status.Profiles);

        lock (sync)
        {
            // FixedMaximum reports no firmware performance preset. Keep the
            // association stored, then reconcile when a preset is confirmed again.
            if (status.ControlAccess != SessionControlAccess.Active
                || !status.DeviceVerified
                || status.UnconfirmedWrites.Contains(DeviceSubsystem.Performance)
                || status.ActivePowerSource is not (
                    PowerSource.PluggedIn or PowerSource.Battery or PowerSource.UsbC)
                || status.ConfirmedState?.Performance is not { } performance)
            {
                MarkUnavailableCore();
                return WindowsPowerModeAutomationObservation.IgnoredUnavailable;
            }

            PowerSource source = status.ActivePowerSource;
            WindowsPowerMode? requested =
                status.Profiles.WindowsPowerModes.ForPerformance(performance);
            var target = new Target(ToSupply(source), performance, requested);
            if (lastObservedTarget == target)
            {
                return WindowsPowerModeAutomationObservation.AlreadyObserved;
            }

            lastObservedTarget = target;
            CancelActiveApply();
            if (requested is null)
            {
                return WindowsPowerModeAutomationObservation.LeftUnchanged;
            }

            long applyGeneration = checked(++generation);
            applyCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                lifetimeCancellation.Token);
            CancellationTokenSource ownedCancellation = applyCancellation;
            applyTasks.RemoveAll(task => task.IsCompleted);
            Task apply = RunApplyAsync(
                applyGeneration,
                source,
                requested.Value,
                ownedCancellation);
            applyTasks.Add(apply);
            return WindowsPowerModeAutomationObservation.ApplyScheduled;
        }
    }

    public void MarkUnavailable()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        lock (sync)
        {
            MarkUnavailableCore();
        }
    }

    public async Task WhenIdleAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task[] observed;
            lock (sync)
            {
                observed = applyTasks.ToArray();
            }

            await Task.WhenAll(observed).WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (sync)
            {
                if (applyTasks.Count == observed.Length
                    && applyTasks.All(task => task.IsCompleted))
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
        Task[] active;
        lock (sync)
        {
            applyCancellation?.Cancel();
            active = applyTasks.ToArray();
        }
        try
        {
            await Task.WhenAll(active).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        applyCancellation?.Dispose();
        lifetimeCancellation.Dispose();
    }

    private async Task RunApplyAsync(
        long applyGeneration,
        PowerSource source,
        WindowsPowerMode requested,
        CancellationTokenSource ownedCancellation)
    {
        try
        {
            WindowsPowerModeApplyResult? result = null;
            for (int attempt = 1; attempt <= MaximumApplyAttempts; attempt++)
            {
                result = await controller.ApplyAsync(
                        source,
                        requested,
                        ownedCancellation.Token)
                    .ConfigureAwait(false);
                if (result.Succeeded || attempt == MaximumApplyAttempts)
                {
                    break;
                }

                await delay(RetryDelay, ownedCancellation.Token).ConfigureAwait(false);
            }

            WindowsPowerModeAutomationResult? published = null;
            lock (sync)
            {
                if (generation == applyGeneration
                    && !ownedCancellation.IsCancellationRequested)
                {
                    published = new WindowsPowerModeAutomationResult(
                        applyGeneration,
                        source,
                        requested,
                        result!);
                    lastResult = published;
                }
            }
            if (published is not null)
            {
                try
                {
                    resultObserver?.Invoke(published);
                }
                catch
                {
                    // Diagnostics cannot make current-user automation fail.
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
                if (ReferenceEquals(applyCancellation, ownedCancellation))
                {
                    applyCancellation = null;
                }
            }
            ownedCancellation.Dispose();
        }
    }

    private void MarkUnavailableCore()
    {
        lastObservedTarget = null;
        CancelActiveApply();
    }

    private void CancelActiveApply()
    {
        applyCancellation?.Cancel();
        applyCancellation = null;
    }

    private static PowerSupply ToSupply(PowerSource source) => source switch
    {
        PowerSource.Battery => PowerSupply.Dc,
        PowerSource.PluggedIn or PowerSource.UsbC => PowerSupply.Ac,
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    private enum PowerSupply
    {
        Ac,
        Dc,
    }

    private sealed record Target(
        PowerSupply Supply,
        PerformancePreset Performance,
        WindowsPowerMode? Mode);
}
