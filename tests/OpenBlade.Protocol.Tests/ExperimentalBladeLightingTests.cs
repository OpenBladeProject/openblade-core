using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class ExperimentalBladeLightingTests
{
    private static ExperimentalBladeModel Model => ExperimentalBladeModels.All.Single(item => item.ProductId == 0x02A0);

    [Fact]
    public async Task ConsentOff_BlocksLightingBeforeAnyTraffic()
    {
        var session = new Session();
        await using var port = Create(session, new ExperimentalFeaturesPolicy(Model));
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetLightingAsync(LightingSettingsFactory.SpectrumCycling(50), CancellationToken.None));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.Empty(session.Commands);
    }

    [Fact]
    public async Task OlderExperimentalModels_DoNotReceiveLightingCommands()
    {
        var model = ExperimentalBladeModels.All[0];
        var session = new Session();
        var policy = new ExperimentalFeaturesPolicy(model) { Enabled = true };
        await using var port = new ExperimentalBladePort(session, new(), model, policy, () => true, () => PowerSource.PluggedIn);
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() =>
            port.SetLightingAsync(LightingSettingsFactory.SpectrumCycling(50), CancellationToken.None));
        Assert.Empty(session.Commands);
    }

    [Fact]
    public async Task Brightness_UsesUpstreamRoundedRawAndIndependentGetWithoutChangingEffect()
    {
        var session = new Session();
        var policy = Enabled();
        await using var port = Create(session, policy);
        await port.SetLightingAsync(LightingSettingsFactory.SpectrumCycling(77), CancellationToken.None);
        RazerCommandSpec setter = Assert.Single(session.Commands, command => command.CommandId < 0x80);
        Assert.Equal(0x03, setter.ClassId);
        Assert.Equal(0x03, setter.CommandId);
        Assert.Equal(new byte[] { 1, 5, 196 }, setter.Arguments.ToArray());
        Assert.Equal(196, session.Raw);
        Assert.Contains(policy.Snapshot().Observations, item => item.Stage == "Readback" && item.Submitted);
        Assert.All(session.Commands, command => Assert.NotEqual(0x00, command.ClassId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LeavingOff_ConfirmsBrightnessBeforeEffectAndPreservesReadablePartialState(bool failEffect)
    {
        var session = new Session { Raw = 0, Effect = 0, FailEffect = failEffect };
        var policy = Enabled();
        await using var port = Create(session, policy);
        if (failEffect)
        {
            BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
                port.SetLightingAsync(LightingSettingsFactory.SpectrumCycling(50), CancellationToken.None));
            Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
            Assert.False(policy.WritesAllowed);
            LightingSettings partial = Assert.IsType<LightingSettings>(await port.ReadStateAsync(CancellationToken.None));
            Assert.Equal(LightingEffect.Off, partial.Effect);
        }
        else
        {
            await port.SetLightingAsync(LightingSettingsFactory.SpectrumCycling(50), CancellationToken.None);
        }
        Assert.Equal(128, session.Raw);
        Assert.Equal(failEffect ? 0 : 3, session.Effect);
        Assert.Equal(new byte[] { 0x03, 0x02 }, session.Commands.Where(command => command.CommandId < 0x80)
            .Select(command => command.CommandId));
        int brightness = session.Commands.FindIndex(command => command.CommandId == 0x03);
        Assert.Equal(0x83, session.Commands[brightness + 1].CommandId);
    }

    [Theory]
    [InlineData(LightingEffect.Off)]
    [InlineData(LightingEffect.Breathing)]
    [InlineData(LightingEffect.Wave)]
    public async Task Effects_AcceptNonEchoAckThenIndependentlyConfirm(LightingEffect effect)
    {
        var session = new Session { AckPayload = [9, 8] };
        await using var port = Create(session, Enabled());
        LightingSettings target = effect switch
        {
            LightingEffect.Off => LightingSettingsFactory.Off(),
            LightingEffect.Breathing => LightingSettingsFactory.BreathingRandom(50),
            LightingEffect.Wave => LightingSettingsFactory.Wave(LightingDirection.Rightward, 50),
            _ => throw new InvalidOperationException(),
        };
        await port.SetLightingAsync(target, CancellationToken.None);
        RazerCommandSpec setter = Assert.Single(session.Commands,
            command => command.ClassId == 0x0F && command.CommandId == 0x02);
        Assert.True(setter.AllowVariableResponseLength);
        Assert.NotNull(setter.PayloadValidator);
        Assert.Equal(effect == LightingEffect.Wave ? new byte[] { 1, 5, 4, 1 }
            : effect == LightingEffect.Off ? new byte[] { 1, 5, 0 } : new byte[] { 1, 5, 2 }, setter.Arguments.ToArray());
        int index = session.Commands.IndexOf(setter);
        Assert.Equal(0x82, session.Commands[index + 1].CommandId);
        Assert.Equal(0x0F, session.Commands[index + 1].ClassId);
        Assert.All(session.Commands, command => Assert.NotEqual(0x00, command.ClassId));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(80)]
    public async Task EffectGetter_ValidatesBoundedPaddedGeometry(int length)
    {
        var session = new Session { EffectReplyLength = length };
        await using var port = Create(session, Enabled());
        LightingSettings? state = await port.ReadStateAsync(CancellationToken.None);
        Assert.Equal(LightingEffect.Spectrum, state!.Effect);
    }

    [Theory]
    [InlineData(1, 3, 0, false)]
    [InlineData(4, 3, 1, false)]
    [InlineData(4, 4, 2, false)]
    [InlineData(3, 6, 0, true)]
    public async Task UnrepresentableBaseline_BlocksEverySetter(byte effect, int length, byte direction, bool unknownTail)
    {
        var session = new Session { Effect = effect, EffectReplyLength = length, Direction = direction, UnknownTail = unknownTail };
        var policy = Enabled();
        await using var port = Create(session, policy);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetLightingAsync(LightingSettingsFactory.SpectrumCycling(60), CancellationToken.None));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.All(session.Commands, command => Assert.True(command.CommandId >= 0x80));
        Assert.Contains(policy.Snapshot().Observations, item => item.Outcome == "UnsupportedState");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AckWithoutApplyingEffectOrBrightness_PausesWithoutReplay(bool effect)
    {
        var session = new Session { IgnoreEffect = effect, IgnoreBrightness = !effect };
        var policy = Enabled();
        await using var port = Create(session, policy);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetLightingAsync(effect ? LightingSettingsFactory.BreathingRandom(50)
                : LightingSettingsFactory.SpectrumCycling(70), CancellationToken.None));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.Single(session.Commands, command => command.CommandId < 0x80);
        Assert.False(policy.WritesAllowed);
        var second = new Session();
        await using var next = Create(second, policy);
        await Assert.ThrowsAsync<BladeWriteException>(() => next.SetChargeLimitAsync(80, CancellationToken.None));
        Assert.Empty(second.Commands);
    }

    [Theory]
    [InlineData(LogoLightingMode.Off)]
    [InlineData(LogoLightingMode.Static)]
    [InlineData(LogoLightingMode.Breathing)]
    public async Task Logo_UsesOnlyItsSourceBackedRegistersAndIndependentGet(LogoLightingMode mode)
    {
        var session = new Session { LogoOn = mode == LogoLightingMode.Off, LogoEffect = 2 };
        await using var port = Create(session, Enabled());
        await port.SetLightingAsync(LightingSettingsFactory.SpectrumCycling(50) with { LogoMode = mode }, CancellationToken.None);
        Assert.Equal(mode, await port.ReadLogoStateAsync(CancellationToken.None));
        RazerCommandSpec[] setters = session.Commands.Where(command => command.CommandId < 0x80).ToArray();
        Assert.Equal(mode == LogoLightingMode.Off ? 1 : 2, setters.Length);
        Assert.All(setters, command => Assert.Equal(0x03, command.ClassId));
        Assert.All(setters, command => Assert.Equal(4, command.Arguments.Span[1]));
        Assert.Equal(0x00, setters[^1].CommandId);
    }

    [Fact]
    public async Task PartialLogoChange_PausesAllControlsWithoutSubmittingAnotherSetter()
    {
        var session = new Session { FailLogoPower = true };
        var policy = Enabled();
        await using var port = Create(session, policy);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetLightingAsync(LightingSettingsFactory.SpectrumCycling(50) with { LogoMode = LogoLightingMode.Breathing }, CancellationToken.None));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.Equal(2, session.Commands.Count(command => command.CommandId < 0x80));
        Assert.False(policy.WritesAllowed);
        int count = session.Commands.Count;
        await Assert.ThrowsAsync<BladeWriteException>(() => port.SetPerformanceAsync(PerformancePreset.Balanced, CancellationToken.None));
        Assert.Equal(count, session.Commands.Count);
    }

    [Fact]
    public async Task RevokedAtNativeBoundary_DoesNotSubmitQueuedLightingWrite()
    {
        var session = new Session();
        var policy = Enabled();
        session.BeforeSubmission = command => { if (command.CommandId < 0x80) { policy.Enabled = false; } };
        await using var port = Create(session, policy);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetLightingAsync(LightingSettingsFactory.SpectrumCycling(70), CancellationToken.None));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.All(session.Commands, command => Assert.True(command.CommandId >= 0x80));
    }

    [Fact]
    public async Task RetainedLowPowerAdmission_BlocksWriteAtSubmission()
    {
        var session = new Session();
        var policy = Enabled();
        bool active = true;
        session.BeforeSubmission = command => { if (command.CommandId < 0x80) { active = false; } };
        await using var port = Create(session, policy);
        port.SetAdditionalWriteAdmission(() => active);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetLightingAsync(LightingSettingsFactory.SpectrumCycling(70), CancellationToken.None));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.All(session.Commands, command => Assert.True(command.CommandId >= 0x80));
    }

    [Fact]
    public async Task NativeBrightnessStep_IsUnavailableWithoutBorrowingAnotherModelsDelta()
    {
        var session = new Session();
        await using var port = Create(session, Enabled());
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() => port.StepBrightnessAsync(KeyboardBrightnessDirection.Increase, CancellationToken.None));
        Assert.Empty(session.Commands);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task WaveAtZeroPercent_IsReportedAsUnsupportedAndBlocksWrites(int raw)
    {
        var session = new Session { Raw = (byte)raw, Effect = 4 };
        var policy = Enabled();
        await using var port = Create(session, policy);
        await Assert.ThrowsAsync<IOException>(() => port.ReadStateAsync(CancellationToken.None));
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetLightingAsync(LightingSettingsFactory.SpectrumCycling(77), CancellationToken.None));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.Contains(policy.Snapshot().Observations, item =>
            item.FailureCategory == "UnrepresentableZeroBrightnessEffect" && item.ConfirmedValue == $"Effect=Wave;Raw={raw};Percent=0");
        Assert.All(session.Commands, command => Assert.True(command.CommandId >= 0x80));
        Assert.True(policy.WritesAllowed);
    }

    [Fact]
    public async Task LightingInvalidation_ClosesSessionOnceWithoutFirmwareWrites()
    {
        var session = new Session();
        var port = Create(session, Enabled());
        LightingRendererOwnershipState[] results = await Task.WhenAll(port.InvalidateAsync(), port.InvalidateForLowPowerAsync());
        Assert.All(results, state => Assert.Equal(LightingRendererOwnershipState.None, state));
        await port.DisposeAsync();
        Assert.Equal(1, session.DisposeCount);
        Assert.Empty(session.Commands);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => port.ReadStateAsync(CancellationToken.None));
    }

    private static ExperimentalFeaturesPolicy Enabled() => new(Model) { Enabled = true };
    private static ExperimentalBladePort Create(Session session, ExperimentalFeaturesPolicy policy) =>
        new(session, new(), Model, policy, () => true, () => PowerSource.PluggedIn);

    private sealed class Session : IRazerCommandSession
    {
        internal byte Raw { get; set; } = 128;
        internal byte Effect { get; set; } = 3;
        internal byte Direction { get; set; } = 1;
        internal int EffectReplyLength { get; set; } = 6;
        internal bool UnknownTail { get; set; }
        internal bool LogoOn { get; set; }
        internal byte LogoEffect { get; set; }
        internal bool IgnoreEffect { get; set; }
        internal bool FailEffect { get; set; }
        internal bool IgnoreBrightness { get; set; }
        internal bool FailLogoPower { get; set; }
        internal byte[] AckPayload { get; set; } = [];
        internal Action<RazerCommandSpec>? BeforeSubmission { get; set; }
        internal List<RazerCommandSpec> Commands { get; } = [];
        public RazerCommandSessionDisposition Disposition => RazerCommandSessionDisposition.Healthy;
        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId, TimeSpan exchangeTimeout, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            BeforeSubmission?.Invoke(command);
            if (command.SubmissionAllowed?.Invoke() == false)
            {
                return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(default, HidSessionFailureKind.IdentityBlocked)
                { Diagnostic = new(HidSessionFailurePhase.Guard, HidSessionFailureKind.IdentityBlocked, null, null) });
            }
            Commands.Add(command);
            _ = assignTransactionId();
            byte[] args = command.Arguments.ToArray();
            byte[] response;
            if (command.ClassId == 0x0F && command.CommandId == 0x82)
            {
                response = new byte[EffectReplyLength];
                response[0] = 1; response[1] = 5; response[2] = Effect;
                if (Effect == 4 && response.Length > 3)
                {
                    response[3] = Direction;
                }

                if (UnknownTail && response.Length > 4)
                {
                    response[4] = 9;
                }
            }
            else if (command.ClassId == 0x0F && command.CommandId == 0x02)
            {
                if (FailEffect)
                {
                    return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(default, HidSessionFailureKind.Timeout)
                    { Diagnostic = new(HidSessionFailurePhase.GetCompletion, HidSessionFailureKind.Timeout, null, null) });
                }
                if (!IgnoreEffect)
                {
                    Effect = args[2]; if (args.Length > 3)
                    {
                        Direction = args[3];
                    }
                }
                response = AckPayload;
            }
            else if (command.ClassId == 0x03 && command.CommandId == 0x83)
            {
                response = [1, 5, Raw];
            }
            else if (command.ClassId == 0x03 && command.CommandId == 0x03)
            {
                if (!IgnoreBrightness)
                {
                    Raw = args[2];
                }

                response = args;
            }
            else if (command.ClassId == 0x03 && command.CommandId == 0x80)
            {
                response = [1, 4, LogoOn ? (byte)1 : (byte)0];
            }
            else if (command.ClassId == 0x03 && command.CommandId == 0x82)
            {
                response = [1, 4, LogoEffect];
            }
            else if (command.ClassId == 0x03 && command.CommandId == 0x02) { LogoEffect = args[2]; response = args; }
            else if (command.ClassId == 0x03 && command.CommandId == 0x00)
            {
                if (FailLogoPower)
                {
                    return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(default, HidSessionFailureKind.Timeout)
                    { Diagnostic = new(HidSessionFailurePhase.GetCompletion, HidSessionFailureKind.Timeout, null, null) });
                }

                LogoOn = args[2] != 0; response = args;
            }
            else
            {
                throw new InvalidOperationException("Unexpected command in lighting test.");
            }

            return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(response, null));
        }
        internal int DisposeCount { get; private set; }
        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}
