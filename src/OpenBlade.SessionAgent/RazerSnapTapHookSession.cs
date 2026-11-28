namespace OpenBlade.SessionAgent;

internal interface IRazerSnapTapHookTransport
{
    IReadOnlyList<byte[]> ReadHooks();
    void SetHook(byte[] hook);
    void ClearHook(byte[] hook);
    void SetHookEnabled(bool enabled);
    void CloseSubscription();
}

internal enum RazerSnapTapHookSessionState
{
    Created,
    Starting,
    Active,
    Stopping,
    Stopped,
    Faulted,
}

/// <summary>Owns only the exact records added for this session's fixed selector set.</summary>
/// <remarks>
/// RzCommon 1.0.0.76 keeps hooks in device+570, while subscriptions belong to
/// file objects (595C/5CD4). Cleanup 1800/1890 does not clear this global table.
/// This class never restores a table snapshot or retries an uncertain write.
/// Its caller must provide independent process-failure recovery: a hung reader
/// still consumes events (5F24), and file cleanup does not release held output.
/// </remarks>
internal sealed class RazerSnapTapHookSession
{
    private readonly object gate = new();
    private readonly IRazerSnapTapHookTransport transport;
    private readonly RazerKeyboardHookPurpose purpose;
    private readonly Action<IReadOnlyList<byte[]>>? recordOwnership;
    private readonly Action<string>? reportStage;
    private readonly Func<bool>? shouldStop;
    private readonly List<byte[]> ownedHooks = [];
    private IReadOnlyList<byte[]> baselineHooks = [];
    private RazerSnapTapHookSessionState state;
    private bool mutationPending;
    private string failureStage = "BaselineRead";
    private string failureReason = "DriverRequestFailed";
    private bool subscriptionCloseAttempted;

    internal RazerSnapTapHookSession(IRazerSnapTapHookTransport transport,
        RazerKeyboardHookPurpose purpose = RazerKeyboardHookPurpose.SnapTap,
        Action<IReadOnlyList<byte[]>>? recordOwnership = null,
        Action<string>? reportStage = null, Func<bool>? shouldStop = null)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _ = RazerSnapTapDriverProtocol.ScanCodes(purpose);
        this.purpose = purpose;
        this.recordOwnership = recordOwnership;
        this.reportStage = reportStage;
        this.shouldStop = shouldStop;
    }

    internal RazerSnapTapHookSessionState State { get { lock (gate) { return state; } } }
    internal bool FailureMayHaveSubmitted { get; private set; }
    // The subscription guard refused before submission: owned hooks are recorded
    // and the subscription was never enabled, so the guardian can reconcile and rearm.
    internal bool SubscriptionDeferred { get; private set; }
    internal bool SubscriptionClosed { get; private set; }
    internal string? FailureReason { get; private set; }
    internal OpenBlade.Contracts.DiagnosticFailure? Failure { get; private set; }
    internal IReadOnlyList<byte[]> BaselineHooks { get { lock (gate) { return Copy(baselineHooks); } } }
    internal IReadOnlyList<byte[]> OwnedHooks { get { lock (gate) { return Copy(ownedHooks); } } }

    internal bool Start()
    {
        lock (gate)
        {
            if (state == RazerSnapTapHookSessionState.Active)
            {
                return true;
            }
            if (state != RazerSnapTapHookSessionState.Created)
            {
                return false;
            }

            state = RazerSnapTapHookSessionState.Starting;
            try
            {
                using var mutation = RazerKeyboardHookMutationGate.Enter();
                SetStage("BaselineRead", "DriverRequestFailed");
                baselineHooks = ReadSnapshot();
                SetStage("BaselineValidation", "HookValidationFailed");
                if (baselineHooks.Any(record =>
                    RazerSnapTapDriverProtocol.TryDecodeHook(record, record.Length, out var hook)
                    && hook is not null && RazerSnapTapDriverProtocol.IsPurposeHook(hook, RazerKeyboardHookPurpose.Combined)))
                {
                    throw new InvalidOperationException("Preexisting keyboard feature hooks cannot be adopted by a new subscriber.");
                }
                VerifyOwnedHooks(baselineHooks, []);
                if (baselineHooks.Count > RazerSnapTapDriverProtocol.MaximumHookEnumerationCount
                    - RazerSnapTapDriverProtocol.ScanCodes(purpose).Count)
                {
                    throw new InvalidOperationException("The hook table has no room within the enumeration safety limit.");
                }
                foreach (ushort scanCode in RazerSnapTapDriverProtocol.ScanCodes(purpose))
                {
                    byte[] hook = RazerSnapTapDriverProtocol.EncodeHook(scanCode, purpose);
                    SetStage("HookMutation", "SetHookFailed");
                    if (shouldStop?.Invoke() == true)
                    {
                        // Leave setup between hooks, before any submission. The worker
                        // takes the clean-pause exit; the guardian clears the owned hooks.
                        throw new KeyboardSubmissionDeferredException();
                    }
                    mutationPending = true;
                    transport.SetHook((byte[])hook.Clone());
                    SetStage("HookReadback", "HookReadbackFailed");
                    VerifyOwnedHooks(ReadSnapshot(), [.. ownedHooks, hook]);
                    ownedHooks.Add(hook);
                    reportStage?.Invoke("OwnershipRecord");
                    recordOwnership?.Invoke(Copy(ownedHooks));
                    mutationPending = false;
                }

                // Install and verify the complete fixed set before subscribing. Hook enumeration
                // confirms records, not the file's subscription flag: Active means
                // SetHookEnabled completed successfully, not independent flag readback.
                mutationPending = true;
                SetStage("Subscription", "EnableFailed");
                transport.SetHookEnabled(true);
                SetStage("SubscriptionReadback", "HookReadbackFailed");
                VerifyOwnedHooks(ReadSnapshot(), ownedHooks);
                mutationPending = false;
                state = RazerSnapTapHookSessionState.Active;
                return true;
            }
            catch (Exception exception)
            {
                return Fail(exception);
            }
        }
    }

    internal bool Stop()
    {
        lock (gate)
        {
            if (state == RazerSnapTapHookSessionState.Stopped)
            {
                return true;
            }
            if (subscriptionCloseAttempted)
            {
                return false;
            }

            bool faulted = state == RazerSnapTapHookSessionState.Faulted;
            if (!faulted && state is not (RazerSnapTapHookSessionState.Active or RazerSnapTapHookSessionState.Created))
            {
                return false;
            }

            if (!faulted)
            {
                state = RazerSnapTapHookSessionState.Stopping;
            }
            subscriptionCloseAttempted = true;
            try
            {
                // Never toggle enable=false: aggregate transitions at 37B2 reach
                // 40BC/4240 and can release unrelated held keyboard/mouse output.
                // Closing this file removes only its subscription. A separately
                // opened guardian handle must clear/read back canonical owned
                // hooks after closure; it must not retain a duplicate of our handle.
                // Closing stays free of diagnostics callbacks.
                failureStage = "SubscriptionClose";
                failureReason = "CloseFailed";
                transport.CloseSubscription();
                SubscriptionClosed = true;
                if (faulted)
                {
                    return false;
                }
                state = RazerSnapTapHookSessionState.Stopped;
                return true;
            }
            catch (Exception exception)
            {
                if (faulted)
                {
                    FailureReason += $" Subscription closure also failed: {exception.Message}";
                    Failure = SnapTapDiagnosticFailure.FromException(
                        SnapTapDiagnosticFailure.Worker,
                        "HookSession",
                        failureStage,
                        failureReason,
                        exception,
                        FailureMayHaveSubmitted,
                        Failure);
                    return false;
                }
                return Fail(exception);
            }
        }
    }

    private IReadOnlyList<byte[]> ReadSnapshot()
    {
        IReadOnlyList<byte[]> records = transport.ReadHooks();
        if (records.Count > RazerSnapTapDriverProtocol.MaximumHookEnumerationCount)
        {
            throw new InvalidOperationException("The hook enumeration exceeded its safety limit.");
        }
        foreach (byte[] record in records)
        {
            if (record is null || !RazerSnapTapDriverProtocol.TryDecodeHook(record, record.Length, out _))
            {
                throw new InvalidOperationException("The hook enumeration contained an invalid record.");
            }
        }
        return Copy(records);
    }

    private void VerifyOwnedHooks(IReadOnlyList<byte[]> actual, IReadOnlyList<byte[]> expected)
    {
        List<byte[]> pair = [];
        List<byte[]> unrelated = [];
        foreach (byte[] record in actual)
        {
            if (!RazerSnapTapDriverProtocol.TryDecodeHook(record, record.Length, out RazerSnapTapHookSnapshot? snapshot)
                || snapshot is null)
            {
                throw new InvalidOperationException("The hook enumeration contained an invalid record.");
            }
            if (RazerSnapTapDriverProtocol.IsPurposeHook(snapshot, purpose))
            {
                pair.Add(record);
            }
            else
            {
                unrelated.Add(record);
            }
        }
        if (pair.Count != expected.Count
            || expected.Any(record => pair.Count(candidate => candidate.AsSpan().SequenceEqual(record)) != 1))
        {
            throw new InvalidOperationException("The keyboard hooks do not match this session's ownership.");
        }
        // Setup holds the shared mutation gate. Disjoint feature changes are
        // legitimate between sessions, never inside our own mutation/readback.
        foreach (byte[] record in baselineHooks)
        {
            int index = unrelated.FindIndex(candidate => candidate.AsSpan().SequenceEqual(record));
            if (index < 0)
            {
                throw new InvalidOperationException("An unrelated keyboard hook changed during setup.");
            }
            unrelated.RemoveAt(index);
        }
        if (unrelated.Count != 0)
        {
            throw new InvalidOperationException("An unrelated keyboard hook was added during setup.");
        }
    }

    // reportStage is diagnostics only and must not throw: it runs inside the mutation
    // sequence, where an exception would leave a confirmed hook unrecorded.
    private void SetStage(string stage, string reason)
    {
        failureStage = stage;
        failureReason = reason;
        reportStage?.Invoke(stage);
    }

    private bool Fail(Exception exception)
    {
        SubscriptionDeferred = exception is KeyboardSubmissionDeferredException;
        FailureMayHaveSubmitted = mutationPending && !SubscriptionDeferred;
        FailureReason = exception.Message;
        Failure = SnapTapDiagnosticFailure.FromException(
            SnapTapDiagnosticFailure.Worker,
            "HookSession",
            failureStage,
            failureReason,
            exception,
            FailureMayHaveSubmitted);
        state = RazerSnapTapHookSessionState.Faulted;
        return false;
    }

    private static IReadOnlyList<byte[]> Copy(IReadOnlyList<byte[]> records) =>
        records.Select(record => (byte[])record.Clone()).ToArray();
}
