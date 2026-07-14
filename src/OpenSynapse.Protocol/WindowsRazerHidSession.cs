namespace OpenSynapse.Protocol;

internal readonly record struct RazerCommandSpec(
    byte ClassId,
    byte CommandId,
    ReadOnlyMemory<byte> Arguments,
    int ExpectedPayloadLength,
    Func<ReadOnlyMemory<byte>, bool>? PayloadValidator = null)
{
    internal RazerCommandSpec(
        byte classId,
        byte commandId,
        byte[] arguments,
        int expectedPayloadLength,
        Func<ReadOnlyMemory<byte>, bool>? payloadValidator = null)
        : this(
            classId,
            commandId,
            (ReadOnlyMemory<byte>)arguments,
            expectedPayloadLength,
            payloadValidator)
    {
    }
}

internal readonly record struct TransactionIdAssignment(
    byte TransactionId,
    HidSessionFailureKind? Failure)
{
    internal static TransactionIdAssignment Valid(byte transactionId)
    {
        if (transactionId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(transactionId));
        }

        return new(transactionId, null);
    }

    internal static TransactionIdAssignment Exhausted() =>
        new(0, HidSessionFailureKind.Exhausted);
}

internal enum RazerCommandSessionDisposition
{
    Healthy,
    RecoveryRequired,
    Disposing,
    FullyDisposed,
}

internal interface IRazerCommandSession : IAsyncDisposable
{
    RazerCommandSessionDisposition Disposition { get; }

    Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(
        RazerCommandSpec command,
        Func<TransactionIdAssignment> assignTransactionId,
        TimeSpan exchangeTimeout,
        CancellationToken cancellationToken);
}

internal static class RazerCommandSessionTimeout
{
    private static readonly TimeSpan MaximumExchangeTimeout =
        TimeSpan.FromMilliseconds(uint.MaxValue - 1d);

    internal static void Validate(TimeSpan exchangeTimeout)
    {
        if (exchangeTimeout <= TimeSpan.Zero ||
            exchangeTimeout == Timeout.InfiniteTimeSpan ||
            exchangeTimeout > MaximumExchangeTimeout)
        {
            throw new ArgumentOutOfRangeException(
                nameof(exchangeTimeout),
                $"Exchange timeout must be positive and no greater than {MaximumExchangeTimeout.TotalMilliseconds:0} milliseconds.");
        }
    }
}

internal sealed class WindowsRazerHidSession : IRazerCommandSession
{
    private const uint IoctlHidSetFeature = 0x000B0191;
    private const uint IoctlHidGetFeature = 0x000B0192;
    private const int FeatureReportLength = RazerReport.ReportLength + 1;
    private readonly object sync = new();
    private readonly Queue<WorkItem> queue = new();
    private readonly SemaphoreSlim queued = new(0);
    private readonly OpenedHidHandle handle;
    private readonly IHidFeatureIo io;
    private readonly IHardwareAccessGuard guard;
    private readonly TimeProvider timeProvider;
    private readonly ValidationRunContext.ActorLease actorLease;
    private readonly Task worker;
    private RazerCommandSessionDisposition state = RazerCommandSessionDisposition.Healthy;

    internal WindowsRazerHidSession(
        OpenedHidHandle handle,
        IHidFeatureIo io,
        IHardwareAccessGuard guard,
        TimeProvider timeProvider,
        ValidationRunContext.ActorLease actorLease)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(io);
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(actorLease);

        this.handle = handle;
        this.io = io;
        this.guard = guard;
        this.timeProvider = timeProvider;
        this.actorLease = actorLease;
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

    public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(
        RazerCommandSpec command,
        Func<TransactionIdAssignment> assignTransactionId,
        TimeSpan exchangeTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(assignTransactionId);
        if (command.Arguments.Length > RazerReport.MaximumPayloadLength)
        {
            throw new ArgumentOutOfRangeException(nameof(command), "Command arguments cannot exceed 80 bytes.");
        }

        if (command.ExpectedPayloadLength is < 0 or > RazerReport.MaximumPayloadLength)
        {
            throw new ArgumentOutOfRangeException(nameof(command), "Expected payload length must be between 0 and 80 bytes.");
        }

        RazerCommandSessionTimeout.Validate(exchangeTimeout);

        var snapshot = new RazerCommandSpec(
            command.ClassId,
            command.CommandId,
            command.Arguments.ToArray(),
            command.ExpectedPayloadLength,
            command.PayloadValidator);
        var item = new WorkItem(snapshot, assignTransactionId, exchangeTimeout, cancellationToken);

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
            if (state is RazerCommandSessionDisposition.Healthy or RazerCommandSessionDisposition.RecoveryRequired)
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

                HidSessionResult<ReadOnlyMemory<byte>> result = await ExecuteClaimedAsync(item).ConfigureAwait(false);
                item.Complete(result);

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
                try
                {
                    actorLease.Dispose();
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
    }

    private async Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteClaimedAsync(WorkItem item)
    {
        using var deadline = new CancellationTokenSource(item.ExchangeTimeout, timeProvider);
        TransactionIdAssignment assignment;
        try
        {
            assignment = item.AssignTransactionId();
        }
        catch
        {
            Poison();
            return Failure(HidSessionFailureKind.ProtocolFailure);
        }

        if (assignment == new TransactionIdAssignment(0, HidSessionFailureKind.Exhausted))
        {
            return Failure(HidSessionFailureKind.Exhausted);
        }

        if (assignment.TransactionId == 0 || assignment.Failure is not null)
        {
            Poison();
            return Failure(HidSessionFailureKind.ProtocolFailure);
        }

        HidSessionFailureKind? cancellation = ClassifyCancellation(item.CancellationToken, deadline.Token);
        if (cancellation is not null)
        {
            return Failure(cancellation.Value);
        }

        byte[] setBuffer = new byte[FeatureReportLength];
        RazerReport request = RazerReport.Create(
            assignment.TransactionId,
            item.Command.ClassId,
            item.Command.CommandId,
            item.Command.Arguments.Span);
        request.Encode().CopyTo(setBuffer, 1);

        HardwareAccessDecision decision;
        try
        {
            decision = guard.Check();
        }
        catch
        {
            return DiagnosticFailure(
                HidSessionFailureKind.ConflictDetectionFailed,
                HidSessionFailurePhase.Guard);
        }

        if (decision.DetectionFailure is not null)
        {
            return DiagnosticFailure(
                HidSessionFailureKind.ConflictDetectionFailed,
                HidSessionFailurePhase.Guard);
        }

        if (decision.Conflicts.Count != 0)
        {
            return DiagnosticFailure(
                HidSessionFailureKind.ConflictBlocked,
                HidSessionFailurePhase.Guard);
        }

        cancellation = ClassifyCancellation(item.CancellationToken, deadline.Token);
        if (cancellation is not null)
        {
            return Failure(cancellation.Value);
        }

        HidFeatureIoResult setResult;
        try
        {
            setResult = await io.ExecuteAsync(
                handle,
                IoctlHidSetFeature,
                setBuffer,
                item.CancellationToken,
                deadline.Token).ConfigureAwait(false);
        }
        catch
        {
            Poison();
            return DiagnosticFailure(
                HidSessionFailureKind.NativeIoFailure,
                HidSessionFailurePhase.SetSubmit);
        }

        if (setResult.Termination == HidFeatureTermination.Completed &&
            setResult.Completion is not { Succeeded: true })
        {
            Poison();
            return DiagnosticFailure(
                HidSessionFailureKind.NativeIoFailure,
                IoPhase(isSet: true, result: setResult),
                setResult.Completion);
        }

        cancellation = ClassifyIoCancellation(setResult, item.CancellationToken, deadline.Token);
        if (cancellation is not null)
        {
            if (setResult.WasSubmitted)
            {
                Poison();
            }

            return setResult.WasSubmitted
                ? DiagnosticFailure(
                    cancellation.Value,
                    HidSessionFailurePhase.SetCompletion,
                    setResult.Completion)
                : Failure(cancellation.Value);
        }

        if (setResult.Termination != HidFeatureTermination.Completed)
        {
            Poison();
            return DiagnosticFailure(
                HidSessionFailureKind.NativeIoFailure,
                IoPhase(isSet: true, result: setResult),
                setResult.Completion);
        }

        cancellation = ClassifyCancellation(item.CancellationToken, deadline.Token);
        if (cancellation is not null)
        {
            Poison();
            return Failure(cancellation.Value);
        }

        byte[] getBuffer = new byte[FeatureReportLength];
        HidFeatureIoResult getResult;
        try
        {
            getResult = await io.ExecuteAsync(
                handle,
                IoctlHidGetFeature,
                getBuffer,
                item.CancellationToken,
                deadline.Token).ConfigureAwait(false);
        }
        catch
        {
            Poison();
            return DiagnosticFailure(
                HidSessionFailureKind.NativeIoFailure,
                HidSessionFailurePhase.GetSubmit);
        }

        if (getResult.Termination == HidFeatureTermination.Completed &&
            !IsCompleteGetFeatureReport(getResult.Completion, getBuffer[0]))
        {
            Poison();
            return DiagnosticFailure(
                HidSessionFailureKind.NativeIoFailure,
                IoPhase(isSet: false, result: getResult),
                getResult.Completion);
        }

        cancellation = ClassifyIoCancellation(getResult, item.CancellationToken, deadline.Token);
        if (cancellation is not null)
        {
            Poison();
            return getResult.WasSubmitted
                ? DiagnosticFailure(
                    cancellation.Value,
                    HidSessionFailurePhase.GetCompletion,
                    getResult.Completion)
                : Failure(cancellation.Value);
        }

        if (getResult.Termination != HidFeatureTermination.Completed)
        {
            Poison();
            return DiagnosticFailure(
                HidSessionFailureKind.NativeIoFailure,
                IoPhase(isSet: false, result: getResult),
                getResult.Completion);
        }

        try
        {
            if (getBuffer[0] != 0)
            {
                throw new RazerProtocolException("Feature report ID is invalid.");
            }

            ReadOnlyMemory<byte> payload = request.ValidateResponse(
                getBuffer.AsSpan(1, RazerReport.ReportLength),
                item.Command.ExpectedPayloadLength);
            if (item.Command.PayloadValidator is not null)
            {
                bool valid;
                try
                {
                    valid = item.Command.PayloadValidator(payload);
                }
                catch
                {
                    throw new RazerProtocolException("Command payload validator failed.");
                }

                if (!valid)
                {
                    throw new RazerProtocolException("Command payload validation failed.");
                }
            }

            return new(payload.ToArray(), null);
        }
        catch (RazerProtocolException)
        {
            Poison();
            return DiagnosticFailure(
                HidSessionFailureKind.ProtocolFailure,
                HidSessionFailurePhase.ProtocolValidation,
                getResult.Completion);
        }
    }

    private static HidSessionFailureKind? ClassifyIoCancellation(
        HidFeatureIoResult result,
        CancellationToken caller,
        CancellationToken deadline)
    {
        if (caller.IsCancellationRequested || result.Termination == HidFeatureTermination.CallerCanceled)
        {
            return HidSessionFailureKind.Canceled;
        }

        if (deadline.IsCancellationRequested || result.Termination == HidFeatureTermination.DeadlineExpired)
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

    private static HidSessionResult<ReadOnlyMemory<byte>> Failure(HidSessionFailureKind failure) =>
        new(default, failure);

    private static HidSessionResult<ReadOnlyMemory<byte>> DiagnosticFailure(
        HidSessionFailureKind failure,
        HidSessionFailurePhase phase,
        NativeIoCompletion? completion = null) =>
        new(default, failure)
        {
            Diagnostic = new HidSessionFailureDiagnostic(
                phase,
                failure,
                completion?.ErrorCode,
                completion?.BytesTransferred),
        };

    private static HidSessionFailurePhase IoPhase(
        bool isSet,
        HidFeatureIoResult result) => (isSet, result.WasSubmitted) switch
        {
            (true, false) => HidSessionFailurePhase.SetSubmit,
            (true, true) => HidSessionFailurePhase.SetCompletion,
            (false, false) => HidSessionFailurePhase.GetSubmit,
            _ => HidSessionFailurePhase.GetCompletion,
        };

    private static bool IsCompleteGetFeatureReport(
        NativeIoCompletion? completion,
        byte reportId) =>
        completion is { Succeeded: true } native &&
        (native.BytesTransferred == FeatureReportLength ||
            (native.BytesTransferred == RazerReport.ReportLength && reportId == 0));

    private sealed class WorkItem
    {
        private readonly TaskCompletionSource<HidSessionResult<ReadOnlyMemory<byte>>> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenRegistration cancellationRegistration;
        private int itemState;

        internal WorkItem(
            RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId,
            TimeSpan exchangeTimeout,
            CancellationToken cancellationToken)
        {
            Command = command;
            AssignTransactionId = assignTransactionId;
            ExchangeTimeout = exchangeTimeout;
            CancellationToken = cancellationToken;
        }

        internal RazerCommandSpec Command { get; }
        internal Func<TransactionIdAssignment> AssignTransactionId { get; }
        internal TimeSpan ExchangeTimeout { get; }
        internal CancellationToken CancellationToken { get; }
        internal Task<HidSessionResult<ReadOnlyMemory<byte>>> Task => completion.Task;

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

        internal void Complete(HidSessionResult<ReadOnlyMemory<byte>> result)
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
