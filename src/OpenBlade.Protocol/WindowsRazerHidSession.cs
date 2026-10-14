namespace OpenBlade.Protocol;

internal readonly record struct RazerCommandSpec(
    byte ClassId,
    byte CommandId,
    ReadOnlyMemory<byte> Arguments,
    int ExpectedPayloadLength,
    Func<ReadOnlyMemory<byte>, bool>? PayloadValidator = null)
{
    internal Func<bool>? SubmissionAllowed { get; init; }

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

/// <summary>
/// Optional SET-only feature-report path for device streams whose captured
/// transport has no corresponding GET response. Normal commands must continue
/// to use <see cref="IRazerCommandSession.ExecuteAsync"/>.
/// </summary>
internal interface IRazerSetFeatureCommandSession
{
    Task<HidSessionResult<ReadOnlyMemory<byte>>> SubmitAsync(
        RazerCommandSpec command,
        Func<TransactionIdAssignment> assignTransactionId,
        TimeSpan exchangeTimeout,
        CancellationToken cancellationToken);

    Task<HidSessionResult<ReadOnlyMemory<byte>>> SubmitBatchAsync(
        IReadOnlyList<RazerCommandSpec> commands,
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

internal sealed class WindowsRazerHidSession :
    IRazerCommandSession,
    IRazerSetFeatureCommandSession
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
    private readonly ControllerExclusivityPolicy exclusivityPolicy;
    private readonly TimeProvider timeProvider;
    private readonly ValidationRunContext.ActorLease actorLease;
    private readonly TimeSpan featureReportSettleDelay;
    private readonly byte featureReportId;
    private readonly Task worker;
    private RazerCommandSessionDisposition state = RazerCommandSessionDisposition.Healthy;

    internal WindowsRazerHidSession(
        OpenedHidHandle handle,
        IHidFeatureIo io,
        IHardwareAccessGuard guard,
        TimeProvider timeProvider,
        ValidationRunContext.ActorLease actorLease,
        ControllerExclusivityPolicy exclusivityPolicy,
        TimeSpan featureReportSettleDelay = default,
        byte featureReportId = 0)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(io);
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(actorLease);
        if (featureReportSettleDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(featureReportSettleDelay));
        }

        this.handle = handle;
        this.io = io;
        this.guard = guard;
        this.exclusivityPolicy = exclusivityPolicy;
        this.timeProvider = timeProvider;
        this.actorLease = actorLease;
        this.featureReportSettleDelay = featureReportSettleDelay;
        this.featureReportId = featureReportId;
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
        CancellationToken cancellationToken) =>
        Enqueue(
            command,
            assignTransactionId,
            exchangeTimeout,
            cancellationToken,
            expectResponse: true);

    public Task<HidSessionResult<ReadOnlyMemory<byte>>> SubmitAsync(
        RazerCommandSpec command,
        Func<TransactionIdAssignment> assignTransactionId,
        TimeSpan exchangeTimeout,
        CancellationToken cancellationToken) =>
        Enqueue(
            command,
            assignTransactionId,
            exchangeTimeout,
            cancellationToken,
            expectResponse: false);

    public Task<HidSessionResult<ReadOnlyMemory<byte>>> SubmitBatchAsync(
        IReadOnlyList<RazerCommandSpec> commands,
        Func<TransactionIdAssignment> assignTransactionId,
        TimeSpan exchangeTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(assignTransactionId);
        if (commands.Count == 0)
        {
            throw new ArgumentException(
                "A SET-only command batch must contain at least one command.",
                nameof(commands));
        }

        RazerCommandSessionTimeout.Validate(exchangeTimeout);
        RazerCommandSpec[] snapshot = SnapshotBatch(commands);
        var item = new WorkItem(
            snapshot,
            assignTransactionId,
            exchangeTimeout,
            cancellationToken);
        return Enqueue(item);
    }

    private Task<HidSessionResult<ReadOnlyMemory<byte>>> Enqueue(
        RazerCommandSpec command,
        Func<TransactionIdAssignment> assignTransactionId,
        TimeSpan exchangeTimeout,
        CancellationToken cancellationToken,
        bool expectResponse)
    {
        ArgumentNullException.ThrowIfNull(assignTransactionId);
        ValidateCommand(command);
        RazerCommandSessionTimeout.Validate(exchangeTimeout);

        var snapshot = new RazerCommandSpec(
            command.ClassId,
            command.CommandId,
            command.Arguments.ToArray(),
            command.ExpectedPayloadLength,
            command.PayloadValidator)
        {
            SubmissionAllowed = command.SubmissionAllowed,
        };
        var item = new WorkItem(
            snapshot,
            assignTransactionId,
            exchangeTimeout,
            cancellationToken,
            expectResponse);

        return Enqueue(item);
    }

    private Task<HidSessionResult<ReadOnlyMemory<byte>>> Enqueue(WorkItem item)
    {
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

    private static RazerCommandSpec[] SnapshotBatch(
        IReadOnlyList<RazerCommandSpec> commands)
    {
        int argumentBytes = 0;
        for (int index = 0; index < commands.Count; index++)
        {
            RazerCommandSpec command = commands[index];
            ValidateCommand(command);
            argumentBytes = checked(argumentBytes + command.Arguments.Length);
        }

        byte[] arguments = GC.AllocateUninitializedArray<byte>(argumentBytes);
        var snapshot = new RazerCommandSpec[commands.Count];
        int offset = 0;
        for (int index = 0; index < commands.Count; index++)
        {
            RazerCommandSpec command = commands[index];
            int length = command.Arguments.Length;
            command.Arguments.Span.CopyTo(arguments.AsSpan(offset, length));
            snapshot[index] = new(
                command.ClassId,
                command.CommandId,
                arguments.AsMemory(offset, length),
                command.ExpectedPayloadLength,
                command.PayloadValidator)
            {
                SubmissionAllowed = command.SubmissionAllowed,
            };
            offset += length;
        }

        return snapshot;
    }

    private static void ValidateCommand(RazerCommandSpec command)
    {
        if (command.Arguments.Length > RazerReport.MaximumPayloadLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(command),
                "Command arguments cannot exceed 80 bytes.");
        }

        if (command.ExpectedPayloadLength is < 0 or > RazerReport.MaximumPayloadLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(command),
                "Expected payload length must be between 0 and 80 bytes.");
        }
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

                HidSessionResult<ReadOnlyMemory<byte>> result =
                    item.BatchCommands is { } batch
                        ? await ExecuteSetBatchClaimedAsync(
                            item,
                            batch).ConfigureAwait(false)
                        : await ExecuteClaimedAsync(item).ConfigureAwait(false);
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
        setBuffer[0] = featureReportId;
        RazerReport request = RazerReport.Create(
            assignment.TransactionId,
            item.Command.ClassId,
            item.Command.CommandId,
            item.Command.Arguments.Span);
        request.Encode().CopyTo(setBuffer, 1);

        if (exclusivityPolicy == ControllerExclusivityPolicy.Strict)
        {
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
        }

        cancellation = ClassifyCancellation(item.CancellationToken, deadline.Token);
        if (cancellation is not null)
        {
            return Failure(cancellation.Value);
        }

        if (SubmissionAdmissionFailure(item.Command) is { } admissionFailure)
        {
            return DiagnosticFailure(admissionFailure, HidSessionFailurePhase.Guard);
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
        catch (Exception exception)
        {
            Poison();
            return IoExceptionFailure(isSet: true, exception);
        }

        if (setResult.Termination == HidFeatureTermination.Completed &&
            setResult.Completion is not { Succeeded: true })
        {
            Poison();
            return DiagnosticFailure(
                HidSessionFailureKind.NativeIoFailure,
                IoPhase(isSet: true, result: setResult),
                setResult.Completion, nativeRetirement: setResult.NativeRetirement);
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
                    setResult.Completion, nativeRetirement: setResult.NativeRetirement)
                : Failure(cancellation.Value);
        }

        if (setResult.Termination != HidFeatureTermination.Completed)
        {
            Poison();
            return DiagnosticFailure(
                HidSessionFailureKind.NativeIoFailure,
                IoPhase(isSet: true, result: setResult),
                setResult.Completion, nativeRetirement: setResult.NativeRetirement);
        }

        cancellation = ClassifyCancellation(item.CancellationToken, deadline.Token);
        if (cancellation is not null)
        {
            Poison();
            return Failure(cancellation.Value);
        }

        if (!item.ExpectResponse)
        {
            return new(ReadOnlyMemory<byte>.Empty, null);
        }

        if (featureReportSettleDelay > TimeSpan.Zero)
        {
            using var settleCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                item.CancellationToken,
                deadline.Token);
            try
            {
                await Task.Delay(
                    featureReportSettleDelay,
                    timeProvider,
                    settleCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (settleCancellation.IsCancellationRequested)
            {
                Poison();
                return Failure(
                    ClassifyCancellation(item.CancellationToken, deadline.Token)
                    ?? HidSessionFailureKind.NativeIoFailure);
            }

            cancellation = ClassifyCancellation(item.CancellationToken, deadline.Token);
            if (cancellation is not null)
            {
                Poison();
                return Failure(cancellation.Value);
            }
        }

        byte[] getBuffer = new byte[FeatureReportLength];
        getBuffer[0] = featureReportId;
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
        catch (Exception exception)
        {
            Poison();
            return IoExceptionFailure(isSet: false, exception);
        }

        if (getResult.Termination == HidFeatureTermination.Completed &&
            !IsCompleteGetFeatureReport(
                getResult.Completion,
                getBuffer[0],
                featureReportId))
        {
            Poison();
            return DiagnosticFailure(
                HidSessionFailureKind.NativeIoFailure,
                IoPhase(isSet: false, result: getResult),
                getResult.Completion, nativeRetirement: getResult.NativeRetirement);
        }

        cancellation = ClassifyIoCancellation(getResult, item.CancellationToken, deadline.Token);
        if (cancellation is not null)
        {
            Poison();
            return getResult.WasSubmitted
                ? DiagnosticFailure(
                    cancellation.Value,
                    HidSessionFailurePhase.GetCompletion,
                    getResult.Completion, nativeRetirement: getResult.NativeRetirement)
                : Failure(cancellation.Value);
        }

        if (getResult.Termination != HidFeatureTermination.Completed)
        {
            Poison();
            return DiagnosticFailure(
                HidSessionFailureKind.NativeIoFailure,
                IoPhase(isSet: false, result: getResult),
                getResult.Completion, nativeRetirement: getResult.NativeRetirement);
        }

        try
        {
            if (getBuffer[0] != featureReportId)
            {
                throw new RazerProtocolException(
                    "Feature report ID is invalid.",
                    HidProtocolValidationFailure.ReportId);
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
                    throw new RazerProtocolException(
                        "Command payload validator failed.",
                        HidProtocolValidationFailure.PayloadValidator);
                }

                if (!valid)
                {
                    throw new RazerProtocolException(
                        "Command payload validation failed.",
                        HidProtocolValidationFailure.PayloadValidator);
                }
            }

            return new(payload.ToArray(), null);
        }
        catch (RazerProtocolException error)
        {
            Poison();
            return DiagnosticFailure(
                HidSessionFailureKind.ProtocolFailure,
                HidSessionFailurePhase.ProtocolValidation,
                getResult.Completion,
                error.ValidationFailure, nativeRetirement: getResult.NativeRetirement);
        }
    }

    private async Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteSetBatchClaimedAsync(
        WorkItem item,
        RazerCommandSpec[] commands)
    {
        using var deadline =
            new CancellationTokenSource(item.ExchangeTimeout, timeProvider);
        byte[] setBuffer = new byte[FeatureReportLength];
        setBuffer[0] = featureReportId;
        bool guardChecked = false;
        for (int index = 0; index < commands.Length; index++)
        {
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

            if (assignment
                == new TransactionIdAssignment(
                    0,
                    HidSessionFailureKind.Exhausted))
            {
                return Failure(HidSessionFailureKind.Exhausted);
            }

            if (assignment.TransactionId == 0 || assignment.Failure is not null)
            {
                Poison();
                return Failure(HidSessionFailureKind.ProtocolFailure);
            }

            HidSessionFailureKind? cancellation = ClassifyCancellation(
                item.CancellationToken,
                deadline.Token);
            if (cancellation is not null)
            {
                return Failure(cancellation.Value);
            }

            if (!guardChecked)
            {
                HidSessionResult<ReadOnlyMemory<byte>>? blocked =
                    CheckStrictExclusivity();
                if (blocked is not null)
                {
                    return blocked.Value;
                }

                guardChecked = true;
            }

            cancellation = ClassifyCancellation(
                item.CancellationToken,
                deadline.Token);
            if (cancellation is not null)
            {
                return Failure(cancellation.Value);
            }

            RazerCommandSpec command = commands[index];
            RazerReport.Encode(
                assignment.TransactionId,
                command.ClassId,
                command.CommandId,
                command.Arguments.Span,
                setBuffer.AsSpan(1, RazerReport.ReportLength));

            if (SubmissionAdmissionFailure(command) is { } admissionFailure)
            {
                if (index > 0)
                {
                    Poison();
                    return DiagnosticFailure(HidSessionFailureKind.NativeIoFailure,
                        HidSessionFailurePhase.SetCompletion);
                }
                return DiagnosticFailure(admissionFailure, HidSessionFailurePhase.Guard);
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
            catch (Exception exception)
            {
                Poison();
                return IoExceptionFailure(isSet: true, exception);
            }

            if (setResult.Termination == HidFeatureTermination.Completed
                && setResult.Completion is not { Succeeded: true })
            {
                Poison();
                return DiagnosticFailure(
                    HidSessionFailureKind.NativeIoFailure,
                    IoPhase(isSet: true, result: setResult),
                    setResult.Completion, nativeRetirement: setResult.NativeRetirement);
            }

            cancellation = ClassifyIoCancellation(
                setResult,
                item.CancellationToken,
                deadline.Token);
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
                        setResult.Completion, nativeRetirement: setResult.NativeRetirement)
                    : Failure(cancellation.Value);
            }

            if (setResult.Termination != HidFeatureTermination.Completed)
            {
                Poison();
                return DiagnosticFailure(
                    HidSessionFailureKind.NativeIoFailure,
                    IoPhase(isSet: true, result: setResult),
                    setResult.Completion, nativeRetirement: setResult.NativeRetirement);
            }

            cancellation = ClassifyCancellation(
                item.CancellationToken,
                deadline.Token);
            if (cancellation is not null)
            {
                Poison();
                return Failure(cancellation.Value);
            }
        }

        return new(ReadOnlyMemory<byte>.Empty, null);
    }

    private HidSessionResult<ReadOnlyMemory<byte>>? CheckStrictExclusivity()
    {
        if (exclusivityPolicy != ControllerExclusivityPolicy.Strict)
        {
            return null;
        }

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

        return decision.Conflicts.Count != 0
            ? DiagnosticFailure(
                HidSessionFailureKind.ConflictBlocked,
                HidSessionFailurePhase.Guard)
            : null;
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

    private static HidSessionFailureKind? SubmissionAdmissionFailure(RazerCommandSpec command)
    {
        try
        {
            return command.SubmissionAllowed?.Invoke() == false
                ? HidSessionFailureKind.ConflictBlocked
                : null;
        }
        catch
        {
            return HidSessionFailureKind.ConflictDetectionFailed;
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

    private static HidSessionResult<ReadOnlyMemory<byte>> Failure(HidSessionFailureKind failure) =>
        new(default, failure);

    private static HidSessionResult<ReadOnlyMemory<byte>> DiagnosticFailure(
        HidSessionFailureKind failure,
        HidSessionFailurePhase phase,
        NativeIoCompletion? completion = null,
        HidProtocolValidationFailure? protocolValidationFailure = null,
        int? managedHResult = null,
        Task? nativeRetirement = null) =>
        new(default, failure)
        {
            NativeRetirement = nativeRetirement,
            Diagnostic = new HidSessionFailureDiagnostic(
                phase,
                failure,
                completion?.ErrorCode ?? managedHResult,
                completion?.BytesTransferred)
            {
                ProtocolValidationFailure = protocolValidationFailure,
            },
        };

    private static HidSessionResult<ReadOnlyMemory<byte>> IoExceptionFailure(
        bool isSet,
        Exception exception)
    {
        if (exception is NativeOperationFinalizationException finalization)
        {
            return DiagnosticFailure(
                HidSessionFailureKind.NativeIoFailure,
                IoPhase(isSet, finalization.Result),
                finalization.Result.Completion,
                managedHResult: finalization.Result.Completion is null
                    ? finalization.InnerException?.HResult
                    : null,
                nativeRetirement: finalization.Result.NativeRetirement);
        }

        // Match the matrix transport: negative values in the native-error slot
        // preserve managed HRESULTs without recording arbitrary exception text.
        return DiagnosticFailure(
            HidSessionFailureKind.NativeIoFailure,
            isSet ? HidSessionFailurePhase.SetSubmit : HidSessionFailurePhase.GetSubmit,
            managedHResult: exception.HResult);
    }

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
        byte reportId,
        byte expectedReportId) =>
        completion is { Succeeded: true } native &&
        (native.BytesTransferred == FeatureReportLength ||
            (native.BytesTransferred == RazerReport.ReportLength
                && reportId == expectedReportId));

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
            CancellationToken cancellationToken,
            bool expectResponse)
        {
            Command = command;
            AssignTransactionId = assignTransactionId;
            ExchangeTimeout = exchangeTimeout;
            CancellationToken = cancellationToken;
            ExpectResponse = expectResponse;
        }

        internal WorkItem(
            RazerCommandSpec[] batchCommands,
            Func<TransactionIdAssignment> assignTransactionId,
            TimeSpan exchangeTimeout,
            CancellationToken cancellationToken)
        {
            BatchCommands = batchCommands;
            AssignTransactionId = assignTransactionId;
            ExchangeTimeout = exchangeTimeout;
            CancellationToken = cancellationToken;
        }

        internal RazerCommandSpec Command { get; }
        internal RazerCommandSpec[]? BatchCommands { get; }
        internal Func<TransactionIdAssignment> AssignTransactionId { get; }
        internal TimeSpan ExchangeTimeout { get; }
        internal CancellationToken CancellationToken { get; }
        internal bool ExpectResponse { get; }
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
