using OpenBlade.Contracts;
using Microsoft.Win32.SafeHandles;

namespace OpenBlade.Protocol.Tests;

public sealed class CoolingPadDemandSessionTests
{
    [Fact]
    public async Task ProductionKnownAdmissionRevocationConsumesIdWithoutReplayingOrPoisoningEnteredSession()
    {
        var fake = new Session();
        var policy = new CoolingPadTransactionPolicy();
        await using var port = new CoolingPadDemandSession(new(fake, new BatteryCommandClient(fake, TimeSpan.FromSeconds(3))),
            new(), TimeProvider.System, new(), productionPolicy: policy);
        await port.QueryRawControlStateAsync(default);
        await port.AcquireAsync(() => true, default);
        await port.EnterDemandAsync(new(2200), () => true, default);
        int before = fake.Commands.Count;
        var denied = await port.UpdateDemandAsync(new(1000), () => false, default);
        Assert.False(denied.Attempted);
        Assert.Equal(CoolingPadSessionFault.AdmissionRevoked, denied.Fault);
        Assert.Equal(before, fake.Commands.Count);
        Assert.True((await port.UpdateDemandAsync(new(1600), () => true, default)).Acknowledged);
        Assert.Equal(before + 1, fake.Commands.Count);
        Assert.Equal(6, policy.CompletedCount); // The revoked assignment is not an ACK.
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 7 }, fake.Ids);
    }

    [Fact]
    public async Task ProductionPolicyContinuesPastOrdinaryCapacityAndRestoresAtCurrentWirePosition()
    {
        var fake = new Session();
        var policy = new CoolingPadTransactionPolicy();
        await using var port = new CoolingPadDemandSession(new(fake, new BatteryCommandClient(fake, TimeSpan.FromSeconds(3))),
            new(), TimeProvider.System, new(), productionPolicy: policy);
        await port.QueryRawControlStateAsync(default);
        await port.AcquireAsync(() => true, default);
        await port.EnterDemandAsync(new(500), () => true, default);
        for (int i = 0; i < 1000; i++)
        { Assert.True((await port.UpdateDemandAsync(new(i % 2 == 0 ? 3200 : 500), () => true, default)).Acknowledged); }
        Assert.True((await port.ReassertMediumAsync(() => true, default)).Acknowledged);
        Assert.True((await port.ReassertQuietAsync(() => true, default)).Acknowledged);
        Assert.True((await port.RestoreRawControlStateAsync(() => true, default)).Acknowledged);
        Assert.Equal(1009, policy.CompletedCount);
        Assert.Equal(Enumerable.Range(1, 1009).Select(i => (byte)(i % 31)), fake.Ids);
    }

    [Theory]
    [InlineData(WindowsServiceState.Running)]
    [InlineData(WindowsServiceState.StartPending)]
    [InlineData(WindowsServiceState.Other)]
    public void ProductionGuardRequiresStoppedVendorAndNoCaptureButAllowsOwnService(WindowsServiceState state)
    {
        Assert.False(new CoolingPadSessionFactory.PadAccessGuard(new Processes([]),
            new Services([new("Razer", "Razer", state)]), production: true).Check().Safe);
        Assert.False(new CoolingPadSessionFactory.PadAccessGuard(new Processes(["OpenBlade.Capture"]), new Services([]), production: true).Check().Safe);
        Assert.False(new CoolingPadSessionFactory.PadAccessGuard(new Processes(null), new Services([]), production: true).Check().Safe);
        Assert.True(new CoolingPadSessionFactory.PadAccessGuard(new Processes(["OpenBlade.Service"]),
            new Services([new("OpenBlade", "OpenBlade", state), new("Razer", "Razer", WindowsServiceState.Stopped)]), production: true).Check().Safe);
    }

    [Theory]
    [InlineData(WindowsServiceState.Running)]
    [InlineData(WindowsServiceState.StartPending)]
    [InlineData(WindowsServiceState.ContinuePending)]
    [InlineData(WindowsServiceState.Other)]
    public void DeveloperCounter_ServiceAbsenceIsFreshAndScoped(WindowsServiceState state)
    {
        var running = new WindowsServiceSnapshot("OpenBlade", "OpenBlade", state);
        Assert.False(new CoolingPadSessionFactory.PadAccessGuard(new Processes([]), new Services([running]),
            requireOpenBladeServiceAbsent: true, developerProcessAllowed: () => true).Check().Safe);
        Assert.False(new CoolingPadSessionFactory.PadAccessGuard(new Processes(["OpenBlade.Service"]), new Services([]),
            requireOpenBladeServiceAbsent: true, developerProcessAllowed: () => true).Check().Safe);
        Assert.True(new CoolingPadSessionFactory.PadAccessGuard(new Processes([]), new Services([]),
            requireOpenBladeServiceAbsent: true, developerProcessAllowed: () => true).Check().Safe);
        Assert.False(new CoolingPadSessionFactory.PadAccessGuard(new Processes(null), new Services([]),
            requireOpenBladeServiceAbsent: true, developerProcessAllowed: () => true).Check().Safe);
        var vendor = new WindowsServiceSnapshot("RazerService", "Razer", state);
        Assert.False(new CoolingPadSessionFactory.PadAccessGuard(new Processes([]), new Services([vendor]),
            requireOpenBladeServiceAbsent: true, developerProcessAllowed: () => true).Check().Safe);
    }

    [Fact]
    public void DeveloperCounter_RequiresRecognizableCurrentApphostAndSoleCurrentPid()
    {
        const string apphost = @"C:\build\OpenBlade.Capture.exe";
        Assert.True(CoolingPadSessionFactory.CounterProcessAdmissionAllowed(apphost, 123, () => [123]));
        Assert.False(CoolingPadSessionFactory.CounterProcessAdmissionAllowed(apphost, 123, () => [123, 456]));
        Assert.False(CoolingPadSessionFactory.CounterProcessAdmissionAllowed(apphost, 123, () => [456]));
        Assert.False(CoolingPadSessionFactory.CounterProcessAdmissionAllowed(apphost, 123, () => []));
        Assert.False(CoolingPadSessionFactory.CounterProcessAdmissionAllowed(apphost, 123, () => throw new InvalidOperationException()));
        Assert.False(CoolingPadSessionFactory.CounterProcessAdmissionAllowed(@"C:\dotnet.exe", 123, () => [123]));
        Assert.False(CoolingPadSessionFactory.CounterProcessAdmissionAllowed(null, 123, () => [123]));
        Assert.False(new CoolingPadSessionFactory.PadAccessGuard(new Processes([]), new Services([]),
            requireOpenBladeServiceAbsent: true).Check().Safe);
        int observations = 0;
        var fresh = new CoolingPadSessionFactory.PadAccessGuard(new Processes([]), new Services([]),
            requireOpenBladeServiceAbsent: true, developerProcessAllowed: () => ++observations == 1);
        Assert.True(fresh.Check().Safe);
        Assert.False(fresh.Check().Safe);
    }

    [Fact]
    public async Task DeveloperCounter_UsesSequentialClaimsAndKeepsRestorationCapacityAfterNormalExhaustion()
    {
        var context = new ValidationRunContext();
        var counter = context.CreateCoolingPadDeveloperCounterPolicy();
        Assert.False(context.TryAllocateTransaction(out _));
        Assert.Throws<InvalidOperationException>(() => context.CreateCoolingPadDeveloperCounterPolicy());
        var fake = new Session();
        await using var port = new CoolingPadDemandSession(new(fake, new BatteryCommandClient(fake, TimeSpan.FromSeconds(3))),
            context, TimeProvider.System, new(), developerCounter: counter);
        Assert.Empty(counter.Snapshot());
        await port.QueryRawControlStateAsync(default);
        await port.AcquireAsync(() => true, default);
        await port.QueryRawControlStateAsync(default);
        await port.EnterDemandAsync(new(2200), () => true, default);
        for (int i = 1; i <= 33; i++)
        { Assert.True((await port.UpdateDemandAsync(new(i % 2 == 1 ? 1000 : 2200), () => true, default)).Acknowledged); }
        Assert.False((await port.UpdateDemandAsync(new(1000), () => true, default)).Attempted);
        Assert.Equal(39, fake.Ids.Count);
        Assert.True((await port.ReassertMediumAsync(() => true, default)).Acknowledged);
        Assert.True((await port.ReassertQuietAsync(() => true, default)).Acknowledged);
        Assert.True((await port.RestoreRawControlStateAsync(() => true, default)).Acknowledged);
        Assert.Equal(43, fake.Ids.Count);
        Assert.Equal(Enumerable.Range(1, 43).Select(x => (byte)(x % 31)), fake.Ids);
        Assert.All(counter.Snapshot(), x => Assert.True(x.Acknowledged && x.OutcomeAvailable));
        Assert.Equal(new byte[] { 9, 10, 11, 12 }, fake.Ids.TakeLast(4));
    }

    [Fact]
    public void DeveloperCounter_DefaultAssignmentAndOrdinaryAllocatorRemainNonzero()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TransactionIdAssignment.Valid(0));
        var context = new ValidationRunContext();
        for (int i = 1; i <= 255; i++) { Assert.True(context.TryAllocateTransaction(out byte id)); Assert.Equal(i, id); }
        Assert.False(context.TryAllocateTransaction(out _));
        Assert.Throws<InvalidOperationException>(() => context.CreateCoolingPadDeveloperCounterPolicy());
    }

    [Fact]
    public async Task ProductionFactory_IsClosedAndCanceledDeveloperOpenDoesNotCreateActors()
    {
        Assert.Equal(CoolingPadSessionFault.AdmissionClosed,
            (await new CoolingPadSessionFactory().OpenAsync(default)).Fault);
        int creates = 0;
        var factory = new CoolingPadSessionFactory(() => { creates++; throw new InvalidOperationException(); }, TimeProvider.System);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.Null((await factory.OpenAsync(canceled.Token)).Session);
        Assert.Equal(0, creates);
    }

    [Fact]
    public async Task TypedOperations_UseExactDistinctCommandsAndExplicitRawRestoration()
    {
        var fake = new Session();
        await using var port = Port(fake);
        Assert.Equal(CoolingPadRawControlState.Raw0000, (await port.QueryRawControlStateAsync(default)).State);
        Assert.True((await port.AcquireAsync(() => true, default)).Acknowledged);
        var entry = await port.EnterDemandAsync(new(2200), () => true, default);
        Assert.True(entry.Entry.Acknowledged);
        Assert.True(entry.Demand.Acknowledged);
        Assert.True((await port.UpdateDemandAsync(new(1350), () => true, default)).Acknowledged);
        Assert.True((await port.RestoreRawControlStateAsync(() => true, default)).Acknowledged);
        Assert.Equal(new[] { "00/84:0000", "00/04:0300", "00/84:0000", "0D/10:000600",
            "0D/01:01052C", "0D/01:01051B", "00/04:0000", "00/84:0000" }, fake.Commands);
        Assert.Equal(fake.Ids.Count, fake.Ids.Distinct().Count());
        Assert.False((await port.UpdateDemandAsync(new(2200), () => true, default)).Attempted);
    }

    [Fact]
    public async Task BaselineThreeOrUnexpectedPayload_CannotAcquire()
    {
        foreach (byte[] payload in new[] { new byte[] { 3, 0 }, new byte[] { 7, 0 }, new byte[] { 0 } })
        {
            var fake = new Session { QueryOverride = payload };
            await using var port = Port(fake);
            await port.QueryRawControlStateAsync(default);
            Assert.False((await port.AcquireAsync(() => true, default)).Attempted);
            Assert.Single(fake.Commands);
        }
    }

    [Fact]
    public async Task FreshnessRejectedAfterEntry_StopsDemandAndEveryLaterWrite()
    {
        var fake = new Session();
        await using var port = Port(fake);
        await port.QueryRawControlStateAsync(default);
        await port.AcquireAsync(() => true, default);
        int checks = 0;
        var entry = await port.EnterDemandAsync(new(2200), () => ++checks == 1, default);
        Assert.True(entry.Entry.Acknowledged);
        Assert.False(entry.Demand.Attempted);
        Assert.Equal(CoolingPadSessionFault.ControllerConflict, entry.Demand.Fault);
        Assert.False((await port.RestoreRawControlStateAsync(() => true, default)).Attempted);
        Assert.Equal(4, fake.Commands.Count);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task FailedOrAmbiguousWrite_NeverRetriesOrRestores(int failureCall)
    {
        var fake = new Session { FailureCall = failureCall };
        await using var port = Port(fake);
        await port.QueryRawControlStateAsync(default);
        await port.AcquireAsync(() => true, default);
        var entry = await port.EnterDemandAsync(new(2200), () => true, default);
        CoolingPadWriteResult failure = failureCall == 4 ? entry.Entry : entry.Demand;
        if (failureCall == 6) { failure = await port.UpdateDemandAsync(new(1350), () => true, default); }
        Assert.True(failure.Attempted);
        Assert.False(failure.Acknowledged);
        Assert.Equal(CoolingPadSessionFault.SubmissionUnknown, failure.Fault);
        Assert.False((await port.RestoreRawControlStateAsync(() => true, default)).Attempted);
        Assert.Equal(failureCall, fake.Commands.Count);
    }

    [Fact]
    public async Task DefaultDemandAndExhaustedEntry_DoNotSendPreamble()
    {
        var fake = new Session();
        var context = new ValidationRunContext();
        await using var port = Port(fake, context);
        await port.QueryRawControlStateAsync(default);
        await port.AcquireAsync(() => true, default);
        Assert.False((await port.EnterDemandAsync(default, () => true, default)).Entry.Attempted);
        while (context.RemainingTransactions > 1) { context.TryAllocateTransaction(out _); }
        Assert.False((await port.EnterDemandAsync(new(2200), () => true, default)).Entry.Attempted);
        Assert.Equal(3, fake.Commands.Count);
    }

    [Fact]
    public async Task ExhaustedUpdate_LeavesPreallocatedExplicitRestorationAvailable()
    {
        var fake = new Session();
        var context = new ValidationRunContext();
        await using var port = Port(fake, context);
        await port.QueryRawControlStateAsync(default);
        await port.AcquireAsync(() => true, default);
        await port.EnterDemandAsync(new(2200), () => true, default);
        while (context.TryAllocateTransaction(out _)) { }
        Assert.False((await port.UpdateDemandAsync(new(1350), () => true, default)).Attempted);
        var restore = await port.RestoreRawControlStateAsync(() => true, default);
        Assert.True(restore.Acknowledged);
        Assert.Null(restore.Fault);
        Assert.Equal(7, fake.Commands.Count);
        Assert.Equal(fake.Ids.Count, fake.Ids.Distinct().Count());
    }

    [Fact]
    public async Task FullRestoration_UsesReservedIdsAndRequiresMediumThenQuietBeforeRawRestore()
    {
        var fake = new Session();
        var context = new ValidationRunContext();
        await using var port = Port(fake, context);
        Assert.False((await port.ReassertMediumAsync(() => true, default)).Attempted);
        await port.QueryRawControlStateAsync(default);
        await port.AcquireAsync(() => true, default);
        Assert.False((await port.ReassertMediumAsync(() => true, default)).Attempted);
        await port.EnterDemandAsync(new(1600), () => true, default);
        Assert.Equal(246, context.RemainingTransactions);
        Assert.False((await port.ReassertQuietAsync(() => true, default)).Attempted);
        while (context.TryAllocateTransaction(out _)) { }
        Assert.False((await port.UpdateDemandAsync(new(1350), () => true, default)).Attempted);
        Assert.True((await port.ReassertMediumAsync(() => true, default)).Acknowledged);
        Assert.False((await port.ReassertMediumAsync(() => true, default)).Attempted);
        Assert.False((await port.RestoreRawControlStateAsync(() => true, default)).Attempted);
        Assert.False((await port.UpdateDemandAsync(new(1600), () => true, default)).Attempted);
        Assert.True((await port.ReassertQuietAsync(() => true, default)).Acknowledged);
        Assert.False((await port.ReassertQuietAsync(() => true, default)).Attempted);
        var restore = await port.RestoreRawControlStateAsync(() => true, default);
        Assert.True(restore.Acknowledged);
        Assert.Null(restore.Fault);
        Assert.Equal(new[] { "0D/10:01012C", "0D/10:000600", "00/04:0000", "00/84:0000" },
            fake.Commands.Skip(5));
        Assert.Equal(fake.Ids.Count, fake.Ids.Distinct().Count());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestorationAdmissionRejected_StopsAllLaterWrites(bool rejectQuiet)
    {
        var fake = new Session();
        await using var port = Port(fake);
        await port.QueryRawControlStateAsync(default);
        await port.AcquireAsync(() => true, default);
        await port.EnterDemandAsync(new(1600), () => true, default);
        if (rejectQuiet) { await port.ReassertMediumAsync(() => true, default); }
        var rejected = rejectQuiet
            ? await port.ReassertQuietAsync(() => false, default)
            : await port.ReassertMediumAsync(() => false, default);
        Assert.False(rejected.Attempted);
        Assert.False(rejected.Acknowledged);
        Assert.False((await port.ReassertQuietAsync(() => true, default)).Attempted);
        Assert.False((await port.RestoreRawControlStateAsync(() => true, default)).Attempted);
        Assert.Equal(rejectQuiet ? 6 : 5, fake.Commands.Count);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    public async Task RestorationUnknownSubmission_PreventsLaterQuietAndRawWrites(int failureCall)
    {
        var fake = new Session { FailureCall = failureCall };
        await using var port = Port(fake);
        await port.QueryRawControlStateAsync(default);
        await port.AcquireAsync(() => true, default);
        await port.EnterDemandAsync(new(1600), () => true, default);
        var failed = await port.ReassertMediumAsync(() => true, default);
        if (failureCall == 7) { failed = await port.ReassertQuietAsync(() => true, default); }
        Assert.True(failed.Attempted);
        Assert.False(failed.Acknowledged);
        Assert.Equal(CoolingPadSessionFault.SubmissionUnknown, failed.Fault);
        Assert.False((await port.ReassertQuietAsync(() => true, default)).Attempted);
        Assert.False((await port.RestoreRawControlStateAsync(() => true, default)).Attempted);
        Assert.Equal(failureCall, fake.Commands.Count);
    }

    [Theory]
    [InlineData("SynapseAppEngine")]
    [InlineData("RazerAppEngine")]
    [InlineData("USBPcapCMD")]
    public void PadGuard_BlocksControllersAndCapture(string process)
    {
        Assert.False(new CoolingPadSessionFactory.PadAccessGuard(new Processes([process]), new Services([])).Check().Safe);
    }

    [Fact]
    public void PadGuard_BlocksAnyActiveRazerServiceAndRejectsDetectionFailure()
    {
        var active = new Services([new("UnknownVendor", "Razer unknown service", WindowsServiceState.Running)]);
        Assert.False(new CoolingPadSessionFactory.PadAccessGuard(new Processes([]), active).Check().Safe);
        Assert.False(new CoolingPadSessionFactory.PadAccessGuard(new Processes(null), new Services([])).Check().Safe);
    }

    [Fact]
    public void PadGuard_RechecksIdentityAndFailsClosedAfterMetadataChanges()
    {
        int checks = 0;
        var guard = new CoolingPadSessionFactory.PadAccessGuard(new Processes([]), new Services([]), () => ++checks == 1);
        Assert.True(guard.Check().Safe);
        Assert.False(guard.Check().Safe);
        Assert.Equal(2, checks);
    }

    [Fact]
    public void MetadataGuard_RequiresOneExactRevisionGeometryAndHost()
    {
        var native = new MetadataNative(CoolingPadSessionFactory.ExactPadIdentity);
        Assert.True(CoolingPadSessionFactory.HostAndPadStillExact(new Host(true), native));
        Assert.Equal(1, native.Disposals);
        foreach (HidHandleIdentity wrong in new[] {
            CoolingPadSessionFactory.ExactPadIdentity with { VersionNumber = 0x0100 },
            CoolingPadSessionFactory.ExactPadIdentity with { FeatureReportByteLength = 90 },
            CoolingPadSessionFactory.ExactPadIdentity with { Usage = 2 },
            CoolingPadSessionFactory.ExactPadIdentity with { ProductId = 0x02C6 } })
        {
            Assert.False(CoolingPadSessionFactory.HostAndPadStillExact(new Host(true), new MetadataNative(wrong)));
        }
        Assert.False(CoolingPadSessionFactory.HostAndPadStillExact(new Host(false), native));
        Assert.False(CoolingPadSessionFactory.HostAndPadStillExact(new Host(true),
            new MetadataNative(CoolingPadSessionFactory.ExactPadIdentity, count: 2)));
        Assert.False(CoolingPadSessionFactory.HostAndPadStillExact(new Host(true),
            new MetadataNative(CoolingPadSessionFactory.ExactPadIdentity, count: 0)));
    }

    [Fact]
    public async Task Factory_PreventsTwoActiveActorsAndReopensOnlyAfterDisposal()
    {
        int opens = 0;
        var factory = new CoolingPadSessionFactory(() => new Actors(() => opens++), TimeProvider.System);
        var first = await factory.OpenAsync(default);
        Assert.NotNull(first.Session);
        Assert.Null((await factory.OpenAsync(default)).Session);
        Assert.Equal(1, opens);
        await first.Session.DisposeAsync();
        var next = await factory.OpenAsync(default);
        Assert.NotNull(next.Session);
        Assert.Equal(2, opens);
        await next.Session.DisposeAsync();
    }

    [Fact]
    public async Task Factory_CancellationAtCompletedOpenNeverExposesActor()
    {
        using var canceled = new CancellationTokenSource();
        var fake = new Session();
        var factory = new CoolingPadSessionFactory(() => new CancelingActors(fake, canceled), TimeProvider.System);
        var opened = await factory.OpenAsync(canceled.Token);
        Assert.Null(opened.Session);
        Assert.NotNull(opened.Fault);
        await fake.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(RazerCommandSessionDisposition.FullyDisposed, fake.Disposition);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlockedGuardOrAdmissionCallback_DeadlineRevokesLateSetAndKeepsFactoryBusy(bool blockCallback)
    {
        var clock = new DeadlineClock();
        var fake = new BlockingSession(blockCallback);
        var factory = new CoolingPadSessionFactory(() => new BlockingActors(fake), clock);
        var opened = await factory.OpenAsync(default);
        ICoolingPadDemandSession port = Assert.IsAssignableFrom<ICoolingPadDemandSession>(opened.Session);
        try
        {
            await port.QueryRawControlStateAsync(default);
            Task<CoolingPadWriteResult> acquire = port.AcquireAsync(() =>
            {
                if (blockCallback) { fake.Block(); }
                return true;
            }, default);
            await fake.BlockStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => clock.HasActive(TimeSpan.FromSeconds(3)));
            clock.Fire(TimeSpan.FromSeconds(3));
            var failed = await acquire.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(CoolingPadSessionFault.SubmissionUnknown, failed.Fault);
            Task disposal = port.DisposeAsync().AsTask();
            await fake.DisposalStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => clock.HasActive(TimeSpan.FromSeconds(5)));
            clock.Fire(TimeSpan.FromSeconds(5));
            await disposal.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.NotEqual(RazerCommandSessionDisposition.FullyDisposed, fake.Disposition);
            Assert.Null((await factory.OpenAsync(default)).Session);
        }
        finally { fake.Release.TrySetResult(); }
        await fake.ExchangeFinished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await fake.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, fake.MutatingSets);
    }

    [Fact]
    public async Task DisposeDuringBlockedGuard_RevokesSetBeforeExchangeDeadline()
    {
        var fake = new BlockingSession(blockCallback: false);
        var clock = new DeadlineClock();
        var actor = new ValidationActor(fake, new BatteryCommandClient(fake, TimeSpan.FromSeconds(3)));
        var port = new CoolingPadDemandSession(actor, new(), clock, new());
        try
        {
            await port.QueryRawControlStateAsync(default);
            Task<CoolingPadWriteResult> acquire = port.AcquireAsync(() => true, default);
            await fake.BlockStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Task disposal = port.DisposeAsync().AsTask();
            await fake.DisposalStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            fake.Release.TrySetResult();
            Assert.False((await acquire.WaitAsync(TimeSpan.FromSeconds(2))).Attempted);
            await disposal.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(0, fake.MutatingSets);
        }
        finally { fake.Release.TrySetResult(); }
    }

    [Fact]
    public async Task CanceledUpdate_StopsSessionWithoutAnyCleanupWrites()
    {
        var fake = new Session();
        await using var port = Port(fake);
        await port.QueryRawControlStateAsync(default);
        await port.AcquireAsync(() => true, default);
        await port.EnterDemandAsync(new(2200), () => true, default);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.False((await port.UpdateDemandAsync(new(1350), () => true, canceled.Token)).Attempted);
        Assert.False((await port.RestoreRawControlStateAsync(() => true, default)).Attempted);
        Assert.Equal(5, fake.Commands.Count);
    }

    [Theory]
    [InlineData("\\\\?\\hid#vid_1532&pid_0f43&mi_00#instance", true)]
    [InlineData("\\\\?\\hid#vid_1532&pid_0f43&mi_01#instance", false)]
    [InlineData("\\\\?\\hid#vid_1532&pid_0f43#instance", false)]
    public void NativeEnumeration_RequiresExplicitControlInterface(string path, bool expected) =>
        Assert.Equal(expected, CoolingPadSessionFactory.PadInterfaceNative.IsControlInterface(path));

    private static CoolingPadDemandSession Port(Session session, ValidationRunContext? context = null) =>
        new(new ValidationActor(session, new BatteryCommandClient(session, TimeSpan.FromSeconds(3))),
            context ?? new(), TimeProvider.System, new());

    private sealed class Processes(string[]? names) : IRunningProcessSource
    {
        public IReadOnlyList<string> GetProcessNames() => names ?? throw new InvalidOperationException();
    }
    private sealed class Services(WindowsServiceSnapshot[] services) : IWindowsServiceSource
    {
        public IReadOnlyList<WindowsServiceSnapshot> GetServices() => services;
    }
    private sealed class Actors(Action opened) : IValidationActorFactory
    {
        public ValueTask<HidSessionResult<ValidationActor>> OpenAsync(ValidationRunContext context)
        {
            opened();
            var session = new Session();
            return ValueTask.FromResult(new HidSessionResult<ValidationActor>(
                new(session, new BatteryCommandClient(session, TimeSpan.FromSeconds(3))), null));
        }
    }
    private sealed class CancelingActors(Session session, CancellationTokenSource cancellation) : IValidationActorFactory
    {
        public ValueTask<HidSessionResult<ValidationActor>> OpenAsync(ValidationRunContext context)
        {
            cancellation.Cancel();
            return ValueTask.FromResult(new HidSessionResult<ValidationActor>(
                new(session, new BatteryCommandClient(session, TimeSpan.FromSeconds(3))), null));
        }
    }
    private sealed class BlockingActors(BlockingSession session) : IValidationActorFactory
    {
        public ValueTask<HidSessionResult<ValidationActor>> OpenAsync(ValidationRunContext context) =>
            ValueTask.FromResult(new HidSessionResult<ValidationActor>(
                new(session, new BatteryCommandClient(session, TimeSpan.FromSeconds(3))), null));
    }
    private sealed class BlockingSession(bool blockCallback) : IRazerCommandSession
    {
        public TaskCompletionSource BlockStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ExchangeFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DisposalStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int MutatingSets { get; private set; }
        public RazerCommandSessionDisposition Disposition { get; private set; } = RazerCommandSessionDisposition.Healthy;
        public void Block()
        {
            BlockStarted.TrySetResult();
            Release.Task.GetAwaiter().GetResult();
        }
        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(RazerCommandSpec command,
            Func<TransactionIdAssignment> assign, TimeSpan timeout, CancellationToken token)
        {
            if (command.CommandId == 0x84)
            {
                return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(new byte[] { 0, 0 }, null));
            }
            if (!blockCallback) { Block(); }
            bool allowed = command.SubmissionAllowed?.Invoke() == true;
            if (allowed) { MutatingSets++; }
            ExchangeFinished.TrySetResult();
            return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(
                allowed ? command.Arguments : default, allowed ? null : HidSessionFailureKind.ConflictBlocked));
        }
        public ValueTask DisposeAsync()
        {
            Disposition = RazerCommandSessionDisposition.Disposing;
            DisposalStarted.TrySetResult();
            Release.Task.GetAwaiter().GetResult();
            Disposition = RazerCommandSessionDisposition.FullyDisposed;
            Disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
    private sealed class DeadlineClock : TimeProvider
    {
        private readonly List<DeadlineTimer> timers = [];
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new DeadlineTimer(callback, state, dueTime);
            lock (timers) { timers.Add(timer); }
            return timer;
        }
        public bool HasActive(TimeSpan due)
        {
            lock (timers) { return timers.Any(t => !t.Disposed && t.Due == due); }
        }
        public void Fire(TimeSpan due)
        {
            DeadlineTimer[] active;
            lock (timers) { active = timers.Where(t => !t.Disposed && t.Due == due).ToArray(); }
            foreach (var timer in active) { timer.Fire(); }
        }
        private sealed class DeadlineTimer(TimerCallback callback, object? state, TimeSpan due) : ITimer
        {
            public TimeSpan Due { get; private set; } = due;
            private int disposed;
            public bool Disposed => Volatile.Read(ref disposed) != 0;
            public bool Change(TimeSpan dueTime, TimeSpan period) { Due = dueTime; return !Disposed; }
            public void Dispose() => Interlocked.Exchange(ref disposed, 1);
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
            public void Fire()
            {
                if (Interlocked.Exchange(ref disposed, 1) == 0) { callback(state); }
            }
        }
    }
    private sealed class Host(bool admitted) : IHostIdentitySource
    {
        public HostIdentityReadResult ReadExactIdentity() => admitted
            ? new(new("Razer", "Blade 16 - RZ09-0528", "RZ09-05289EN4", "unrelated BIOS"), null,
                HostFirmwareCompatibility.ModelScoped)
            : new(null, HidSessionFailureKind.IdentityBlocked);
    }
    private sealed class MetadataNative(HidHandleIdentity identity, int count = 1) : IHidCandidateNative
    {
        public int Disposals { get; private set; }
        public IReadOnlyList<string> EnumerateDevicePaths() => Enumerable.Range(0, count).Select(i => i.ToString()).ToArray();
        public HidCandidateOpenResult TryOpen(string path, HidOpenParameters parameters) =>
            new(new OpenedCandidateHandle(new SafeFileHandle(new IntPtr(1), ownsHandle: false), () => Disposals++), null);
        public HidHandleIdentity ReadIdentity(OpenedCandidateHandle handle) => identity;
        public OpenedHidHandle BindSelected(OpenedCandidateHandle handle) => throw new NotSupportedException();
    }
    private sealed class Session : IRazerCommandSession
    {
        public List<string> Commands { get; } = [];
        public List<byte> Ids { get; } = [];
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public byte[]? QueryOverride { get; init; }
        public int FailureCall { get; init; }
        private byte raw;
        public RazerCommandSessionDisposition Disposition { get; private set; } = RazerCommandSessionDisposition.Healthy;
        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(RazerCommandSpec command,
            Func<TransactionIdAssignment> assign, TimeSpan timeout, CancellationToken token)
        {
            // Match the native actor: assignment is consumed before fresh submission admission.
            var assigned = assign();
            if (assigned.Failure is { } failure)
            {
                return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(default, failure));
            }
            if (command.SubmissionAllowed?.Invoke() == false)
            {
                return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(default, HidSessionFailureKind.ConflictBlocked));
            }
            Ids.Add(assigned.TransactionId);
            Commands.Add($"{command.ClassId:X2}/{command.CommandId:X2}:{Convert.ToHexString(command.Arguments.Span)}");
            if (Commands.Count == FailureCall)
            {
                return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(default, HidSessionFailureKind.Timeout));
            }
            if (command.ClassId == 0 && command.CommandId == 4) { raw = command.Arguments.Span[0]; }
            byte[] payload = command.ClassId == 0 && command.CommandId == 0x84
                ? QueryOverride ?? [raw, 0] : command.Arguments.ToArray();
            return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(payload, null));
        }
        public ValueTask DisposeAsync()
        {
            Disposition = RazerCommandSessionDisposition.FullyDisposed;
            Disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
}
