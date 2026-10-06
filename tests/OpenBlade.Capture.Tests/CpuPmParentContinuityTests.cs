using OpenBlade.Capture;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class CpuPmParentContinuityTests
{
    [Fact]
    public async Task DelayedInitialAcWithinBudgetArmsWithoutReregistering()
    {
        using var registered = new ManualResetEventSlim();
        var platform = new FakePlatform { Fault = "initial", PowerRegistered = registered.Set }; var scope = new CpuPmParentContinuity(platform);
        var pending = Task.Factory.StartNew(scope.Arm, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            Assert.True(registered.Wait(TimeSpan.FromSeconds(5))); Thread.Sleep(25);
            Assert.False(pending.IsCompleted);
            platform.Power!(new(0x8013, WindowsCpuPmSessionContinuity.AcDcSource, 4, 0));
            Assert.True(await pending.WaitAsync(TimeSpan.FromSeconds(5))); Assert.True(scope.ReadFresh());
            Assert.Equal(CpuPmContinuityFailure.None, scope.Diagnostic.Failure);
            Assert.Equal(1, platform.PowerRegistrations); Assert.Equal(1, platform.SuspendRegistrations);
        }
        finally
        {
            platform.Power?.Invoke(new(0x8013, WindowsCpuPmSessionContinuity.AcDcSource, 4, 0));
            _ = await pending.WaitAsync(TimeSpan.FromSeconds(5)); _ = scope.Release();
        }
    }
    [Theory]
    [InlineData("dc")]
    [InlineData("suspend")]
    [InlineData("malformed")]
    [InlineData("session")]
    [InlineData("gap")]
    public async Task FailureDuringInitialWaitRemainsStickyAfterValidAc(string fault)
    {
        using var registered = new ManualResetEventSlim();
        var platform = new FakePlatform { Fault = "initial", PowerRegistered = registered.Set }; var scope = new CpuPmParentContinuity(platform);
        var pending = Task.Factory.StartNew(scope.Arm, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            Assert.True(registered.Wait(TimeSpan.FromSeconds(5))); Thread.Sleep(25);
            var ac = new CpuPmPowerNotification(0x8013, WindowsCpuPmSessionContinuity.AcDcSource, 4, 0);
            switch (fault)
            {
                case "dc": platform.Power!(ac with { Source = 1 }); break;
                case "suspend": platform.Suspend!(4); break;
                case "malformed": platform.Power!(ac with { DataLength = 7 }); break;
                case "session": platform.Value = platform.Value with { SessionId = 99 }; break;
                case "gap": platform.Value = platform.Value with { TickMilliseconds = 1250 }; break;
            }
            platform.Power!(ac); Assert.False(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
            var expected = fault switch
            {
                "dc" => CpuPmContinuityFailure.PowerChanged,
                "suspend" => CpuPmContinuityFailure.SuspendResume,
                "malformed" => CpuPmContinuityFailure.MalformedNotification,
                "session" => CpuPmContinuityFailure.SessionChanged,
                _ => CpuPmContinuityFailure.ClockGap
            };
            Assert.Equal(expected, scope.Diagnostic.Failure); Assert.False(scope.ReadFresh());
            Assert.Equal(1, platform.PowerRegistrations); Assert.Equal(1, platform.SuspendRegistrations); Assert.Equal(0, platform.Unregisters);
        }
        finally
        {
            platform.Power?.Invoke(new(0x8013, WindowsCpuPmSessionContinuity.AcDcSource, 4, 0));
            _ = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
    [Fact]
    public void LateInitialPowerIsRecordedWithoutReplacingFirstFailureOrRearming()
    {
        var platform = new FakePlatform { Fault = "initial" }; var scope = new CpuPmParentContinuity(platform);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(scope.Arm()); Assert.Equal(CpuPmContinuityFailure.InitialPowerUnknown, scope.Diagnostic.Failure);
        Assert.True(elapsed.Elapsed >= WindowsCpuPmSessionContinuity.InitialPowerWaitBudget - TimeSpan.FromMilliseconds(25));
        platform.Power!(new(0x8013, WindowsCpuPmSessionContinuity.AcDcSource, 4, 0));
        var diagnostic = scope.Diagnostic;
        Assert.True(diagnostic.InitialPowerObserved); Assert.Equal(CpuPmContinuityFailure.InitialPowerUnknown, diagnostic.Failure);
        Assert.Equal(0x8013u, diagnostic.LastPowerType); Assert.True(diagnostic.LastPowerSettingMatches);
        Assert.Equal(4u, diagnostic.LastPowerDataLength); Assert.Equal(0u, diagnostic.LastPowerSource);
        Assert.Equal(0u, diagnostic.SuspendRegistrationStatus); Assert.Equal(0u, diagnostic.PowerRegistrationStatus);
        Assert.False(scope.ReadFresh()); Assert.Equal(0, platform.Unregisters);
        Assert.Equal(1, platform.PowerRegistrations); Assert.Equal(1, platform.SuspendRegistrations);
    }
    [Fact]
    public void SuspendDuringRegistrationKeepsRawTypeAndFirstCauseDespiteLaterAc()
    {
        var platform = new FakePlatform { Fault = "initial-resume" }; var scope = new CpuPmParentContinuity(platform);
        Assert.False(scope.Arm()); Assert.Equal(CpuPmContinuityFailure.SuspendResume, scope.Diagnostic.Failure);
        Assert.Equal(18u, scope.Diagnostic.LastSuspendType); Assert.True(scope.Diagnostic.InitialPowerObserved);
        platform.Power!(new(0x8013, Guid.Empty, 7, null));
        Assert.Equal(CpuPmContinuityFailure.SuspendResume, scope.Diagnostic.Failure);
        Assert.False(scope.Diagnostic.LastPowerSettingMatches); Assert.Equal(7u, scope.Diagnostic.LastPowerDataLength);
        Assert.Null(scope.Diagnostic.LastPowerSource); Assert.False(scope.ReadFresh());
    }
    [Fact]
    public void RegistrationErrorAndUnavailableReadHaveDistinctStickyDiagnostics()
    {
        var platform = new FakePlatform { Fault = "power-registration" }; var scope = new CpuPmParentContinuity(platform);
        Assert.False(scope.Arm()); Assert.Equal(CpuPmContinuityFailure.RegistrationFailed, scope.Diagnostic.Failure);
        Assert.Equal(5u, scope.Diagnostic.PowerRegistrationStatus);
        var reading = new CpuPmParentContinuity(new FakePlatform { ThrowRead = true });
        Assert.False(reading.Arm()); Assert.Equal(CpuPmContinuityFailure.ObservationUnavailable, reading.Diagnostic.Failure);
        Assert.Null(reading.Diagnostic.SuspendRegistrationStatus);
    }
    [Fact]
    public void InitialSynchronousAcCallbackArmsWithoutAnyDriverLifetime()
    {
        var platform = new FakePlatform(); var scope = new CpuPmParentContinuity(platform);
        Assert.True(scope.Arm()); Assert.True(scope.ReadFresh());
        platform.Power!(new(0x8013, WindowsCpuPmSessionContinuity.AcDcSource, 4, 0));
        Assert.True(scope.ReadFresh()); Assert.True(scope.Release());
        Assert.False(scope.ReadFresh()); Assert.False(scope.Release());
        Assert.Equal(2, platform.Unregisters);
    }
    [Theory]
    [InlineData("battery")]
    [InlineData("suspend")]
    [InlineData("resume")]
    [InlineData("malformed")]
    [InlineData("gap")]
    [InlineData("regression")]
    [InlineData("session")]
    [InlineData("locked")]
    [InlineData("unavailable")]
    [InlineData("expired")]
    [InlineData("read-error")]
    public void EveryContinuityFailurePermanentlyRefusesEvenAfterRecovery(string fault)
    {
        var platform = new FakePlatform(); var scope = new CpuPmParentContinuity(platform); Assert.True(scope.Arm());
        switch (fault)
        {
            case "battery": platform.Power!(new(0x8013, WindowsCpuPmSessionContinuity.AcDcSource, 4, 1)); break;
            case "suspend": platform.Suspend!(4); break;
            case "resume": platform.Suspend!(18); break;
            case "malformed": platform.Power!(new(0x8013, Guid.Empty, 4, 0)); break;
            case "gap": platform.Value = platform.Value with { TickMilliseconds = 1250 }; break;
            case "regression": platform.Value = platform.Value with { TickMilliseconds = 999 }; break;
            case "session": platform.Value = platform.Value with { SessionId = 8 }; break;
            case "locked": platform.Value = platform.Value with { LocalActiveDefault = false }; break;
            case "unavailable": platform.Value = platform.Value with { Available = false }; break;
            case "expired": platform.Value = platform.Value with { TickMilliseconds = 121000, Unbiased100Nanoseconds = 1210000000 }; break;
            case "read-error": platform.ThrowRead = true; break;
        }
        Assert.False(scope.ReadFresh()); platform.ThrowRead = false; platform.Value = FakePlatform.Initial;
        Assert.False(scope.ReadFresh()); Assert.Equal(0, platform.Unregisters);
    }
    [Theory]
    [InlineData("claim")]
    [InlineData("initial")]
    [InlineData("power-registration")]
    [InlineData("suspend-registration")]
    public void UnknownRegistrationNeverArmsOrAutomaticallyReleases(string fault)
    {
        var platform = new FakePlatform { Fault = fault }; var scope = new CpuPmParentContinuity(platform);
        Assert.False(scope.Arm()); Assert.False(scope.ReadFresh()); Assert.Equal(0, platform.Unregisters);
    }
    [Fact]
    public void UnregisterFailureStillAttemptsBothAndScopeCannotBeReused()
    {
        var platform = new FakePlatform(); var scope = new CpuPmParentContinuity(platform); Assert.True(scope.Arm());
        platform.Fault = "unregister"; Assert.False(scope.Release()); Assert.Equal(2, platform.Unregisters); Assert.False(scope.Arm());
    }
    internal sealed class FakePlatform : ICpuPmContinuityPlatform
    {
        internal static readonly CpuPmContinuityReading Initial = new(true, true, true, 7, 1000, 10000000);
        internal CpuPmContinuityReading Value = Initial;
        internal string Fault = "";
        internal bool ThrowRead;
        internal Action<CpuPmPowerNotification>? Power;
        internal Action<uint>? Suspend;
        internal int Unregisters;
        internal int PowerRegistrations, SuspendRegistrations;
        internal Action? PowerRegistered;
        public bool TryClaimScope() => Fault != "claim";
        public CpuPmContinuityReading Read() => ThrowRead ? throw new IOException() : Value;
        public uint RegisterPower(Action<CpuPmPowerNotification> callback, out nint handle)
        {
            PowerRegistrations++; Power = callback; handle = 2;
            if (Fault != "initial") { callback(new(0x8013, WindowsCpuPmSessionContinuity.AcDcSource, 4, 0)); }
            PowerRegistered?.Invoke();
            return Fault == "power-registration" ? 5u : 0;
        }
        public uint RegisterSuspend(Action<uint> callback, out nint handle)
        { SuspendRegistrations++; Suspend = callback; handle = 1; if (Fault == "initial-resume") { callback(18); } return Fault == "suspend-registration" ? 5u : 0; }
        public uint UnregisterPower(nint handle) { Unregisters++; return Fault == "unregister" ? 5u : 0; }
        public uint UnregisterSuspend(nint handle) { Unregisters++; return 0; }
    }
}
