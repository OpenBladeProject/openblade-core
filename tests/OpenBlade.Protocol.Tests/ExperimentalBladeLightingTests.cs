using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class ExperimentalBladeLightingTests
{
    private static ExperimentalBladeModel Model => ModelFor(0x02A0);

    [Fact]
    public async Task RecoveryPaused_BlocksLightingBeforeAnyTraffic()
    {
        var session = new Session();
        var policy = new ExperimentalFeaturesPolicy(Model);
        policy.RequireRecovery();
        await using var port = Create(session, policy);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetLightingAsync(LightingSettingsFactory.SpectrumCycling(50), CancellationToken.None));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.Empty(session.Commands);
    }

    [Fact]
    public void Capabilities_AreExactPerExperimentalModel()
    {
        DeviceCapabilities blade16 = ExperimentalBladePort.CreateCapabilities(ModelFor(0x029F), true);
        Assert.True(blade16.SupportsUniformLightingFrames);
        Assert.Contains(LightingEffect.AmbientAwareness, blade16.LightingEffects);
        Assert.Contains(LightingEffect.AudioMeter, blade16.LightingEffects);

        DeviceCapabilities blade15 = ExperimentalBladePort.CreateCapabilities(ModelFor(0x0255), true);
        Assert.Contains(BreathingColorMode.Single, blade15.SupportedBreathingColorModes!);
        Assert.DoesNotContain(BreathingColorMode.Dual, blade15.SupportedBreathingColorModes!);
        Assert.DoesNotContain(LightingEffect.Wave, blade15.LightingEffects);
        Assert.Empty(blade15.LogoLightingModes);

        DeviceCapabilities blade18 = ExperimentalBladePort.CreateCapabilities(ModelFor(0x02A0), true);
        Assert.Contains(LightingEffect.Static, blade18.LightingEffects);
        Assert.False(blade18.SupportsUniformLightingFrames);
        Assert.DoesNotContain(LightingEffect.AmbientAwareness, blade18.LightingEffects);
    }

    [Theory]
    [InlineData(0x029F)]
    [InlineData(0x02A0)]
    public async Task UniformStatic_UsesExactSixBySixteenFrameAndRestoresReadableBaseline(int productId)
    {
        ExperimentalBladeModel model = ModelFor((ushort)productId);
        var session = new Session();
        await using var port = Create(session, new ExperimentalFeaturesPolicy(model), model);
        var color = new RgbColor(0x12, 0x34, 0x56);

        await port.SetLightingAsync(
            LightingSettingsFactory.Static(color, 50),
            CancellationToken.None);

        RazerCommandSpec[] rows = session.Commands
            .Where(command => command.ClassId == 0x03 && command.CommandId == 0x0B)
            .ToArray();
        Assert.Equal(6, rows.Length);
        for (int row = 0; row < rows.Length; row++)
        {
            byte[] payload = rows[row].Arguments.ToArray();
            Assert.Equal(52, payload.Length);
            Assert.Equal(new byte[] { 0xFF, (byte)row, 0, 15 }, payload[..4]);
            for (int offset = 4; offset < payload.Length; offset += 3)
            {
                Assert.Equal(new byte[] { color.Red, color.Green, color.Blue }, payload[offset..(offset + 3)]);
            }
        }
        Assert.Contains(session.Commands, command =>
            command.ClassId == 0x03 && command.CommandId == 0x0A
            && command.Arguments.Span.SequenceEqual(new byte[] { 5, 0 }));
        Assert.Equal(LightingRendererOwnershipState.Static, port.OwnershipState);
        Assert.Null(await port.ReadStateAsync(CancellationToken.None));

        Assert.Equal(
            LightingRendererOwnershipState.Static,
            await port.RelinquishRendererAsync(CancellationToken.None));
        Assert.Equal(LightingRendererOwnershipState.None, port.OwnershipState);
        Assert.Equal(3, session.Effect);
    }

    [Fact]
    public async Task Blade16UniformHostFrame_IsTypedSetOnlyAndKeepsSpatialLayoutOutOfProtocol()
    {
        ExperimentalBladeModel model = ModelFor(0x029F);
        var session = new Session();
        var policy = new ExperimentalFeaturesPolicy(model);
        await using var port = Create(session, policy, model);
        await port.SetLightingAsync(
            LightingSettingsFactory.AmbientAwareness(AmbientAwarenessRegion.Full, 50),
            CancellationToken.None);

        Assert.Equal(LightingRendererOwnershipState.AmbientAwareness, port.OwnershipState);
        await port.PushUniformLightingFrameAsync(
            new UniformLightingFrame(new RgbColor(9, 8, 7)),
            CancellationToken.None);
        Assert.Equal(6, session.Commands.Count(command => command.CommandId == 0x0B));
        Assert.All(
            session.Commands.Where(command => command.CommandId == 0x0B),
            command => Assert.DoesNotContain(command.Arguments.ToArray(),
                value => value == 17));
        Assert.Null(await port.ReadStateAsync(CancellationToken.None));
        Assert.Contains(policy.Snapshot().Observations, observation =>
            observation.Operation == "KeyboardUniformFrame"
            && observation.Outcome == "Unavailable"
            && observation.FailureCategory == "FrameColorReadbackUnavailable");
    }

    [Fact]
    public async Task Blade15SingleColorBreathing_UsesReadableStandardEffect()
    {
        ExperimentalBladeModel model = ModelFor(0x0255);
        var session = new Session();
        await using var port = Create(session, new ExperimentalFeaturesPolicy(model), model);
        var color = new RgbColor(0x22, 0x44, 0x66);

        await port.SetLightingAsync(
            LightingSettingsFactory.BreathingSingle(color, 50),
            CancellationToken.None);

        Assert.Contains(session.Commands, command =>
            command.ClassId == 0x03 && command.CommandId == 0x0A
            && command.Arguments.Span.SequenceEqual(
                new byte[] { 3, 1, color.Red, color.Green, color.Blue }));
        LightingSettings state = Assert.IsType<LightingSettings>(
            await port.ReadStateAsync(CancellationToken.None));
        Assert.Equal(LightingSettingsFactory.BreathingSingle(color, 50), state);
    }

    [Fact]
    public async Task Blade15SingleColorBreathingToRandom_ForcesBuiltinSetterAndConfirmsStandardEffect()
    {
        ExperimentalBladeModel model = ModelFor(0x0255);
        var session = new Session
        {
            Effect = 2,
            StandardEffect = [3, 1, 0x22, 0x44, 0x66],
        };
        await using var port = Create(session, new ExperimentalFeaturesPolicy(model), model);

        await port.SetLightingAsync(
            LightingSettingsFactory.BreathingRandom(50),
            CancellationToken.None);

        Assert.Contains(session.Commands, command =>
            command.ClassId == 0x0F && command.CommandId == 0x02
            && command.Arguments.Span.SequenceEqual(new byte[] { 1, 5, 2 }));
        Assert.Contains(session.Commands, command =>
            command.ClassId == 0x03 && command.CommandId == 0x8A);
        Assert.Equal(
            LightingSettingsFactory.BreathingRandom(50),
            await port.ReadStateAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Blade15UnknownStandardBreathingType_IsRejectedWithoutGuessingRandom()
    {
        ExperimentalBladeModel model = ModelFor(0x0255);
        var session = new Session
        {
            Effect = 2,
            StandardEffect = [7, 0, 0, 0, 0],
        };
        await using var port = Create(session, new ExperimentalFeaturesPolicy(model), model);

        DeviceStateContentRejectedException failure =
            await Assert.ThrowsAsync<DeviceStateContentRejectedException>(() =>
                port.ReadStateAsync(CancellationToken.None));

        Assert.Equal(DeviceStateQuery.KeyboardLightingEffect, failure.Query);
        Assert.All(session.Commands, command => Assert.True(command.CommandId >= 0x80));
    }

    [Theory]
    [InlineData(0x029D)]
    [InlineData(0x02C5)]
    public async Task Blade14Profiles_UseIndependentLogoReadbackForRicherModes(int productId)
    {
        ExperimentalBladeModel model = ModelFor((ushort)productId);
        var session = new Session();
        await using var port = Create(session, new ExperimentalFeaturesPolicy(model), model);

        await port.SetLightingAsync(
            LightingSettingsFactory.SpectrumCycling(50) with
            {
                LogoMode = LogoLightingMode.Breathing,
            },
            CancellationToken.None);

        Assert.Equal(
            LogoLightingMode.Breathing,
            await port.ReadLogoStateAsync(CancellationToken.None));
        Assert.Contains(session.Commands, command =>
            command.ClassId == 0x03 && command.CommandId == 0x02
            && command.Arguments.Span.SequenceEqual(new byte[] { 1, 4, 2 }));
    }

    [Fact]
    public async Task Blade15RejectsWaveLogoAndSpatialFramesBeforeSubmission()
    {
        ExperimentalBladeModel model = ModelFor(0x0255);
        var session = new Session();
        await using var port = Create(session, new ExperimentalFeaturesPolicy(model), model);

        await Assert.ThrowsAsync<CapabilityUnavailableException>(() =>
            port.SetLightingAsync(
                LightingSettingsFactory.Wave(LightingDirection.Rightward, 50),
                CancellationToken.None));
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() =>
            port.SetLightingAsync(
                LightingSettingsFactory.SpectrumCycling(50) with
                {
                    LogoMode = LogoLightingMode.Static,
                },
                CancellationToken.None));
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() =>
            port.PushUniformLightingFrameAsync(
                new UniformLightingFrame(new RgbColor(1, 2, 3)),
                CancellationToken.None));
        Assert.Empty(session.Commands);
    }

    [Fact]
    public async Task SharedFactoryGate_KeepsBatteryExchangeOutsideSixRowFrameTransaction()
    {
        ExperimentalBladeModel model = ModelFor(0x029F);
        var gate = new SemaphoreSlim(1, 1);
        var ownership = new ExperimentalLightingOwnershipState();
        var lightingSession = new Session { BlockFirstFrame = true };
        var batterySession = new ProbeSession();
        var policy = new ExperimentalFeaturesPolicy(model);
        await using var lighting = new ExperimentalBladePort(
            lightingSession, new(), model, policy, () => true,
            () => PowerSource.PluggedIn, sharedOperationGate: gate,
            sharedLightingOwnership: ownership);
        await using var battery = new ExperimentalBladePort(
            batterySession, new(), model, policy, () => true,
            () => PowerSource.PluggedIn, sharedOperationGate: gate,
            sharedLightingOwnership: ownership);

        Task frame = lighting.SetLightingAsync(
            LightingSettingsFactory.Static(new RgbColor(1, 2, 3), 50),
            CancellationToken.None);
        await lightingSession.FrameEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task<ConfirmedBatteryState> query = battery.QueryProtectionAsync(CancellationToken.None);
        await Task.Delay(25);
        Assert.False(batterySession.Entered.Task.IsCompleted);

        lightingSession.ReleaseFrame.TrySetResult();
        await frame;
        Assert.True((await query).ProtectionEnabled);
        Assert.Equal(7, lightingSession.Commands.Count(command =>
            command.ClassId == 0x03 && command.CommandId is 0x0B or 0x0A));
    }

    [Fact]
    public async Task SharedOwnership_SurvivesPortReplacementAndRestoresFromFreshSession()
    {
        ExperimentalBladeModel model = ModelFor(0x029F);
        var gate = new SemaphoreSlim(1, 1);
        var ownership = new ExperimentalLightingOwnershipState();
        var policy = new ExperimentalFeaturesPolicy(model);
        var firstSession = new Session();
        var first = new ExperimentalBladePort(
            firstSession, new(), model, policy, () => true,
            () => PowerSource.PluggedIn, sharedOperationGate: gate,
            sharedLightingOwnership: ownership);
        await first.SetLightingAsync(
            LightingSettingsFactory.Static(new RgbColor(1, 2, 3), 50),
            CancellationToken.None);
        await first.DisposeAsync();

        var replacementSession = new Session();
        await using var replacement = new ExperimentalBladePort(
            replacementSession, new(), model, policy, () => true,
            () => PowerSource.PluggedIn, sharedOperationGate: gate,
            sharedLightingOwnership: ownership);
        Assert.Equal(LightingRendererOwnershipState.Static, replacement.OwnershipState);
        Assert.Null(await replacement.ReadStateAsync(CancellationToken.None));

        Assert.Equal(
            LightingRendererOwnershipState.Static,
            await replacement.RelinquishRendererAsync(CancellationToken.None));
        Assert.Equal(LightingRendererOwnershipState.None, replacement.OwnershipState);
        Assert.Contains(replacementSession.Commands, command =>
            command.ClassId == 0x0F && command.CommandId == 0x02);
    }

    [Fact]
    public async Task IndeterminateFrameRow_PausesWithoutReplayAndFaultsOwnership()
    {
        ExperimentalBladeModel model = ModelFor(0x029F);
        var session = new Session { FailFrameRow = true };
        var policy = new ExperimentalFeaturesPolicy(model);
        await using var port = Create(session, policy, model);

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetLightingAsync(
                LightingSettingsFactory.Static(new RgbColor(1, 2, 3), 50),
                CancellationToken.None));

        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.False(policy.WritesAllowed);
        Assert.Equal(LightingRendererOwnershipState.Faulted, port.OwnershipState);
        Assert.Single(session.Commands, command => command.CommandId == 0x0B);
        int count = session.Commands.Count;
        await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetLightingAsync(
                LightingSettingsFactory.Static(new RgbColor(4, 5, 6), 50),
                CancellationToken.None));
        Assert.Equal(count, session.Commands.Count);
    }

    [Fact]
    public async Task FaultedFrame_ExplicitBuiltinChoiceReassertsAndConfirmsBeforeClearing()
    {
        ExperimentalBladeModel model = ModelFor(0x029F);
        var session = new Session { FailFrameRow = true };
        var policy = new ExperimentalFeaturesPolicy(model);
        var ownership = new ExperimentalLightingOwnershipState();
        await using var port = new ExperimentalBladePort(
            session, new(), model, policy, () => true,
            () => PowerSource.PluggedIn,
            sharedLightingOwnership: ownership);
        await Assert.ThrowsAsync<BladeWriteException>(() => port.SetLightingAsync(
            LightingSettingsFactory.Static(new RgbColor(1, 2, 3), 50),
            CancellationToken.None));
        policy.CompleteRecovery();
        session.FailFrameRow = false;
        int before = session.Commands.Count;

        await port.SetLightingAsync(
            LightingSettingsFactory.SpectrumCycling(50),
            CancellationToken.None);

        Assert.Contains(session.Commands.Skip(before), command =>
            command.ClassId == 0x0F && command.CommandId == 0x02
            && command.Arguments.Span.SequenceEqual(new byte[] { 1, 5, 3 }));
        Assert.Equal(LightingRendererOwnershipState.None, port.OwnershipState);
        Assert.Equal(
            LightingEffect.Spectrum,
            (await port.ReadStateAsync(CancellationToken.None))!.Effect);
    }

    [Fact]
    public async Task FaultedFrame_FailedBuiltinReassertRemainsFaultedAndPaused()
    {
        ExperimentalBladeModel model = ModelFor(0x029F);
        var session = new Session { FailFrameRow = true };
        var policy = new ExperimentalFeaturesPolicy(model);
        var ownership = new ExperimentalLightingOwnershipState();
        await using var port = new ExperimentalBladePort(
            session, new(), model, policy, () => true,
            () => PowerSource.PluggedIn,
            sharedLightingOwnership: ownership);
        await Assert.ThrowsAsync<BladeWriteException>(() => port.SetLightingAsync(
            LightingSettingsFactory.Static(new RgbColor(1, 2, 3), 50),
            CancellationToken.None));
        policy.CompleteRecovery();
        session.FailFrameRow = false;
        session.FailEffect = true;

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetLightingAsync(
                LightingSettingsFactory.SpectrumCycling(50),
                CancellationToken.None));

        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.Equal(LightingRendererOwnershipState.Faulted, port.OwnershipState);
        Assert.False(policy.WritesAllowed);
    }

    [Fact]
    public async Task HostFrameToSameIntentColorStatic_AlwaysRepaintsAllRows()
    {
        ExperimentalBladeModel model = ModelFor(0x029F);
        var session = new Session();
        var ownership = new ExperimentalLightingOwnershipState();
        await using var port = new ExperimentalBladePort(
            session, new(), model, new ExperimentalFeaturesPolicy(model),
            () => true, () => PowerSource.PluggedIn,
            sharedLightingOwnership: ownership);
        LightingSettings ambient = LightingSettingsFactory.AmbientAwareness(
            AmbientAwarenessRegion.Full,
            50);
        await port.SetLightingAsync(ambient, CancellationToken.None);
        await port.PushUniformLightingFrameAsync(
            new UniformLightingFrame(new RgbColor(9, 8, 7)),
            CancellationToken.None);
        int before = session.Commands.Count;

        await port.SetLightingAsync(
            LightingSettingsFactory.Static(ambient.Color, 50),
            CancellationToken.None);

        RazerCommandSpec[] rows = session.Commands.Skip(before)
            .Where(command => command.ClassId == 0x03 && command.CommandId == 0x0B)
            .ToArray();
        Assert.Equal(ExperimentalBladeLightingFrame.RowCount, rows.Length);
        Assert.All(rows, command =>
            Assert.All(command.Arguments.Span[4..].ToArray().Chunk(3), rgb =>
                Assert.Equal(
                    new byte[] { ambient.Color.Red, ambient.Color.Green, ambient.Color.Blue },
                    rgb)));
        Assert.Equal(LightingRendererOwnershipState.Static, port.OwnershipState);
    }

    [Fact]
    public async Task UniformFrameTelemetry_AggregatesSuccessesAndRetainsPriorOperationsAndFailure()
    {
        ExperimentalBladeModel model = ModelFor(0x029F);
        var session = new Session();
        var policy = new ExperimentalFeaturesPolicy(model);
        await using var port = Create(session, policy, model);
        policy.Record("BatteryProtection", "Readback", "Confirmed", confirmed: "80");
        policy.Record("Performance", "Readback", "Confirmed", confirmed: "Balanced");
        await port.SetLightingAsync(
            LightingSettingsFactory.AmbientAwareness(AmbientAwarenessRegion.Full, 50),
            CancellationToken.None);

        for (int frame = 0; frame < 100; frame++)
        {
            await port.PushUniformLightingFrameAsync(
                new UniformLightingFrame(new RgbColor((byte)frame, 2, 3)),
                CancellationToken.None);
        }

        DiagnosticExperimentalSupport beforeFailure = policy.Snapshot();
        Assert.True(beforeFailure.Observations.Count <= DiagnosticExperimentalSupport.MaxObservations);
        Assert.Contains(beforeFailure.Observations, item => item.Operation == "BatteryProtection");
        Assert.Contains(beforeFailure.Observations, item => item.Operation == "Performance");
        ExperimentalControlObservation aggregate = Assert.Single(
            beforeFailure.Observations,
            item => item.Operation == "KeyboardUniformFrame");
        Assert.Equal("AppliedWithoutColorReadback;Frames=100", aggregate.ConfirmedValue);
        Assert.DoesNotContain(beforeFailure.Observations, item =>
            item.Operation == "KeyboardUniformFrame"
            && item.Stage == "Submission"
            && item.Outcome == "Acknowledged");

        session.FailFrameRow = true;
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.PushUniformLightingFrameAsync(
                new UniformLightingFrame(new RgbColor(1, 2, 3)),
                CancellationToken.None));

        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.False(policy.WritesAllowed);
        Assert.Equal(LightingRendererOwnershipState.Faulted, port.OwnershipState);
        DiagnosticExperimentalSupport afterFailure = policy.Snapshot();
        Assert.True(afterFailure.Observations.Count <= DiagnosticExperimentalSupport.MaxObservations);
        Assert.Contains(afterFailure.Observations, item => item.Operation == "BatteryProtection");
        Assert.Contains(afterFailure.Observations, item => item.Operation == "Performance");
        Assert.Contains(afterFailure.Observations, item =>
            item.Operation == "KeyboardUniformFrame"
            && item.Outcome == "Indeterminate"
            && item.Submitted);

        policy.CompleteRecovery();
        session.FailFrameRow = false;
        await port.SetLightingAsync(
            LightingSettingsFactory.AmbientAwareness(AmbientAwarenessRegion.Full, 50),
            CancellationToken.None);
        for (int frame = 0; frame < 40; frame++)
        {
            await port.PushUniformLightingFrameAsync(
                new UniformLightingFrame(new RgbColor((byte)frame, 4, 5)),
                CancellationToken.None);
        }

        DiagnosticExperimentalSupport afterLaterSuccesses = policy.Snapshot();
        Assert.True(afterLaterSuccesses.Observations.Count
            <= DiagnosticExperimentalSupport.MaxObservations);
        Assert.Contains(afterLaterSuccesses.Observations,
            item => item.Operation == "BatteryProtection");
        Assert.Contains(afterLaterSuccesses.Observations,
            item => item.Operation == "Performance");
        Assert.Contains(afterLaterSuccesses.Observations, item =>
            item.Operation == "KeyboardUniformFrame"
            && item.Outcome == "Indeterminate"
            && item.Submitted);
        Assert.Contains(afterLaterSuccesses.Observations, item =>
            item.Operation == "KeyboardUniformFrame"
            && item.ConfirmedValue == "AppliedWithoutColorReadback;Frames=140");
    }

    [Fact]
    public async Task LowPowerInvalidation_IsWriteFreeAndFreshApplyRepaints()
    {
        ExperimentalBladeModel model = ModelFor(0x029F);
        var session = new Session();
        var ownership = new ExperimentalLightingOwnershipState();
        var policy = new ExperimentalFeaturesPolicy(model);
        var port = new ExperimentalBladePort(
            session, new(), model, policy,
            () => true, () => PowerSource.PluggedIn,
            sharedLightingOwnership: ownership);
        LightingSettings requested = LightingSettingsFactory.Static(
            new RgbColor(1, 2, 3), 50);
        await port.SetLightingAsync(requested,
            CancellationToken.None);
        int before = session.Commands.Count;

        Assert.Equal(
            LightingRendererOwnershipState.Static,
            await port.InvalidateForLowPowerAsync());

        Assert.Equal(before, session.Commands.Count);
        Assert.Equal(LightingRendererOwnershipState.Static,
            (LightingRendererOwnershipState)Volatile.Read(ref ownership.Ownership));
        Assert.Equal(1, session.DisposeCount);

        var replacementSession = new Session();
        await using var replacement = new ExperimentalBladePort(
            replacementSession, new(), model, policy,
            () => true, () => PowerSource.PluggedIn,
            sharedLightingOwnership: ownership);
        await replacement.SetLightingAsync(requested, CancellationToken.None);

        Assert.Equal(ExperimentalBladeLightingFrame.RowCount,
            replacementSession.Commands.Count(command =>
                command.ClassId == 0x03 && command.CommandId == 0x0B));
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
        DeviceStateContentRejectedException rejection = Assert.IsType<DeviceStateContentRejectedException>(failure.InnerException);
        Assert.Equal(DeviceStateQuery.KeyboardLightingEffect, rejection.Query);
        Assert.StartsWith($"01 05 {effect:X2}", rejection.RejectedPayloadHex);
    }

    [Fact]
    public async Task UnknownPaddedEffect_RetainsOnlyBoundedPayloadEvidence()
    {
        var session = new Session { Effect = 99, EffectReplyLength = 80 };
        await using var port = Create(session, Enabled());
        DeviceStateContentRejectedException rejection = await Assert.ThrowsAsync<DeviceStateContentRejectedException>(() =>
            port.ReadStateAsync(CancellationToken.None));
        Assert.Equal(DeviceStateQuery.KeyboardLightingEffect, rejection.Query);
        Assert.Equal(16 * 3 - 1 + 4, rejection.RejectedPayloadHex.Length);
        Assert.EndsWith(" ...", rejection.RejectedPayloadHex);
        Assert.All(session.Commands, command => Assert.True(command.CommandId >= 0x80));
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
        session.BeforeSubmission = command => { if (command.CommandId < 0x80) { policy.RequireRecovery(); } };
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
        DeviceStateContentRejectedException rejection = await Assert.ThrowsAsync<DeviceStateContentRejectedException>(() =>
            port.ReadStateAsync(CancellationToken.None));
        Assert.Equal(DeviceStateQuery.KeyboardLightingCombination, rejection.Query);
        Assert.StartsWith($"01 05 {raw:X2} / 01 05 04 01", rejection.RejectedPayloadHex);
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

    private static ExperimentalBladeModel ModelFor(ushort productId) =>
        ExperimentalBladeModels.All.Single(item => item.ProductId == productId);
    private static ExperimentalFeaturesPolicy Enabled() => new(Model);
    private static ExperimentalBladePort Create(
        Session session,
        ExperimentalFeaturesPolicy policy,
        ExperimentalBladeModel? model = null) =>
        new(session, new(), model ?? Model, policy, () => true, () => PowerSource.PluggedIn);

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
        internal byte[] StandardEffect { get; set; } = [0, 0, 0, 0, 0];
        internal Action<RazerCommandSpec>? BeforeSubmission { get; set; }
        internal bool BlockFirstFrame { get; set; }
        internal bool FailFrameRow { get; set; }
        internal TaskCompletionSource FrameEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseFrame { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
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
            if (BlockFirstFrame
                && command.ClassId == 0x03
                && command.CommandId == 0x0B)
            {
                BlockFirstFrame = false;
                FrameEntered.TrySetResult();
                return WaitForFrameReleaseAsync();
            }
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
                    if (Effect == 2)
                    {
                        StandardEffect = [0, 0, 0, 0, 0];
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
            else if (command.ClassId == 0x03 && command.CommandId == 0x0B)
            {
                if (FailFrameRow)
                {
                    return Task.FromResult(
                        new HidSessionResult<ReadOnlyMemory<byte>>(
                            default,
                            HidSessionFailureKind.Timeout)
                        {
                            Diagnostic = new(
                                HidSessionFailurePhase.GetCompletion,
                                HidSessionFailureKind.Timeout,
                                null,
                                null),
                        });
                }
                response = AckPayload;
            }
            else if (command.ClassId == 0x03 && command.CommandId == 0x0A)
            {
                if (args[0] == 6)
                {
                    Effect = 1;
                    StandardEffect = [6, args[1], args[2], args[3], 0];
                }
                else if (args[0] == 3)
                {
                    Effect = 2;
                    StandardEffect = args;
                }
                response = AckPayload;
            }
            else if (command.ClassId == 0x03 && command.CommandId == 0x8A)
            {
                response = StandardEffect;
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

            async Task<HidSessionResult<ReadOnlyMemory<byte>>> WaitForFrameReleaseAsync()
            {
                await ReleaseFrame.Task.WaitAsync(token);
                return new HidSessionResult<ReadOnlyMemory<byte>>(AckPayload, null);
            }
        }
        internal int DisposeCount { get; private set; }
        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ProbeSession : IRazerCommandSession
    {
        internal TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public RazerCommandSessionDisposition Disposition =>
            RazerCommandSessionDisposition.Healthy;

        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(
            RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId,
            TimeSpan exchangeTimeout,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert.Equal((byte)0x07, command.ClassId);
            Assert.Equal((byte)0x92, command.CommandId);
            _ = assignTransactionId();
            Entered.TrySetResult();
            return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(
                new byte[] { 0xD0 }, null));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
