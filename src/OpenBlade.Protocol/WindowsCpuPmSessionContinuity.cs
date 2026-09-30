using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenBlade.Protocol;

// Supplied by the future attended lifecycle owner. This scope never discovers,
// creates or attests driver ownership; dynamic service checks remain separate.
internal sealed record CpuPmRunOwnedDriverLifetime(Guid RunId, Guid DriverLifetimeId, bool ConfirmedFreshRunOwned);
internal enum CpuPmContinuityFailure
{
    None, LifetimeUnknown, AlreadyUsed, RegistrationFailed, InitialPowerUnknown,
    PowerChanged, SuspendResume, MalformedNotification, ObservationUnavailable, SessionChanged, ClockRegression,
    ClockGap, Expired, Disposed, UnregisterFailed
}
internal sealed record CpuPmContinuityEvidence(CpuPmSessionContinuity Continuity, Guid SessionId,
    Guid DriverLifetimeId, CpuPmContinuityFailure Failure);
internal readonly record struct CpuPmContinuityReading(bool Available, bool OnAc, bool LocalActiveDefault,
    uint SessionId, ulong TickMilliseconds, ulong Unbiased100Nanoseconds);
internal readonly record struct CpuPmPowerNotification(uint Type, Guid Setting, uint DataLength, uint? Source);

internal interface ICpuPmContinuityPlatform
{
    bool TryClaimScope();
    CpuPmContinuityReading Read();
    uint RegisterPower(Action<CpuPmPowerNotification> callback, out nint handle);
    uint RegisterSuspend(Action<uint> callback, out nint handle);
    uint UnregisterPower(nint handle);
    uint UnregisterSuspend(nint handle);
}

// One-use, synchronous scope for a future separately attended adapter only.
// Nothing in the production service or existing query factory constructs it.
internal sealed partial class WindowsCpuPmSessionContinuity : IDisposable
{
    internal static readonly Guid AcDcSource = new("5D3E9A59-E9D5-4B00-A6BD-FF34FF516548");
    internal const uint PowerSettingChange = 0x8013;
    internal static readonly TimeSpan InitialPowerWaitBudget = TimeSpan.FromSeconds(1);
    private readonly object gate = new();
    private readonly ICpuPmContinuityPlatform platform;
    private readonly CpuPmRunOwnedDriverLifetime? lifetime;
    private readonly Guid sessionId = Guid.NewGuid();
    private CpuPmContinuityReading initial, previous;
    private CpuPmContinuityFailure failure;
    private nint powerHandle, suspendHandle;
    private bool initialPower, armed, disposed;
    private long initialPowerTimestamp;

    private WindowsCpuPmSessionContinuity(ICpuPmContinuityPlatform platform, CpuPmRunOwnedDriverLifetime? lifetime)
    { this.platform = platform; this.lifetime = lifetime; }

    internal static WindowsCpuPmSessionContinuity Arm(CpuPmRunOwnedDriverLifetime? lifetime) => Arm(new NativePlatform(), lifetime);

    internal static WindowsCpuPmSessionContinuity Arm(ICpuPmContinuityPlatform platform, CpuPmRunOwnedDriverLifetime? lifetime)
    {
        ArgumentNullException.ThrowIfNull(platform);
        var scope = new WindowsCpuPmSessionContinuity(platform, lifetime);
        scope.Initialize();
        return scope;
    }

    private void Initialize()
    {
        try
        {
            if (!platform.TryClaimScope()) { Interrupt(CpuPmContinuityFailure.AlreadyUsed); return; }
            if (!ValidLifetime(lifetime)) { Interrupt(CpuPmContinuityFailure.LifetimeUnknown); return; }
            initial = previous = platform.Read();
            if (!SafeReading(initial)) { Interrupt(CpuPmContinuityFailure.ObservationUnavailable); return; }
            // No gate lock across registration: Windows can call back synchronously.
            uint status = platform.RegisterSuspend(Suspended, out suspendHandle);
            if (status != 0 || suspendHandle == 0) { Interrupt(CpuPmContinuityFailure.RegistrationFailed); return; }
            status = platform.RegisterPower(PowerChanged, out powerHandle);
            if (status != 0 || powerHandle == 0) { Interrupt(CpuPmContinuityFailure.RegistrationFailed); return; }
            lock (gate)
            {
                armed = WaitForInitialPower();
            }
            _ = ReadFresh(lifetime);
        }
        catch { Interrupt(CpuPmContinuityFailure.RegistrationFailed); }
        finally
        {
            // Failed partial registration has no authority and is cleaned up now.
            bool failed;
            lock (gate) { failed = !armed || failure != CpuPmContinuityFailure.None; }
            if (failed) { ReleaseRegistrations(); }
        }
    }

    // Microsoft promises a current-value callback after registration, not that
    // it completes before registration returns. Wait only for that real callback;
    // never re-register, synthesize AC, clear a failure, or extend this deadline.
    // https://learn.microsoft.com/windows/win32/api/powersetting/nf-powersetting-powersettingregisternotification
    // Caller holds gate; Monitor.Wait releases it for the native callback.
    private bool WaitForInitialPower()
    {
        long started = Stopwatch.GetTimestamp();
        while (failure == CpuPmContinuityFailure.None && !disposed)
        {
            if (initialPower)
            {
                if (initialPowerTimestamp > started && Stopwatch.GetElapsedTime(started, initialPowerTimestamp) >= InitialPowerWaitBudget)
                { Fail(CpuPmContinuityFailure.InitialPowerUnknown); return false; }
                return true;
            }
            TimeSpan remaining = InitialPowerWaitBudget - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero) { Fail(CpuPmContinuityFailure.InitialPowerUnknown); return false; }
            _ = Monitor.Wait(gate, remaining);
        }
        return false;
    }

    internal CpuPmContinuityEvidence ReadFresh(CpuPmRunOwnedDriverLifetime? freshLifetime)
    {
        lock (gate)
        {
            if (disposed) { Fail(CpuPmContinuityFailure.Disposed); }
            if (!ValidLifetime(freshLifetime) || freshLifetime != lifetime) { Fail(CpuPmContinuityFailure.LifetimeUnknown); }
            if (failure != CpuPmContinuityFailure.None || !armed) { return Evidence(); }
        }
        CpuPmContinuityReading now;
        try { now = platform.Read(); }
        catch { Interrupt(CpuPmContinuityFailure.ObservationUnavailable); lock (gate) { return Evidence(); } }
        lock (gate)
        {
            // A callback/dispose may have invalidated the scope during the read.
            if (failure != CpuPmContinuityFailure.None || disposed) { return Evidence(); }
            if (!SafeReading(now)) { Fail(CpuPmContinuityFailure.ObservationUnavailable); }
            else if (now.SessionId != initial.SessionId) { Fail(CpuPmContinuityFailure.SessionChanged); }
            else if (now.TickMilliseconds < previous.TickMilliseconds || now.Unbiased100Nanoseconds < previous.Unbiased100Nanoseconds)
            { Fail(CpuPmContinuityFailure.ClockRegression); }
            else
            {
                ulong tick = now.TickMilliseconds - initial.TickMilliseconds;
                ulong unbiased = now.Unbiased100Nanoseconds - initial.Unbiased100Nanoseconds;
                if (tick >= 90_000) { Fail(CpuPmContinuityFailure.Expired); }
                else
                {
                    // Tick includes sleep; unbiased time excludes it. 250ms is a
                    // conservative rejection threshold above ordinary tick skew.
                    ulong tick100ns = tick * 10_000;
                    ulong gap = tick100ns >= unbiased ? tick100ns - unbiased : unbiased - tick100ns;
                    if (gap >= 2_500_000) { Fail(CpuPmContinuityFailure.ClockGap); }
                }
            }
            previous = now;
            return Evidence();
        }
    }

    private static bool ValidLifetime(CpuPmRunOwnedDriverLifetime? value) =>
        value is { ConfirmedFreshRunOwned: true } && value.RunId != Guid.Empty && value.DriverLifetimeId != Guid.Empty;
    private static bool SafeReading(CpuPmContinuityReading value) =>
        value.Available && value.OnAc && value.LocalActiveDefault && value.SessionId is not (0 or uint.MaxValue);
    private CpuPmContinuityEvidence Evidence() => new(
        armed && !disposed && failure == CpuPmContinuityFailure.None ? CpuPmSessionContinuity.ConfirmedFreshRunOwnedLifetime
            : failure == CpuPmContinuityFailure.None ? CpuPmSessionContinuity.Unknown : CpuPmSessionContinuity.Interrupted,
        sessionId, lifetime?.DriverLifetimeId ?? Guid.Empty, failure);
    private void Fail(CpuPmContinuityFailure reason)
    { if (failure == CpuPmContinuityFailure.None) { failure = reason; } Monitor.PulseAll(gate); }
    private void Interrupt(CpuPmContinuityFailure reason) { lock (gate) { Fail(reason); } }

    private void PowerChanged(CpuPmPowerNotification notification)
    {
        try
        {
            lock (gate)
            {
                if (disposed) { return; }
                if (notification.Type != PowerSettingChange || notification.Setting != AcDcSource
                    || notification.DataLength != 4 || notification.Source is null)
                { Fail(CpuPmContinuityFailure.MalformedNotification); return; }
                if (notification.Source != 0) { Fail(CpuPmContinuityFailure.PowerChanged); return; }
                if (!initialPower) { initialPowerTimestamp = Stopwatch.GetTimestamp(); }
                initialPower = true; // first current-value callback is initialization
                Monitor.PulseAll(gate);
            }
        }
        catch { /* Never unwind across a native callback boundary. */ }
    }
    private void Suspended(uint type)
    {
        try { Interrupt(type is 4 or 7 or 18 ? CpuPmContinuityFailure.SuspendResume : CpuPmContinuityFailure.MalformedNotification); }
        catch { /* Never unwind across a native callback boundary. */ }
    }

    public void Dispose()
    {
        lock (gate) { if (disposed) { return; } disposed = true; }
        ReleaseRegistrations();
        lock (gate) { Fail(CpuPmContinuityFailure.Disposed); }
    }
    private void ReleaseRegistrations()
    {
        nint power, suspend;
        lock (gate) { power = powerHandle; suspend = suspendHandle; powerHandle = suspendHandle = 0; }
        // DWORD zero means success. Attempt both once, even if the first fails.
        if (power != 0) { try { if (platform.UnregisterPower(power) != 0) { Interrupt(CpuPmContinuityFailure.UnregisterFailed); } } catch { Interrupt(CpuPmContinuityFailure.UnregisterFailed); } }
        if (suspend != 0) { try { if (platform.UnregisterSuspend(suspend) != 0) { Interrupt(CpuPmContinuityFailure.UnregisterFailed); } } catch { Interrupt(CpuPmContinuityFailure.UnregisterFailed); } }
    }

    private sealed class NativePlatform : ICpuPmContinuityPlatform
    {
        private static int claimed;
        // Microsoft does not document callback quiescence after unregister.
        // Root this sole provider/closures/delegates until process exit, with no
        // unmanaged context allocation. A second native scope is never allowed.
        private static NativePlatform? processRoot;
        private Notify? powerCallback, suspendCallback;
        public bool TryClaimScope()
        {
            if (!OperatingSystem.IsWindows() || Interlocked.CompareExchange(ref claimed, 1, 0) != 0) { return false; }
            processRoot = this;
            return true;
        }
        public CpuPmContinuityReading Read()
        {
            using var self = Process.GetCurrentProcess();
            uint console = WTSGetActiveConsoleSessionId();
            if (console == uint.MaxValue || console != self.SessionId || !GetSystemPowerStatus(out var power)) { return default; }
            nint desktop = OpenInputDesktop(0, false, 1);
            if (desktop == 0) { return default; }
            bool unlocked;
            try
            {
                var name = new StringBuilder(256);
                unlocked = GetUserObjectInformation(desktop, 2, name, 512, out _) && name.ToString() == "Default";
            }
            finally { _ = CloseDesktop(desktop); }
            ulong tick = GetTickCount64();
            bool clock = QueryUnbiasedInterruptTime(out ulong unbiased);
            return new(clock, power.AcLineStatus == 1, unlocked, console, tick, unbiased);
        }
        public uint RegisterPower(Action<CpuPmPowerNotification> callback, out nint handle)
        {
            powerCallback = (_, type, setting) =>
            {
                try
                {
                    CpuPmPowerNotification value = new(type, Guid.Empty, 0, null);
                    if (type == PowerSettingChange && setting != 0)
                    {
                        Guid guid = Marshal.PtrToStructure<Guid>(setting);
                        uint length = unchecked((uint)Marshal.ReadInt32(setting, 16));
                        value = new(type, guid, length, guid == AcDcSource && length == 4
                            ? unchecked((uint)Marshal.ReadInt32(setting, 20)) : null);
                    }
                    callback(value);
                }
                catch { try { callback(default); } catch { } }
                return 0;
            };
            var recipient = new Subscribe { Callback = Marshal.GetFunctionPointerForDelegate(powerCallback) };
            Guid settingGuid = AcDcSource;
            return PowerSettingRegisterNotification(ref settingGuid, 2, ref recipient, out handle);
        }
        public uint RegisterSuspend(Action<uint> callback, out nint handle)
        {
            suspendCallback = (_, type, _) => { try { callback(type); } catch { } return 0; };
            var recipient = new Subscribe { Callback = Marshal.GetFunctionPointerForDelegate(suspendCallback) };
            return PowerRegisterSuspendResumeNotification(2, ref recipient, out handle);
        }
        public uint UnregisterPower(nint handle) => PowerSettingUnregisterNotification(handle);
        public uint UnregisterSuspend(nint handle) => PowerUnregisterSuspendResumeNotification(handle);

        // https://learn.microsoft.com/en-us/windows/win32/api/powersetting/nf-powersetting-powersettingregisternotification
        // https://learn.microsoft.com/en-us/windows/win32/api/powerbase/nf-powerbase-powerregistersuspendresumenotification
        // https://learn.microsoft.com/en-us/windows/win32/api/powrprof/nc-powrprof-device_notify_callback_routine
        // Power*Notification return DWORD; DEVICE_NOTIFY_CALLBACK_ROUTINE returns ULONG.
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint Notify(nint context, uint type, nint setting);
        [StructLayout(LayoutKind.Sequential)] private struct Subscribe { public nint Callback; public nint Context; }
        [StructLayout(LayoutKind.Sequential)] private struct Power { public byte AcLineStatus, BatteryFlag, BatteryLifePercent, SystemStatus; public uint BatteryLifeTime, BatteryFullLifeTime; }
        [DllImport("powrprof.dll")] private static extern uint PowerSettingRegisterNotification(ref Guid setting, uint flags, ref Subscribe recipient, out nint handle);
        [DllImport("powrprof.dll")] private static extern uint PowerRegisterSuspendResumeNotification(uint flags, ref Subscribe recipient, out nint handle);
        [DllImport("powrprof.dll")] private static extern uint PowerSettingUnregisterNotification(nint handle);
        [DllImport("powrprof.dll")] private static extern uint PowerUnregisterSuspendResumeNotification(nint handle);
        [DllImport("kernel32.dll")] private static extern ulong GetTickCount64();
        [DllImport("kernel32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryUnbiasedInterruptTime(out ulong time);
        [DllImport("kernel32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetSystemPowerStatus(out Power power);
        [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
        [DllImport("user32.dll")] private static extern nint OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetUserObjectInformation(nint handle, int index, StringBuilder value, uint length, out uint needed);
        [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseDesktop(nint handle);
    }
}
