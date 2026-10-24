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

/// <summary>Owns only the two exact A/D records added by this session.</summary>
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
    private readonly List<byte[]> ownedHooks = [];
    private IReadOnlyList<byte[]> baselineHooks = [];
    private RazerSnapTapHookSessionState state;
    private bool mutationPending;
    private string failureStage = "BaselineRead";
    private string failureReason = "DriverRequestFailed";
    private bool subscriptionCloseAttempted;

    internal RazerSnapTapHookSession(IRazerSnapTapHookTransport transport)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    internal RazerSnapTapHookSessionState State { get { lock (gate) { return state; } } }
    internal bool FailureMayHaveSubmitted { get; private set; }
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
                failureStage = "BaselineRead";
                failureReason = "DriverRequestFailed";
                baselineHooks = ReadSnapshot();
                failureStage = "BaselineValidation";
                failureReason = "HookValidationFailed";
                VerifyOwnedHooks(baselineHooks, []);
                if (baselineHooks.Count > RazerSnapTapDriverProtocol.MaximumHookEnumerationCount - 2)
                {
                    throw new InvalidOperationException("The hook table has no room within the enumeration safety limit.");
                }
                foreach (ushort scanCode in new ushort[] { 0x1E, 0x20 })
                {
                    byte[] hook = RazerSnapTapDriverProtocol.EncodeHook(scanCode);
                    mutationPending = true;
                    failureStage = "HookMutation";
                    failureReason = "SetHookFailed";
                    transport.SetHook((byte[])hook.Clone());
                    failureStage = "HookReadback";
                    failureReason = "HookReadbackFailed";
                    VerifyOwnedHooks(ReadSnapshot(), [.. ownedHooks, hook]);
                    ownedHooks.Add(hook);
                    mutationPending = false;
                }

                // Install and verify both records before subscribing. Hook enumeration
                // confirms records, not the file's subscription flag: Active means
                // SetHookEnabled completed successfully, not independent flag readback.
                mutationPending = true;
                failureStage = "Subscription";
                failureReason = "EnableFailed";
                transport.SetHookEnabled(true);
                failureStage = "SubscriptionReadback";
                failureReason = "HookReadbackFailed";
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
                failureStage = "SubscriptionClose";
                failureReason = "CloseFailed";
                transport.CloseSubscription();
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

    private static void VerifyOwnedHooks(IReadOnlyList<byte[]> actual, IReadOnlyList<byte[]> expected)
    {
        List<byte[]> pair = [];
        foreach (byte[] record in actual)
        {
            if (!RazerSnapTapDriverProtocol.TryDecodeHook(record, record.Length, out RazerSnapTapHookSnapshot? snapshot)
                || snapshot is null)
            {
                throw new InvalidOperationException("The hook enumeration contained an invalid record.");
            }
            if (RazerSnapTapDriverProtocol.IsAdHook(snapshot))
            {
                pair.Add(record);
            }
        }
        if (pair.Count != expected.Count
            || expected.Any(record => pair.Count(candidate => candidate.AsSpan().SequenceEqual(record)) != 1))
        {
            throw new InvalidOperationException("The A/D hooks do not match this session's ownership.");
        }
    }

    private bool Fail(Exception exception)
    {
        FailureMayHaveSubmitted = mutationPending;
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
