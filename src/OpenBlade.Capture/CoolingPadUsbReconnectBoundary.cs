using OpenBlade.Protocol;

namespace OpenBlade.Capture;

internal sealed record PadUsbReconnectObservation
{
    public bool InitiallyPresent { get; init; }
    public bool DisconnectAcknowledged { get; init; }
    public double ObservedAbsenceMilliseconds { get; init; }
    public bool ReconnectAcknowledged { get; init; }
    public bool Reconnected { get; init; }
    public bool PhysicalMediumConfirmed { get; init; }
    public string? Failure { get; init; }
    public bool PowerAndSamePadSamePortAreOperatorProvenanceOnly => true;
    public bool PassiveObservationEstablishesExactIdentity => false;
    public int NominalPollingIntervalMilliseconds => 250;
    public bool CanExcludeReconnectBetweenSamples => false;
    public bool Success => Failure is null && InitiallyPresent && DisconnectAcknowledged
        && ObservedAbsenceMilliseconds >= 5000 && ReconnectAcknowledged && Reconnected && PhysicalMediumConfirmed;
}

// Enumerates device paths only. It does not open a HID command session, query
// firmware, or operate services. Consecutive samples establish observed absence;
// physical power retention and same-pad/same-port continuity are operator reports.
internal sealed class CoolingPadUsbReconnectBoundary(
    ICoolingPadPhysicalPresenceSource source, TimeProvider? time = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null,
    TimeSpan? observationTimeout = null, PendingDisposalRegistry? retained = null)
{
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private readonly Func<TimeSpan, CancellationToken, Task> wait = delay ?? ((duration, token) => Task.Delay(duration, token));
    private readonly PendingDisposalRegistry pending = retained ?? PendingDisposalRegistry.Shared;
    private readonly TimeSpan sampleTimeout = observationTimeout ?? TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan PhaseTimeout = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan MinimumAbsence = TimeSpan.FromSeconds(5);
    private int started;

    internal async Task<PadUsbReconnectObservation> RunAsync(
        Func<string, CancellationToken, Task<bool>> confirm, CancellationToken token)
    {
        var result = new PadUsbReconnectObservation();
        if (Interlocked.Exchange(ref started, 1) != 0) { return result with { Failure = "AlreadyUsed" }; }
        try
        {
            if (await ObserveAsync(token).ConfigureAwait(false) != CoolingPadPhysicalPresenceObservation.Present)
            { throw new IOException("UsbNotInitiallyPresent"); }
            result = result with { InitiallyPresent = true };
            if (!await CoolingPadFanProcessContinuityCommand.ConfirmAsync(confirm, "disconnectUsbOnly", token, pending: pending).ConfigureAwait(false))
            { throw new IOException("UsbDisconnectRejected"); }
            result = result with { DisconnectAcknowledged = true };
            await WaitForAsync(CoolingPadPhysicalPresenceObservation.Absent, token).ConfigureAwait(false);
            long absentSince = clock.GetTimestamp();
            while (clock.GetElapsedTime(absentSince) < MinimumAbsence)
            {
                await PauseAsync(token).ConfigureAwait(false);
                if (await ObserveAsync(token).ConfigureAwait(false) != CoolingPadPhysicalPresenceObservation.Absent)
                { throw new IOException("UsbReconnectedBeforeFiveSeconds"); }
            }
            result = result with { ObservedAbsenceMilliseconds = clock.GetElapsedTime(absentSince).TotalMilliseconds };
            // The reconnect request cannot precede the measured absence period.
            if (!await CoolingPadFanProcessContinuityCommand.ConfirmAsync(confirm, "reconnectUsbSamePort", token, pending: pending).ConfigureAwait(false))
            { throw new IOException("UsbReconnectRejected"); }
            result = result with { ReconnectAcknowledged = true };
            await WaitForAsync(CoolingPadPhysicalPresenceObservation.Present, token).ConfigureAwait(false);
            result = result with { Reconnected = true };
            if (!await CoolingPadFanProcessContinuityCommand.ConfirmAsync(confirm, "postReconnectPhysical", token, pending: pending).ConfigureAwait(false))
            { throw new IOException("PostReconnectPhysicalRejected"); }
            token.ThrowIfCancellationRequested();
            return result with { PhysicalMediumConfirmed = true };
        }
        catch (OperationCanceledException) { return result with { Failure = "Canceled" }; }
        catch (Exception ex) { return result with { Failure = ex.GetType().Name + ": " + ex.Message }; }
    }

    private async Task WaitForAsync(CoolingPadPhysicalPresenceObservation expected, CancellationToken token)
    {
        long start = clock.GetTimestamp();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var observed = await ObserveAsync(token).ConfigureAwait(false);
            if (clock.GetElapsedTime(start) >= PhaseTimeout) { throw new TimeoutException("UsbPresenceTimeout"); }
            if (observed == expected) { return; }
            await PauseAsync(token).ConfigureAwait(false);
        }
    }
    private async Task PauseAsync(CancellationToken token)
    {
        Task paused = Task.Run(() => wait(PollInterval, token), CancellationToken.None);
        try { await paused.WaitAsync(sampleTimeout, token).ConfigureAwait(false); }
        finally { pending.Retain(paused); }
        token.ThrowIfCancellationRequested();
    }
    private async Task<CoolingPadPhysicalPresenceObservation> ObserveAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        long start = clock.GetTimestamp();
        Task<CoolingPadPhysicalPresenceObservation> sample = Task.Run(source.Observe, CancellationToken.None);
        try
        {
            var value = await sample.WaitAsync(sampleTimeout, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (clock.GetElapsedTime(start) >= sampleTimeout) { throw new TimeoutException("StaleUsbPresenceObservation"); }
            if (value is not (CoolingPadPhysicalPresenceObservation.Present or CoolingPadPhysicalPresenceObservation.Absent))
            { throw new IOException("UsbPresenceObservationFailed"); }
            return value;
        }
        finally { pending.Retain(sample); }
    }
}
