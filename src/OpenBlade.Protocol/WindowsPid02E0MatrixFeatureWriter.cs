namespace OpenBlade.Protocol;

internal sealed class WindowsPid02E0MatrixFeatureOpener
{
    private const int ErrorAccessDenied = 5;
    private const string ExactManufacturer = "Razer";
    private const string ExactProductName = "Blade 16 - RZ09-0581";
    private const string ExactSku = "RZ09-05819EN4";

    internal static readonly HidOpenParameters OperationalOpenParameters = new(
        DesiredAccess: 0xC0000000u,
        ShareMode: 0x00000003u,
        CreationDisposition: 3u,
        FlagsAndAttributes: 0x40000000u,
        Inheritable: false);

    internal static readonly HidOpenParameters MetadataOnlyOpenParameters =
        OperationalOpenParameters with { DesiredAccess = 0 };

    private readonly IHostIdentitySource hostIdentity;
    private readonly IHardwareAccessGuard guard;
    private readonly IHidCandidateNative native;
    private readonly ControllerExclusivityPolicy exclusivityPolicy;

    internal WindowsPid02E0MatrixFeatureOpener(
        IHostIdentitySource hostIdentity,
        IHardwareAccessGuard guard,
        IHidCandidateNative native,
        ControllerExclusivityPolicy exclusivityPolicy)
    {
        ArgumentNullException.ThrowIfNull(hostIdentity);
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(native);
        this.hostIdentity = hostIdentity;
        this.guard = guard;
        this.native = native;
        this.exclusivityPolicy = exclusivityPolicy;
    }

    internal HidSessionResult<OpenedHidHandle> Open()
    {
        HostIdentityReadResult identity;
        try
        {
            identity = hostIdentity.ReadExactIdentity();
        }
        catch
        {
            return Failure(HidSessionFailureKind.IdentityBlocked);
        }

        if (!IsExactHost(identity))
        {
            return Failure(HidSessionFailureKind.IdentityBlocked);
        }

        if (exclusivityPolicy == ControllerExclusivityPolicy.Strict)
        {
            HardwareAccessDecision access;
            try
            {
                access = guard.Check();
            }
            catch
            {
                return Failure(HidSessionFailureKind.ConflictDetectionFailed);
            }

            if (access.DetectionFailure is not null)
            {
                return Failure(HidSessionFailureKind.ConflictDetectionFailed);
            }

            if (access.Conflicts.Count != 0)
            {
                return Failure(HidSessionFailureKind.ConflictBlocked);
            }
        }

        IReadOnlyList<string> paths;
        try
        {
            paths = native.EnumerateDevicePaths();
        }
        catch
        {
            return Failure(HidSessionFailureKind.NativeIoFailure);
        }

        List<OpenedCandidateHandle> writableExact = [];
        int metadataOnlyExact = 0;
        try
        {
            foreach (string path in paths)
            {
                HidCandidateOpenResult opened;
                try
                {
                    opened = native.TryOpen(path, OperationalOpenParameters);
                }
                catch
                {
                    return Failure(HidSessionFailureKind.NativeIoFailure);
                }

                OpenedCandidateHandle? candidate = opened.Candidate;
                if (candidate is null)
                {
                    if (opened.Win32Error == ErrorAccessDenied)
                    {
                        HidSessionFailureKind? fallbackFailure = InspectMetadataOnly(
                            path,
                            ref metadataOnlyExact);
                        if (fallbackFailure is not null)
                        {
                            return Failure(fallbackFailure.Value);
                        }
                    }

                    continue;
                }

                try
                {
                    HidHandleIdentity handleIdentity = native.ReadIdentity(candidate);
                    if (TargetBladeDevice.IsExactMatrixInterface(handleIdentity))
                    {
                        writableExact.Add(candidate);
                        candidate = null;
                    }
                }
                catch
                {
                    return Failure(HidSessionFailureKind.NativeIoFailure);
                }
                finally
                {
                    candidate?.Dispose();
                }
            }

            int exactCount = writableExact.Count + metadataOnlyExact;
            if (exactCount != 1)
            {
                return Failure(HidSessionFailureKind.IdentityBlocked);
            }

            if (writableExact.Count == 0)
            {
                return Failure(HidSessionFailureKind.NativeIoFailure);
            }

            OpenedCandidateHandle selected = writableExact[0];
            writableExact.Clear();
            try
            {
                return new(native.BindSelected(selected), null);
            }
            catch
            {
                selected.Dispose();
                return Failure(HidSessionFailureKind.NativeIoFailure);
            }
        }
        finally
        {
            foreach (OpenedCandidateHandle candidate in writableExact)
            {
                candidate.Dispose();
            }
        }
    }

    private HidSessionFailureKind? InspectMetadataOnly(
        string path,
        ref int metadataOnlyExact)
    {
        HidCandidateOpenResult fallback;
        try
        {
            fallback = native.TryOpen(path, MetadataOnlyOpenParameters);
        }
        catch
        {
            return HidSessionFailureKind.NativeIoFailure;
        }

        if (fallback.Candidate is not { } candidate)
        {
            return null;
        }

        using (candidate)
        {
            try
            {
                if (TargetBladeDevice.IsExactMatrixInterface(native.ReadIdentity(candidate)))
                {
                    metadataOnlyExact++;
                }

                return null;
            }
            catch
            {
                return HidSessionFailureKind.NativeIoFailure;
            }
        }
    }

    private static bool IsExactHost(HostIdentityReadResult identity) =>
        identity.Failure is null
        && identity.Evidence is { } evidence
        && string.Equals(evidence.Manufacturer, ExactManufacturer, StringComparison.Ordinal)
        && string.Equals(evidence.ProductName, ExactProductName, StringComparison.Ordinal)
        && string.Equals(evidence.Sku, ExactSku, StringComparison.Ordinal)
        && identity.FirmwareCompatibility switch
        {
            HostFirmwareCompatibility.Tested =>
                TargetBladeDevice.IsValidatedBiosVersion(evidence.BiosVersion),
            HostFirmwareCompatibility.ModelScoped =>
                !string.IsNullOrWhiteSpace(evidence.BiosVersion),
            HostFirmwareCompatibility.UntestedAllowed =>
                !string.IsNullOrWhiteSpace(evidence.BiosVersion),
            _ => false,
        };

    private static HidSessionResult<OpenedHidHandle> Failure(HidSessionFailureKind failure) =>
        new(null, failure);
}

internal interface IPid02E0MatrixFeatureWriter : IAsyncDisposable
{
    RazerCommandSessionDisposition Disposition { get; }

    Task<HidSessionResult<HidUnit>> WriteAsync(
        Pid02E0MatrixReport report,
        CancellationToken cancellationToken);
}

internal readonly record struct MatrixWritePhaseTiming(
    TimeSpan QueueWait,
    TimeSpan ControllerGuard,
    TimeSpan? NativeSubmitToCompletion,
    TimeSpan? ManagedCompletion);

internal interface IMatrixWriteTimingSink
{
    void Record(MatrixWritePhaseTiming timing);
}

internal sealed class WindowsPid02E0MatrixFeatureWriter : IPid02E0MatrixFeatureWriter
{
    private const uint IoctlHidSetFeature = 0x000B0191;

    private readonly object sync = new();
    private readonly Queue<WorkItem> queue = new();
    private readonly SemaphoreSlim queued = new(0);
    private readonly OpenedHidHandle handle;
    private readonly IHidFeatureIo io;
    private readonly IHardwareAccessGuard guard;
    private readonly ControllerExclusivityPolicy exclusivityPolicy;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan writeTimeout;
    private readonly IMatrixWriteTimingSink? timingSink;
    private readonly Task worker;
    private RazerCommandSessionDisposition state = RazerCommandSessionDisposition.Healthy;

    internal WindowsPid02E0MatrixFeatureWriter(
        OpenedHidHandle handle,
        IHidFeatureIo io,
        IHardwareAccessGuard guard,
        TimeProvider timeProvider,
        TimeSpan writeTimeout,
        ControllerExclusivityPolicy exclusivityPolicy,
        IMatrixWriteTimingSink? timingSink = null)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(io);
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(timeProvider);
        RazerCommandSessionTimeout.Validate(writeTimeout);
        this.handle = handle;
        this.io = io;
        this.guard = guard;
        this.timeProvider = timeProvider;
        this.writeTimeout = writeTimeout;
        this.exclusivityPolicy = exclusivityPolicy;
        this.timingSink = timingSink;
        worker = RunAsync();
    }

    public RazerCommandSessionDisposition Disposition
    {
        get
        {
            lock (sync)
            {
                return state;
            }
        }
    }

    public Task<HidSessionResult<HidUnit>> WriteAsync(
        Pid02E0MatrixReport report,
        CancellationToken cancellationToken)
    {
        byte[] buffer = Pid02E0MatrixReportCodec.EncodeFeatureReportBuffer(report);
        if (buffer.Length != TargetBladeDevice.MatrixFeatureReportByteLength ||
            buffer[0] != Pid02E0MatrixReportCodec.ReportId)
        {
            throw new InvalidOperationException("The matrix codec produced an invalid feature buffer.");
        }

        var item = new WorkItem(
            buffer,
            cancellationToken,
            timingSink is null ? 0 : timeProvider.GetTimestamp());
        lock (sync)
        {
            if (state != RazerCommandSessionDisposition.Healthy)
            {
                return Task.FromResult(Failure(
                    state == RazerCommandSessionDisposition.RecoveryRequired
                        ? HidSessionFailureKind.Poisoned
                        : HidSessionFailureKind.Disposed));
            }

            queue.Enqueue(item);
            item.RegisterQueuedCancellation();
            queued.Release();
        }

        return item.Task;
    }

    public ValueTask DisposeAsync()
    {
        lock (sync)
        {
            if (state is RazerCommandSessionDisposition.Healthy or
                RazerCommandSessionDisposition.RecoveryRequired)
            {
                state = RazerCommandSessionDisposition.Disposing;
                DrainQueued(HidSessionFailureKind.Disposed);
                queued.Release();
            }

            return new ValueTask(worker);
        }
    }

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                await queued.WaitAsync().ConfigureAwait(false);
                WorkItem? item = null;
                bool stop;
                lock (sync)
                {
                    while (queue.Count > 0)
                    {
                        WorkItem candidate = queue.Dequeue();
                        candidate.UnregisterQueuedCancellation();
                        if (candidate.TryClaim())
                        {
                            item = candidate;
                            break;
                        }
                    }

                    stop = item is null && state != RazerCommandSessionDisposition.Healthy;
                }

                if (stop)
                {
                    break;
                }

                if (item is null)
                {
                    continue;
                }

                item.Complete(await ExecuteClaimedAsync(item).ConfigureAwait(false));
                lock (sync)
                {
                    if (state != RazerCommandSessionDisposition.Healthy)
                    {
                        break;
                    }
                }
            }
        }
        finally
        {
            try
            {
                await handle.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                lock (sync)
                {
                    state = RazerCommandSessionDisposition.FullyDisposed;
                    DrainQueued(HidSessionFailureKind.Disposed);
                }

                queued.Dispose();
            }
        }
    }

    private async Task<HidSessionResult<HidUnit>> ExecuteClaimedAsync(WorkItem item)
    {
        long claimedTimestamp = timingSink is null ? 0 : timeProvider.GetTimestamp();
        long guardStartedTimestamp = claimedTimestamp;
        long guardCompletedTimestamp = guardStartedTimestamp;
        using var deadline = new CancellationTokenSource(writeTimeout, timeProvider);
        HidSessionFailureKind? cancellation = ClassifyCancellation(
            item.CancellationToken,
            deadline.Token);
        if (cancellation is not null)
        {
            return Failure(cancellation.Value);
        }

        if (exclusivityPolicy == ControllerExclusivityPolicy.Strict)
        {
            if (timingSink is not null)
            {
                guardStartedTimestamp = timeProvider.GetTimestamp();
            }
            HardwareAccessDecision access;
            try
            {
                access = guard.Check();
            }
            catch
            {
                return DiagnosticFailure(
                    HidSessionFailureKind.ConflictDetectionFailed,
                    HidSessionFailurePhase.Guard);
            }

            if (access.DetectionFailure is not null)
            {
                return DiagnosticFailure(
                    HidSessionFailureKind.ConflictDetectionFailed,
                    HidSessionFailurePhase.Guard);
            }

            if (access.Conflicts.Count != 0)
            {
                return DiagnosticFailure(
                    HidSessionFailureKind.ConflictBlocked,
                    HidSessionFailurePhase.Guard);
            }
            if (timingSink is not null)
            {
                guardCompletedTimestamp = timeProvider.GetTimestamp();
            }
        }

        cancellation = ClassifyCancellation(item.CancellationToken, deadline.Token);
        if (cancellation is not null)
        {
            return Failure(cancellation.Value);
        }

        HidFeatureIoResult result;
        try
        {
            result = await io.ExecuteAsync(
                handle,
                IoctlHidSetFeature,
                item.Buffer,
                item.CancellationToken,
                deadline.Token).ConfigureAwait(false);
        }
        catch (NativeOperationFinalizationException exception)
        {
            Poison();
            NativeIoCompletion? completion = exception.Result.Completion;
            return DiagnosticFailure(
                HidSessionFailureKind.NativeIoFailure,
                exception.Result.WasSubmitted
                    ? HidSessionFailurePhase.SetCompletion
                    : HidSessionFailurePhase.SetSubmit,
                completion,
                managedHResult: completion is null
                    ? exception.InnerException?.HResult
                    : null);
        }
        catch (Exception exception)
        {
            Poison();
            // Preserve the closed public diagnostic shape. Native Win32 errors
            // are non-negative; a negative value in the same numeric slot is
            // the managed HRESULT from preparation or cleanup.
            return DiagnosticFailure(
                HidSessionFailureKind.NativeIoFailure,
                HidSessionFailurePhase.SetSubmit,
                managedHResult: exception.HResult);
        }

        cancellation = ClassifyIoCancellation(
            result,
            item.CancellationToken,
            deadline.Token);
        if (cancellation is not null)
        {
            if (result.WasSubmitted)
            {
                Poison();
                return DiagnosticFailure(
                    cancellation.Value,
                    HidSessionFailurePhase.SetCompletion,
                    result.Completion);
            }

            return Failure(cancellation.Value);
        }

        if (result.Termination != HidFeatureTermination.Completed ||
            result.Completion is not { Succeeded: true })
        {
            Poison();
            return DiagnosticFailure(
                HidSessionFailureKind.NativeIoFailure,
                result.WasSubmitted
                    ? HidSessionFailurePhase.SetCompletion
                    : HidSessionFailurePhase.SetSubmit,
                result.Completion);
        }

        if (timingSink is not null)
        {
            RecordTiming(new MatrixWritePhaseTiming(
                timeProvider.GetElapsedTime(item.EnqueuedTimestamp, claimedTimestamp),
                timeProvider.GetElapsedTime(
                    guardStartedTimestamp,
                    guardCompletedTimestamp),
                result.Timing?.SubmitToCompletion,
                result.Timing?.CompletionToManagedReturn));
        }
        return new(new HidUnit(), null);
    }

    private void RecordTiming(MatrixWritePhaseTiming timing)
    {
        try
        {
            timingSink?.Record(timing);
        }
        catch
        {
            // Developer-only timing observation must not affect a hardware write.
        }
    }

    private void Poison()
    {
        lock (sync)
        {
            if (state == RazerCommandSessionDisposition.Healthy)
            {
                state = RazerCommandSessionDisposition.RecoveryRequired;
                DrainQueued(HidSessionFailureKind.Poisoned);
            }
        }
    }

    private void DrainQueued(HidSessionFailureKind failure)
    {
        while (queue.Count > 0)
        {
            WorkItem item = queue.Dequeue();
            item.UnregisterQueuedCancellation();
            item.FailQueued(failure);
        }
    }

    private static HidSessionFailureKind? ClassifyIoCancellation(
        HidFeatureIoResult result,
        CancellationToken caller,
        CancellationToken deadline)
    {
        if (caller.IsCancellationRequested ||
            result.Termination == HidFeatureTermination.CallerCanceled)
        {
            return HidSessionFailureKind.Canceled;
        }

        if (deadline.IsCancellationRequested ||
            result.Termination == HidFeatureTermination.DeadlineExpired)
        {
            return HidSessionFailureKind.Timeout;
        }

        return null;
    }

    private static HidSessionFailureKind? ClassifyCancellation(
        CancellationToken caller,
        CancellationToken deadline)
    {
        if (caller.IsCancellationRequested)
        {
            return HidSessionFailureKind.Canceled;
        }

        return deadline.IsCancellationRequested ? HidSessionFailureKind.Timeout : null;
    }

    private static HidSessionResult<HidUnit> Failure(HidSessionFailureKind failure) =>
        new(default, failure);

    private static HidSessionResult<HidUnit> DiagnosticFailure(
        HidSessionFailureKind failure,
        HidSessionFailurePhase phase,
        NativeIoCompletion? completion = null,
        int? managedHResult = null) =>
        new(default, failure)
        {
            Diagnostic = new HidSessionFailureDiagnostic(
                phase,
                failure,
                completion?.ErrorCode ?? managedHResult,
                completion?.BytesTransferred),
        };

    private sealed class WorkItem
    {
        private readonly TaskCompletionSource<HidSessionResult<HidUnit>> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenRegistration cancellationRegistration;
        private int itemState;

        internal WorkItem(
            byte[] buffer,
            CancellationToken cancellationToken,
            long enqueuedTimestamp)
        {
            Buffer = buffer;
            CancellationToken = cancellationToken;
            EnqueuedTimestamp = enqueuedTimestamp;
        }

        internal byte[] Buffer { get; }

        internal CancellationToken CancellationToken { get; }

        internal long EnqueuedTimestamp { get; }

        internal Task<HidSessionResult<HidUnit>> Task => completion.Task;

        internal void RegisterQueuedCancellation()
        {
            if (CancellationToken.CanBeCanceled)
            {
                cancellationRegistration = CancellationToken.Register(
                    static state => ((WorkItem)state!).CancelQueued(),
                    this);
            }
        }

        internal void UnregisterQueuedCancellation() => cancellationRegistration.Unregister();

        internal bool TryClaim() => Interlocked.CompareExchange(ref itemState, 1, 0) == 0;

        internal void Complete(HidSessionResult<HidUnit> result)
        {
            if (Interlocked.CompareExchange(ref itemState, 2, 1) == 1)
            {
                completion.TrySetResult(result);
            }
        }

        internal void FailQueued(HidSessionFailureKind failure)
        {
            if (Interlocked.CompareExchange(ref itemState, 2, 0) == 0)
            {
                completion.TrySetResult(Failure(failure));
            }
        }

        private void CancelQueued()
        {
            if (Interlocked.CompareExchange(ref itemState, 2, 0) == 0)
            {
                completion.TrySetResult(Failure(HidSessionFailureKind.QueuedCanceled));
            }
        }
    }
}
