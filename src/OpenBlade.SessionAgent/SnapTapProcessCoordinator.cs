using System.Diagnostics;
using System.Threading.Channels;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

public interface ISnapTapPreferenceSource
{
    bool SnapTapEnabled { get; }
    event Action<bool>? SnapTapEnabledChanged;
}

public interface ISnapTapChildProcess : IDisposable
{
    int Id { get; }
    bool HasExited { get; }
    int ExitCode { get; }
    Task WaitForExitAsync(CancellationToken cancellationToken);
    void RequestStop();
}

/// <summary>Owns one helper, with explicit preference transitions as the only restart authority.</summary>
public sealed class SnapTapProcessCoordinator(
    ISnapTapPreferenceSource preferenceSource,
    Func<ISnapTapChildProcess> startChild,
    Action<SnapTapRuntimeStatus> publishStatus)
{
    private readonly Channel<bool> changes = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private int offGeneration;
    private int enabled;
    private int started;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref started, 1) != 0)
        {
            throw new InvalidOperationException("The Snap Tap coordinator can only run once.");
        }

        ISnapTapChildProcess? child = null;
        Task? exited = null;
        bool latched = false;
        bool stopping = false;
        bool supervisionFailed = false;
        int observedOff = 0;
        preferenceSource.SnapTapEnabledChanged += OnPreferenceChanged;
        OnPreferenceChanged(preferenceSource.SnapTapEnabled);
        Task<bool>? changed = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int currentOff = Volatile.Read(ref offGeneration);
                bool offObserved = currentOff != observedOff;
                observedOff = currentOff;
                if (offObserved)
                {
                    latched = false;
                }

                bool requested = Volatile.Read(ref enabled) != 0;
                if (child is not null && (offObserved || !requested) && !stopping)
                {
                    stopping = true;
                    if (!await StopAsync(child, exited!).ConfigureAwait(false))
                    {
                        latched |= !Publish(SnapTapRuntimeState.Failed);
                    }
                }

                if (child is not null && exited!.IsCompleted)
                {
                    if (!child.HasExited)
                    {
                        throw new InvalidOperationException("The helper exit observer ended before the process exited.");
                    }
                    bool successful = await ObserveExitAsync(child, exited).ConfigureAwait(false);
                    bool unavailable = child.ExitCode == 3;
                    bool recoveryRequired = child.ExitCode == 4;
                    bool driverMissing = child.ExitCode == 5;
                    bool unsupportedModel = child.ExitCode == 6;
                    bool manualDriverSetup = child.ExitCode == 7;
                    DisposeChild(child);
                    child = null;
                    exited = null;
                    if (!successful || !stopping)
                    {
                        latched = true;
                        latched |= !Publish(recoveryRequired ? SnapTapRuntimeState.RecoveryRequired
                            : unsupportedModel ? SnapTapRuntimeState.UnsupportedModel
                            : manualDriverSetup ? SnapTapRuntimeState.ManualDriverSetupRequired
                            : driverMissing ? SnapTapRuntimeState.DriverMissing
                            : unavailable ? SnapTapRuntimeState.Unavailable : SnapTapRuntimeState.Failed);
                    }
                    else
                    {
                        latched |= !Publish(SnapTapRuntimeState.Off);
                    }
                    stopping = false;
                }

                if (child is null && requested && !latched)
                {
                    // The child publishes its own Active/Waiting/Paused state after launch.
                    if (!Publish(SnapTapRuntimeState.Starting, Environment.ProcessId))
                    {
                        latched = true;
                    }
                    else
                    {
                        try
                        {
                            child = startChild();
                            exited = child.WaitForExitAsync(CancellationToken.None);
                        }
                        catch (Exception exception)
                        {
                            Trace.TraceWarning("Snap Tap helper startup failed: {0}", exception.GetType().Name);
                            latched = true;
                            Publish(SnapTapRuntimeState.Failed);
                            if (child is not null)
                            {
                                // A process may exist even when observing it failed. Keep ownership.
                                stopping = true;
                                try { child.RequestStop(); } catch (Exception) { }
                                throw;
                            }
                        }
                    }
                }
                else if (child is null && !requested && !latched)
                {
                    latched = !Publish(SnapTapRuntimeState.Off);
                }

                changed ??= changes.Reader.WaitToReadAsync(cancellationToken).AsTask();
                if (exited is null)
                {
                    await changed.ConfigureAwait(false);
                }
                else
                {
                    await Task.WhenAny(changed, exited).ConfigureAwait(false);
                }
                if (changed.IsCompleted)
                {
                    await changed.ConfigureAwait(false);
                    while (changes.Reader.TryRead(out _)) { }
                    changed = null;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            supervisionFailed = true;
            Trace.TraceWarning("Snap Tap supervision failed: {0}", exception.GetType().Name);
            Publish(SnapTapRuntimeState.Failed);
        }
        finally
        {
            preferenceSource.SnapTapEnabledChanged -= OnPreferenceChanged;
            if (child is not null)
            {
                bool stopped = await StopAsync(child, exited).ConfigureAwait(false);
                if (stopped)
                {
                    bool success = await ObserveExitAsync(child, exited!).ConfigureAwait(false);
                    DisposeChild(child);
                    Publish(success ? SnapTapRuntimeState.Off : SnapTapRuntimeState.Failed);
                }
                else
                {
                    Publish(SnapTapRuntimeState.Failed);
                    _ = DisposeAfterExitAsync(child, exited);
                }
            }
        }

        // Program observes all session tasks together. A fault in this optional
        // feature must not complete its task and stop display/Fn-key services.
        if (supervisionFailed && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            GC.KeepAlive(child);
        }
    }

    private void OnPreferenceChanged(bool value)
    {
        if (!value)
        {
            Interlocked.Increment(ref offGeneration);
        }
        Volatile.Write(ref enabled, value ? 1 : 0);
        changes.Writer.TryWrite(true);
    }

    private bool Publish(SnapTapRuntimeState state, int processId = 0)
    {
        try
        {
            publishStatus(new(state, DateTimeOffset.UtcNow, processId));
            return true;
        }
        catch (Exception exception)
        {
            Trace.TraceWarning("Snap Tap status publication failed: {0}", exception.GetType().Name);
            return false;
        }
    }

    private static async Task<bool> StopAsync(ISnapTapChildProcess child, Task? exited)
    {
        try { child.RequestStop(); }
        catch (Exception exception) { Trace.TraceWarning("Snap Tap stop request failed: {0}", exception.GetType().Name); }
        if (exited is null)
        {
            return false;
        }
        try
        {
            await exited.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException) { return false; }
        catch (Exception)
        {
            try { return child.HasExited; }
            catch (Exception) { return false; }
        }
    }

    private static async Task<bool> ObserveExitAsync(ISnapTapChildProcess child, Task exited)
    {
        try
        {
            await exited.ConfigureAwait(false);
            return child.HasExited && child.ExitCode == 0;
        }
        catch (Exception) { return false; }
    }

    private static async Task DisposeAfterExitAsync(ISnapTapChildProcess child, Task? exited)
    {
        try
        {
            await (exited ?? child.WaitForExitAsync(CancellationToken.None)).ConfigureAwait(false);
            if (child.HasExited)
            {
                DisposeChild(child);
            }
        }
        catch (Exception exception) { Trace.TraceWarning("Snap Tap deferred cleanup failed: {0}", exception.GetType().Name); }
    }

    private static void DisposeChild(ISnapTapChildProcess child)
    {
        try { child.Dispose(); }
        catch (Exception exception) { Trace.TraceWarning("Snap Tap process handle cleanup failed: {0}", exception.GetType().Name); }
    }
}
