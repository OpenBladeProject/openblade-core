using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed partial class ProductionLightingPortTests
{
    [Fact]
    public async Task RecoveryAdmission_02E0InitialActivationBeforeMatrixRefusalRemainsPartial()
    {
        var session = new LightingSession(initialBrightness: 0xFF);
        var writer = new MatrixWriter();
        await using var port = CreatePort(session, writer, admitted: true);
        bool admitted = true;
        port.SetRecoveryWriteAdmission(() => admitted);
        writer.WriteObserved = () => admitted = false;
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetLightingAsync(LightingSettingsFactory.Static(new RgbColor(1, 2, 3), 100), default));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.Equal(BladeWriteFailurePhase.ControlRecoveryAdmission, failure.FailurePhase);
        Assert.Equal(LightingRendererOwnershipState.Faulted, port.OwnershipState);
        Assert.Empty(writer.Reports);
        Assert.Single(session.Commands, command => command.ClassId == 3 && command.CommandId == 0x0A);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RecoveryAdmission_RetainedRendererReportsCompletedBrightnessOrLogoEffect(bool rz090528, bool rejectLogoState)
    {
        var session = new LightingSession(initialBrightness: 0xFF);
        await using IProductionLightingPort port = CreateOwnedRendererReadPort(session, new ManualTimeProvider(), rz090528);
        bool admitted = true;
        Assert.IsAssignableFrom<ILightingRecoveryWriteAdmission>(port).SetRecoveryWriteAdmission(() => admitted);
        LightingSettings baseline = LightingSettingsFactory.Static(new RgbColor(1, 2, 3), 100);
        await port.SetLightingAsync(baseline, default);
        int commands = session.Commands.Count;
        session.CommandObserved = command =>
        {
            if (command.ClassId == 3 && command.CommandId == (rejectLogoState ? 0x00 : 0x02))
            {
                admitted = false;
            }
        };
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetLightingAsync(baseline with { Brightness = 50, LogoMode = LogoLightingMode.Breathing }, default));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.Equal(BladeWriteFailurePhase.ControlRecoveryAdmission, failure.FailurePhase);
        Assert.Equal(1, session.BrightnessSetCalls);
        Assert.Equal(rejectLogoState ? 1 : 0, session.Commands.Skip(commands).Count(command => command.ClassId == 3 && command.CommandId == 0x02));
        Assert.DoesNotContain(session.Commands.Skip(commands), command => command.ClassId == 3 && command.CommandId == 0x00);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryAdmission_LateMatrixRefusalPreservesAmbientAndPlaybackOwnership(bool playback)
    {
        var session = new LightingSession(initialBrightness: 0xFF);
        var writer = new MatrixWriter();
        await using var port = CreatePort(session, writer, admitted: true);
        bool admitted = true;
        port.SetRecoveryWriteAdmission(() => admitted);
        await port.SetLightingAsync(playback
            ? LightingSettingsFactory.Static(new RgbColor(1, 2, 3), 100)
            : LightingSettingsFactory.AmbientAwareness(AmbientAwarenessRegion.Right, 100), default);
        LightingRendererOwnershipState ownership = port.OwnershipState;
        int reports = writer.Reports.Count;
        int commands = session.Commands.Count;
        writer.WriteObserved = () => admitted = false;
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() => playback
            ? port.SetPlaybackMuteIndicatorAsync(true, default)
            : port.PushAmbientLightingFrameAsync(new AmbientLightingFrame(new RgbColor[AmbientLightingFrame.SlotCount]), default));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.Equal(BladeWriteFailurePhase.ControlRecoveryAdmission, failure.FailurePhase);
        Assert.Equal(ownership, port.OwnershipState);
        Assert.Equal(reports, writer.Reports.Count);
        Assert.Equal(commands, session.Commands.Count);
        writer.WriteObserved = null;
        admitted = true;
        if (playback)
        {
            await port.SetPlaybackMuteIndicatorAsync(true, default);
            Assert.Equal(new MatrixRgb(255, 0, 0), writer.Reports[^1].Frame[2]);
        }
        else
        {
            await port.PushAmbientLightingFrameAsync(new AmbientLightingFrame(new RgbColor[AmbientLightingFrame.SlotCount]), default);
        }
        Assert.Equal(reports + 1, writer.Reports.Count);
        Assert.Equal(commands, session.Commands.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task RecoveryAdmission_02C6AcknowledgedFrameKeepsEarlierRowsAsPartialSubmission(int refusedRow)
    {
        var session = new LightingSession(initialBrightness: 0xFF);
        await using IProductionLightingPort port = CreateOwnedRendererReadPort(session, new ManualTimeProvider(), true);
        bool admitted = true;
        Assert.IsAssignableFrom<ILightingRecoveryWriteAdmission>(port).SetRecoveryWriteAdmission(() => admitted);
        int rows = 0;
        session.CommandObserved = command =>
        {
            if (command.ClassId == 3 && command.CommandId == 0x0B && rows++ == refusedRow)
            {
                admitted = false;
            }
        };
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetLightingAsync(LightingSettingsFactory.Static(new RgbColor(1, 2, 3), 100), default));
        Assert.Equal(BladeWriteFailurePhase.ControlRecoveryAdmission, failure.FailurePhase);
        Assert.Equal(refusedRow == 0 ? WriteSubmissionState.KnownNotApplied : WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.Equal(refusedRow, session.Commands.Count(command => command.ClassId == 3 && command.CommandId == 0x0B));
        Assert.Equal(refusedRow == 0 ? LightingRendererOwnershipState.None : LightingRendererOwnershipState.Faulted, port.OwnershipState);
        Assert.Equal(0, session.BrightnessSetCalls);
        Assert.Equal(0, session.DeviceModeSetCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryAdmission_PausesOwnedStreamWithoutTeardownOrReplay(bool rz090528)
    {
        var session = new LightingSession(initialBrightness: 0xFF);
        var clock = new ManualTimeProvider();
        var writer = new MatrixWriter();
        await using IProductionLightingPort port = CreateOwnedRendererReadPort(session, clock, rz090528, writer);
        bool admitted = true;
        Assert.IsAssignableFrom<ILightingRecoveryWriteAdmission>(port).SetRecoveryWriteAdmission(() => admitted);
        await port.SetLightingAsync(LightingSettingsFactory.SpectrumCycling(100), default);
        int frames = writer.Reports.Count;
        int commands = session.SubmittedCommands.Count;
        int deviceModeWrites = session.DeviceModeSetCalls;
        admitted = false;

        clock.Advance(TimeSpan.FromSeconds(1));
        await Task.Delay(50);
        Assert.Equal(frames, writer.Reports.Count);
        Assert.Equal(commands, session.SubmittedCommands.Count);
        Assert.Equal(LightingRendererOwnershipState.Spectrum, port.OwnershipState);
        await Assert.ThrowsAsync<BladeWriteException>(() => port.ReleaseDriverModeForLowPowerAsync(static () => true, default));
        Assert.Equal(deviceModeWrites, session.DeviceModeSetCalls);
        Assert.Equal(LightingRendererOwnershipState.Spectrum, port.OwnershipState);
        Assert.True(await port.ReadDriverModeAsync(default));

        admitted = true;
        clock.Advance(TimeSpan.FromSeconds(1));
        await Task.Delay(50);
        Assert.True(rz090528 ? session.SubmittedCommands.Count > commands : writer.Reports.Count > frames);
        Assert.Equal(deviceModeWrites, session.DeviceModeSetCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryAdmission_RevokedDuringReadDoesNotRollbackOrRestore(bool rz090528)
    {
        var session = new LightingSession(initialBrightness: 0xFF);
        var writer = new MatrixWriter();
        await using IProductionLightingPort port = CreateOwnedRendererReadPort(session, new ManualTimeProvider(), rz090528, writer);
        bool admitted = true;
        Assert.IsAssignableFrom<ILightingRecoveryWriteAdmission>(port).SetRecoveryWriteAdmission(() => admitted);
        session.BrightnessQueried = () => admitted = false;

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetLightingAsync(LightingSettingsFactory.Off(), default));

        Assert.Equal(BladeWriteFailurePhase.ControlRecoveryAdmission, failure.FailurePhase);
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.Equal(0, session.BrightnessSetCalls);
        Assert.Equal(0, session.DeviceModeSetCalls);
        Assert.Empty(session.SubmittedCommands);
        Assert.Empty(writer.Reports);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task RecoveryAdmission_02C6ChecksEveryRowAndPreservesPartialSubmission(int refusedRow)
    {
        var session = new LightingSession(initialBrightness: 0xFF);
        var clock = new ManualTimeProvider();
        await using IProductionLightingPort port = CreateOwnedRendererReadPort(session, clock, true);
        bool admitted = true;
        Assert.IsAssignableFrom<ILightingRecoveryWriteAdmission>(port).SetRecoveryWriteAdmission(() => admitted);
        await port.SetLightingAsync(LightingSettingsFactory.SpectrumCycling(100), default);
        session.BeforeStreamSubmission = row => { if (row == refusedRow) { admitted = false; } };
        clock.Advance(TimeSpan.FromSeconds(1));
        await Task.Delay(50);

        Assert.False(admitted);
        Assert.Equal(refusedRow, session.SubmittedCommands.Count);
        Assert.Equal(refusedRow == 0 ? LightingRendererOwnershipState.Spectrum : LightingRendererOwnershipState.Faulted, port.OwnershipState);
        Assert.Equal(0, session.BrightnessSetCalls);
        Assert.Equal(0, session.DeviceModeSetCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryAdmission_RemainsSeparateFromLowPowerDriverCleanup(bool rz090528)
    {
        var session = new LightingSession(initialBrightness: 0xFF);
        (IProductionLightingPort port, _) = CreateDriverModeCleanupPort(session, rz090528, static () => false);
        await using (port)
        {
            Assert.IsAssignableFrom<ILightingRecoveryWriteAdmission>(port).SetRecoveryWriteAdmission(static () => true);
            Assert.False(await port.ReleaseDriverModeForLowPowerAsync(static () => true, default));
            Assert.Equal(1, session.DeviceModeSetCalls);
        }
    }
}

public sealed partial class WindowsPid02E0MatrixFeatureWriterTests
{
    [Fact]
    public async Task RecoveryAdmission_NativeMatrixQueueRechecksBeforeSetFeature()
    {
        using var gate = new SemaphoreSlim(1, 1);
        await gate.WaitAsync();
        var io = new ScriptedIo();
        var guard = new ScriptedGuard();
        var owned = CreateOwnedHandle();
        await using var writer = CreateWriter(owned.Handle, io, guard, sharedCommandGate: gate);
        bool admitted = true;
        Pid02E0MatrixReport report = Report(1, MatrixRgb.Black) with { SubmissionAllowed = () => admitted };
        Task<HidSessionResult<HidUnit>> queued = writer.WriteAsync(report, default);
        await guard.Checked.Task.WaitAsync(TimeSpan.FromSeconds(1));
        admitted = false;
        gate.Release();

        HidSessionResult<HidUnit> result = await queued.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(HidSessionFailureKind.ConflictBlocked, result.Failure);
        Assert.Equal(HidSessionFailurePhase.Guard, result.Diagnostic?.Phase);
        Assert.Empty(io.Calls);
        Assert.Equal(RazerCommandSessionDisposition.Healthy, writer.Disposition);
        Assert.Equal(Pid02E0MatrixReportCodec.EncodeFeatureReportBuffer(Report(1, MatrixRgb.Black)),
            Pid02E0MatrixReportCodec.EncodeFeatureReportBuffer(report));
    }
}

public sealed partial class ExperimentalBladeLightingTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task RecoveryAdmission_ExperimentalFrameChecksEveryRowAndKeepsPartialUncertainty(int refusedRow)
    {
        var session = new Session();
        var clock = new AnimationClock();
        var policy = Enabled();
        await using var port = AnimatedPort(session, clock, policy);
        bool admitted = true;
        port.SetRecoveryWriteAdmission(() => admitted);
        int rows = 0;
        session.BeforeSubmission = command =>
        {
            if (command.ClassId == 3 && command.CommandId == 0x0B && rows++ == refusedRow)
            {
                admitted = false;
            }
        };

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetLightingAsync(LightingSettingsFactory.Fire(50), default));

        Assert.Equal(BladeWriteFailurePhase.ControlRecoveryAdmission, failure.FailurePhase);
        Assert.Equal(refusedRow == 0 ? WriteSubmissionState.KnownNotApplied : WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.Equal(refusedRow, session.Commands.Count(command => command.ClassId == 3 && command.CommandId == 0x0B));
        Assert.Equal(refusedRow == 0, policy.WritesAllowed);
    }

    [Fact]
    public async Task RecoveryAdmission_ExperimentalControlChecksNativeBoundaryWithoutRestore()
    {
        var session = new Session();
        bool admitted = true;
        await using var port = Create(session, Enabled());
        port.SetRecoveryWriteAdmission(() => admitted);
        session.BeforeSubmission = command => { if (command.CommandId < 0x80) { admitted = false; } };

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetLightingAsync(LightingSettingsFactory.SpectrumCycling(70), default));

        Assert.Equal(BladeWriteFailurePhase.ControlRecoveryAdmission, failure.FailurePhase);
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.All(session.Commands, command => Assert.True(command.CommandId >= 0x80));
    }

    [Fact]
    public async Task RecoveryAdmission_ExperimentalOwnedAnimationWaitsWithoutReactivation()
    {
        var session = new Session();
        var clock = new AnimationClock();
        bool admitted = true;
        await using var port = AnimatedPort(session, clock);
        port.SetRecoveryWriteAdmission(() => admitted);
        await port.SetLightingAsync(LightingSettingsFactory.Fire(50), default);
        await clock.WaitForTimerCountAsync(1);
        int batches = session.AnimationBatches;
        int activationCount = session.Commands.Count(command => command.ClassId == 3 && command.CommandId == 10);
        LightingRendererOwnershipState ownership = port.OwnershipState;
        admitted = false;
        clock.Advance(TimeSpan.FromMilliseconds(50));
        await clock.WaitForTimerCountAsync(2);
        Assert.Equal(batches, session.AnimationBatches);
        Assert.Equal(ownership, port.OwnershipState);
        admitted = true;
        clock.Advance(TimeSpan.FromMilliseconds(50));
        await clock.WaitForTimerCountAsync(3);
        Assert.Equal(batches + 1, session.AnimationBatches);
        Assert.Equal(activationCount, session.Commands.Count(command => command.ClassId == 3 && command.CommandId == 10));
    }
}
