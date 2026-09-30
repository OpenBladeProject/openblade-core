using System.Diagnostics;
using OpenBlade.Protocol;

namespace OpenBlade.Capture;

internal sealed record CpuPmParentContinuityDiagnostic(CpuPmContinuityFailure Failure, uint? LastSuspendType,
    uint? LastPowerType, bool? LastPowerSettingMatches, uint? LastPowerDataLength, uint? LastPowerSource,
    bool InitialPowerObserved, uint? SuspendRegistrationStatus, uint? PowerRegistrationStatus);

// Local process/power continuity only; never evidence of driver ownership.
internal sealed class CpuPmParentContinuity
{
    private readonly object gate = new();
    private readonly ICpuPmContinuityPlatform platform;
    private CpuPmContinuityReading initial, previous;
    private nint power, suspend;
    private bool initialPower, armed, released, refused;
    private long initialPowerTimestamp;
    private CpuPmContinuityFailure failure;
    private uint? lastSuspendType, lastPowerType, lastPowerLength, lastPowerSource, suspendRegistrationStatus, powerRegistrationStatus;
    private bool? lastPowerSettingMatches;
    internal CpuPmParentContinuity(ICpuPmContinuityPlatform platform) => this.platform = platform;
    internal CpuPmParentContinuityDiagnostic Diagnostic
    {
        get
        {
            lock (gate)
            {
                return new(failure, lastSuspendType, lastPowerType, lastPowerSettingMatches,
                lastPowerLength, lastPowerSource, initialPower, suspendRegistrationStatus, powerRegistrationStatus);
            }
        }
    }
    internal bool Arm()
    {
        lock (gate) { if (armed || refused || released) { return Deny(CpuPmContinuityFailure.AlreadyUsed); } }
        CpuPmContinuityFailure stageFailure = CpuPmContinuityFailure.ObservationUnavailable;
        try
        {
            if (!platform.TryClaimScope()) { return Deny(CpuPmContinuityFailure.AlreadyUsed); }
            initial = previous = platform.Read();
            if (!Safe(initial)) { return Deny(CpuPmContinuityFailure.ObservationUnavailable); }
            stageFailure = CpuPmContinuityFailure.RegistrationFailed;
            uint status = platform.RegisterSuspend(Suspended, out suspend);
            lock (gate) { suspendRegistrationStatus = status; }
            if (status != 0 || suspend == 0) { return Deny(CpuPmContinuityFailure.RegistrationFailed); }
            status = platform.RegisterPower(PowerChanged, out power);
            lock (gate) { powerRegistrationStatus = status; }
            if (status != 0 || power == 0) { return Deny(CpuPmContinuityFailure.RegistrationFailed); }
            lock (gate)
            {
                if (!WaitForInitialPower()) { return false; }
                armed = true;
            }
            return ReadFresh();
        }
        catch { return Deny(stageFailure); }
        // Failed partial registration remains rooted until process exit. An
        // initialized owner's caller releases only after verified cleanup.
        // No unregister is triggered by cancellation or error.
    }
    // Same single registration/current-value callback contract as the child
    // scope. This bounded wait never supplies driver-lifetime evidence.
    // Caller holds gate; callbacks can enter while Monitor.Wait releases it.
    private bool WaitForInitialPower()
    {
        long started = Stopwatch.GetTimestamp();
        while (!refused && !released)
        {
            if (initialPower)
            {
                if (initialPowerTimestamp > started && Stopwatch.GetElapsedTime(started, initialPowerTimestamp) >= WindowsCpuPmSessionContinuity.InitialPowerWaitBudget)
                { return Deny(CpuPmContinuityFailure.InitialPowerUnknown); }
                return true;
            }
            TimeSpan remaining = WindowsCpuPmSessionContinuity.InitialPowerWaitBudget - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero) { return Deny(CpuPmContinuityFailure.InitialPowerUnknown); }
            _ = Monitor.Wait(gate, remaining);
        }
        return false;
    }
    internal bool ReadFresh()
    {
        lock (gate) { if (!armed || released || refused) { return false; } }
        CpuPmContinuityReading value;
        try { value = platform.Read(); } catch { return Deny(CpuPmContinuityFailure.ObservationUnavailable); }
        lock (gate)
        {
            if (!armed || released || refused) { return false; }
            if (!Safe(value)) { return Deny(CpuPmContinuityFailure.ObservationUnavailable); }
            if (value.SessionId != initial.SessionId) { return Deny(CpuPmContinuityFailure.SessionChanged); }
            if (value.TickMilliseconds < previous.TickMilliseconds || value.Unbiased100Nanoseconds < previous.Unbiased100Nanoseconds)
            { return Deny(CpuPmContinuityFailure.ClockRegression); }
            ulong elapsed = value.TickMilliseconds - initial.TickMilliseconds;
            ulong unbiased = value.Unbiased100Nanoseconds - initial.Unbiased100Nanoseconds;
            if (elapsed >= 120_000) { return Deny(CpuPmContinuityFailure.Expired); }
            ulong ticks = elapsed * 10_000;
            ulong gap = ticks >= unbiased ? ticks - unbiased : unbiased - ticks;
            if (gap >= 2_500_000) { return Deny(CpuPmContinuityFailure.ClockGap); }
            previous = value; return true;
        }
    }
    private static bool Safe(CpuPmContinuityReading value) => value.Available && value.OnAc
        && value.LocalActiveDefault && value.SessionId is not (0 or uint.MaxValue);
    private bool Deny(CpuPmContinuityFailure reason)
    { lock (gate) { if (failure == CpuPmContinuityFailure.None) { failure = reason; } refused = true; Monitor.PulseAll(gate); return false; } }
    private void PowerChanged(CpuPmPowerNotification value)
    {
        try
        {
            lock (gate)
            {
                lastPowerType = value.Type; lastPowerSettingMatches = value.Setting == WindowsCpuPmSessionContinuity.AcDcSource;
                lastPowerLength = value.DataLength; lastPowerSource = value.Source;
                if (released) { return; }
                if (value.Type != WindowsCpuPmSessionContinuity.PowerSettingChange
                    || value.Setting != WindowsCpuPmSessionContinuity.AcDcSource || value.DataLength != 4 || value.Source is null)
                { _ = Deny(CpuPmContinuityFailure.MalformedNotification); return; }
                if (value.Source != 0) { _ = Deny(CpuPmContinuityFailure.PowerChanged); return; }
                if (!initialPower) { initialPowerTimestamp = Stopwatch.GetTimestamp(); }
                initialPower = true;
                Monitor.PulseAll(gate);
            }
        }
        catch { _ = Deny(CpuPmContinuityFailure.MalformedNotification); }
    }
    private void Suspended(uint type)
    {
        try
        {
            lock (gate)
            {
                lastSuspendType = type; _ = Deny(type is 4 or 7 or 18
                ? CpuPmContinuityFailure.SuspendResume : CpuPmContinuityFailure.MalformedNotification);
            }
        }
        catch { /* Never unwind across a native callback. */ }
    }
    internal bool Release()
    {
        nint oldPower, oldSuspend;
        lock (gate)
        {
            if (released) { return false; }
            released = true; refused = true;
            Monitor.PulseAll(gate);
            oldPower = power; oldSuspend = suspend; power = suspend = 0;
        }
        bool success = true;
        if (oldPower != 0) { try { success &= platform.UnregisterPower(oldPower) == 0; } catch { success = false; } }
        if (oldSuspend != 0) { try { success &= platform.UnregisterSuspend(oldSuspend) == 0; } catch { success = false; } }
        _ = Deny(success ? CpuPmContinuityFailure.Disposed : CpuPmContinuityFailure.UnregisterFailed);
        return success;
    }
}
