using System.Diagnostics;
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
    Func<SnapTapRuntimeStatus?>? readStatus = null,
    Func<KeyboardInputBackend, ISnapTapChildProcess>? startBackendChild = null,
    Func<bool>? canStartChild = null,
    TimeSpan? stopTimeout = null,
    Func<bool>? ownsSharedInput = null,
    Func<bool>? ownsFunctionRowInput = null,
    Action? prepareChild = null,
    Func<KeyboardDriverFeatures>? publicationFeatures = null,
    Func<bool>? canPublishIdleStatus = null)
{
    private readonly Channel<bool> changes = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private int offGeneration;
    private int enabled;
    private int started;
    private int backend = (int)KeyboardInputBackend.RazerDriver;
    private int backendGeneration;
    private KeyboardInputBackend publicationBackend = KeyboardInputBackend.RazerDriver;
    private int hasChild;
    private int sharedInput;
    private int ownershipRecovery;
    private int functionRowInput;
    private int functionRowRecovery;
    private SnapTapRuntimeStatus? lastPublishedStatus;
    private KeyboardDriverFeatures? lastPublishedFeatures;
    public event Action<bool>? OwnershipChanged;
    public bool HasChild => Volatile.Read(ref hasChild) != 0;
    public bool HasFunctionRowOwner => HasChild && Volatile.Read(ref functionRowInput) != 0;
    public bool FunctionRowRecoveryRequired => Volatile.Read(ref functionRowRecovery) != 0;
    public bool CanTransferOwnership => (!HasChild || Volatile.Read(ref sharedInput) == 0)
        && Volatile.Read(ref ownershipRecovery) == 0;
    public bool CanTransferFunctionRowOwnership => (!HasChild || Volatile.Read(ref functionRowInput) == 0)
        && Volatile.Read(ref functionRowRecovery) == 0;

    internal SnapTapRuntimeStatus? FunctionRowWindowsTransferStatus(KeyboardInputBackend requested,
        DiagnosticFailure? startupFailure)
    {
        if (requested != KeyboardInputBackend.WindowsHook || CanTransferFunctionRowOwnership && startupFailure is null) { return null; }
        bool recovery = FunctionRowRecoveryRequired || startupFailure is not null && !HasFunctionRowOwner;
        return new(recovery ? SnapTapRuntimeState.RecoveryRequired : SnapTapRuntimeState.WaitingForRelease,
            DateTimeOffset.UtcNow, Environment.ProcessId, Failure: recovery
                ? startupFailure ?? SnapTapDiagnosticFailure.Create("FunctionRowWindowsOwnership", "TransferInput",
                    "OwnershipJournal", "DriverFunctionRowOwnershipRecoveryRequired", true) : null)
        { Backend = KeyboardInputBackend.WindowsHook };
    }

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
        bool admissionOnlyLatch = false;
        KeyboardInputBackend childBackend = KeyboardInputBackend.RazerDriver;
        bool stopping = false;
        bool supervisionFailed = false;
        DiagnosticFailure? coordinatorFailure = null;
        int observedOff = 0;
        int observedBackend = 0;
        ISnapTapBackendPreferenceSource? backendSource = preferenceSource as ISnapTapBackendPreferenceSource;
        if (backendSource is not null)
        {
            backendSource.SnapTapBackendChanged += OnBackendChanged;
            OnBackendChanged(backendSource.SnapTapBackend);
        }
        preferenceSource.SnapTapEnabledChanged += OnPreferenceChanged;
        OnPreferenceChanged(preferenceSource.SnapTapEnabled);
        Task<bool>? changed = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                bool launchBlocked = false;
                int currentOff = Volatile.Read(ref offGeneration);
                bool offObserved = currentOff != observedOff;
                observedOff = currentOff;
                if (offObserved)
                {
                    latched = false;
                    admissionOnlyLatch = false;
                }

                bool requested = Volatile.Read(ref enabled) != 0;
                int currentBackendGeneration = Volatile.Read(ref backendGeneration);
                bool backendChanged = currentBackendGeneration != observedBackend;
                observedBackend = currentBackendGeneration;
                KeyboardInputBackend selected = (KeyboardInputBackend)Volatile.Read(ref backend);
                if (backendChanged && admissionOnlyLatch)
                {
                    latched = false;
                    admissionOnlyLatch = false;
                }
                if (child is not null && (offObserved || !requested || childBackend != selected) && !stopping)
                {
                    stopping = true;
                    if (!await StopAsync(child, exited!, stopTimeout).ConfigureAwait(false))
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
                    admissionOnlyLatch = IsNoWriteAdmission(childExitCode, terminal);
                    if (!admissionOnlyLatch && (!successful || !stopping || terminal?.Failure?.MayHaveSubmitted == true))
                    {
                        // Preference Off generations may reset this coordinator's
                        // restart latch, but never another backend's recovery barrier.
                        LatchOwnedRecovery();
                    }
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
                    // An explicit backend change may retry a rejected admission,
                    // but never bypass a failed or uncertain cleanup.
                    if (childBackend != (KeyboardInputBackend)Volatile.Read(ref backend) && admissionOnlyLatch)
                    {
                        latched = false;
                        admissionOnlyLatch = false;
                    }
                    bool cleanupConfirmed = successful && stopping && terminal?.Failure?.MayHaveSubmitted != true;
                    stopping = false;
                    // Complete terminal publication before another coordinator may
                    // transfer ownership of the same physical pair.
                    Volatile.Write(ref hasChild, 0);
                    NotifyOwnershipChanged(cleanupConfirmed);
                }

                requested = Volatile.Read(ref enabled) != 0;
                selected = (KeyboardInputBackend)Volatile.Read(ref backend);
                if (child is null && requested && !latched)
                {
                    // Snapshot a typed request once for the launch gate, status,
                    // ownership flags and process arguments. The child independently
                    // rejects a snapshot made obsolete before native admission.
                    prepareChild?.Invoke();
                    publicationBackend = selected;
                    if (selected == KeyboardInputBackend.Unknown
                        || (startBackendChild is null && selected != KeyboardInputBackend.RazerDriver))
                    {
                        latched = true;
                        admissionOnlyLatch = true;
                        Publish(SnapTapRuntimeState.Unavailable, failure: SnapTapDiagnosticFailure.Create(
                            SnapTapDiagnosticFailure.Coordinator, "SelectBackend", "Admission", "BackendUnavailable"));
                    }
                    else if (canStartChild?.Invoke() == false)
                    {
                        launchBlocked = true;
                        latched |= !Publish(SnapTapRuntimeState.WaitingForRelease);
                    }
                    // The child publishes its own Active/Waiting/Paused state after launch.
                    else if (!Publish(SnapTapRuntimeState.Starting, Environment.ProcessId))
                    {
                        latched = true;
                    }
                    else
                    {
                        try
                        {
                            childStartedAt = DateTimeOffset.UtcNow;
                            admissionOnlyLatch = false;
                            childBackend = selected;
                            Volatile.Write(ref sharedInput, ownsSharedInput?.Invoke() != false ? 1 : 0);
                            Volatile.Write(ref functionRowInput, ownsFunctionRowInput?.Invoke() == true ? 1 : 0);
                            child = startBackendChild is null ? startChild() : startBackendChild(selected);
                            Volatile.Write(ref hasChild, 1);
                            exited = child.WaitForExitAsync(CancellationToken.None);
                            NotifyOwnershipChanged(cleanupConfirmed: false);
                        }
                        catch (Exception exception)
                        {
                            DiagnosticTrace.TraceException(
                                "OpenBlade.SessionAgent.SnapTapProcessCoordinator",
                                "StartChild",
                                exception);
                            latched = true;
                            coordinatorFailure = SnapTapDiagnosticFailure.FromException(
                                SnapTapDiagnosticFailure.Coordinator, "StartChild",
                                child is null ? "ProcessStart" : "ExitObservation",
                                child is null ? "HelperStartFailed" : "ExitObservationFailed",
                                exception);
                            if (child is not null)
                            {
                                LatchOwnedRecovery();
                                NotifyOwnershipChanged(cleanupConfirmed: false);
                                // A process may exist even when observing it failed. Keep ownership.
                                stopping = true;
                                try { child.RequestStop(); } catch (Exception) { }
                                throw;
                            }
                            Publish(SnapTapRuntimeState.Failed, failure: coordinatorFailure);
                        }
                    }
                }
                else if (child is null && !requested && !latched)
                {
                    // The retired producer must not overwrite a different selected
                    // backend, even when its previous status was an admission error.
                    if (canPublishIdleStatus?.Invoke() != false)
                    {
                        publicationBackend = selected;
                        latched = !Publish(SnapTapRuntimeState.Off);
                    }
                }

                changed ??= changes.Reader.WaitToReadAsync(cancellationToken).AsTask();
                if (exited is null)
                {
                    if (launchBlocked)
                    {
                        // Poll only while an existing input owner is draining held
                        // keys; idle/active coordinators continue to wait on events.
                        await Task.WhenAny(changed, Task.Delay(50, cancellationToken)).ConfigureAwait(false);
                    }
                    else { await changed.ConfigureAwait(false); }
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
            DiagnosticTrace.TraceException(
                "OpenBlade.SessionAgent.SnapTapProcessCoordinator",
                "RunSupervision",
                exception);
            coordinatorFailure ??= SnapTapDiagnosticFailure.FromException(
                SnapTapDiagnosticFailure.Coordinator, "Run", "Supervision",
                "SupervisionFailed", exception);
            Publish(SnapTapRuntimeState.Failed, failure: coordinatorFailure);
        }
        finally
        {
            preferenceSource.SnapTapEnabledChanged -= OnPreferenceChanged;
            if (backendSource is not null) { backendSource.SnapTapBackendChanged -= OnBackendChanged; }
            if (child is not null)
            {
                bool stopped = await StopAsync(child, exited, stopTimeout).ConfigureAwait(false);
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
                    if (!success && !IsNoWriteAdmission(exitCode, terminal)) { LatchOwnedRecovery(); }
                    Volatile.Write(ref hasChild, 0);
                    NotifyOwnershipChanged(success && terminal?.Failure?.MayHaveSubmitted != true);
                }
                else
                {
                    LatchOwnedRecovery();
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

    private void LatchOwnedRecovery()
    {
        if (Volatile.Read(ref sharedInput) != 0) { Volatile.Write(ref ownershipRecovery, 1); }
        if (Volatile.Read(ref functionRowInput) != 0) { Volatile.Write(ref functionRowRecovery, 1); }
    }

    private void NotifyOwnershipChanged(bool cleanupConfirmed)
    {
        try { OwnershipChanged?.Invoke(cleanupConfirmed); }
        catch (Exception exception)
        {
            DiagnosticTrace.TraceException("OpenBlade.SessionAgent.SnapTapProcessCoordinator", "OwnershipChanged", exception);
        }
    }

    private static bool IsNoWriteAdmission(int exitCode, SnapTapRuntimeStatus? terminal) =>
        exitCode is 3 or 5 or 6 or 7 && terminal?.Failure?.MayHaveSubmitted != true;

    private void OnBackendChanged(KeyboardInputBackend value)
    {
        value = value.Normalize();
        if (Interlocked.Exchange(ref backend, (int)value) != (int)value)
        {
            Interlocked.Increment(ref backendGeneration);
            changes.Writer.TryWrite(true);
        }
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
            DiagnosticTrace.TraceException(
                "OpenBlade.SessionAgent.SnapTapProcessCoordinator",
                "ReadTerminalStatus",
                exception);
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
            Failure: failure)
        { Backend = publicationBackend });

    private bool Publish(SnapTapRuntimeStatus status)
    {
        KeyboardDriverFeatures? features = publicationFeatures?.Invoke();
        if (lastPublishedStatus is { } previous && features == lastPublishedFeatures
            && status with { UpdatedAt = previous.UpdatedAt } == previous)
        {
            return true;
        }
        if (status.Failure is { } failure)
        {
            TraceFailure(failure);
        }
        try
        {
            publishStatus(status);
            lastPublishedStatus = status;
            lastPublishedFeatures = features;
            return true;
        }
        catch (Exception exception)
        {
            DiagnosticTrace.TraceException(
                "OpenBlade.SessionAgent.SnapTapProcessCoordinator",
                "PublishStatus",
                exception);
            return false;
        }
    }

    private static void TraceFailure(DiagnosticFailure failure)
        => Trace.TraceWarning("Snap Tap diagnostic failure: {0}", failure);

    private static async Task<bool> StopAsync(ISnapTapChildProcess child, Task? exited, TimeSpan? timeout)
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
            await exited.WaitAsync(timeout ?? TimeSpan.FromSeconds(2)).ConfigureAwait(false);
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
        catch (Exception exception)
        {
            DiagnosticTrace.TraceException(
                "OpenBlade.SessionAgent.SnapTapProcessCoordinator",
                "DisposeAfterExit",
                exception);
        }
    }

    private static void DisposeChild(ISnapTapChildProcess child)
    {
        try { child.Dispose(); }
        catch (Exception exception)
        {
            DiagnosticTrace.TraceException(
                "OpenBlade.SessionAgent.SnapTapProcessCoordinator",
                "DisposeChild",
                exception);
        }
    }
}
