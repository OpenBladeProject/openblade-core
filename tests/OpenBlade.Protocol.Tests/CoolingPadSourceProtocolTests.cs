using OpenBlade.Contracts;

namespace OpenBlade.Protocol.Tests;

public sealed class CoolingPadSourceProtocolTests
{
    [Theory]
    [InlineData(-50)]
    [InlineData(0)]
    [InlineData(499)]
    [InlineData(501)]
    [InlineData(3250)]
    public void DemandRejectsUnsupportedTargets(int rpm) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => CoolingPadProtocol.DemandSet(rpm));

    [Fact]
    public void FanReadbackPreservesVendorPresetRatherThanInventingAnActiveCurve()
    {
        // Image 0x3228: the reply echoes the owner selector; Smart presets and Off carry no speed byte.
        Assert.True(CoolingPadProtocol.TryDecodeFan([0, 4], CoolingPadFanOwnership.Firmware, out var state));
        Assert.Equal(new CoolingPadFanConfiguration(CoolingPadFanOwnership.Firmware, CoolingPadFanPreset.Balanced, 0), state);
        Assert.True(CoolingPadProtocol.TryDecodeFan([1, 1, 40], CoolingPadFanOwnership.Synapse, out var fixedState));
        Assert.Equal(new CoolingPadFanConfiguration(CoolingPadFanOwnership.Synapse, CoolingPadFanPreset.Medium, 2000), fixedState);
        Assert.False(CoolingPadProtocol.TryDecodeFan([0, 1, 40], CoolingPadFanOwnership.Synapse, out _));
        Assert.False(CoolingPadProtocol.TryDecodeFan([0, 4, 0], CoolingPadFanOwnership.Firmware, out _));
        Assert.False(CoolingPadProtocol.TryDecodeFan([0, 1], CoolingPadFanOwnership.Firmware, out _));
        Assert.False(CoolingPadProtocol.TryDecodeFan([0, 7], CoolingPadFanOwnership.Firmware, out _));
        Assert.False(CoolingPadProtocol.TryDecodeFan([1, 1, 65], CoolingPadFanOwnership.Synapse, out _));
    }

    [Fact]
    public void FanSetFollowsTheFirmwareOwnerAndSpeedRules()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CoolingPadProtocol.FanSet(
            new(CoolingPadFanOwnership.Synapse, CoolingPadFanPreset.Quiet, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => CoolingPadProtocol.FanSet(
            new(CoolingPadFanOwnership.Firmware, CoolingPadFanPreset.Low, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => CoolingPadProtocol.FanSet(
            new(CoolingPadFanOwnership.Firmware, CoolingPadFanPreset.Balanced, 1500)));
        Assert.Equal([0, 6, 0], CoolingPadProtocol.FanSet(new(CoolingPadFanOwnership.Firmware, CoolingPadFanPreset.Off, 0)).Arguments.ToArray());
        Assert.Equal([1, 1, 40], CoolingPadProtocol.FanSet(new(CoolingPadFanOwnership.Synapse, CoolingPadFanPreset.Medium, 2000)).Arguments.ToArray());
    }

    [Fact]
    public void GeometryAdmissionRejectsAnotherAccessoryOrTruncatedDescriptor()
    {
        // Firmware 1.10.00 region reply: region 5, one row (byte 3), 18 LEDs (byte 4).
        byte[] region = [5, 0x19, 3, 1, 18];
        Assert.True(CoolingPadProtocol.TryDecodeRegion(region, out byte id)); Assert.Equal(5, id);
        Assert.False(CoolingPadProtocol.TryDecodeRegion([5, 0x19, 3, 1, 16], out _));
        Assert.False(CoolingPadProtocol.TryDecodeRegion([5, 0x19, 3, 18, 1], out _));
        Assert.False(CoolingPadProtocol.TryDecodeRegion(region.AsSpan(0, 4), out _));
        Assert.Throws<ArgumentException>(() => CoolingPadProtocol.FrameSet(new CoolingPadColor[19]));
    }

    [Fact]
    public void EffectReadbackUsesTheFirmwareRecordLayout()
    {
        // [0, stored region 5, Static, flags, rate, one color, slot 1, stale slot 2].
        byte[] reply = [0, 5, 1, 0, 0, 1, 255, 0, 0, 9, 9, 9];
        Assert.True(CoolingPadProtocol.TryDecodeEffect(reply, 1, out var effect));
        Assert.Equal((1, 0, 1), (effect!.Profile, effect.Region, effect.Effect));
        Assert.Equal([new CoolingPadColor(255, 0, 0)], effect.Colors);
        Assert.True(CoolingPadProtocol.TryDecodeEffect(reply, 0, out var active)); Assert.Equal(0, active!.Profile);
        Assert.False(CoolingPadProtocol.TryDecodeEffect([1, .. reply[1..]], 1, out _));
        Assert.False(CoolingPadProtocol.TryDecodeEffect(reply.AsSpan(0, 9), 1, out _));
        Assert.False(CoolingPadProtocol.TryDecodeEffect([0, 5, 7, 0, 0, 0, 0, 0, 0, 0, 0, 0], 1, out _));
        Assert.False(CoolingPadProtocol.TryDecodeEffect([0, 5, 1, 0, 0, 3, 0, 0, 0, 0, 0, 0], 1, out _));
    }

    [Theory]
    [InlineData(7, true)]
    [InlineData(9, true)]
    [InlineData(5, false)]
    [InlineData(6, false)]
    [InlineData(10, false)]
    public void EffectReplyAcceptsOnlyTheFirmwareEffectsItDoesNotDecode(byte effect, bool accepted)
    {
        // Firmware 1.10.00 has effects 7 and 9, which the pad's lighting button selects.
        byte[] reply = [0, 5, effect, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        Assert.False(CoolingPadProtocol.TryDecodeEffect(reply, 1, out _));
        Assert.Equal(accepted, CoolingPadProtocol.EffectQuery().PayloadValidator!(reply));
        Assert.Equal(accepted, CoolingPadProtocol.EffectQuery(0).PayloadValidator!(reply));
        Assert.False(CoolingPadProtocol.EffectQuery().PayloadValidator!(reply.AsMemory(0, 9)));
        Assert.False(CoolingPadProtocol.EffectQuery().PayloadValidator!((byte[])[1, .. reply[1..]]));
        Assert.True(CoolingPadProtocol.EffectQuery().PayloadValidator!((byte[])[0, 5, 1, 0, 0, 1, 255, 0, 0, 9, 9, 9]));
        Assert.False(CoolingPadProtocol.EffectQuery().PayloadValidator!((byte[])[0, 5, 1, 0, 0, 3, 0, 0, 0, 0, 0, 0]));
    }

    [Fact]
    public async Task AnEffectThePadButtonSelectsReadsAsUnknownLightingWithTheFanIntact()
    {
        var wire = new Wire { Fan = new(CoolingPadFanOwnership.Firmware, CoolingPadFanPreset.Low, 1000) };
        wire.SetRawEffectRecords([0, 5, 7, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        await using var device = Create(wire);
        var snapshot = await device.ReadSnapshotAsync(default);
        Assert.Null(snapshot.LightingConfiguration);
        Assert.Null(snapshot.ActiveLightingConfiguration);
        Assert.Null(snapshot.LightingMode);
        Assert.Equal(new CoolingPadFanConfiguration(CoolingPadFanOwnership.Firmware, CoolingPadFanPreset.Low, 1000), snapshot.FanConfiguration);
        Assert.Equal((byte)255, snapshot.BrightnessRaw);

        // Only the volatile record holds the unknown effect: the showing effect is still unknown.
        wire.Effect = new(1, 0, 1, 0, 0, [new(0, 255, 0)]);
        wire.SetRawEffectRecords(null, [0, 5, 9, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        snapshot = await device.ReadSnapshotAsync(default);
        Assert.NotNull(snapshot.LightingConfiguration);
        Assert.Null(snapshot.ActiveLightingConfiguration);
        Assert.Null(snapshot.LightingMode);
    }

    [Fact]
    public void EffectConfirmationComparesOnlyFirmwareStoredFields()
    {
        CoolingPadLightingConfiguration staticRed = new(1, 0, 1, 0, 0, [new(255, 0, 0)]);
        Assert.True(CoolingPadProtocol.ConfirmsEffect(staticRed, staticRed));
        Assert.False(CoolingPadProtocol.ConfirmsEffect(staticRed, staticRed with { Colors = [new(0, 255, 0)] }));
        Assert.False(CoolingPadProtocol.ConfirmsEffect(staticRed, staticRed with { Profile = 0 }));
        // Breathing does not store color slot 1, so its earlier bytes are not a mismatch.
        CoolingPadLightingConfiguration breathing = new(1, 0, 2, 0, 0, [new(255, 0, 0)]);
        Assert.True(CoolingPadProtocol.ConfirmsEffect(breathing, breathing with { Colors = [new(0, 255, 0)] }));
        Assert.False(CoolingPadProtocol.ConfirmsEffect(breathing, breathing with { Colors = [] }));
        CoolingPadLightingConfiguration twoColor = new(1, 0, 2, 0, 0, [new(1, 2, 3), new(4, 5, 6)]);
        Assert.False(CoolingPadProtocol.ConfirmsEffect(twoColor, twoColor with { Colors = [new(1, 2, 3), new(0, 0, 0)] }));
        // Effect 8 stores only its effect id.
        CoolingPadLightingConfiguration custom = new(0, 0, 8, 0, 0, []);
        Assert.True(CoolingPadProtocol.ConfirmsEffect(custom, custom with { Rate = 40, Colors = [new(1, 2, 3)] }));
        Assert.False(CoolingPadProtocol.ConfirmsEffect(custom, custom with { Effect = 1 }));
    }

    [Fact]
    public async Task FixedTransitionUsesDestinationDirectlyAndIndependentReadback()
    {
        var wire = new Wire(); await using var device = Create(wire);
        await device.SetFanAsync(CoolingPadFanMode.Fixed, 2200, () => true, default);
        // Readback queries the Synapse-owner record that the write updated.
        Assert.Equal(new[] { "0D10:01012C", "0D90:010000" }, wire.Commands);
        Assert.Equal(new CoolingPadFanConfiguration(CoolingPadFanOwnership.Synapse, CoolingPadFanPreset.Medium, 2200), wire.Fan);
        Assert.DoesNotContain(wire.Commands, x => x.StartsWith("0004", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FixedConfiguredTargetDoesNotReuseAnOlderHostDemandReading()
    {
        var wire = new Wire { Fan = new(CoolingPadFanOwnership.Firmware, CoolingPadFanPreset.Low, 1000) };
        await using var device = Create(wire);
        await device.SetFanAsync(CoolingPadFanMode.Fixed, 2200, () => true, default);
        // The independent host-demand register starts at zero; the fixed-mode target is in 0D90.
        var snapshot = await device.ReadSnapshotAsync(default);
        Assert.Equal(2200, snapshot.TargetRpm);
        Assert.Equal(CoolingPadFanMode.Fixed, snapshot.FanMode);
    }

    [Fact]
    public async Task UnconfirmedSessionReportsTheFirmwareOwnedSelection()
    {
        // Without its own confirmed write, the session cannot know which owner selected last.
        var wire = new Wire { Fan = new(CoolingPadFanOwnership.Synapse, CoolingPadFanPreset.Medium, 2200) };
        wire.Fan = new(CoolingPadFanOwnership.Firmware, CoolingPadFanPreset.Balanced, 0);
        await using var device = Create(wire);
        var snapshot = await device.ReadSnapshotAsync(default);
        Assert.Equal(new CoolingPadFanConfiguration(CoolingPadFanOwnership.Firmware, CoolingPadFanPreset.Balanced, 0), snapshot.FanConfiguration);
        Assert.Contains("0D90:000000", wire.Commands);
    }

    [Fact]
    public async Task RestoringTheFirmwareSelectionAfterFixedConfirmsTheFirmwareRecord()
    {
        var wire = new Wire { Fan = new(CoolingPadFanOwnership.Firmware, CoolingPadFanPreset.Medium, 1600) };
        await using var device = Create(wire);
        var baseline = await device.ReadSnapshotAsync(default);
        await device.SetFanAsync(CoolingPadFanMode.Fixed, 2000, () => true, default);
        // Medium's speed is shared, so the firmware record now reads 2000 until restoration.
        Assert.Equal(2000, wire.Selection(CoolingPadFanOwnership.Firmware).ConfiguredRpm);
        wire.Commands.Clear();
        await device.RestoreAsync(baseline, true, false, () => true, default);
        Assert.Equal(new[] { "0D10:000120", "0D90:000000" }, wire.Commands);
        Assert.Equal(baseline.FanConfiguration, wire.Fan);
        Assert.Null((await device.ReadSnapshotAsync(default)).FanMode);
    }
    [Fact]
    public async Task SmartTransitionPreservesSelectedSmartPresetAndDoesNotResetThroughFixed()
    {
        var wire = new Wire { Fan = new(CoolingPadFanOwnership.Firmware, CoolingPadFanPreset.Performance, 0) };
        await using var device = Create(wire);
        await device.SetFanAsync(CoolingPadFanMode.HostCurve, 1500, () => true, default);
        // The demand echo confirms it; 0D81 reports the ramping speed and is not used as confirmation.
        Assert.Equal(new[] { "0D90:000000", "0D01:01051E" }, wire.Commands);
        Assert.Equal(CoolingPadFanPreset.Performance, wire.Fan.Preset);
    }

    [Fact]
    public async Task AutoSelectsTheProfilePresetDirectlyThenSendsTheDemand()
    {
        // Synapse Smart: setThermalFanCoolingMode(smart.active, 0, 0), then 0D/01 per demand.
        var wire = new Wire { Fan = new(CoolingPadFanOwnership.Synapse, CoolingPadFanPreset.Medium, 2200) };
        await using var device = Create(wire);
        await device.SetFanAsync(CoolingPadFanMode.Auto, 2200, () => true, default, CoolingPadFanPreset.Balanced);
        Assert.Equal(new[] { "0D90:000000", "0D10:000400", "0D90:000000", "0D01:01052C" }, wire.Commands);
        Assert.Equal(new CoolingPadFanConfiguration(CoolingPadFanOwnership.Firmware, CoolingPadFanPreset.Balanced, 0), wire.Fan);
    }

    [Fact]
    public async Task AutoSkipsTheSelectionOnlyWhenThisSessionLeftThatPresetRunning()
    {
        // The firmware record matches, but another owner may have written last: select it once.
        var wire = new Wire { Fan = new(CoolingPadFanOwnership.Firmware, CoolingPadFanPreset.Balanced, 0) };
        await using var device = Create(wire);
        await device.SetFanAsync(CoolingPadFanMode.Auto, 1500, () => true, default, CoolingPadFanPreset.Balanced);
        Assert.Contains("0D10:000400", wire.Commands);
        wire.Commands.Clear();
        await device.SetFanAsync(CoolingPadFanMode.Auto, 2000, () => true, default, CoolingPadFanPreset.Balanced);
        Assert.Equal(new[] { "0D90:000000", "0D01:010528" }, wire.Commands);
        wire.Commands.Clear();
        // A profile change goes straight to its own preset, with no reset through another mode.
        await device.SetFanAsync(CoolingPadFanMode.Auto, 2000, () => true, default, CoolingPadFanPreset.Performance);
        Assert.Equal(new[] { "0D90:000000", "0D10:000500", "0D90:000000", "0D01:010528" }, wire.Commands);
        wire.Commands.Clear();
        await device.SetFanAsync(CoolingPadFanMode.Fixed, 2000, () => true, default);
        wire.Commands.Clear();
        await device.SetFanAsync(CoolingPadFanMode.Auto, 1000, () => true, default, CoolingPadFanPreset.Performance);
        Assert.Equal(new[] { "0D90:010000", "0D10:000500", "0D90:000000", "0D01:010514" }, wire.Commands);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(CoolingPadFanPreset.Medium)]
    [InlineData(CoolingPadFanPreset.Off)]
    public async Task AutoRequiresASmartPresetBeforeAnyCommand(CoolingPadFanPreset? preset)
    {
        var wire = new Wire(); await using var device = Create(wire);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            device.SetFanAsync(CoolingPadFanMode.Auto, 1500, () => true, default, preset));
        Assert.Empty(wire.Commands);
    }

    [Fact]
    public async Task GuardRevocationBeforeSubmissionSendsNothingAndCanBeRetriedExplicitly()
    {
        var wire = new Wire(); await using var device = Create(wire);
        var failure = await Assert.ThrowsAsync<CoolingPadOperationException>(() =>
            device.SetFanAsync(CoolingPadFanMode.Fixed, 1500, () => false, default));
        Assert.False(failure.SubmissionUnknown); Assert.True(failure.LocalAdmissionDeferred); Assert.Empty(wire.Commands);
        await device.SetFanAsync(CoolingPadFanMode.Fixed, 1500, () => true, default);
        Assert.Equal(1500, wire.Fan.ConfiguredRpm);
    }

    [Fact]
    public async Task RealControllerBoundaryIsNotALocalAdmissionDeferral()
    {
        var wire = new Wire { ControllerBlocked = true }; await using var device = Create(wire);
        var failure = await Assert.ThrowsAsync<CoolingPadOperationException>(() =>
            device.SetFanAsync(CoolingPadFanMode.Fixed, 1500, () => true, default));
        Assert.False(failure.SubmissionUnknown);
        Assert.False(failure.LocalAdmissionDeferred);
        Assert.Empty(wire.Commands);
    }

    [Fact]
    public async Task LocalGateClosingAfterSmartModeWriteIsUncertainAndCannotBeDeferred()
    {
        var wire = new Wire(); await using var device = Create(wire);
        int admissions = 0;
        var failure = await Assert.ThrowsAsync<CoolingPadOperationException>(() =>
            device.SetFanAsync(CoolingPadFanMode.HostCurve, 1500, () => ++admissions == 1, default));
        Assert.True(failure.SubmissionUnknown);
        Assert.False(failure.LocalAdmissionDeferred);
        Assert.Contains("0D10:000300", wire.Commands);
        Assert.DoesNotContain(wire.Commands, command => command.StartsWith("0D01:"));
    }

    [Fact]
    public async Task CancellationAtAdmissionRemainsObservableWithoutAnUncertainWrite()
    {
        var wire = new Wire(); await using var device = Create(wire);
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => device.SetFanAsync(
            CoolingPadFanMode.Fixed, 1500, () => { cancellation.Cancel(); return false; }, cancellation.Token));
        Assert.Empty(wire.Commands);
    }
    [Fact]
    public async Task MismatchingIndependentFanReadbackStopsAllLaterWrites()
    {
        var wire = new Wire { WrongFanReadback = true }; await using var device = Create(wire);
        var failure = await Assert.ThrowsAsync<CoolingPadOperationException>(() =>
            device.SetFanAsync(CoolingPadFanMode.Fixed, 1500, () => true, default));
        Assert.True(failure.SubmissionUnknown);
        int before = wire.Commands.Count;
        await Assert.ThrowsAsync<CoolingPadOperationException>(() => device.SetFanAsync(CoolingPadFanMode.Off, null, () => true, default));
        Assert.Equal(before, wire.Commands.Count);
    }

    [Fact]
    public async Task AmbiguousNativeSubmissionNeverFallsBackOrRestores()
    {
        var wire = new Wire { FailNextSet = true }; await using var device = Create(wire);
        var failure = await Assert.ThrowsAsync<CoolingPadOperationException>(() =>
            device.SetFanAsync(CoolingPadFanMode.Fixed, 1500, () => true, default));
        Assert.True(failure.SubmissionUnknown);
        await Assert.ThrowsAsync<CoolingPadOperationException>(() => device.RestoreAsync(
            new("pad") { FanConfiguration = new(CoolingPadFanOwnership.Firmware, CoolingPadFanPreset.Off, 0) }, true, false, () => true, default));
        Assert.Single(wire.Commands);
    }

    [Fact]
    public async Task RestorePreservesOriginalPresetAndExactRawBrightness()
    {
        var wire = new Wire { RawBrightness = 101 };
        await using var device = Create(wire);
        var saved = await device.ReadSnapshotAsync(default);
        Assert.Equal(40, saved.BrightnessPercent);
        await device.SetFanAsync(CoolingPadFanMode.Fixed, 2200, () => true, default);
        await device.SetLightingAsync(new() { LightingMode = CoolingPadLightingMode.Static, BrightnessPercent = 75, Color = new(255, 0, 0) }, () => true, default);
        wire.Commands.Clear();
        await device.RestoreAsync(saved, true, true, () => true, default);
        Assert.Equal(saved.FanConfiguration, wire.Fan);
        Assert.Equal(101, wire.RawBrightness);
        Assert.Contains("0F04:010065", wire.Commands);
        Assert.DoesNotContain("0D10:01012C", wire.Commands);
    }

    [Fact]
    public async Task LightingModeIsConfirmedOnlyWhenBothProfilesHoldTheSameEffect()
    {
        // The strip shows the last effect written. A saved Off beside a different
        // unsaved effect must not be reported as Off from profile 1 alone.
        var wire = new Wire { Effect = new(1, 0, 0, 0, 0, []), ActiveEffect = new(0, 0, 3, 0, 0, []) };
        await using var device = Create(wire);
        Assert.Null((await device.ReadSnapshotAsync(default)).LightingMode);
        wire.ActiveEffect = new(0, 0, 0, 0, 0, []);
        Assert.Equal(CoolingPadLightingMode.Off, (await device.ReadSnapshotAsync(default)).LightingMode);
    }

    [Fact]
    public async Task OwnConfirmedLightingWriteIdentifiesTheShowingRecordUntilItChanges()
    {
        // A saved effect leaves the unsaved record as it was; the strip shows the saved write.
        var wire = new Wire { Effect = new(1, 0, 0, 0, 0, []), ActiveEffect = new(0, 0, 3, 0, 0, []) };
        await using var device = Create(wire);
        await device.SetLightingAsync(new() { LightingMode = CoolingPadLightingMode.Off }, () => true, default);
        Assert.Equal(CoolingPadLightingMode.Off, (await device.ReadSnapshotAsync(default)).LightingMode);
        wire.Effect = new(1, 0, 2, 0, 0, []);
        Assert.Null((await device.ReadSnapshotAsync(default)).LightingMode);
        await using var restarted = Create(wire);
        wire.Effect = new(1, 0, 0, 0, 0, []);
        Assert.Null((await restarted.ReadSnapshotAsync(default)).LightingMode);
    }

    [Fact]
    public async Task ExistingCustomPixelsAreNeverReplacedWithAnUnrestorableMirror()
    {
        var wire = new Wire { ActiveEffect = new(0, 0, 8, 0, 0, []) }; await using var device = Create(wire);
        var failure = await Assert.ThrowsAsync<CoolingPadOperationException>(() =>
            device.SetLightingAsync(new() { LightingMode = CoolingPadLightingMode.KeyboardMirror }, () => true, default));
        Assert.False(failure.SubmissionUnknown);
        Assert.DoesNotContain(wire.Commands, x => x.StartsWith("0F02", StringComparison.Ordinal) || x.StartsWith("0004", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MirrorUsesBoundedSetOnlyFramesAndRestoresOriginalMode()
    {
        var wire = new Wire(); await using var device = Create(wire);
        var saved = await device.ReadSnapshotAsync(default);
        await device.SetLightingAsync(new() { LightingMode = CoolingPadLightingMode.KeyboardMirror }, () => true, default);
        await device.PushMirrorFrameAsync(Enumerable.Repeat(new CoolingPadColor(1, 2, 3), 18).ToArray(), () => true, default);
        Assert.Equal(1, wire.SetOnlyFrames);
        await device.RestoreAsync(saved, false, true, () => true, default);
        Assert.Equal(CoolingPadDeviceMode.Normal, wire.Mode);
        Assert.Equal(1, wire.Effect.Effect);
    }

    [Fact]
    public async Task HungActorCancellationRevokesLateSubmissionAndRetainsLeaseUntilNativeRetirement()
    {
        var wire = new HeldWire();
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var device = new SourceBackedCoolingPadDevice(
            new ValidationActor(wire, new BatteryCommandClient(wire, TimeSpan.FromSeconds(1))),
            "pad", () => { released.TrySetResult(); return Task.CompletedTask; });
        using var cancellation = new CancellationTokenSource();
        Task write = device.SetFanAsync(CoolingPadFanMode.Fixed, 1500, () => true, cancellation.Token);
        await wire.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();
        var failure = await Assert.ThrowsAsync<CoolingPadOperationException>(() => write.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.True(failure.SubmissionUnknown);
        Assert.False(wire.Command.SubmissionAllowed!());
        await Assert.ThrowsAsync<CoolingPadOperationException>(() =>
            device.SetFanAsync(CoolingPadFanMode.Off, null, () => true, default));
        await device.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(7));
        Assert.False(released.Task.IsCompleted);
        wire.CompleteRetirement();
        await wire.Retired.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await released.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class HeldWire : IRazerCommandSession
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Retired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<HidSessionResult<ReadOnlyMemory<byte>>> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource retirement = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal RazerCommandSpec Command { get; private set; }
        public RazerCommandSessionDisposition Disposition { get; private set; } = RazerCommandSessionDisposition.Healthy;
        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId, TimeSpan exchangeTimeout, CancellationToken cancellationToken)
        {
            Command = command;
            Entered.TrySetResult();
            return pending.Task;
        }
        internal void CompleteRetirement()
        {
            pending.TrySetResult(new(default, HidSessionFailureKind.QueuedCanceled));
            retirement.TrySetResult();
        }
        public async ValueTask DisposeAsync()
        {
            await retirement.Task;
            Disposition = RazerCommandSessionDisposition.FullyDisposed;
            Retired.TrySetResult();
        }
    }
    [Theory]
    [InlineData("FanControl")]
    [InlineData("fancontrol")]
    [InlineData("OpenBlade.Capture")]
    [InlineData("USBPcapCMD")]
    public void KnownPadControllerBlocksTheNativeAdmissionGuard(string controller)
    {
        var guard = new WindowsCoolingPadDeviceProvider.CoolingPadAccessGuard(
            new ControllerProcesses(controller), new NoServices(), () => true);
        var result = guard.Check();
        Assert.False(result.Safe);
        Assert.Contains(controller, result.Conflicts);
    }

    [Fact]
    public void FailureMessageNamesTheTypedCommandAndSessionClassification()
    {
        var result = new HidSessionResult<ReadOnlyMemory<byte>>(default, HidSessionFailureKind.ProtocolFailure)
        {
            Diagnostic = new(HidSessionFailurePhase.ProtocolValidation, HidSessionFailureKind.ProtocolFailure, null, 91)
            { ProtocolValidationFailure = HidProtocolValidationFailure.Status },
            QueryResponseStatus = 0x05,
        };
        Assert.Equal("Cooling-pad operation failed: ProtocolFailure (0D/90, ProtocolValidation, Status, status 0x05).",
            SourceBackedCoolingPadDevice.DescribeFailure(CoolingPadProtocol.FanQuery(), result));
        Assert.Equal("Cooling-pad operation failed: Timeout (0F/82).", SourceBackedCoolingPadDevice.DescribeFailure(
            CoolingPadProtocol.EffectQuery(), new(default, HidSessionFailureKind.Timeout)));
    }

    private sealed class ControllerProcesses(string name) : IRunningProcessSource
    {
        public IReadOnlyList<string> GetProcessNames() => [name];
    }
    private sealed class NoServices : IWindowsServiceSource
    {
        public IReadOnlyList<WindowsServiceSnapshot> GetServices() => [];
    }
    [Fact]
    public async Task OwnedMirrorCanSwitchThroughBuiltInLightingWithoutAnIntermediateRestore()
    {
        var wire = new Wire(); await using var device = Create(wire);
        await device.SetLightingAsync(new() { LightingMode = CoolingPadLightingMode.KeyboardMirror }, () => true, default);
        await device.SetLightingAsync(new() { LightingMode = CoolingPadLightingMode.Static, Color = new(255, 0, 0) }, () => true, default);
        await device.SetLightingAsync(new() { LightingMode = CoolingPadLightingMode.KeyboardMirror }, () => true, default);
        Assert.Equal(CoolingPadDeviceMode.Driver, wire.Mode);
        Assert.Equal(8, wire.ActiveEffect!.Effect);
        Assert.Equal(new CoolingPadColor(255, 0, 0), wire.Effect.Colors[0]);
    }

    [Fact]
    public async Task ARealControllerBoundaryInvalidatesPreviouslyOwnedCustomPixels()
    {
        var wire = new Wire(); await using var device = Create(wire);
        await device.SetLightingAsync(new() { LightingMode = CoolingPadLightingMode.KeyboardMirror }, () => true, default);
        wire.ControllerBlocked = true;
        await Assert.ThrowsAsync<CoolingPadOperationException>(() => device.ReadSnapshotAsync(default));
        wire.ControllerBlocked = false;
        int before = wire.Commands.Count;
        var failure = await Assert.ThrowsAsync<CoolingPadOperationException>(() =>
            device.SetLightingAsync(new() { LightingMode = CoolingPadLightingMode.KeyboardMirror }, () => true, default));
        Assert.False(failure.SubmissionUnknown);
        Assert.DoesNotContain(wire.Commands.Skip(before), command => command.StartsWith("0F02", StringComparison.Ordinal));
    }
    [Fact]
    public async Task ExplicitNewMirrorCanUseTheProtectedOriginalBaselineAfterNativeReopen()
    {
        var wire = new Wire { Mode = CoolingPadDeviceMode.Driver, ActiveEffect = new(0, 0, 8, 0, 0, []) };
        await using var device = Create(wire);
        var original = new CoolingPadDeviceSnapshot("pad")
        {
            LightingConfiguration = new(1, 0, 1, 0, 0, [new(0, 255, 0)]),
            ActiveLightingConfiguration = new(0, 0, 1, 0, 0, [new(0, 255, 0)]),
            BrightnessRaw = 101,
            DeviceMode = CoolingPadDeviceMode.Normal,
        };
        device.BindLightingBaseline(original);
        await device.SetLightingAsync(new() { LightingMode = CoolingPadLightingMode.KeyboardMirror }, () => true, default);
        Assert.Equal(CoolingPadDeviceMode.Driver, wire.Mode);
        await device.SetLightingAsync(new() { LightingMode = CoolingPadLightingMode.Static, Color = new(255, 0, 0) }, () => true, default);
        Assert.Equal(CoolingPadDeviceMode.Normal, wire.Mode);
        Assert.Equal(1, wire.Effect.Effect);
    }

    [Fact]
    public async Task RetainedMirrorProvenanceCannotBeForgedFromCurrentCustomReadback()
    {
        var wire = new Wire { ActiveEffect = new(0, 0, 8, 0, 0, []) };
        await using var device = Create(wire);
        var current = await device.ReadSnapshotAsync(default);
        Assert.Throws<CoolingPadOperationException>(() => device.BindLightingBaseline(current));
        await Assert.ThrowsAsync<CoolingPadOperationException>(() =>
            device.SetLightingAsync(new() { LightingMode = CoolingPadLightingMode.KeyboardMirror }, () => true, default));
        Assert.DoesNotContain(wire.Commands, command => command.StartsWith("0F02", StringComparison.Ordinal));
    }
    private static SourceBackedCoolingPadDevice Create(Wire wire) => new(
        new ValidationActor(wire, new BatteryCommandClient(wire, TimeSpan.FromSeconds(1))), "pad", () => Task.CompletedTask);

    private sealed class Wire : IRazerCommandSession, IRazerSetFeatureCommandSession
    {
        // Firmware 1.10.00 fan state (image 0x3228/0x3290): one selection per owner, a speed table
        // shared by Low/Medium/High, and the selection written last runs.
        private readonly byte[] ownerPresets = [(byte)CoolingPadFanPreset.Off, (byte)CoolingPadFanPreset.Low];
        private readonly byte[] speeds = [20, 30, 40];
        private CoolingPadFanOwnership runningOwner = CoolingPadFanOwnership.Firmware;
        internal CoolingPadFanConfiguration Fan
        {
            get
            {
                byte preset = ownerPresets[(int)runningOwner];
                return new(runningOwner, (CoolingPadFanPreset)preset, preset < 3 ? speeds[preset] * 50 : 0);
            }
            set => StoreFan((byte)value.Ownership, (byte)value.Preset, (byte)(value.ConfiguredRpm / 50));
        }
        internal CoolingPadFanConfiguration Selection(CoolingPadFanOwnership owner)
        {
            byte preset = ownerPresets[(int)owner];
            return new(owner, (CoolingPadFanPreset)preset, preset < 3 ? speeds[preset] * 50 : 0);
        }
        private bool StoreFan(byte owner, byte preset, byte speed)
        {
            if (owner > 1 || preset > 6 || owner == 1 && preset >= 3 || preset < 3 && speed is < 10 or > 64) { return false; }
            ownerPresets[owner] = preset;
            if (preset < 3) { speeds[preset] = speed; }
            runningOwner = (CoolingPadFanOwnership)owner;
            return true;
        }
        // Firmware 1.10.00 effect records in GET order: [0, region 5, effect, flags, rate, count, slot 1, slot 2].
        private byte[] persistentRecord = Record(new(1, 0, 1, 0, 0, [new(0, 255, 0)]));
        private byte[]? volatileRecord;
        internal CoolingPadLightingConfiguration Effect { get => Decode(persistentRecord, 1); set => persistentRecord = Record(value); }
        internal CoolingPadLightingConfiguration? ActiveEffect
        {
            get => volatileRecord is null ? null : Decode(volatileRecord, 0);
            set => volatileRecord = value is null ? null : Record(value);
        }
        // Sets effect records the decoder does not admit, as the pad's lighting button can.
        internal void SetRawEffectRecords(byte[]? persistent, byte[]? active = null)
        {
            if (persistent is not null) { persistentRecord = persistent; }
            volatileRecord = active;
        }
        internal byte RawBrightness { get; set; } = 255;
        internal CoolingPadDeviceMode Mode { get; set; }
        internal bool ControllerBlocked { get; set; }
        internal bool WrongFanReadback { get; set; }
        internal bool FailNextSet { get; set; }
        internal int SetOnlyFrames { get; private set; }
        internal List<string> Commands { get; } = [];
        private int target;
        public RazerCommandSessionDisposition Disposition { get; private set; } = RazerCommandSessionDisposition.Healthy;
        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId, TimeSpan exchangeTimeout, CancellationToken cancellationToken) =>
            Task.FromResult(Execute(command, assignTransactionId));
        private HidSessionResult<ReadOnlyMemory<byte>> Execute(RazerCommandSpec command, Func<TransactionIdAssignment> assign)
        {
            Assert.Equal(command.ClassId == 15 ? 31 : 2, assign().TransactionId);
            if (ControllerBlocked) { return new(default, HidSessionFailureKind.ConflictBlocked); }
            if (command.SubmissionAllowed?.Invoke() == false) { return new(default, HidSessionFailureKind.ConflictBlocked); }
            byte[] p = command.Arguments.ToArray();
            Commands.Add($"{command.ClassId:X2}{command.CommandId:X2}:{Convert.ToHexString(p)}");
            if (command.CommandId < 128 && FailNextSet) { FailNextSet = false; return new(default, HidSessionFailureKind.NativeIoFailure); }
            switch ((command.ClassId, command.CommandId))
            {
                case (13, 16): Assert.True(StoreFan(p[0], p[1], p[2])); break;
                case (13, 144):
                    byte selected = ownerPresets[p[0]];
                    p = selected < 3 ? [p[0], selected, (byte)(WrongFanReadback ? 11 : speeds[selected])] : [p[0], selected];
                    break;
                case (13, 1): target = p[2] * 50; break;
                case (13, 129): p = [1, 5, (byte)(target / 50)]; break;
                case (0, 4): Mode = (CoolingPadDeviceMode)p[0]; break;
                case (0, 132): p = [(byte)Mode, 0]; break;
                case (15, 128): p = [5, 0x19, 3, 1, 18]; break;
                case (15, 132): p = [1, 5, RawBrightness]; break;
                case (15, 4): RawBrightness = p[2]; break;
                case (15, 130): p = (byte[])(p[0] == 0 ? volatileRecord ?? persistentRecord : persistentRecord).Clone(); break;
                case (15, 2):
                    byte[] arguments = new byte[80]; p.CopyTo(arguments, 0);
                    // Volatile custom mode leaves the persistent record unchanged.
                    if (p[0] == 1) { Store(persistentRecord, arguments); }
                    else { volatileRecord ??= (byte[])persistentRecord.Clone(); Store(volatileRecord, arguments); }
                    break;
            }
            Assert.True(command.PayloadValidator?.Invoke(p) ?? true);
            return new(p, null);
        }
        public Task<HidSessionResult<ReadOnlyMemory<byte>>> SubmitAsync(RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId, TimeSpan exchangeTimeout, CancellationToken cancellationToken)
        { SetOnlyFrames++; return Task.FromResult(Execute(command, assignTransactionId)); }
        public Task<HidSessionResult<ReadOnlyMemory<byte>>> SubmitBatchAsync(IReadOnlyList<RazerCommandSpec> commands,
            Func<TransactionIdAssignment> assignTransactionId, TimeSpan exchangeTimeout, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DisposeAsync() { Disposition = RazerCommandSessionDisposition.FullyDisposed; return ValueTask.CompletedTask; }

        private static byte[] Record(CoolingPadLightingConfiguration effect)
        {
            byte[] record = new byte[12];
            record[1] = 5; record[2] = effect.Effect; record[3] = effect.Flags; record[4] = effect.Rate;
            record[5] = (byte)effect.Colors.Count;
            for (int i = 0; i < effect.Colors.Count; i++)
            {
                record[6 + i * 3] = effect.Colors[i].R; record[7 + i * 3] = effect.Colors[i].G; record[8 + i * 3] = effect.Colors[i].B;
            }
            return record;
        }

        private static CoolingPadLightingConfiguration Decode(byte[] record, byte profile)
        {
            Assert.True(CoolingPadProtocol.TryDecodeEffect(record, profile, out var effect));
            return effect!;
        }

        // Firmware SET storage (image 0x6E40): effect 8 stores only its id; slot 1 is stored only for Static.
        private static void Store(byte[] record, byte[] arguments)
        {
            record[2] = arguments[2];
            if (arguments[2] == 8) { return; }
            record[3] = arguments[3]; record[4] = arguments[4]; record[5] = arguments[5];
            if (arguments[2] == 1) { arguments.AsSpan(6, 3).CopyTo(record.AsSpan(6)); }
            arguments.AsSpan(9, 3).CopyTo(record.AsSpan(9));
        }
    }
}
