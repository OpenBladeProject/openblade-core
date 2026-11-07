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
        Assert.True(CoolingPadProtocol.TryDecodeFan([0, 4, 0], out var state));
        Assert.Equal(new CoolingPadFanConfiguration(CoolingPadFanOwnership.Firmware, CoolingPadFanPreset.Balanced, 0), state);
        Assert.False(CoolingPadProtocol.TryDecodeFan([2, 4, 0], out _));
        Assert.False(CoolingPadProtocol.TryDecodeFan([0, 7, 0], out _));
        Assert.False(CoolingPadProtocol.TryDecodeFan([1, 1, 65], out _));
    }

    [Fact]
    public void GeometryAdmissionRejectsAnotherAccessoryOrTruncatedDescriptor()
    {
        byte[] region = new byte[80]; region[0] = 5; region[3] = 18; region[4] = 1;
        Assert.True(CoolingPadProtocol.TryDecodeRegion(region, out byte id)); Assert.Equal(5, id);
        region[3] = 16;
        Assert.False(CoolingPadProtocol.TryDecodeRegion(region, out _));
        Assert.False(CoolingPadProtocol.TryDecodeRegion(region.AsSpan(0, 4), out _));
        Assert.Throws<ArgumentException>(() => CoolingPadProtocol.FrameSet(new CoolingPadColor[19]));
    }

    [Fact]
    public async Task FixedTransitionUsesDestinationDirectlyAndIndependentReadback()
    {
        var wire = new Wire(); await using var device = Create(wire);
        await device.SetFanAsync(CoolingPadFanMode.Fixed, 2200, () => true, default);
        Assert.Equal(new[] { "0D10:01012C", "0D90:000000" }, wire.Commands);
        Assert.Equal(new CoolingPadFanConfiguration(CoolingPadFanOwnership.Synapse, CoolingPadFanPreset.Medium, 2200), wire.Fan);
        Assert.DoesNotContain(wire.Commands, x => x.StartsWith("0004", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FixedConfiguredTargetDoesNotReuseAnOlderHostDemandReading()
    {
        var wire = new Wire { Fan = new(CoolingPadFanOwnership.Synapse, CoolingPadFanPreset.Medium, 2200) };
        await using var device = Create(wire);
        // The independent host-demand register starts at zero; the fixed-mode target is in0D90.
        var snapshot = await device.ReadSnapshotAsync(default);
        Assert.Equal(2200, snapshot.TargetRpm);
        Assert.Equal(CoolingPadFanMode.Fixed, snapshot.FanMode);
    }
    [Fact]
    public async Task SmartTransitionPreservesSelectedSmartPresetAndDoesNotResetThroughFixed()
    {
        var wire = new Wire { Fan = new(CoolingPadFanOwnership.Firmware, CoolingPadFanPreset.Performance, 0) };
        await using var device = Create(wire);
        await device.SetFanAsync(CoolingPadFanMode.HostCurve, 1500, () => true, default);
        Assert.Equal(new[] { "0D90:000000", "0D01:01051E", "0D81:010500" }, wire.Commands);
        Assert.Equal(CoolingPadFanPreset.Performance, wire.Fan.Preset);
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
        internal CoolingPadFanConfiguration Fan { get; set; } = new(CoolingPadFanOwnership.Firmware, CoolingPadFanPreset.Off, 0);
        internal CoolingPadLightingConfiguration Effect { get; set; } = new(1, 0, 1, 0, 0, [new(0, 255, 0)]);
        internal CoolingPadLightingConfiguration? ActiveEffect { get; set; }
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
                case (13, 16): CoolingPadProtocol.TryDecodeFan(p, out var fan); Fan = fan!; target = Fan.ConfiguredRpm; break;
                case (13, 144): p = [(byte)Fan.Ownership, (byte)Fan.Preset, (byte)(WrongFanReadback ? 1 : Fan.ConfiguredRpm / 50)]; break;
                case (13, 1): target = p[2] * 50; break;
                case (13, 129): p = [1, 5, (byte)(target / 50)]; break;
                case (0, 4): Mode = (CoolingPadDeviceMode)p[0]; break;
                case (0, 132): p = [(byte)Mode, 0]; break;
                case (15, 128): p = new byte[80]; p[0] = 5; p[3] = 18; p[4] = 1; break;
                case (15, 132): p = [1, 5, RawBrightness]; break;
                case (15, 4): RawBrightness = p[2]; break;
                case (15, 130):
                    var selectedEffect = p[0] == 0 ? ActiveEffect ?? Effect with { Profile = 0 } : Effect;
                    p = new byte[80]; var encoded = CoolingPadProtocol.EffectSet(selectedEffect).Arguments; encoded.CopyTo(p); break;
                case (15, 2):
                    byte[] effect = new byte[80]; p.CopyTo(effect, 0);
                    // The emulated GET persistent profile returns the previous persistent effect for volatile custom mode.
                    CoolingPadProtocol.TryDecodeEffect(effect, out var decoded);
                    if (p[0] == 1) { Effect = decoded!; } else { ActiveEffect = decoded; }
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
    }
}
