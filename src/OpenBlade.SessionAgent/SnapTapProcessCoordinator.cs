using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using OpenBlade.Contracts;
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
    Action<SnapTapRuntimeStatus> publishStatus,
    Func<SnapTapRuntimeStatus?>? readStatus = null)
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
        DateTimeOffset childStartedAt = DateTimeOffset.MinValue;
        bool latched = false;
        bool stopping = false;
        bool supervisionFailed = false;
        DiagnosticFailure? coordinatorFailure = null;
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
                        latched |= !Publish(SnapTapRuntimeState.Failed, failure: SnapTapDiagnosticFailure.Create(
                            SnapTapDiagnosticFailure.Coordinator, "StopChild", "WaitForExit", "StopTimedOut"));
                    }
                }

                if (child is not null && exited!.IsCompleted)
                {
                    if (!child.HasExited)
                    {
                        if (exited.IsFaulted)
                        {
                            try
                            {
                                await exited.ConfigureAwait(false);
                            }
                            catch (Exception exception)
                            {
                                coordinatorFailure = SnapTapDiagnosticFailure.FromException(
                                    SnapTapDiagnosticFailure.Coordinator, "ObserveChild", "ExitObservation",
                                    "ExitObservationFailed", exception);
                                throw;
                            }
                        }
                        throw new InvalidOperationException("The helper exit observer ended before the process exited.");
                    }
                    bool successful = await ObserveExitAsync(child, exited).ConfigureAwait(false);
                    int childId = child.Id;
                    int childExitCode = child.ExitCode;
                    SnapTapRuntimeStatus? terminal = ReadMatchingTerminalStatus(
                        readStatus, childId, childStartedAt);
                    bool unavailable = childExitCode == 3;
                    bool recoveryRequired = childExitCode == 4;
                    bool driverMissing = childExitCode == 5;
                    bool unsupportedModel = childExitCode == 6;
                    bool manualDriverSetup = childExitCode == 7;
                    DisposeChild(child);
                    child = null;
                    exited = null;
                    if (!successful || !stopping)
                    {
                        latched = true;
                        latched |= terminal is not null
                            ? !Publish(terminal with { UpdatedAt = DateTimeOffset.UtcNow })
                            : !Publish(recoveryRequired ? SnapTapRuntimeState.RecoveryRequired
                                : unsupportedModel ? SnapTapRuntimeState.UnsupportedModel
                                : manualDriverSetup ? SnapTapRuntimeState.ManualDriverSetupRequired
                                : driverMissing ? SnapTapRuntimeState.DriverMissing
                                : unavailable ? SnapTapRuntimeState.Unavailable : SnapTapRuntimeState.Failed,
                                failure: SnapTapDiagnosticFailure.Create(
                                    SnapTapDiagnosticFailure.Coordinator, "ObserveChild", "ProcessExit",
                                    "TerminalStatusUnavailable", processExitCode: childExitCode));
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
                            childStartedAt = DateTimeOffset.UtcNow;
                            child = startChild();
                            exited = child.WaitForExitAsync(CancellationToken.None);
                        }
                        catch (Exception exception)
                        {
                            Trace.TraceWarning("Snap Tap helper startup failed: {0}", exception.GetType().Name);
                            latched = true;
                            coordinatorFailure = SnapTapDiagnosticFailure.FromException(
                                SnapTapDiagnosticFailure.Coordinator, "StartChild",
                                child is null ? "ProcessStart" : "ExitObservation",
                                child is null ? "HelperStartFailed" : "ExitObservationFailed",
                                exception);
                            Publish(SnapTapRuntimeState.Failed, failure: coordinatorFailure);
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
            coordinatorFailure ??= SnapTapDiagnosticFailure.FromException(
                SnapTapDiagnosticFailure.Coordinator, "Run", "Supervision",
                "SupervisionFailed", exception);
            Publish(SnapTapRuntimeState.Failed, failure: coordinatorFailure);
        }
        finally
        {
            preferenceSource.SnapTapEnabledChanged -= OnPreferenceChanged;
            if (child is not null)
            {
                bool stopped = await StopAsync(child, exited).ConfigureAwait(false);
                SnapTapRuntimeStatus? terminal = ReadMatchingTerminalStatus(
                    readStatus, child.Id, childStartedAt);
                if (stopped)
                {
                    bool success = await ObserveExitAsync(child, exited!).ConfigureAwait(false);
                    if (!success)
                    {
                        try { success = child.HasExited && child.ExitCode == 0; }
                        catch (Exception) { }
                    }
                    int exitCode = child.ExitCode;
                    DisposeChild(child);
                    if (success)
                    {
                        Publish(coordinatorFailure is null ? SnapTapRuntimeState.Off : SnapTapRuntimeState.Failed,
                            failure: coordinatorFailure);
                    }
                    else if (coordinatorFailure is not null)
                    {
                        Publish(SnapTapRuntimeState.Failed, failure: SnapTapDiagnosticFailure.Create(
                            SnapTapDiagnosticFailure.Coordinator, "StopChild", "ProcessExit",
                            "StopFailed", cause: coordinatorFailure, processExitCode: exitCode));
                    }
                    else if (terminal is not null)
                    {
                        Publish(terminal with { UpdatedAt = DateTimeOffset.UtcNow });
                    }
                    else
                    {
                        Publish(SnapTapRuntimeState.Failed, failure: SnapTapDiagnosticFailure.Create(
                            SnapTapDiagnosticFailure.Coordinator, "StopChild", "ProcessExit",
                            "StopFailed", processExitCode: exitCode));
                    }
                }
                else
                {
                    if (coordinatorFailure is not null)
                    {
                        Publish(SnapTapRuntimeState.Failed, failure: SnapTapDiagnosticFailure.Create(
                            SnapTapDiagnosticFailure.Coordinator, "StopChild", "WaitForExit",
                            "StopTimedOut", cause: coordinatorFailure));
                    }
                    else if (terminal is not null)
                    {
                        Publish(terminal with { UpdatedAt = DateTimeOffset.UtcNow });
                    }
                    else
                    {
                        Publish(SnapTapRuntimeState.Failed, failure: SnapTapDiagnosticFailure.Create(
                            SnapTapDiagnosticFailure.Coordinator, "StopChild", "WaitForExit",
                            "StopTimedOut"));
                    }
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

    internal static SnapTapRuntimeStatus? MatchingTerminalStatus(
        SnapTapRuntimeStatus? status,
        int processId,
        DateTimeOffset startedAt) => status is not null
            && status.ProcessId == processId
            && status.UpdatedAt >= startedAt
            && SnapTapStatusStore.IsTerminalState(status.State)
            && status.Failure is not null
                ? status : null;

    private static SnapTapRuntimeStatus? ReadMatchingTerminalStatus(
        Func<SnapTapRuntimeStatus?>? readStatus,
        int processId,
        DateTimeOffset startedAt)
    {
        if (readStatus is null)
        {
            return null;
        }
        try
        {
            return MatchingTerminalStatus(readStatus(), processId, startedAt);
        }
        catch (Exception exception)
        {
            Trace.TraceWarning("Snap Tap terminal status read failed: {0}", exception.GetType().Name);
            return null;
        }
    }

    private bool Publish(
        SnapTapRuntimeState state,
        int processId = 0,
        DiagnosticFailure? failure = null) => Publish(new(
            state,
            DateTimeOffset.UtcNow,
            processId,
            Failure: failure));

    private bool Publish(SnapTapRuntimeStatus status)
    {
        if (status.Failure is { } failure)
        {
            TraceFailure(failure);
        }
        try
        {
            publishStatus(status);
            return true;
        }
        catch (Exception exception)
        {
            Trace.TraceWarning("Snap Tap status publication failed: {0}", exception.GetType().Name);
            return false;
        }
    }

    private static void TraceFailure(DiagnosticFailure failure)
    {
        int depth = 0;
        for (DiagnosticFailure? current = failure; current is not null && depth < 4; current = current.Cause)
        {
            Trace.TraceWarning(
                "Snap Tap diagnostic failure: depth={0}; component={1}; operation={2}; stage={3}; reason={4}; mayHaveSubmitted={5}; exception={6}; hresult={7}; native={8}; processExitCode={9}.",
                depth, current.Component, current.Operation, current.Stage, current.Reason,
                current.MayHaveSubmitted, current.ExceptionType ?? "None",
                current.HResult?.ToString(CultureInfo.InvariantCulture) ?? "None",
                current.NativeErrorCode?.ToString(CultureInfo.InvariantCulture) ?? "None",
                current.ProcessExitCode?.ToString(CultureInfo.InvariantCulture) ?? "None");
            depth++;
        }
    }

    private static async Task<bool> StopAsync(ISnapTapChildProcess child, Task? exited)
    {
        try { child.RequestStop(); }
        catch (Exception exception)
        {
            TraceFailure(SnapTapDiagnosticFailure.FromException(
                SnapTapDiagnosticFailure.Coordinator, "StopChild", "StopRequest", "StopRequestFailed", exception));
        }
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
        catch (Exception exception)
        {
            TraceFailure(SnapTapDiagnosticFailure.FromException(
                SnapTapDiagnosticFailure.Coordinator, "StopChild", "WaitForExit", "ChildWaitFailed", exception));
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
        catch (Exception exception)
        {
            TraceFailure(SnapTapDiagnosticFailure.FromException(
                SnapTapDiagnosticFailure.Coordinator, "ObserveChild", "ExitObservation", "ExitObservationFailed", exception));
            return false;
        }
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
