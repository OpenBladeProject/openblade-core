using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class WindowsCpuPmSessionContinuityTests
{
    private static CpuPmRunOwnedDriverLifetime Lifetime() => new(Guid.NewGuid(), Guid.NewGuid(), true);

    [Fact]
    public async Task DelayedActualInitialAcIsAwaitedWithExactlyOneRegistration()
    {
        using var registered = new ManualResetEventSlim();
        var fake = new Fake { InitialPower = false, PowerRegistered = registered.Set }; var lifetime = Lifetime();
        var pending = Task.Factory.StartNew(() => WindowsCpuPmSessionContinuity.Arm(fake, lifetime), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            Assert.True(registered.Wait(TimeSpan.FromSeconds(5))); Thread.Sleep(25);
            Assert.False(pending.IsCompleted); fake.Power!(Ac());
            using var scope = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(CpuPmContinuityFailure.None, scope.ReadFresh(lifetime).Failure);
            Assert.Equal(new[] { "suspend", "power" }, fake.Registered);
        }
        finally { fake.Power?.Invoke(Ac()); using var final = await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Theory]
    [InlineData("dc")]
    [InlineData("suspend")]
    [InlineData("malformed")]
    [InlineData("session")]
    [InlineData("gap")]
    public async Task FailureWhileWaitingCannotBeRepairedByInitialAc(string fault)
    {
        using var registered = new ManualResetEventSlim();
        var fake = new Fake { InitialPower = false, PowerRegistered = registered.Set }; var lifetime = Lifetime();
        var pending = Task.Factory.StartNew(() => WindowsCpuPmSessionContinuity.Arm(fake, lifetime), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            Assert.True(registered.Wait(TimeSpan.FromSeconds(5))); Thread.Sleep(25);
            switch (fault)
            {
                case "dc": fake.Power!(Ac() with { Source = 1 }); break;
                case "suspend": fake.Suspend!(18); break;
                case "malformed": fake.Power!(Ac() with { DataLength = 3 }); break;
                case "session": fake.Reading = fake.Reading with { SessionId = 99 }; break;
                case "gap": fake.Reading = fake.Reading with { TickMilliseconds = 1250 }; break;
            }
            fake.Power!(Ac()); using var scope = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            var expected = fault switch
            {
                "dc" => CpuPmContinuityFailure.PowerChanged,
                "suspend" => CpuPmContinuityFailure.SuspendResume,
                "malformed" => CpuPmContinuityFailure.MalformedNotification,
                "session" => CpuPmContinuityFailure.SessionChanged,
                _ => CpuPmContinuityFailure.ClockGap
            };
            Assert.Equal(expected, scope.ReadFresh(lifetime).Failure); Assert.Equal(2, fake.Registered.Count); Assert.Equal(2, fake.Unregistered.Count);
        }
        finally { fake.Power?.Invoke(Ac()); using var final = await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public void SynchronousInitialAcIsInitializationAndScopeIdentityIsStable()
    {
        var fake = new Fake(); var lifetime = Lifetime();
        using var scope = WindowsCpuPmSessionContinuity.Arm(fake, lifetime);
        var first = scope.ReadFresh(lifetime);
        Assert.Equal(CpuPmSessionContinuity.ConfirmedFreshRunOwnedLifetime, first.Continuity);
        Assert.NotEqual(Guid.Empty, first.SessionId);
        Assert.Equal(lifetime.DriverLifetimeId, first.DriverLifetimeId);
        fake.Power!(Ac());
        Assert.Equal(first, scope.ReadFresh(lifetime));
        Assert.Equal(new[] { "suspend", "power" }, fake.Registered);
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(uint.MaxValue)]
    public void AnyNonAcPowerIrreversiblyInterruptsEvenIfAcReturns(uint source)
    {
        var fake = new Fake(); var lifetime = Lifetime();
        using var scope = WindowsCpuPmSessionContinuity.Arm(fake, lifetime);
        fake.Power!(Ac() with { Source = source }); fake.Power(Ac());
        Assert.Equal(CpuPmContinuityFailure.PowerChanged, scope.ReadFresh(lifetime).Failure);
    }

    [Theory]
    [InlineData(4u)]
    [InlineData(7u)]
    [InlineData(18u)]
    public void EverySuspendResumeEventInterrupts(uint type)
    {
        var fake = new Fake(); var lifetime = Lifetime();
        using var scope = WindowsCpuPmSessionContinuity.Arm(fake, lifetime);
        fake.Suspend!(type);
        Assert.Equal(CpuPmContinuityFailure.SuspendResume, scope.ReadFresh(lifetime).Failure);
    }

    [Fact]
    public void SuspendDuringRegistrationCannotBeLost()
    {
        var fake = new Fake { DuringSuspendRegistration = callback => callback(4) }; var lifetime = Lifetime();
        using var scope = WindowsCpuPmSessionContinuity.Arm(fake, lifetime);
        Assert.Equal(CpuPmContinuityFailure.SuspendResume, scope.ReadFresh(lifetime).Failure);
        Assert.Equal(2, fake.Unregistered.Count);
    }

    [Fact]
    public void MissingInitialNotificationCannotBeRepairedByALateOne()
    {
        var fake = new Fake { InitialPower = false }; var lifetime = Lifetime();
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        using var scope = WindowsCpuPmSessionContinuity.Arm(fake, lifetime);
        Assert.True(elapsed.Elapsed >= WindowsCpuPmSessionContinuity.InitialPowerWaitBudget - TimeSpan.FromMilliseconds(25));
        fake.Power!(Ac());
        Assert.Equal(CpuPmContinuityFailure.InitialPowerUnknown, scope.ReadFresh(lifetime).Failure);
        Assert.Equal(2, fake.Unregistered.Count);
        Assert.Equal(2, fake.Registered.Count);
    }

    [Fact]
    public void DcThenAcDuringRegistrationStillRefuses()
    {
        var fake = new Fake { DuringPowerRegistration = callback => { callback(Ac() with { Source = 1 }); callback(Ac()); } };
        var lifetime = Lifetime(); using var scope = WindowsCpuPmSessionContinuity.Arm(fake, lifetime);
        Assert.Equal(CpuPmContinuityFailure.PowerChanged, scope.ReadFresh(lifetime).Failure);
    }

    [Theory]
    [InlineData("type")]
    [InlineData("guid")]
    [InlineData("length")]
    [InlineData("missing")]
    public void MalformedPowerPayloadNeverThrowsAndPermanentlyRefuses(string fault)
    {
        var fake = new Fake(); var lifetime = Lifetime(); using var scope = WindowsCpuPmSessionContinuity.Arm(fake, lifetime);
        var value = fault switch
        {
            "type" => Ac() with { Type = 0 },
            "guid" => Ac() with { Setting = Guid.Empty },
            "length" => Ac() with { DataLength = 3 },
            _ => Ac() with { Source = null }
        };
        Assert.Null(Record.Exception(() => fake.Power!(value)));
        Assert.Equal(CpuPmContinuityFailure.MalformedNotification, scope.ReadFresh(lifetime).Failure);
    }

    [Theory]
    [InlineData(249u, false)]
    [InlineData(250u, true)]
    [InlineData(251u, true)]
    public void MissedSleepClockGapHasConservativeBoundary(uint gapMs, bool denied)
    {
        var fake = new Fake(); var lifetime = Lifetime(); using var scope = WindowsCpuPmSessionContinuity.Arm(fake, lifetime);
        fake.Reading = fake.Reading with { TickMilliseconds = 2_000 + gapMs, Unbiased100Nanoseconds = 20_000_000 };
        Assert.Equal(denied ? CpuPmContinuityFailure.ClockGap : CpuPmContinuityFailure.None, scope.ReadFresh(lifetime).Failure);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EitherClockRegressingRefuses(bool tick)
    {
        var fake = new Fake(); var lifetime = Lifetime(); using var scope = WindowsCpuPmSessionContinuity.Arm(fake, lifetime);
        fake.Reading = tick ? fake.Reading with { TickMilliseconds = 999 } : fake.Reading with { Unbiased100Nanoseconds = 9_999_999 };
        Assert.Equal(CpuPmContinuityFailure.ClockRegression, scope.ReadFresh(lifetime).Failure);
    }

    [Fact]
    public void ReverseClockDivergenceAndNinetySecondExpirationRefuse()
    {
        var lifetime = Lifetime(); var fake = new Fake(); using var scope = WindowsCpuPmSessionContinuity.Arm(fake, lifetime);
        fake.Reading = fake.Reading with { Unbiased100Nanoseconds = 12_500_000 };
        Assert.Equal(CpuPmContinuityFailure.ClockGap, scope.ReadFresh(lifetime).Failure);
        var expiring = new Fake(); using var other = WindowsCpuPmSessionContinuity.Arm(expiring, lifetime);
        expiring.Reading = expiring.Reading with { TickMilliseconds = 91_000, Unbiased100Nanoseconds = 910_000_000 };
        Assert.Equal(CpuPmContinuityFailure.Expired, other.ReadFresh(lifetime).Failure);
    }

    [Theory]
    [InlineData("read")]
    [InlineData("ac")]
    [InlineData("desktop")]
    [InlineData("session")]
    public void FreshReadMustRetainPowerLocalDefaultSessionAndAvailability(string fault)
    {
        var fake = new Fake(); var lifetime = Lifetime(); using var scope = WindowsCpuPmSessionContinuity.Arm(fake, lifetime);
        fake.Reading = fault switch
        {
            "read" => fake.Reading with { Available = false },
            "ac" => fake.Reading with { OnAc = false },
            "desktop" => fake.Reading with { LocalActiveDefault = false },
            _ => fake.Reading with { SessionId = 2 }
        };
        Assert.Equal(fault == "session" ? CpuPmContinuityFailure.SessionChanged : CpuPmContinuityFailure.ObservationUnavailable, scope.ReadFresh(lifetime).Failure);
        fake.Reading = new(true, true, true, 1, 1000, 10_000_000);
        Assert.Equal(CpuPmSessionContinuity.Interrupted, scope.ReadFresh(lifetime).Continuity);
    }

    [Fact]
    public void CallbackDuringReadWinsOverFreshSuccessfulReading()
    {
        var fake = new Fake(); var lifetime = Lifetime(); using var scope = WindowsCpuPmSessionContinuity.Arm(fake, lifetime);
        fake.DuringRead = () => fake.Suspend!(18);
        Assert.Equal(CpuPmContinuityFailure.SuspendResume, scope.ReadFresh(lifetime).Failure);
    }

    [Fact]
    public void UnknownOrChangedRunOwnedLifetimeHasNoAuthority()
    {
        var fake = new Fake(); using var unknown = WindowsCpuPmSessionContinuity.Arm(fake, null);
        Assert.Empty(fake.Registered); Assert.Equal(CpuPmContinuityFailure.LifetimeUnknown, unknown.ReadFresh(null).Failure);
        var lifetime = Lifetime(); using var scope = WindowsCpuPmSessionContinuity.Arm(new Fake(), lifetime);
        Assert.Equal(CpuPmContinuityFailure.LifetimeUnknown, scope.ReadFresh(lifetime with { DriverLifetimeId = Guid.NewGuid() }).Failure);
    }

    [Theory]
    [InlineData("suspend")]
    [InlineData("power")]
    [InlineData("zero-handle")]
    [InlineData("throw")]
    public void FailedPartialRegistrationIsCleanedOnceAndNeverAdmitted(string fault)
    {
        var fake = new Fake { RegistrationFault = fault }; var lifetime = Lifetime();
        var scope = WindowsCpuPmSessionContinuity.Arm(fake, lifetime);
        Assert.Equal(CpuPmContinuityFailure.RegistrationFailed, scope.ReadFresh(lifetime).Failure);
        int count = fake.Unregistered.Count; scope.Dispose(); scope.Dispose();
        Assert.Equal(count, fake.Unregistered.Count);
        Assert.Equal(fault == "suspend" || fault == "zero-handle" ? 1 : 2, count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnregisterDwordFailureOrExceptionStillAttemptsBothAndRetainsFailure(bool throws)
    {
        var fake = new Fake { UnregisterThrows = throws, UnregisterStatus = 5 }; var lifetime = Lifetime();
        var scope = WindowsCpuPmSessionContinuity.Arm(fake, lifetime); scope.Dispose(); scope.Dispose();
        Assert.Equal(new[] { "power", "suspend" }, fake.Unregistered);
        Assert.Equal(CpuPmContinuityFailure.UnregisterFailed, scope.ReadFresh(lifetime).Failure);
        Assert.Null(Record.Exception(() => { fake.Power!(Ac()); fake.Suspend!(18); }));
    }

    [Fact]
    public void DisposedAndConsumedProviderCannotRearm()
    {
        var fake = new Fake(); var lifetime = Lifetime(); var scope = WindowsCpuPmSessionContinuity.Arm(fake, lifetime);
        scope.Dispose(); Assert.Equal(CpuPmContinuityFailure.Disposed, scope.ReadFresh(lifetime).Failure);
        using var again = WindowsCpuPmSessionContinuity.Arm(fake, lifetime);
        Assert.Equal(CpuPmContinuityFailure.AlreadyUsed, again.ReadFresh(lifetime).Failure);
        Assert.Equal(2, fake.Registered.Count);
    }

    [Fact]
    public void ReadExceptionFailsClosedWithoutEscaping()
    {
        var fake = new Fake(); var lifetime = Lifetime(); using var scope = WindowsCpuPmSessionContinuity.Arm(fake, lifetime);
        fake.DuringRead = () => throw new IOException();
        Assert.Equal(CpuPmContinuityFailure.ObservationUnavailable, scope.ReadFresh(lifetime).Failure);
    }

    private static CpuPmPowerNotification Ac() => new(WindowsCpuPmSessionContinuity.PowerSettingChange, WindowsCpuPmSessionContinuity.AcDcSource, 4, 0);
    // All tests use this provider. No test constructs or invokes the native one.
    private sealed class Fake : ICpuPmContinuityPlatform
    {
        private bool used;
        internal bool InitialPower = true, UnregisterThrows;
        internal string? RegistrationFault;
        internal uint UnregisterStatus;
        internal CpuPmContinuityReading Reading = new(true, true, true, 1, 1000, 10_000_000);
        internal Action<CpuPmPowerNotification>? Power;
        internal Action<uint>? Suspend;
        internal Action? DuringRead;
        internal Action? PowerRegistered;
        internal Action<Action<CpuPmPowerNotification>>? DuringPowerRegistration;
        internal Action<Action<uint>>? DuringSuspendRegistration;
        internal List<string> Registered = [], Unregistered = [];
        public bool TryClaimScope() { if (used) { return false; } used = true; return true; }
        public CpuPmContinuityReading Read() { DuringRead?.Invoke(); return Reading; }
        public uint RegisterSuspend(Action<uint> callback, out nint handle)
        { Registered.Add("suspend"); Suspend = callback; handle = 1; DuringSuspendRegistration?.Invoke(callback); return RegistrationFault == "suspend" ? 5u : 0; }
        public uint RegisterPower(Action<CpuPmPowerNotification> callback, out nint handle)
        {
            Registered.Add("power"); Power = callback; handle = RegistrationFault == "zero-handle" ? 0 : 2;
            if (RegistrationFault == "throw") { throw new IOException(); }
            if (InitialPower) { callback(Ac()); }
            DuringPowerRegistration?.Invoke(callback);
            PowerRegistered?.Invoke();
            return RegistrationFault == "power" ? 5u : 0;
        }
        public uint UnregisterPower(nint handle) { Unregistered.Add("power"); if (UnregisterThrows) { throw new IOException(); } return UnregisterStatus; }
        public uint UnregisterSuspend(nint handle) { Unregistered.Add("suspend"); return UnregisterStatus; }
    }
}
