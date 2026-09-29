using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class ProductionLightingPortTests
{
    private static readonly TimeSpan TestDeadlockGuard = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(ReactiveLightingDuration.Short, 500)]
    [InlineData(ReactiveLightingDuration.Medium, 1500)]
    [InlineData(ReactiveLightingDuration.Long, 2000)]
    public void ReactiveFadeDuration_MatchesPhysicalSynapseCaptures(
        ReactiveLightingDuration duration,
        int expectedMilliseconds)
    {
        Assert.Equal(
            TimeSpan.FromMilliseconds(expectedMilliseconds),
            ProductionLightingPort.ReactiveFadeDuration(duration));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task LowPowerClose_DoesNotAddWritesAfterSeparateGuardedReleaseOrExpiredBoundary(
        bool rz090528,
        bool releasePermitted)
    {
        var session = new LightingSession(initialBrightness: 0xFF)
        {
            DeviceMode = RazerDeviceMode.Normal,
        };
        IProductionLightingPort port = CreateDriverModeCleanupPort(session, rz090528).Port;
        await port.EnsureDriverModeAsync(default);
        Assert.True(OwnsDriverMode(port));

        bool? released = await port.ReleaseDriverModeForLowPowerAsync(
            () => releasePermitted, default);
        int commandsBeforeClose = session.Commands.Count;
        await port.InvalidateForLowPowerAsync();
        await port.DisposeAsync();

        Assert.Equal(releasePermitted ? false : (bool?)null, released);
        Assert.Equal(releasePermitted ? RazerDeviceMode.Normal : RazerDeviceMode.Driver, session.DeviceMode);
        Assert.Equal(commandsBeforeClose, session.Commands.Count);
        Assert.Equal(RazerCommandSessionDisposition.FullyDisposed, session.Disposition);
    }

    [Fact]
    public void ProductionAdmissionUsesOracleAndReviewedSemanticEvidence()
    {
        ProductionLightingOracleEvidence evidence =
            ProductionLightingEvidenceSource.ReadValidatedOracle();
        IProductionLightingPortFactory factory = ProductionLightingFactory.CreateWindows();
        var windowsFactory = Assert.IsType<ProductionLightingFactory>(factory);

        Assert.Equal(TargetBladeDevice.Model, evidence.Device.Model);
        Assert.Equal("synapseOracleCapture", evidence.EvidenceKind);
        Assert.True(evidence.VariantCapture.Decoded);
        Assert.True(evidence.ReactiveTimingCapture.Decoded);
        Assert.True(evidence.DualBreathingCapture.Decoded);
        Assert.True(evidence.ReactiveMainKeyMapCapture.Decoded);
        Assert.True(evidence.ReactiveFunctionKeyMapCapture.Decoded);
        Assert.True(evidence.RippleCapture.Decoded);
        Assert.Null(typeof(ProductionLightingOracleCaptureEvidence).GetProperty("Applied"));
        Assert.Null(typeof(ProductionLightingOracleCaptureEvidence).GetProperty("Restored"));
        Assert.Equal(
            new[]
            {
                LightingEffect.Off,
                LightingEffect.Static,
                LightingEffect.Spectrum,
                LightingEffect.Breathing,
                LightingEffect.Reactive,
                LightingEffect.Tidal,
                LightingEffect.Starlight,
                LightingEffect.Wave,
                LightingEffect.Wheel,
                LightingEffect.Ripple,
                LightingEffect.AmbientAwareness,
                LightingEffect.AudioMeter,
                LightingEffect.Sequence,
            }.Order(),
            factory.Capabilities.LightingEffects.Order());
        Assert.Equal(
            DeviceFeatureAccess.ReadWrite,
            factory.Capabilities.SpecialKeyBindingsAccess);
        Assert.Equal(
            SpecialKeySlotSet.All,
            factory.Capabilities.WritableSpecialKeySlots);
        Assert.True(factory.Capabilities.SupportsKeyboardBrightness);
        Assert.True(factory.Capabilities.SupportsPlaybackMuteIndicator);
        Assert.Equal(
            KeyboardLightingLayoutIds.Rz09_0581,
            factory.Capabilities.KeyboardLightingLayoutId);
        Assert.True(factory.Capabilities.SupportedLightingRefreshRates.SetEquals(
        [
            LightingRefreshRate.ThirtyHz,
            LightingRefreshRate.SixtyHz,
        ]));
        Assert.Null(factory.Capabilities.LampArrayLightingAccess);
        Assert.Empty(factory.Capabilities.PerformancePresets);
        Assert.Empty(factory.Capabilities.FanModes);
        Assert.Empty(factory.Capabilities.ChargeLimits);
        Assert.False(windowsFactory.MatrixWriteTimingEnabled);
        Assert.False(ProductionLightingEvidenceSource.HasCurrentExactBuildValidationArtifact(
            Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.json"),
            TestBuildIdentity()));
    }

    [Fact]
    public void ProductionReadOnlyCapabilities_DoNotAdvertiseKeyboardBrightness()
    {
        IProductionLightingPortFactory factory =
            ProductionLightingFactory.CreateWindowsReadOnly();

        Assert.False(factory.Capabilities.SupportsKeyboardBrightness);
        Assert.False(Assert.IsType<ProductionLightingFactory>(factory)
            .MatrixWriteTimingEnabled);
    }

    [Fact]
    public void ExactBuildValidation_EnablesMatrixTimingOnlyWhenSinkIsSupplied()
    {
        IProductionLightingPortFactory uninstrumented =
            ProductionLightingFactory.CreateWindowsForExactBuildValidation();
        IProductionLightingPortFactory instrumented =
            ProductionLightingFactory.CreateWindowsForExactBuildValidation(
                new NoopMatrixWriteTimingSink());

        Assert.False(Assert.IsType<ProductionLightingFactory>(uninstrumented)
            .MatrixWriteTimingEnabled);
        Assert.True(Assert.IsType<ProductionLightingFactory>(instrumented)
            .MatrixWriteTimingEnabled);
    }

    [Fact]
    public void Rz090528ValidationCapabilities_AdmitCapturedRowEffectsBrightnessAndLogo()
    {
        DeviceCapabilities capabilities =
            Rz090528BrightnessLightingPort.CreateCapabilities(
                staticEffectAdmitted: true,
                spectrumEffectAdmitted: true);

        Assert.True(capabilities.SupportsKeyboardBrightness);
        Assert.False(capabilities.SupportsPlaybackMuteIndicator);
        Assert.Equal(
            DeviceFeatureAccess.ReadWrite,
            capabilities.SpecialKeyBindingsAccess);
        Assert.Equal(
            SpecialKeySlotSet.All,
            capabilities.WritableSpecialKeySlots);
        Assert.Equal(
            DeviceFeatureAccess.ObservedOnly,
            capabilities.LampArrayLightingAccess);
        Assert.True(capabilities.LightingEffects.SetEquals(
        [
            LightingEffect.Off,
            LightingEffect.Spectrum,
            LightingEffect.Static,
        ]));
        Assert.True(capabilities.ObservedLightingEffects.SetEquals(
        [
            LightingEffect.Starlight,
            LightingEffect.Ripple,
            LightingEffect.Tidal,
            LightingEffect.Wave,
            LightingEffect.Wheel,
        ]));
        Assert.True(capabilities.LogoLightingModes.SetEquals(
            Enum.GetValues<LogoLightingMode>()));
        Assert.Empty(capabilities.PerformancePresets);
        Assert.Empty(capabilities.FanModes);
        Assert.Empty(capabilities.ChargeLimits);
        Assert.False(capabilities.SupportsBatteryProtectionOff);
        Assert.False(capabilities.SupportsTemporaryFullCharge);
        Assert.Equal(
            [LightingRefreshRate.ThirtyHz],
            capabilities.SupportedLightingRefreshRates);
    }

    [Fact]
    public void Rz090528ProductionCapabilities_KeepUnvalidatedMatrixEffectsObservedOnly()
    {
        DeviceCapabilities capabilities =
            Rz090528BrightnessLightingPort.CreateCapabilities();

        Assert.Equal([LightingEffect.Off], capabilities.LightingEffects);
        Assert.Contains(LightingEffect.Static, capabilities.ObservedLightingEffects);
        Assert.Contains(LightingEffect.Spectrum, capabilities.ObservedLightingEffects);
    }

    [Fact]
    public async Task Rz090528BrightnessStep_UsesExactQuerySetReadbackSequence()
    {
        var session = new LightingSession(initialBrightness: 49);
        await using var port = new Rz090528BrightnessLightingPort(
            new ValidationActor(
                session,
                new BatteryCommands(),
                new TemporaryCommands()),
            TimeSpan.FromSeconds(1));

        byte result = await port.StepBrightnessAsync(
            KeyboardBrightnessDirection.Increase,
            default);

        Assert.Equal(29, result);
        Assert.Equal(
            [
                (0x03, 0x83, "010500"),
                (0x03, 0x03, "01054A"),
                (0x03, 0x83, "010500"),
            ],
            session.Commands.Select(CommandTuple).ToArray());
    }

    [Fact]
    public async Task Rz090528LightingOffAndLogo_UseExactSelectorAndReadback()
    {
        var session = new LightingSession(initialBrightness: 49);
        await using var port = new Rz090528BrightnessLightingPort(
            new ValidationActor(
                session,
                new BatteryCommands(),
                new TemporaryCommands()),
            TimeSpan.FromSeconds(1));

        await port.SetLightingAsync(
            LightingSettingsFactory.Off() with
            {
                LogoMode = LogoLightingMode.Breathing,
            },
            default);

        Assert.Equal(0, session.Brightness);
        Assert.True(session.LogoEnabled);
        Assert.Equal(LogoLightingProtocol.BreathingEffect, session.LogoEffect);
        Assert.Equal(LogoLightingMode.Breathing, await port.ReadLogoStateAsync(default));
        Assert.Contains(
            (0x03, 0x02, "000402"),
            session.Commands.Select(CommandTuple));
        Assert.Contains(
            (0x03, 0x00, "000401"),
            session.Commands.Select(CommandTuple));
        Assert.All(
            session.Commands.Where(command =>
                command.ClassId == 0x03
                && command.CommandId is 0x80 or 0x82),
            command => Assert.Equal(0x00, command.Arguments.Span[0]));
    }

    [Fact]
    public async Task Rz090528Static_UsesSixCapturedRowsThenLatchAndOwnsRenderer()
    {
        var session = new LightingSession(initialBrightness: 0);
        await using var port = new Rz090528BrightnessLightingPort(
            new ValidationActor(
                session,
                new BatteryCommands(),
                new TemporaryCommands()),
            TimeSpan.FromSeconds(1),
            staticEffectAdmitted: true);

        await port.SetLightingAsync(
            LightingSettingsFactory.Static(new RgbColor(0x47, 0xE1, 0x0C), 50),
            default);

        Assert.Equal(128, session.Brightness);
        Assert.Equal(LightingRendererOwnershipState.Static, port.OwnershipState);
        Assert.Null(typeof(Rz090528BrightnessLightingPort)
            .GetField("streamTask", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(port));
        RazerCommandSpec[] rows = session.Commands
            .Where(command =>
                command.ClassId == Rz090528MatrixRowProtocol.CommandClass
                && command.CommandId == Rz090528MatrixRowProtocol.RowCommandId)
            .ToArray();
        Assert.Equal(Rz090528MatrixFrame.RowCount, rows.Length);
        Assert.Equal(
            Enumerable.Range(0, Rz090528MatrixFrame.RowCount),
            rows.Select(command => (int)command.Arguments.Span[1]));
        Assert.All(rows, command =>
        {
            Assert.Equal(Rz090528MatrixRowProtocol.RowPayloadLength, command.Arguments.Length);
            Assert.Equal(0xFF, command.Arguments.Span[0]);
            Assert.Equal(0x00, command.Arguments.Span[2]);
            Assert.Equal(0x10, command.Arguments.Span[3]);
        });
        Assert.Equal(
            (0x03, 0x0A, "0500"),
            CommandTuple(session.Commands.Last()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rz090528DriverModeDrift_RelinquishesStaticWithoutRedrawing(
        bool failReadback)
    {
        var session = new LightingSession(initialBrightness: 128)
        {
            FailDeviceModeQueryOnCall = failReadback ? 2 : null,
        };
        await using var port = new Rz090528BrightnessLightingPort(
            new ValidationActor(session, new BatteryCommands(), new TemporaryCommands()),
            TimeSpan.FromSeconds(1),
            staticEffectAdmitted: true);
        LightingSettings settings = LightingSettingsFactory.Static(new RgbColor(0x47, 0xE1, 0x0C), 50);
        await port.SetLightingAsync(settings, default);
        Assert.Equal(LightingRendererOwnershipState.Static, port.OwnershipState);
        session.Commands.Clear();
        session.DeviceMode = RazerDeviceMode.Normal;

        if (failReadback)
        {
            await Assert.ThrowsAsync<IOException>(() => port.MaintainDriverModeAsync(default));
        }
        else
        {
            Assert.True(await port.MaintainDriverModeAsync(default));
        }

        Assert.Equal(LightingRendererOwnershipState.None, port.OwnershipState);
        Assert.Null(await port.ReadStateAsync(default));
        Assert.Equal(128, session.Brightness);
        Assert.DoesNotContain(session.Commands, command =>
            command.CommandId is Rz090528MatrixRowProtocol.RowCommandId
                or Rz090528MatrixRowProtocol.LatchCommandId);

        // Re-selecting the same saved intent must send a fresh frame, rather
        // than retaining the frame whose ownership was lost.
        await port.SetLightingAsync(settings, default);
        Assert.Equal(LightingRendererOwnershipState.Static, port.OwnershipState);
        Assert.Equal(Rz090528MatrixFrame.RowCount, session.Commands.Count(command =>
            command.CommandId == Rz090528MatrixRowProtocol.RowCommandId));
    }

    [Fact]
    public async Task Rz090528DriverModeUnchanged_PreservesStaticWithoutRedrawing()
    {
        var session = new LightingSession(initialBrightness: 128);
        await using var port = new Rz090528BrightnessLightingPort(
            new ValidationActor(session, new BatteryCommands(), new TemporaryCommands()),
            TimeSpan.FromSeconds(1),
            staticEffectAdmitted: true);
        await port.SetLightingAsync(
            LightingSettingsFactory.Static(new RgbColor(0x47, 0xE1, 0x0C), 50), default);
        session.Commands.Clear();

        Assert.True(await port.MaintainDriverModeAsync(default));

        Assert.Equal(LightingRendererOwnershipState.Static, port.OwnershipState);
        Assert.Equal([(0x00, 0x84, "0000")], session.Commands.Select(CommandTuple).ToArray());
    }

    [Fact]
    public async Task Rz090528DriverModeDrift_PreservesContinuousRenderer()
    {
        var session = new LightingSession(initialBrightness: 128);
        await using var port = new Rz090528BrightnessLightingPort(
            new ValidationActor(session, new BatteryCommands(), new TemporaryCommands()),
            TimeSpan.FromSeconds(1),
            spectrumEffectAdmitted: true,
            timeProvider: new ManualTimeProvider());
        await port.SetLightingAsync(LightingSettingsFactory.SpectrumCycling(50), default);
        session.DeviceMode = RazerDeviceMode.Normal;

        Assert.True(await port.MaintainDriverModeAsync(default));

        Assert.Equal(LightingRendererOwnershipState.Spectrum, port.OwnershipState);
        Assert.Equal(RazerDeviceMode.Driver, session.DeviceMode);
    }

    [Fact]
    public async Task Rz090528Spectrum_AcknowledgesFirstFrameThenStreamsSetOnlyFrames()
    {
        var session = new LightingSession(initialBrightness: 128);
        await using var port = new Rz090528BrightnessLightingPort(
            new ValidationActor(
                session,
                new BatteryCommands(),
                new TemporaryCommands()),
            TimeSpan.FromSeconds(1),
            staticEffectAdmitted: true,
            spectrumEffectAdmitted: true);

        await port.SetLightingAsync(
            LightingSettingsFactory.SpectrumCycling(50),
            default);

        for (int attempt = 0;
            attempt < 100 && session.SubmittedCommands.Count < 7;
            attempt++)
        {
            await Task.Delay(10);
        }

        Assert.Equal(LightingRendererOwnershipState.Spectrum, port.OwnershipState);
        Assert.Equal(7, session.Commands.Count(command =>
            command.CommandId is Rz090528MatrixRowProtocol.RowCommandId
                or Rz090528MatrixRowProtocol.LatchCommandId));
        Assert.True(session.SubmittedBatchCount >= 1);
        Assert.True(session.SubmittedCommands.Count >= 7);
        Assert.All(
            session.SubmittedCommands,
            command => Assert.Equal(
                Rz090528MatrixRowProtocol.CommandClass,
                command.ClassId));
        Assert.Contains(
            session.SubmittedCommands,
            command => command.CommandId
                == Rz090528MatrixRowProtocol.LatchCommandId);
    }

    [Fact]
    public async Task Rz090528NonLoopingSequence_HoldsFinalFrameWithoutFaultingRenderer()
    {
        var time = new ManualTimeProvider();
        var session = new LightingSession(initialBrightness: 128);
        await using var port = new Rz090528BrightnessLightingPort(
            new ValidationActor(
                session,
                new BatteryCommands(),
                new TemporaryCommands()),
            TimeSpan.FromSeconds(1),
            allSynapseEffectsAdmitted: true,
            timeProvider: time);
        var first = new RgbColor[AmbientLightingFrame.SlotCount];
        first[59] = new RgbColor(1, 2, 3);
        var second = new RgbColor[AmbientLightingFrame.SlotCount];
        second[59] = new RgbColor(4, 5, 6);
        LightingSettings settings = LightingSettingsFactory.Sequence(
            KeyboardLightingLayoutIds.Rz09_0528,
            KeyboardLightingSequenceCodec.Encode(
            [
                new KeyboardLightingSequenceFrame(100, first),
                new KeyboardLightingSequenceFrame(200, second),
            ]),
            loop: false,
            brightness: 50);

        await port.SetLightingAsync(settings, default);
        Task stream = Assert.IsAssignableFrom<Task>(
            typeof(Rz090528BrightnessLightingPort)
                .GetField("streamTask", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(port));
        time.Advance(TimeSpan.FromMilliseconds(100));
        for (int attempt = 0;
            attempt < 100 && session.SubmittedBatchCount < 1;
            attempt++)
        {
            await Task.Delay(10);
        }
        time.Advance(TimeSpan.FromMilliseconds(200));

        await stream.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(LightingRendererOwnershipState.Sequence, port.OwnershipState);
        Assert.Equal(settings, typeof(Rz090528BrightnessLightingPort)
            .GetField("lastDeliveredState", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(port));
        Assert.NotNull(typeof(Rz090528BrightnessLightingPort)
            .GetField("activeEffect", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(port));
        RazerCommandSpec finalRow = session.SubmittedCommands.Last(command =>
            command.CommandId == Rz090528MatrixRowProtocol.RowCommandId
            && command.Arguments.Span[1] == 3);
        int colorOffset = 4 + ((59 % Rz090528MatrixFrame.ColumnCount) * 3);
        Assert.Equal([4, 5, 6], finalRow.Arguments.Span
            .Slice(colorOffset, 3)
            .ToArray());
    }

    [Fact]
    public async Task Rz090528Reactive_PulseWakeRendersWithPostWakeTime()
    {
        var time = new ManualTimeProvider();
        var session = new LightingSession(initialBrightness: 128);
        await using var port = new Rz090528BrightnessLightingPort(
            new ValidationActor(
                session,
                new BatteryCommands(),
                new TemporaryCommands()),
            TimeSpan.FromSeconds(1),
            allSynapseEffectsAdmitted: true,
            timeProvider: time);
        await port.SetLightingAsync(
            LightingSettingsFactory.Reactive(
                new RgbColor(0, 255, 0),
                brightness: 50,
                ReactiveLightingDuration.Medium),
            default);

        time.Advance(TimeSpan.FromMilliseconds(100));
        await port.PulseReactiveKeyAsync(new ReactiveKeyPulse(59), default);
        for (int attempt = 0;
            attempt < 100 && session.SubmittedCommands.Count < 7;
            attempt++)
        {
            await Task.Delay(10);
        }

        RazerCommandSpec row = Assert.Single(session.SubmittedCommands, command =>
                command.CommandId == Rz090528MatrixRowProtocol.RowCommandId
                && command.Arguments.Span[1] == 3);
        int column = 59 % Rz090528MatrixFrame.ColumnCount;
        int colorOffset = 4 + (column * 3);
        Assert.Equal(0, row.Arguments.Span[colorOffset]);
        Assert.NotEqual(0, row.Arguments.Span[colorOffset + 1]);
        Assert.Equal(0, row.Arguments.Span[colorOffset + 2]);
    }

    [Fact]
    public async Task Rz090528ReactivePulse_DoesNotWaitBehindFirmwareOperationGate()
    {
        var session = new LightingSession(initialBrightness: 128);
        await using var port = new Rz090528BrightnessLightingPort(
            new ValidationActor(
                session,
                new BatteryCommands(),
                new TemporaryCommands()),
            TimeSpan.FromSeconds(1),
            allSynapseEffectsAdmitted: true);
        await port.SetLightingAsync(
            LightingSettingsFactory.Reactive(
                new RgbColor(0, 255, 0),
                brightness: 50,
                ReactiveLightingDuration.Medium),
            default);
        SemaphoreSlim gate = Rz090528OperationGate(port);
        await gate.WaitAsync();
        try
        {
            await port.PulseReactiveKeyAsync(new ReactiveKeyPulse(59), default)
                .WaitAsync(TimeSpan.FromMilliseconds(250));
        }
        finally
        {
            gate.Release();
        }
    }

    [Fact]
    public async Task Rz090528ReactivePulse_IsRejectedAfterCloseStarts()
    {
        var session = new LightingSession(initialBrightness: 128);
        var port = new Rz090528BrightnessLightingPort(
            new ValidationActor(
                session,
                new BatteryCommands(),
                new TemporaryCommands()),
            TimeSpan.FromSeconds(1),
            allSynapseEffectsAdmitted: true);
        await port.SetLightingAsync(
            LightingSettingsFactory.Reactive(
                new RgbColor(0, 255, 0),
                brightness: 50,
                ReactiveLightingDuration.Medium),
            default);
        SemaphoreSlim gate = Rz090528OperationGate(port);
        await gate.WaitAsync();
        Task closing = port.DisposeAsync().AsTask();
        try
        {
            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                port.PulseReactiveKeyAsync(new ReactiveKeyPulse(59), default));
        }
        finally
        {
            gate.Release();
        }
        await closing;
    }

    [Fact]
    public async Task Rz090528ReadDriverMode_UsesQueryOnly()
    {
        var session = new LightingSession(initialBrightness: 0)
        {
            DeviceMode = RazerDeviceMode.Driver,
        };
        await using var port = new Rz090528BrightnessLightingPort(
            new ValidationActor(
                session,
                new BatteryCommands(),
                new TemporaryCommands()),
            TimeSpan.FromSeconds(1));

        Assert.True(await port.ReadDriverModeAsync(default));
        Assert.Equal(
            [(0x00, 0x84, "0000")],
            session.Commands.Select(CommandTuple).ToArray());
    }

    [Fact]
    public async Task Rz090528DriverModeLifecycle_AcquiresAndRestoresOwnedMode()
    {
        var session = new LightingSession(initialBrightness: 0)
        {
            DeviceMode = RazerDeviceMode.Normal,
        };
        var port = new Rz090528BrightnessLightingPort(
            new ValidationActor(
                session,
                new BatteryCommands(),
                new TemporaryCommands()),
            TimeSpan.FromSeconds(1));

        Assert.True(await port.MaintainDriverModeAsync(default));
        Assert.Equal(RazerDeviceMode.Driver, session.DeviceMode);
        Assert.Equal(
            [
                (0x00, 0x84, "0000"),
                (0x00, 0x04, "0300"),
                (0x00, 0x84, "0000"),
            ],
            session.Commands.Select(CommandTuple).ToArray());

        await port.DisposeAsync();

        Assert.Equal(RazerDeviceMode.Normal, session.DeviceMode);
        Assert.Equal(DriverModeRestoreOutcome.Released, port.DriverModeRestoreOutcome);
        Assert.Equal(
            [
                (0x00, 0x84, "0000"),
                (0x00, 0x04, "0300"),
                (0x00, 0x84, "0000"),
                (0x00, 0x84, "0000"),
                (0x00, 0x04, "0000"),
                (0x00, 0x84, "0000"),
            ],
            session.Commands.Select(CommandTuple).ToArray());
    }

    [Fact]
    public async Task Rz090528DriverModeLifecycle_PreservesPreExistingDriverMode()
    {
        var session = new LightingSession(initialBrightness: 0)
        {
            DeviceMode = RazerDeviceMode.Driver,
        };
        var port = new Rz090528BrightnessLightingPort(
            new ValidationActor(
                session,
                new BatteryCommands(),
                new TemporaryCommands()),
            TimeSpan.FromSeconds(1));

        Assert.True(await port.MaintainDriverModeAsync(default));
        await port.DisposeAsync();

        Assert.Equal(RazerDeviceMode.Driver, session.DeviceMode);
        Assert.Equal(
            [(0x00, 0x84, "0000")],
            session.Commands.Select(CommandTuple).ToArray());
    }

    [Fact]
    public async Task Rz090528ReleaseDriverMode_UsesSetAndIndependentReadback()
    {
        var session = new LightingSession(initialBrightness: 0)
        {
            DeviceMode = RazerDeviceMode.Driver,
        };
        await using var port = new Rz090528BrightnessLightingPort(
            new ValidationActor(
                session,
                new BatteryCommands(),
                new TemporaryCommands()),
            TimeSpan.FromSeconds(1));

        Assert.False(await port.ReleaseDriverModeAsync(default));

        Assert.Equal(RazerDeviceMode.Normal, session.DeviceMode);
        Assert.Equal(
            [
                (0x00, 0x84, "0000"),
                (0x00, 0x04, "0000"),
                (0x00, 0x84, "0000"),
            ],
            session.Commands.Select(CommandTuple).ToArray());
    }

    [Fact]
    public async Task Rz090528Invalidate_RestoresOwnedModeAndClosesSession()
    {
        var session = new LightingSession(initialBrightness: 0)
        {
            DeviceMode = RazerDeviceMode.Normal,
        };
        var port = new Rz090528BrightnessLightingPort(
            new ValidationActor(
                session,
                new BatteryCommands(),
                new TemporaryCommands()),
            TimeSpan.FromSeconds(1));
        await port.EnsureDriverModeAsync(default);

        Assert.Equal(
            LightingRendererOwnershipState.None,
            await port.InvalidateAsync());

        Assert.Equal(RazerDeviceMode.Normal, session.DeviceMode);
        Assert.Equal(
            RazerCommandSessionDisposition.FullyDisposed,
            session.Disposition);
    }

    [Fact]
    public async Task Rz090528DisposeWithinDeadline_RetainsStalledCleanup()
    {
        var time = new ManualTimeProvider();
        var registry = new PendingDisposalRegistry();
        var session = new LightingSession(initialBrightness: 0);
        var port = new Rz090528BrightnessLightingPort(
            new ValidationActor(
                session,
                new BatteryCommands(),
                new TemporaryCommands()),
            TimeSpan.FromSeconds(1),
            timeProvider: time,
            cleanupDrainTimeout: TimeSpan.FromSeconds(1),
            pendingDisposals: registry);
        var gate = Assert.IsType<SemaphoreSlim>(
            typeof(Rz090528BrightnessLightingPort)
                .GetField(
                    "operationGate",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(port));
        await gate.WaitAsync();

        Task<bool> disposal = port.DisposeWithinCleanupDeadlineAsync();
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.False(await disposal);
        Assert.Equal(1, registry.Count);
        Assert.NotEqual(
            RazerCommandSessionDisposition.FullyDisposed,
            session.Disposition);

        gate.Release();
        await registry.WhenEmptyAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, registry.Count);
        Assert.Equal(
            RazerCommandSessionDisposition.FullyDisposed,
            session.Disposition);
        Assert.True(await port.DisposeWithinCleanupDeadlineAsync());
    }

    [Fact]
    public async Task Rz090528DisposeWithinDeadline_ReturnsFalseWhenCleanupFaults()
    {
        var session = new LightingSession(initialBrightness: 0)
        {
            DisposeFailure = new IOException("Synthetic close failure."),
        };
        var registry = new PendingDisposalRegistry();
        var port = new Rz090528BrightnessLightingPort(
            new ValidationActor(
                session,
                new BatteryCommands(),
                new TemporaryCommands()),
            TimeSpan.FromSeconds(1),
            pendingDisposals: registry);

        bool drained = await port.DisposeWithinCleanupDeadlineAsync();

        Assert.False(drained);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task DriverModeLifecycle_AcquiresVerifiedModeAndRestoresOwnedBaseline()
    {
        var session = new LightingSession(initialBrightness: 0xC5)
        {
            DeviceMode = RazerDeviceMode.Normal,
        };
        var port = CreatePort(session, new MatrixWriter(), admitted: true);

        await port.EnsureDriverModeAsync(default);
        Assert.True(await port.ReadDriverModeAsync(default));

        Assert.Equal(RazerDeviceMode.Driver, session.DeviceMode);
        Assert.Equal(
            [
                (0x00, 0x84, "0000"),
                (0x00, 0x04, "0300"),
                (0x00, 0x84, "0000"),
                (0x00, 0x84, "0000"),
            ],
            session.Commands.Select(CommandTuple).ToArray());

        await port.DisposeAsync();

        Assert.Equal(RazerDeviceMode.Normal, session.DeviceMode);
        Assert.Equal(
            [
                (0x00, 0x84, "0000"),
                (0x00, 0x04, "0300"),
                (0x00, 0x84, "0000"),
                (0x00, 0x84, "0000"),
                (0x00, 0x84, "0000"),
                (0x00, 0x04, "0000"),
                (0x00, 0x84, "0000"),
            ],
            session.Commands.Select(CommandTuple).ToArray());
        Assert.Equal(DriverModeRestoreOutcome.Released, port.DriverModeRestoreOutcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DriverModeCleanup_ReadbackMismatchRetainsOwnershipAndReportsIndeterminate(
        bool rz090528)
    {
        var session = new LightingSession(initialBrightness: 0xC5)
        {
            DeviceMode = RazerDeviceMode.Normal,
        };
        (IProductionLightingPort port, MatrixWriter? writer) =
            CreateDriverModeCleanupPort(session, rz090528);
        await port.MaintainDriverModeAsync(default);
        session.DeviceModeQueryOverrides.Enqueue(RazerDeviceMode.Driver);
        session.DeviceModeQueryOverrides.Enqueue(RazerDeviceMode.Driver);

        await port.DisposeAsync();

        Assert.Equal(DriverModeRestoreOutcome.Indeterminate, port.DriverModeRestoreOutcome);
        Assert.True(OwnsDriverMode(port));
        Assert.Equal(RazerCommandSessionDisposition.FullyDisposed, session.Disposition);
        if (writer is not null)
        {
            Assert.Equal(RazerCommandSessionDisposition.FullyDisposed, writer.Disposition);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DriverModeCleanup_QueryFailureRetainsOwnershipAndReportsFailed(
        bool rz090528)
    {
        var session = new LightingSession(initialBrightness: 0xC5)
        {
            DeviceMode = RazerDeviceMode.Normal,
            FailDeviceModeQueryOnCall = 3,
        };
        (IProductionLightingPort port, MatrixWriter? writer) =
            CreateDriverModeCleanupPort(session, rz090528);
        await port.MaintainDriverModeAsync(default);

        await port.DisposeAsync();

        Assert.Equal(DriverModeRestoreOutcome.Failed, port.DriverModeRestoreOutcome);
        Assert.True(OwnsDriverMode(port));
        Assert.Equal(RazerCommandSessionDisposition.FullyDisposed, session.Disposition);
        if (writer is not null)
        {
            Assert.Equal(RazerCommandSessionDisposition.FullyDisposed, writer.Disposition);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DriverModeCleanup_ForeignChangeSkipsWriteAndReportsOutcome(
        bool rz090528)
    {
        var session = new LightingSession(initialBrightness: 0xC5)
        {
            DeviceMode = RazerDeviceMode.Normal,
        };
        (IProductionLightingPort port, _) = CreateDriverModeCleanupPort(session, rz090528);
        await port.MaintainDriverModeAsync(default);
        Assert.Equal(1, session.DeviceModeSetCalls);
        session.DeviceMode = RazerDeviceMode.Normal;

        await port.DisposeAsync();

        Assert.Equal(DriverModeRestoreOutcome.ForeignChange, port.DriverModeRestoreOutcome);
        Assert.False(OwnsDriverMode(port));
        Assert.Equal(1, session.DeviceModeSetCalls);
        Assert.Equal(RazerCommandSessionDisposition.FullyDisposed, session.Disposition);
    }

    [Fact]
    public async Task DriverModeLifecycle_DoesNotClaimOrRestoreExistingDriverMode()
    {
        var session = new LightingSession(initialBrightness: 0xC5)
        {
            DeviceMode = RazerDeviceMode.Driver,
        };
        var port = CreatePort(session, new MatrixWriter(), admitted: true);

        await port.EnsureDriverModeAsync(default);
        Assert.True(await port.ReadDriverModeAsync(default));
        await port.DisposeAsync();

        Assert.Equal(RazerDeviceMode.Driver, session.DeviceMode);
        Assert.Equal(
            [(0x00, 0x84, "0000"), (0x00, 0x84, "0000")],
            session.Commands.Select(CommandTuple).ToArray());
    }

    [Fact]
    public async Task ReleaseDriverMode_ExplicitlyRestoresPreExistingDriverMode()
    {
        var session = new LightingSession(initialBrightness: 0xC5)
        {
            DeviceMode = RazerDeviceMode.Driver,
        };
        var port = CreatePort(session, new MatrixWriter(), admitted: true);

        Assert.False(await port.ReleaseDriverModeAsync(default));

        Assert.Equal(RazerDeviceMode.Normal, session.DeviceMode);
        Assert.Equal(
            [
                (0x00, 0x84, "0000"),
                (0x00, 0x04, "0000"),
                (0x00, 0x84, "0000"),
            ],
            session.Commands.Select(CommandTuple).ToArray());
    }

    [Fact]
    public async Task ReleaseDriverMode_WhenAlreadyNormal_DoesNotSubmitAWrite()
    {
        var session = new LightingSession(initialBrightness: 0xC5)
        {
            DeviceMode = RazerDeviceMode.Normal,
        };
        var port = CreatePort(session, new MatrixWriter(), admitted: true);

        Assert.False(await port.ReleaseDriverModeAsync(default));

        Assert.Equal(RazerDeviceMode.Normal, session.DeviceMode);
        Assert.Equal(
            [(0x00, 0x84, "0000"), (0x00, 0x84, "0000")],
            session.Commands.Select(CommandTuple).ToArray());
    }

    [Fact]
    public async Task DriverModeLifecycle_ReacquiresModeAfterFirmwareFallsBackToNormal()
    {
        var session = new LightingSession(initialBrightness: 0xC5)
        {
            DeviceMode = RazerDeviceMode.Driver,
        };
        var port = CreatePort(session, new MatrixWriter(), admitted: true);

        await port.EnsureDriverModeAsync(default);
        session.DeviceMode = RazerDeviceMode.Normal;
        await port.EnsureDriverModeAsync(default);

        Assert.Equal(RazerDeviceMode.Driver, session.DeviceMode);
        Assert.Equal(
            [
                (0x00, 0x84, "0000"),
                (0x00, 0x84, "0000"),
                (0x00, 0x04, "0300"),
                (0x00, 0x84, "0000"),
            ],
            session.Commands.Select(CommandTuple).ToArray());

        await port.DisposeAsync();

        Assert.Equal(RazerDeviceMode.Normal, session.DeviceMode);
    }

    [Fact]
    public async Task DriverModeReadWaitsForInFlightMatrixFeatureWrite()
    {
        var session = new LightingSession(initialBrightness: 0xFF)
        {
            DeviceMode = RazerDeviceMode.Driver,
        };
        var writer = new MatrixWriter();
        await using var port = CreatePort(session, writer, admitted: true);
        await port.SetLightingAsync(
            LightingSettingsFactory.SpectrumCycling(brightness: 100),
            default);

        int initialWrites = writer.Reports.Count;
        int initialCommands = session.Commands.Count;
        writer.BlockNextWrite();
        await writer.WaitForWriteCountAsync(initialWrites + 1, TimeSpan.FromSeconds(2));

        Task<bool?> read = port.ReadDriverModeAsync(default);
        await Task.Delay(100);

        Assert.False(read.IsCompleted);
        Assert.Equal(initialCommands, session.Commands.Count);

        writer.ReleaseBlockedWrite();
        Assert.True(await read.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(initialCommands + 1, session.Commands.Count);
    }

    [Fact]
    public async Task MaintainDriverMode_DoesNotStopTheActiveLightingStream()
    {
        var session = new LightingSession(initialBrightness: 0xFF)
        {
            DeviceMode = RazerDeviceMode.Driver,
        };
        var writer = new MatrixWriter();
        await using var port = CreatePort(session, writer, admitted: true);
        await port.SetLightingAsync(
            LightingSettingsFactory.SpectrumCycling(brightness: 100),
            default);

        await port.MaintainDriverModeAsync(default);
        int writesAfterMaintenance = writer.Reports.Count;

        await writer.WaitForWriteCountAsync(
            writesAfterMaintenance + 1,
            TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RelinquishRenderer_StopsStreamWithoutClosingPortOrChangingDriverMode(
        bool rz090528)
    {
        var session = new LightingSession(initialBrightness: 0xFF)
        {
            DeviceMode = RazerDeviceMode.Driver,
        };
        var time = new ManualTimeProvider();
        MatrixWriter? writer = rz090528 ? null : new MatrixWriter();
        await using IProductionLightingPort port = rz090528
            ? new Rz090528BrightnessLightingPort(
                new ValidationActor(
                    session,
                    new BatteryCommands(),
                    new TemporaryCommands()),
                TimeSpan.FromSeconds(1),
                spectrumEffectAdmitted: true,
                timeProvider: time)
            : CreatePort(
                session,
                writer,
                admitted: true,
                timeProvider: time);
        await port.SetLightingAsync(
            LightingSettingsFactory.SpectrumCycling(brightness: 100),
            default);

        LightingRendererOwnershipState relinquished =
            await port.RelinquishRendererAsync(default);
        int commandsAfterRelinquish = session.Commands.Count;
        int framesAfterRelinquish = writer?.Reports.Count ?? 0;
        time.Advance(TimeSpan.FromSeconds(1));
        await Task.Delay(50);

        Assert.Equal(LightingRendererOwnershipState.Spectrum, relinquished);
        Assert.Equal(LightingRendererOwnershipState.None, port.OwnershipState);
        Assert.Equal(RazerDeviceMode.Driver, session.DeviceMode);
        Assert.Equal(commandsAfterRelinquish, session.Commands.Count);
        Assert.Equal(framesAfterRelinquish, writer?.Reports.Count ?? 0);
        Assert.True(await port.ReadDriverModeAsync(default));
    }

    [Fact]
    public async Task LowPowerCleanup_DefaultInterfaceCannotCallOrdinaryLightingSetter()
    {
        await using IProductionLightingPort port = new LegacyLightingPort();

        LightingLowPowerOffResult result = await port.TurnOffForLowPowerAsync(
            () => true, default);

        Assert.Equal(LightingLowPowerOffOutcome.KnownNotApplied, result.Outcome);
        Assert.Equal(LightingRendererOwnershipState.Static, result.OwnershipBefore);
        Assert.Null(await port.ReleaseDriverModeForLowPowerAsync(() => true, default));
        Assert.Equal(LightingRendererOwnershipState.Static, port.OwnershipState);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReleaseDriverModeForLowPower_BoundaryClearingDuringQueryPreventsNormalWrite(
        bool rz090528)
    {
        var session = new LightingSession(initialBrightness: 0xFF);
        await using IProductionLightingPort port =
            CreateDriverModeCleanupPort(session, rz090528).Port;
        bool stillSuspending = true;
        session.DeviceModeQueried = () => stillSuspending = false;

        Assert.Null(await port.ReleaseDriverModeForLowPowerAsync(
            () => stillSuspending, default));

        Assert.Equal(RazerDeviceMode.Driver, session.DeviceMode);
        Assert.Equal(0, session.DeviceModeSetCalls);
        RazerCommandSpec query = Assert.Single(session.Commands);
        Assert.Equal((0x00, 0x84), ((int)query.ClassId, (int)query.CommandId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TurnOffForLowPower_ConfirmsZeroAndRetiresOwnedRendererWithoutChangingDriverMode(
        bool rz090528)
    {
        var session = new LightingSession(initialBrightness: 0xFF);
        var time = new ManualTimeProvider();
        var writer = new MatrixWriter();
        await using IProductionLightingPort port = CreateOwnedRendererReadPort(
            session, time, rz090528, writer);
        await port.SetLightingAsync(
            LightingSettingsFactory.SpectrumCycling(brightness: 100), default);
        int commandCount = session.Commands.Count;
        int frameCount = writer.Reports.Count;
        int submittedCount = session.SubmittedCommands.Count;

        LightingLowPowerOffResult result = await port.TurnOffForLowPowerAsync(
            () => true, default);
        time.Advance(TimeSpan.FromSeconds(1));
        await Task.Delay(50);

        Assert.Equal(LightingRendererOwnershipState.Spectrum, result.OwnershipBefore);
        Assert.Equal(LightingLowPowerOffOutcome.ConfirmedOff, result.Outcome);
        Assert.Equal(0, session.Brightness);
        Assert.Equal(LightingRendererOwnershipState.None, port.OwnershipState);
        Assert.Equal(RazerDeviceMode.Driver, session.DeviceMode);
        Assert.Equal(
            [(0x03, 0x83), (0x03, 0x03), (0x03, 0x83)],
            session.Commands.Skip(commandCount)
                .Select(command => ((int)command.ClassId, (int)command.CommandId)));
        Assert.Equal(frameCount, writer.Reports.Count);
        Assert.Equal(submittedCount, session.SubmittedCommands.Count);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    public async Task TurnOffForLowPower_FailureNeverRestoresBrightnessOrRestartsRenderer(
        bool rz090528,
        int failureStage)
    {
        var session = new LightingSession(initialBrightness: 0xFF);
        var time = new ManualTimeProvider();
        var writer = new MatrixWriter();
        await using IProductionLightingPort port = CreateOwnedRendererReadPort(
            session, time, rz090528, writer);
        await port.SetLightingAsync(
            LightingSettingsFactory.SpectrumCycling(brightness: 100), default);
        int commandCount = session.Commands.Count;
        int frameCount = writer.Reports.Count;
        int submittedCount = session.SubmittedCommands.Count;
        if (failureStage == 1)
        {
            session.FailBrightnessSetOnCall = session.BrightnessSetCalls + 1;
        }
        else
        {
            session.FailBrightnessQueryOnCall = session.BrightnessQueryCalls
                + (failureStage == 0 ? 1 : 2);
        }

        LightingLowPowerOffResult result = await port.TurnOffForLowPowerAsync(
            () => true, default);
        time.Advance(TimeSpan.FromSeconds(1));
        await Task.Delay(50);

        Assert.Equal(LightingRendererOwnershipState.Spectrum, result.OwnershipBefore);
        Assert.Equal(
            failureStage == 0
                ? LightingLowPowerOffOutcome.KnownNotApplied
                : LightingLowPowerOffOutcome.Indeterminate,
            result.Outcome);
        Assert.Equal(LightingRendererOwnershipState.None, port.OwnershipState);
        RazerCommandSpec[] writes = session.Commands.Skip(commandCount)
            .Where(command => (command.ClassId, command.CommandId) == (0x03, 0x03))
            .ToArray();
        Assert.Equal(failureStage == 0 ? 0 : 1, writes.Length);
        Assert.All(writes, command => Assert.Equal(0, command.Arguments.Span[2]));
        Assert.DoesNotContain(session.Commands.Skip(commandCount),
            command => (command.ClassId, command.CommandId) == (0x00, 0x04));
        Assert.Equal(frameCount, writer.Reports.Count);
        Assert.Equal(submittedCount, session.SubmittedCommands.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TurnOffForLowPower_ExpiredBoundaryLeavesActiveRendererUntouched(
        bool rz090528)
    {
        var session = new LightingSession(initialBrightness: 0xFF);
        await using IProductionLightingPort port = CreateOwnedRendererReadPort(
            session, new ManualTimeProvider(), rz090528);
        await port.SetLightingAsync(
            LightingSettingsFactory.SpectrumCycling(brightness: 100), default);
        int commandCount = session.Commands.Count;

        LightingLowPowerOffResult result = await port.TurnOffForLowPowerAsync(
            () => false, default);

        Assert.Equal(LightingLowPowerOffOutcome.KnownNotApplied, result.Outcome);
        Assert.Equal(LightingRendererOwnershipState.Spectrum, result.OwnershipBefore);
        Assert.Equal(LightingRendererOwnershipState.Spectrum, port.OwnershipState);
        Assert.Equal(commandCount, session.Commands.Count);
        Assert.Equal(0xFF, session.Brightness);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TurnOffForLowPower_BoundaryClearingDuringReadPreventsSubmission(
        bool rz090528)
    {
        var session = new LightingSession(initialBrightness: 0xFF);
        var time = new ManualTimeProvider();
        var writer = new MatrixWriter();
        await using IProductionLightingPort port = CreateOwnedRendererReadPort(
            session, time, rz090528, writer);
        await port.SetLightingAsync(
            LightingSettingsFactory.SpectrumCycling(brightness: 100), default);
        int commandCount = session.Commands.Count;
        int frameCount = writer.Reports.Count;
        int submittedCount = session.SubmittedCommands.Count;
        bool stillSuspending = true;
        session.BrightnessQueried = () => stillSuspending = false;

        LightingLowPowerOffResult result = await port.TurnOffForLowPowerAsync(
            () => stillSuspending, default);
        time.Advance(TimeSpan.FromSeconds(1));
        await Task.Delay(50);

        Assert.Equal(LightingLowPowerOffOutcome.KnownNotApplied, result.Outcome);
        Assert.Equal(LightingRendererOwnershipState.Spectrum, result.OwnershipBefore);
        Assert.Equal(LightingRendererOwnershipState.None, port.OwnershipState);
        RazerCommandSpec query = Assert.Single(session.Commands.Skip(commandCount));
        Assert.Equal((0x03, 0x83), ((int)query.ClassId, (int)query.CommandId));
        Assert.Equal(0xFF, session.Brightness);
        Assert.Equal(frameCount, writer.Reports.Count);
        Assert.Equal(submittedCount, session.SubmittedCommands.Count);
    }

    [Fact]
    public async Task ReleaseDriverMode_DrainsTheLightingStreamBeforeModeWrite()
    {
        var session = new LightingSession(initialBrightness: 0xFF)
        {
            DeviceMode = RazerDeviceMode.Driver,
        };
        var writer = new MatrixWriter();
        await using var port = CreatePort(session, writer, admitted: true);
        await port.SetLightingAsync(
            LightingSettingsFactory.SpectrumCycling(brightness: 100),
            default);

        int initialWrites = writer.Reports.Count;
        int initialCommands = session.Commands.Count;
        writer.BlockNextWrite();
        await writer.WaitForWriteCountAsync(initialWrites + 1, TimeSpan.FromSeconds(2));

        Task<bool?> release = port.ReleaseDriverModeAsync(default);
        await Task.Delay(100);

        Assert.False(release.IsCompleted);
        Assert.Equal(initialCommands, session.Commands.Count);

        writer.ReleaseBlockedWrite();
        Assert.False(await release.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(RazerDeviceMode.Normal, session.DeviceMode);
    }

    [Fact]
    public async Task ProductionFactory_AcquiresValidatedDriverMode()
    {
        var session = new LightingSession(initialBrightness: 0xC5)
        {
            DeviceMode = RazerDeviceMode.Normal,
        };
        var factory = new ProductionLightingFactory(
            admitted: true,
            static () => new ValidationRunContext(),
            _ => new LightingActorFactory(session),
            static () => new HidSessionResult<IPid02E0MatrixFeatureWriter>(
                new MatrixWriter(),
                null),
            TimeProvider.System,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1),
            new PendingDisposalRegistry());

        HidSessionResult<IProductionLightingPort> opened = await factory.OpenAsync(default);

        Assert.Null(opened.Failure);
        Assert.NotNull(opened.Value);
        Assert.Equal(RazerDeviceMode.Driver, session.DeviceMode);
        Assert.Equal(
            [
                (0x00, 0x84, "0000"),
                (0x00, 0x04, "0300"),
                (0x00, 0x84, "0000"),
            ],
            session.Commands.Select(CommandTuple).ToArray());
        await opened.Value!.DisposeAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DriverModeMaintenance_SuspendDuringModeQuerySubmitsNoDriverWrite(bool rz090528)
    {
        bool active = true;
        var queried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new LightingSession(0x80)
        {
            DeviceMode = RazerDeviceMode.Normal,
            DeviceModeQueryBoundary = () =>
            {
                queried.TrySetResult();
                return release.Task;
            },
        };
        await using IProductionLightingPort port =
            CreateDriverModeCleanupPort(session, rz090528, () => active).Port;
        Task<bool?> maintenance = port.MaintainDriverModeAsync(default);
        await queried.Task.WaitAsync(TimeSpan.FromSeconds(2));
        active = false;
        release.TrySetResult();

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() => maintenance);
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.Equal(0, session.DeviceModeSetCalls);
        Assert.False(OwnsDriverMode(port));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DriverModeCleanup_SuspendDuringModeQuerySubmitsNoNormalWrite(bool rz090528)
    {
        bool active = true;
        var session = new LightingSession(0x80) { DeviceMode = RazerDeviceMode.Normal };
        IProductionLightingPort port = CreateDriverModeCleanupPort(session, rz090528, () => active).Port;
        await port.EnsureDriverModeAsync(default);
        session.DeviceModeQueried = () => active = false;

        await port.DisposeAsync();

        Assert.Equal(1, session.DeviceModeSetCalls);
        Assert.Equal(RazerDeviceMode.Driver, session.DeviceMode);
        Assert.Equal(DriverModeRestoreOutcome.Failed, port.DriverModeRestoreOutcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProductionFactory_GuardsAcquisitionAndFailedOpenCleanup(bool failAfterSubmission)
    {
        bool active = true;
        var session = new LightingSession(0x80)
        {
            DeviceMode = RazerDeviceMode.Normal,
            FailDeviceModeQueryOnCall = failAfterSubmission ? 2 : null,
        };
        session.DeviceModeQueried = () =>
        {
            if (session.DeviceModeQueryCalls == (failAfterSubmission ? 2 : 1))
            {
                active = false;
            }
        };
        var factory = new ProductionLightingFactory(true,
            static () => new ValidationRunContext(), _ => new LightingActorFactory(session),
            static () => new(new MatrixWriter(), null), TimeProvider.System,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), new PendingDisposalRegistry());

        HidSessionResult<IProductionLightingPort> opened = await factory.OpenAsync(() => active, default);

        Assert.Null(opened.Value);
        Assert.NotNull(opened.Failure);
        Assert.Equal(failAfterSubmission ? 1 : 0, session.DeviceModeSetCalls);
        Assert.Equal(failAfterSubmission ? RazerDeviceMode.Driver : RazerDeviceMode.Normal, session.DeviceMode);
    }

    [Fact]
    public async Task ProductionFactory_PreservesActorOpenDiagnostic()
    {
        var diagnostic = new HidSessionFailureDiagnostic(
            HidSessionFailurePhase.GetCompletion,
            HidSessionFailureKind.Timeout,
            NativeWin32Error: 995,
            TransferredByteCount: 12);
        var actorResult = new HidSessionResult<ValidationActor>(
            null,
            HidSessionFailureKind.Timeout)
        {
            Diagnostic = diagnostic,
        };
        var factory = new ProductionLightingFactory(
            admitted: false,
            static () => new ValidationRunContext(),
            _ => new FixedLightingActorFactory(actorResult),
            static () => new HidSessionResult<IPid02E0MatrixFeatureWriter>(null, null),
            TimeProvider.System,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1),
            new PendingDisposalRegistry());

        HidSessionResult<IProductionLightingPort> opened = await factory.OpenAsync(default);

        Assert.Null(opened.Value);
        Assert.Equal(HidSessionFailureKind.Timeout, opened.Failure);
        Assert.Equal(diagnostic, opened.Diagnostic);
    }

    [Fact]
    public async Task ProductionFactory_PreservesMatrixOpenDiagnostic()
    {
        var session = new LightingSession(initialBrightness: 0xC5);
        var diagnostic = new HidSessionFailureDiagnostic(
            HidSessionFailurePhase.SetSubmit,
            HidSessionFailureKind.NativeIoFailure,
            NativeWin32Error: 5,
            TransferredByteCount: 0);
        var matrixResult = new HidSessionResult<IPid02E0MatrixFeatureWriter>(
            null,
            HidSessionFailureKind.NativeIoFailure)
        {
            Diagnostic = diagnostic,
        };
        var factory = new ProductionLightingFactory(
            admitted: true,
            static () => new ValidationRunContext(),
            _ => new LightingActorFactory(session),
            () => matrixResult,
            TimeProvider.System,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1),
            new PendingDisposalRegistry());

        HidSessionResult<IProductionLightingPort> opened = await factory.OpenAsync(default);

        Assert.Null(opened.Value);
        Assert.Equal(HidSessionFailureKind.NativeIoFailure, opened.Failure);
        Assert.Equal(diagnostic, opened.Diagnostic);
    }

    [Theory]
    [InlineData(true, 0x00, LogoLightingMode.Off)]
    [InlineData(false, 0x00, LogoLightingMode.Static)]
    [InlineData(false, 0x02, LogoLightingMode.Breathing)]
    public async Task LogoQueryUsesIndependentStateAndEffectReads(
        bool disabled,
        byte effect,
        LogoLightingMode expected)
    {
        var session = new LightingSession(initialBrightness: 0xC5)
        {
            LogoEnabled = !disabled,
            LogoEffect = effect,
        };
        await using var port = CreatePort(session, new MatrixWriter(), admitted: true);

        Assert.Equal(expected, await port.ReadLogoStateAsync(default));
        Assert.Equal(
            [(0x03, 0x80, "010400"), (0x03, 0x82, "010400")],
            session.Commands.Select(command => (
                (int)command.ClassId,
                (int)command.CommandId,
                Convert.ToHexString(command.Arguments.Span))).ToArray());
    }

    [Fact]
    public async Task LightingWriteAppliesAndReadsBackRequestedLogoMode()
    {
        var session = new LightingSession(initialBrightness: 0xC5)
        {
            LogoEnabled = true,
            LogoEffect = LogoLightingProtocol.BreathingEffect,
        };
        var writer = new MatrixWriter();
        await using var port = CreatePort(session, writer, admitted: true);
        LightingSettings requested = LightingSettingsFactory.Static(
            new RgbColor(0, 255, 0),
            77) with
        {
            LogoMode = LogoLightingMode.Static,
        };

        await port.SetLightingAsync(requested, default);

        Assert.True(session.LogoEnabled);
        Assert.Equal(LogoLightingProtocol.StaticEffect, session.LogoEffect);
        Assert.Contains(session.Commands, command =>
            (command.ClassId, command.CommandId) == (0x03, 0x02)
            && command.Arguments.Span.SequenceEqual(new byte[] { 0x01, 0x04, 0x00 }));
        Assert.Contains(session.Commands, command =>
            (command.ClassId, command.CommandId) == (0x03, 0x00)
            && command.Arguments.Span.SequenceEqual(new byte[] { 0x01, 0x04, 0x01 }));
        Assert.Equal(LogoLightingMode.Static, await port.ReadLogoStateAsync(default));
    }

    [Fact]
    public void LightingAdmission_RequiresApprovedRuntimeSemantics()
    {
        ProductionLightingSemanticAdmissionEvidence approved =
            ProductionLightingEvidenceSource.ReadValidatedSemanticAdmission();
        ProductionLightingSemanticAdmissionEvidence current =
            ProductionLightingEvidenceSource.BuildCurrentSemanticAdmission();

        Assert.Equal(
            JsonSerializer.Serialize(approved),
            JsonSerializer.Serialize(current));
        Assert.True(
            ProductionLightingEvidenceSource.IsProductionLightingImplementationApproved(),
            "Current lighting wire semantics must match reviewed evidence.");
        Assert.Equal(
            [LightingRefreshRate.ThirtyHz, LightingRefreshRate.SixtyHz],
            approved.SupportedRefreshRates);
        Assert.False(
            ProductionLightingEvidenceSource.SemanticAdmissionMatches(
                approved,
                current with
                {
                    DeviceMode = current.DeviceMode with
                    {
                        SetDriver = "00/04:FF00",
                    },
                }));
        Assert.False(
            ProductionLightingEvidenceSource.SemanticAdmissionMatches(
                approved,
                current with
                {
                    Matrix = current.Matrix with
                    {
                        KnownAnswerFeatureReportSha256 =
                            new string('A', 64),
                    },
                }));
        Assert.DoesNotContain(
            typeof(ProductionLightingEvidenceSource).Assembly
                .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(),
            attribute =>
                attribute.Key == "OpenBladeLightingImplementationFingerprint");
    }

    [Fact]
    public void ExactBuildValidation_RequiresMatchingCommitAndAllHardwareGates()
    {
        const string commit = "403aa01b11e969a6351595154569ef56f8878efa";
        const string version = "0.1.0+403aa01b11e969a6351595154569ef56f8878efa";
        ProductionLightingBuildIdentity identity = TestBuildIdentity();
        ProductionLightingExactBuildValidationEvidence evidence = ValidEvidence();

        Assert.Equal(version, identity.InformationalVersion);
        Assert.Equal(commit, identity.CommitHash);
        Assert.True(
            ProductionLightingEvidenceSource
                .IsExactBuildValidationEvidenceValid(evidence, identity));
        Assert.False(ProductionLightingEvidenceSource.IsExactBuildValidationEvidenceValid(
            evidence,
            identity with
            {
                ProtocolAssemblySha256 =
                    "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
            }));
        Assert.False(ProductionLightingEvidenceSource.IsExactBuildValidationEvidenceValid(
            evidence with { BaselineRestored = false },
            identity));
    }

    [Fact]
    public void ExternalExactBuildArtifact_UsesInjectedPathAndCurrentBinaryHash()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"openblade-lighting-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllBytes(
                path,
                JsonSerializer.SerializeToUtf8Bytes(
                    ValidEvidence(),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)
                    {
                        Converters =
                        {
                            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase),
                        },
                    }));

            Assert.True(
                ProductionLightingEvidenceSource
                    .HasCurrentExactBuildValidationArtifact(
                        path,
                        TestBuildIdentity()));
            Assert.False(
                ProductionLightingEvidenceSource
                    .HasCurrentExactBuildValidationArtifact(
                        path,
                        TestBuildIdentity() with
                        {
                            ProtocolAssemblySha256 =
                                "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB",
                        }));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task StaticWrite_UsesBrightnessReadbackHandshakeAndOneMatrixFrame()
    {
        var session = new LightingSession(initialBrightness: 0xFF);
        var writer = new MatrixWriter();
        await using var port = CreatePort(session, writer, admitted: true);
        LightingSettings requested = LightingSettingsFactory.Static(
            new RgbColor(0x12, 0x34, 0x56),
            brightness: 77);

        Assert.Equal(LightingRendererOwnershipState.None, port.OwnershipState);

        await port.SetLightingAsync(requested, default);

        Assert.Equal(LightingRendererOwnershipState.Static, port.OwnershipState);

        Assert.Equal(
            [
                (0x03, 0x83, "010500"),
                (0x03, 0x03, "0105C5"),
                (0x03, 0x83, "010500"),
                (0x00, 0x86, "000000"),
                (0x00, 0x84, "0000"),
                (0x03, 0x0A, "0500"),
            ],
            session.Commands.Select(command => (
                (int)command.ClassId,
                (int)command.CommandId,
                Convert.ToHexString(command.Arguments.Span))).ToArray());
        Pid02E0MatrixReport report = Assert.Single(writer.Reports);
        Assert.Equal(new MatrixRgb(0x12, 0x34, 0x56), report.Frame[1]);
        Assert.Equal(MatrixRgb.Black, report.Frame[0]);
    }

    [Fact]
    public async Task BrightnessStep_UsesCapturedRawStepAndOneWriteAfterCacheWarmup()
    {
        var session = new LightingSession(initialBrightness: 0xC0);
        await using var port = CreatePort(session, new MatrixWriter(), admitted: true);
        _ = await port.ReadStateAsync(default);
        session.Commands.Clear();

        byte down = await port.StepBrightnessAsync(
            KeyboardBrightnessDirection.Decrease,
            default);
        byte up = await port.StepBrightnessAsync(
            KeyboardBrightnessDirection.Increase,
            default);

        Assert.Equal(69, down);
        Assert.Equal(75, up);
        Assert.Equal(
            [
                (0x03, 0x03, "0105B0"),
                (0x03, 0x03, "0105C0"),
            ],
            session.Commands.Select(CommandTuple).ToArray());
    }

    [Fact]
    public async Task BrightnessStep_QueriesOnlyWhenRawCacheIsUnknown()
    {
        var session = new LightingSession(initialBrightness: 0xC0);
        await using var port = CreatePort(session, new MatrixWriter(), admitted: true);

        _ = await port.StepBrightnessAsync(
            KeyboardBrightnessDirection.Decrease,
            default);
        _ = await port.StepBrightnessAsync(
            KeyboardBrightnessDirection.Decrease,
            default);

        Assert.Equal(
            [
                (0x03, 0x83, "010500"),
                (0x03, 0x03, "0105B0"),
                (0x03, 0x03, "0105A0"),
            ],
            session.Commands.Select(CommandTuple).ToArray());
    }

    [Fact]
    public async Task BrightnessStep_FailedWriteInvalidatesRawCache()
    {
        var session = new LightingSession(initialBrightness: 0xC0);
        await using var port = CreatePort(session, new MatrixWriter(), admitted: true);
        _ = await port.ReadStateAsync(default);
        session.Commands.Clear();
        session.FailBrightnessSetOnCall = 1;

        await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.StepBrightnessAsync(
                KeyboardBrightnessDirection.Decrease,
                default));
        session.FailBrightnessSetOnCall = null;
        _ = await port.StepBrightnessAsync(
            KeyboardBrightnessDirection.Decrease,
            default);

        Assert.Equal(
            [
                (0x03, 0x03, "0105B0"),
                (0x03, 0x83, "010500"),
                (0x03, 0x03, "0105B0"),
            ],
            session.Commands.Select(CommandTuple).ToArray());
    }

    [Fact]
    public async Task TransitionFailureReportsSanitizedCommandAndValidationCategory()
    {
        var session = new LightingSession(initialBrightness: 0xC5)
        {
            TransitionFailure = new HidSessionResult<ReadOnlyMemory<byte>>(
                default,
                HidSessionFailureKind.ProtocolFailure)
            {
                Diagnostic = new(
                    HidSessionFailurePhase.ProtocolValidation,
                    HidSessionFailureKind.ProtocolFailure,
                    null,
                    91)
                {
                    ProtocolValidationFailure =
                        HidProtocolValidationFailure.PayloadValidator,
                },
            },
        };
        var writer = new MatrixWriter();
        await using var port = CreatePort(session, writer, admitted: true);

        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetLightingAsync(
                LightingSettingsFactory.Static(new RgbColor(1, 2, 3), 77),
                default));

        Assert.Equal(
            "The lighting command 00/86 failed (ProtocolFailure, PayloadValidator).",
            error.Message);
    }

    [Fact]
    public async Task Transition84UnexpectedSuccessfulPayloadIsReportedWithoutContinuing()
    {
        var session = new LightingSession(initialBrightness: 0xC5)
        {
            Transition84Response = [0x02, 0x00],
        };
        var writer = new MatrixWriter();
        await using var port = CreatePort(session, writer, admitted: true);

        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetLightingAsync(
                LightingSettingsFactory.Static(new RgbColor(1, 2, 3), 77),
                default));

        Assert.Equal(
            "The lighting command 00/84 failed (ProtocolFailure, PayloadValidator).",
            error.Message);
        Assert.DoesNotContain(
            session.Commands,
            command => (command.ClassId, command.CommandId) == (0x03, 0x0A));
        Assert.Empty(writer.Reports);
    }

    [Fact]
    public async Task Transition84ExactDeviceIdleResponseContinuesToMatrixWrite()
    {
        var session = new LightingSession(initialBrightness: 0xC5)
        {
            Transition84Response = [0x00, 0x00],
        };
        var writer = new MatrixWriter();
        await using var port = CreatePort(session, writer, admitted: true);

        await port.SetLightingAsync(
            LightingSettingsFactory.Static(new RgbColor(1, 2, 3), 77),
            default);

        Assert.Contains(
            session.Commands,
            command => (command.ClassId, command.CommandId) == (0x03, 0x0A));
        Assert.Single(writer.Reports);
    }

    [Fact]
    public async Task MatrixFailureReportsBoundedNativeDiagnostic()
    {
        var session = new LightingSession(initialBrightness: 0xC5);
        var writer = new MatrixWriter
        {
            NextDiagnostic = new(
                HidSessionFailurePhase.SetSubmit,
                HidSessionFailureKind.NativeIoFailure,
                87,
                0),
        };
        writer.Failures.Enqueue(HidSessionFailureKind.NativeIoFailure);
        await using var port = CreatePort(session, writer, admitted: true);

        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetLightingAsync(
                LightingSettingsFactory.Static(new RgbColor(1, 2, 3), 77),
                default));

        Assert.Equal(
            "The matrix write failed (NativeIoFailure, SetSubmit, 87, 0).",
            error.Message);
    }

    [Fact]
    public async Task ReadState_DoesNotPromoteOwnedRendererIntentToConfirmedState()
    {
        var session = new LightingSession(initialBrightness: 0xFF);
        var writer = new MatrixWriter();
        await using var port = CreatePort(session, writer, admitted: true);
        LightingSettings requested = LightingSettingsFactory.Static(
            new RgbColor(0, 0xFF, 0),
            brightness: 77);
        await port.SetLightingAsync(requested, default);

        Assert.Null(await port.ReadStateAsync(default));
    }

    [Fact]
    public async Task ReadState_DoesNotFabricateEffectWhenExternalControllerEnablesLighting()
    {
        var session = new LightingSession(initialBrightness: 0);
        var writer = new MatrixWriter();
        await using var port = CreatePort(session, writer, admitted: true);
        await port.SetLightingAsync(LightingSettingsFactory.Off(), default);

        session.Brightness = 0xFF;

        Assert.Null(await port.ReadStateAsync(default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadState_StableExternalOffStopsAndRelinquishesOwnedRenderer(
        bool rz090528)
    {
        var time = new ManualTimeProvider();
        var session = new LightingSession(initialBrightness: 0xFF);
        await using IProductionLightingPort port =
            CreateOwnedRendererReadPort(session, time, rz090528);
        await port.SetLightingAsync(
            LightingSettingsFactory.SpectrumCycling(100),
            default);
        session.BrightnessQueryOverrides.Enqueue(0);
        session.BrightnessQueryOverrides.Enqueue(0);

        Task<LightingSettings?> read = port.ReadStateAsync(default);
        Assert.False(read.IsCompleted);
        time.Advance(OwnedRendererOffObservation.OffConfirmationDelay);
        LightingSettings? observed = await read;

        Assert.Equal(LightingEffect.Off, observed?.Effect);
        Assert.Equal(LightingRendererOwnershipState.None, port.OwnershipState);
        Exception exception = await Assert.ThrowsAnyAsync<Exception>(() =>
            port.PulseReactiveKeyAsync(new ReactiveKeyPulse(19), default));
        if (!rz090528)
        {
            Assert.Equal(
                WriteSubmissionState.KnownNotApplied,
                Assert.IsType<BladeWriteException>(exception).SubmissionState);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadState_TransientOffPreservesOwnedRenderer(bool rz090528)
    {
        var time = new ManualTimeProvider();
        var session = new LightingSession(initialBrightness: 0xFF);
        await using IProductionLightingPort port =
            CreateOwnedRendererReadPort(session, time, rz090528);
        await port.SetLightingAsync(
            LightingSettingsFactory.SpectrumCycling(100),
            default);
        session.BrightnessQueryOverrides.Enqueue(0);
        session.BrightnessQueryOverrides.Enqueue(0xFF);

        Task<LightingSettings?> read = port.ReadStateAsync(default);
        Assert.False(read.IsCompleted);
        time.Advance(OwnedRendererOffObservation.OffConfirmationDelay);
        LightingSettings? result = await read;

        Assert.Null(result);
        Assert.Equal(LightingRendererOwnershipState.Spectrum, port.OwnershipState);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadState_AlreadyRelinquishedOffDoesNotReconfirm(
        bool rz090528)
    {
        var time = new ManualTimeProvider();
        var session = new LightingSession(initialBrightness: 0xFF);
        await using IProductionLightingPort port =
            CreateOwnedRendererReadPort(session, time, rz090528);
        await port.SetLightingAsync(
            LightingSettingsFactory.Static(new RgbColor(1, 2, 3), 100),
            default);
        session.BrightnessQueryOverrides.Enqueue(0);
        session.BrightnessQueryOverrides.Enqueue(0);

        Task<LightingSettings?> firstRead = port.ReadStateAsync(default);
        Assert.False(firstRead.IsCompleted);
        time.Advance(OwnedRendererOffObservation.OffConfirmationDelay);
        LightingSettings? firstResult = await firstRead;
        Assert.Equal(LightingEffect.Off, firstResult?.Effect);
        Assert.Equal(LightingRendererOwnershipState.None, port.OwnershipState);
        int queriesAfterRelinquish = session.BrightnessQueryCalls;
        session.Brightness = 0;

        Task<LightingSettings?> secondRead = port.ReadStateAsync(default);

        Assert.True(secondRead.IsCompleted);
        LightingSettings? secondResult = await secondRead;
        Assert.Equal(LightingEffect.Off, secondResult?.Effect);
        Assert.Equal(LightingRendererOwnershipState.None, port.OwnershipState);
        Assert.Equal(queriesAfterRelinquish + 1, session.BrightnessQueryCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadState_ConfirmationFailurePreservesOwnedRenderer(
        bool rz090528)
    {
        var time = new ManualTimeProvider();
        var session = new LightingSession(initialBrightness: 0xFF);
        await using IProductionLightingPort port =
            CreateOwnedRendererReadPort(session, time, rz090528);
        await port.SetLightingAsync(
            LightingSettingsFactory.Static(new RgbColor(1, 2, 3), 100),
            default);
        session.BrightnessQueryOverrides.Enqueue(0);
        session.FailBrightnessQueryOnCall = session.BrightnessQueryCalls + 2;

        Task<LightingSettings?> read = port.ReadStateAsync(default);
        Assert.False(read.IsCompleted);
        time.Advance(OwnedRendererOffObservation.OffConfirmationDelay);

        _ = await Assert.ThrowsAnyAsync<Exception>(() => read);
        Assert.Equal(LightingRendererOwnershipState.Static, port.OwnershipState);
        if (!rz090528)
        {
            int queriesBeforeStep = session.BrightnessQueryCalls;
            session.FailBrightnessQueryOnCall = null;
            session.Brightness = 0xFF;

            _ = await port.StepBrightnessAsync(
                KeyboardBrightnessDirection.Decrease,
                default);

            Assert.True(session.BrightnessQueryCalls > queriesBeforeStep);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadState_CancellationPreservesOwnershipAndReleasesGate(
        bool rz090528)
    {
        var time = new ManualTimeProvider();
        var session = new LightingSession(initialBrightness: 0xFF);
        await using IProductionLightingPort port =
            CreateOwnedRendererReadPort(session, time, rz090528);
        await port.SetLightingAsync(
            LightingSettingsFactory.Static(new RgbColor(1, 2, 3), 100),
            default);
        session.BrightnessQueryOverrides.Enqueue(0);
        using var cancellation = new CancellationTokenSource();

        Task<LightingSettings?> read = port.ReadStateAsync(cancellation.Token);
        Assert.False(read.IsCompleted);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.Equal(LightingRendererOwnershipState.Static, port.OwnershipState);
        session.Brightness = 0xFF;
        if (!rz090528)
        {
            int queriesBeforeStep = session.BrightnessQueryCalls;

            _ = await port.StepBrightnessAsync(
                KeyboardBrightnessDirection.Decrease,
                default);

            Assert.True(session.BrightnessQueryCalls > queriesBeforeStep);
        }
        else
        {
            Assert.Null(await port.ReadStateAsync(default));
        }
    }

    [Fact]
    public async Task ReactivePulse_IsRejectedUnlessReactiveIsCurrent()
    {
        var session = new LightingSession(initialBrightness: 0xFF);
        var writer = new MatrixWriter();
        await using var port = CreatePort(session, writer, admitted: true);
        await port.SetLightingAsync(
            LightingSettingsFactory.Static(new RgbColor(1, 2, 3), 100),
            default);
        BladeWriteException exception = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.PulseReactiveKeyAsync(new ReactiveKeyPulse(19), default));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, exception.SubmissionState);
    }

    [Fact]
    public async Task PlaybackMuteIndicator_OverlaysF1RedAndRestoresTheCurrentEffect()
    {
        var session = new LightingSession(initialBrightness: 0xFF);
        var writer = new MatrixWriter();
        await using var port = CreatePort(session, writer, admitted: true);
        var baseColor = new RgbColor(0x12, 0x34, 0x56);
        await port.SetLightingAsync(
            LightingSettingsFactory.Static(baseColor, 100),
            default);

        await port.SetPlaybackMuteIndicatorAsync(muted: true, default);

        Assert.Equal(2, writer.Reports.Count);
        Assert.Equal(new MatrixRgb(0xFF, 0x00, 0x00), writer.Reports[^1].Frame[2]);
        Assert.Equal(new MatrixRgb(0x12, 0x34, 0x56), writer.Reports[^1].Frame[3]);

        await port.SetPlaybackMuteIndicatorAsync(muted: false, default);

        Assert.Equal(3, writer.Reports.Count);
        Assert.Equal(new MatrixRgb(0x12, 0x34, 0x56), writer.Reports[^1].Frame[2]);
    }

    [Fact]
    public async Task PlaybackMuteIndicator_SetBeforeLightingAppliesToInitialFrame()
    {
        var session = new LightingSession(initialBrightness: 0);
        var writer = new MatrixWriter();
        await using var port = CreatePort(session, writer, admitted: true);

        await port.SetPlaybackMuteIndicatorAsync(muted: true, default);
        Assert.Empty(writer.Reports);

        await port.SetLightingAsync(
            LightingSettingsFactory.Static(new RgbColor(0, 0xFF, 0), 100),
            default);

        Assert.Single(writer.Reports);
        Assert.Equal(new MatrixRgb(0xFF, 0x00, 0x00), writer.Reports[0].Frame[2]);
        Assert.Equal(new MatrixRgb(0, 0xFF, 0), writer.Reports[0].Frame[3]);
    }

    [Fact]
    public async Task PlaybackMuteIndicator_FailedWriteRollsBackRenderStateForRetry()
    {
        var session = new LightingSession(initialBrightness: 0xFF);
        var writer = new MatrixWriter();
        await using var port = CreatePort(session, writer, admitted: true);
        var baseColor = new RgbColor(0x12, 0x34, 0x56);
        LightingSettings lighting = LightingSettingsFactory.Static(baseColor, 100);
        await port.SetLightingAsync(lighting, default);
        writer.Failures.Enqueue(HidSessionFailureKind.ConflictBlocked);

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetPlaybackMuteIndicatorAsync(muted: true, default));

        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        await port.SetLightingAsync(lighting, default);
        Assert.Equal(new MatrixRgb(0x12, 0x34, 0x56), writer.Reports[^1].Frame[2]);

        await port.SetPlaybackMuteIndicatorAsync(muted: true, default);
        Assert.Equal(new MatrixRgb(0xFF, 0, 0), writer.Reports[^1].Frame[2]);
    }

    [Fact]
    public async Task ReactivePulse_WritesTheRequestedSlotWhenLiveRendererIsReactive()
    {
        var session = new LightingSession(initialBrightness: 0xFF);
        var writer = new MatrixWriter();
        await using var port = CreatePort(session, writer, admitted: true);
        await port.SetLightingAsync(
            LightingSettingsFactory.Reactive(
                new RgbColor(0x12, 0x34, 0x56),
                100,
                ReactiveLightingDuration.Medium),
            default);

        await port.PulseReactiveKeyAsync(new ReactiveKeyPulse(19), default);
        await writer.WaitForWriteCountAsync(2, TimeSpan.FromSeconds(2));

        Assert.Equal(new MatrixRgb(0x12, 0x34, 0x56), writer.Reports[^1].Frame[19]);
    }

    [Fact]
    public async Task ReactivePulse_RetainsTheExistingRendererTask()
    {
        var session = new LightingSession(initialBrightness: 0xFF);
        var writer = new MatrixWriter();
        await using var port = CreatePort(session, writer, admitted: true);
        await port.SetLightingAsync(
            LightingSettingsFactory.Reactive(
                new RgbColor(0x12, 0x34, 0x56),
                100,
                ReactiveLightingDuration.Medium),
            default);
        FieldInfo streamTaskField = typeof(ProductionLightingPort).GetField(
            "streamTask",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        Task before = Assert.IsAssignableFrom<Task>(streamTaskField.GetValue(port));

        await port.PulseReactiveKeyAsync(new ReactiveKeyPulse(19), default);

        Assert.Same(before, streamTaskField.GetValue(port));
    }

    [Fact]
    public async Task ReactivePulse_ReturnsWithoutWaitingForTheMatrixWrite()
    {
        var session = new LightingSession(initialBrightness: 0xFF);
        var writer = new MatrixWriter();
        await using var port = CreatePort(session, writer, admitted: true);
        await port.SetLightingAsync(
            LightingSettingsFactory.Reactive(
                new RgbColor(0x12, 0x34, 0x56),
                100,
                ReactiveLightingDuration.Medium),
            default);
        writer.BlockNextWrite();

        await port.PulseReactiveKeyAsync(new ReactiveKeyPulse(19), default)
            .WaitAsync(TimeSpan.FromMilliseconds(250));
        await writer.WaitForWriteCountAsync(2, TimeSpan.FromSeconds(2));

        writer.ReleaseBlockedWrite();
    }

    [Fact]
    public async Task ReactivePulse_QueuedDuringSlowWriteStillReceivesAVisibleFrame()
    {
        var session = new LightingSession(initialBrightness: 0xFF);
        var writer = new MatrixWriter();
        await using var port = CreatePort(session, writer, admitted: true);
        await port.SetLightingAsync(
            LightingSettingsFactory.Reactive(
                new RgbColor(0x12, 0x34, 0x56),
                100,
                ReactiveLightingDuration.Short),
            default);
        writer.BlockNextWrite();

        await port.PulseReactiveKeyAsync(new ReactiveKeyPulse(19), default);
        await writer.WaitForWriteCountAsync(2, TimeSpan.FromSeconds(2));
        await port.PulseReactiveKeyAsync(new ReactiveKeyPulse(53), default);
        await Task.Delay(TimeSpan.FromMilliseconds(600));

        writer.ReleaseBlockedWrite();
        await writer.WaitForWriteCountAsync(3, TimeSpan.FromSeconds(2));

        Assert.Equal(new MatrixRgb(0x12, 0x34, 0x56), writer.Reports[^1].Frame[53]);
    }

    [Fact]
    public async Task ReactivePulse_QueuedDuringCadenceRestartWakesReplacementStream()
    {
        var session = new LightingSession(initialBrightness: 0xFF);
        var writer = new MatrixWriter();
        await using var port = CreatePort(session, writer, admitted: true);
        await port.SetLightingAsync(
            LightingSettingsFactory.Reactive(
                new RgbColor(0x12, 0x34, 0x56),
                100,
                ReactiveLightingDuration.Medium),
            default);
        writer.BlockNextWrite();

        await port.PulseReactiveKeyAsync(new ReactiveKeyPulse(19), default);
        await writer.WaitForWriteCountAsync(2, TimeSpan.FromSeconds(2));
        Task switchCadence = port.SetLightingRefreshRateAsync(
            LightingRefreshRate.SixtyHz,
            default);
        await port.PulseReactiveKeyAsync(new ReactiveKeyPulse(53), default);

        writer.ReleaseBlockedWrite();
        await switchCadence.WaitAsync(TimeSpan.FromSeconds(2));
        await writer.WaitForWriteCountAsync(3, TimeSpan.FromSeconds(2));

        Assert.Equal(new MatrixRgb(0x12, 0x34, 0x56), writer.Reports[^1].Frame[53]);
    }

    [Fact]
    public async Task ReactivePulse_DoesNotWaitBehindFirmwareOperationGate()
    {
        var session = new LightingSession(initialBrightness: 0xFF);
        var writer = new MatrixWriter();
        await using var port = CreatePort(session, writer, admitted: true);
        await port.SetLightingAsync(
            LightingSettingsFactory.Reactive(
                new RgbColor(0x12, 0x34, 0x56),
                100,
                ReactiveLightingDuration.Medium),
            default);
        var gate = Assert.IsType<SemaphoreSlim>(typeof(ProductionLightingPort)
            .GetField("operationGate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(port));
        await gate.WaitAsync();
        try
        {
            await port.PulseReactiveKeyAsync(new ReactiveKeyPulse(19), default)
                .WaitAsync(TimeSpan.FromMilliseconds(250));
        }
        finally
        {
            gate.Release();
        }

        await writer.WaitForWriteCountAsync(2, TimeSpan.FromSeconds(2));
        Assert.Equal(new MatrixRgb(0x12, 0x34, 0x56), writer.Reports[^1].Frame[19]);
    }

    [Fact]
    public async Task ReactivePulse_ClampsStaleObservedTimestampToVisibleEnvelope()
    {
        var session = new LightingSession(initialBrightness: 0xFF);
        await using var port = CreatePort(session, new MatrixWriter(), admitted: true);
        FieldInfo startedField = typeof(ProductionLightingPort).GetField(
            "effectStartedTimestamp",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        MethodInfo elapsedMethod = typeof(ProductionLightingPort).GetMethod(
            "KeyPulseElapsedOrFallback",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        long started = TimeProvider.System.GetTimestamp();
        startedField.SetValue(port, started);
        long observed = started + (TimeProvider.System.TimestampFrequency / 4);
        TimeSpan fallback = TimeSpan.FromSeconds(1);

        var elapsed = Assert.IsType<TimeSpan>(elapsedMethod.Invoke(
            port,
            [new ReactiveKeyPulse(19, observed), fallback]));

        Assert.InRange(
            elapsed,
            TimeSpan.FromMilliseconds(920),
            TimeSpan.FromMilliseconds(930));
    }

    [Fact]
    public async Task ReactivePulse_UsesRenderTimeWhenObservedBeforeEffectStart()
    {
        var session = new LightingSession(initialBrightness: 0xFF);
        await using var port = CreatePort(session, new MatrixWriter(), admitted: true);
        FieldInfo startedField = typeof(ProductionLightingPort).GetField(
            "effectStartedTimestamp",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        MethodInfo elapsedMethod = typeof(ProductionLightingPort).GetMethod(
            "KeyPulseElapsedOrFallback",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        long started = TimeProvider.System.GetTimestamp();
        startedField.SetValue(port, started);
        TimeSpan fallback = TimeSpan.FromSeconds(2);

        var elapsed = Assert.IsType<TimeSpan>(elapsedMethod.Invoke(
            port,
            [new ReactiveKeyPulse(19, started - 1), fallback]));

        Assert.Equal(fallback, elapsed);
    }

    [Fact]
    public async Task ReactivePulse_ClampsFutureObservedTimestampToRenderTime()
    {
        var session = new LightingSession(initialBrightness: 0xFF);
        await using var port = CreatePort(session, new MatrixWriter(), admitted: true);
        FieldInfo startedField = typeof(ProductionLightingPort).GetField(
            "effectStartedTimestamp",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        MethodInfo elapsedMethod = typeof(ProductionLightingPort).GetMethod(
            "KeyPulseElapsedOrFallback",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        long started = TimeProvider.System.GetTimestamp();
        startedField.SetValue(port, started);
        long observed = started + (TimeProvider.System.TimestampFrequency * 5);
        TimeSpan fallback = TimeSpan.FromMilliseconds(125);

        var elapsed = Assert.IsType<TimeSpan>(elapsedMethod.Invoke(
            port,
            [new ReactiveKeyPulse(19, observed), fallback]));

        Assert.Equal(fallback, elapsed);
    }

    [Fact]
    public void FrameDeadline_AdvancesFromScheduledTimeAndSkipsMissedFrames()
    {
        var effect = new Pid02E0SpectrumEffect(
            cycleDuration: TimeSpan.FromSeconds(1));

        TimeSpan? next = ProductionLightingPort.AdvanceFrameDeadline(
            effect,
            currentDeadline: Pid02E0LightingTiming.AnimatedFrameInterval,
            observedAfterWrite: TimeSpan.FromMilliseconds(82));

        Assert.Equal(Pid02E0LightingTiming.AnimatedFrameInterval * 3, next);
    }

    [Fact]
    public void SixtyHzFrameDeadline_UsesSelectedCadenceAndSkipsMissedFrames()
    {
        var effect = new Pid02E0SpectrumEffect(
            cycleDuration: TimeSpan.FromSeconds(1));
        TimeSpan interval = ProductionLightingPort.RefreshInterval(
            LightingRefreshRate.SixtyHz);

        TimeSpan? next = ProductionLightingPort.AdvanceFrameDeadline(
            effect,
            currentDeadline: interval,
            observedAfterWrite: TimeSpan.FromMilliseconds(42),
            selectedRefreshRate: LightingRefreshRate.SixtyHz);

        Assert.Equal(interval * 3, next);
        Assert.Equal(
            TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 30),
            ProductionLightingPort.RefreshInterval(LightingRefreshRate.ThirtyHz));
    }

    [Fact]
    public void SelectableCadence_PreservesIdleAndBreathingPlateauSuppression()
    {
        var reactive = new Pid02E0ReactiveEffect(
            new MatrixRgb(0x10, 0x20, 0x30),
            TimeSpan.FromMilliseconds(500));
        TimeSpan idle = reactive.GetNextFrameDelay(TimeSpan.Zero)!.Value;

        Assert.Equal(
            idle,
            ProductionLightingPort.ApplyRefreshRateToDelay(
                reactive,
                idle,
                LightingRefreshRate.SixtyHz));

        var breathing = Pid02E0BreathingEffect.Single(
            new MatrixRgb(0x10, 0x20, 0x30));
        TimeSpan plateau = breathing.GetNextFrameDelay(
            TimeSpan.FromMilliseconds(1200))!.Value;
        TimeSpan selected = ProductionLightingPort.ApplyRefreshRateToDelay(
            breathing,
            plateau,
            LightingRefreshRate.SixtyHz);

        Assert.True(plateau > Pid02E0LightingTiming.AnimatedFrameInterval);
        Assert.Equal(
            plateau - Pid02E0LightingTiming.AnimatedFrameInterval
                + ProductionLightingPort.RefreshInterval(LightingRefreshRate.SixtyHz),
            selected);
        Assert.Equal(
            plateau,
            ProductionLightingPort.ApplyRefreshRateToDelay(
                breathing,
                plateau,
                LightingRefreshRate.ThirtyHz));
    }

    [Fact]
    public void SixtyHzFrameDeadline_PreservesReactiveAndRippleIdleKeepalive()
    {
        IPid02E0LightingEffect[] effects =
        [
            new Pid02E0ReactiveEffect(
                new MatrixRgb(0x10, 0x20, 0x30),
                TimeSpan.FromMilliseconds(500)),
            new Pid02E0RippleEffect(new MatrixRgb(0x10, 0x20, 0x30)),
        ];

        foreach (IPid02E0LightingEffect effect in effects)
        {
            Assert.Equal(
                Pid02E0LightingTiming.IdleKeepaliveInterval,
                ProductionLightingPort.AdvanceFrameDeadline(
                    effect,
                    currentDeadline: TimeSpan.Zero,
                    observedAfterWrite: TimeSpan.Zero,
                    selectedRefreshRate: LightingRefreshRate.SixtyHz));
        }
    }

    [Fact]
    public void SixtyHzFrameDeadline_PreservesBreathingPlateauSuppression()
    {
        var effect = Pid02E0BreathingEffect.Single(
            new MatrixRgb(0x10, 0x20, 0x30));
        TimeSpan current = TimeSpan.FromMilliseconds(1200);
        TimeSpan delay = effect.GetNextFrameDelay(current)!.Value;

        Assert.Equal(
            current - Pid02E0LightingTiming.AnimatedFrameInterval
                + delay
                + ProductionLightingPort.RefreshInterval(LightingRefreshRate.SixtyHz),
            ProductionLightingPort.AdvanceFrameDeadline(
                effect,
                currentDeadline: current,
                observedAfterWrite: current,
                selectedRefreshRate: LightingRefreshRate.SixtyHz));
    }

    [Fact]
    public async Task Rz090528RefreshRate_AcceptsOnlyFixedThirtyHz()
    {
        await using var port = new Rz090528BrightnessLightingPort(
            new ValidationActor(
                new LightingSession(initialBrightness: 49),
                new BatteryCommands(),
                new TemporaryCommands()),
            TimeSpan.FromSeconds(1));

        await port.SetLightingRefreshRateAsync(
            LightingRefreshRate.ThirtyHz,
            CancellationToken.None);
        await Assert.ThrowsAsync<CapabilityUnavailableException>(
            () => port.SetLightingRefreshRateAsync(
                LightingRefreshRate.SixtyHz,
                CancellationToken.None));
    }

    [Fact]
    public void Rz090528FrameDeadline_AdvancesFromScheduleAndSkipsMissedFrames()
    {
        var effect = new Rz090528SpectrumEffect();

        TimeSpan? next = Rz090528BrightnessLightingPort.AdvanceFrameDeadline(
            effect,
            currentDeadline: Rz090528LightingTiming.AnimatedFrameInterval,
            observedAfterWrite: TimeSpan.FromMilliseconds(82));

        Assert.Equal(Rz090528LightingTiming.AnimatedFrameInterval * 3, next);
    }

    [Fact]
    public void Rz090528FrameDeadline_RejectsNonPositiveEffectDelay()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Rz090528BrightnessLightingPort.AdvanceFrameDeadline(
                new NonAdvancingRz090528Effect(),
                currentDeadline: TimeSpan.Zero,
                observedAfterWrite: TimeSpan.Zero));
    }

    [Fact]
    public async Task AmbientAwareness_WritesOnlyTheTypedSessionSampleWhileOwned()
    {
        var session = new LightingSession(initialBrightness: 0xFF);
        var writer = new MatrixWriter();
        await using var port = CreatePort(session, writer, admitted: true);
        await port.SetLightingAsync(
            LightingSettingsFactory.AmbientAwareness(
                AmbientAwarenessRegion.Right,
                100),
            default);
        RgbColor[] colors = new RgbColor[AmbientLightingFrame.SlotCount];
        colors[19] = new RgbColor(0x12, 0x34, 0x56);

        await port.PushAmbientLightingFrameAsync(
            new AmbientLightingFrame(colors),
            default);

        Assert.Equal(
            LightingRendererOwnershipState.AmbientAwareness,
            port.OwnershipState);
        Assert.Equal(new MatrixRgb(0x12, 0x34, 0x56), writer.Reports[^1].Frame[19]);
        Assert.Equal(MatrixRgb.Black, writer.Reports[^1].Frame[0]);
    }

    [Fact]
    public async Task AudioMeter_WritesOnlyTheTypedSessionSampleWhileOwned()
    {
        var session = new LightingSession(initialBrightness: 0xFF);
        var writer = new MatrixWriter();
        await using var port = CreatePort(session, writer, admitted: true);
        await port.SetLightingAsync(
            LightingSettingsFactory.AudioMeter(colorBoost: 1.25, brightness: 100),
            default);
        RgbColor[] colors = new RgbColor[AmbientLightingFrame.SlotCount];
        colors[19] = new RgbColor(0x12, 0x34, 0x56);

        await port.PushAmbientLightingFrameAsync(
            new AmbientLightingFrame(colors),
            default);

        Assert.Equal(
            LightingRendererOwnershipState.AudioMeter,
            port.OwnershipState);
        Assert.Equal(new MatrixRgb(0x12, 0x34, 0x56), writer.Reports[^1].Frame[19]);
        Assert.Equal(MatrixRgb.Black, writer.Reports[^1].Frame[0]);
    }

    [Fact]
    public async Task KeyPulse_StartsRippleWhenLiveRendererIsRipple()
    {
        var session = new LightingSession(initialBrightness: 0xFF);
        var writer = new MatrixWriter();
        await using var port = CreatePort(session, writer, admitted: true);
        await port.SetLightingAsync(
            LightingSettingsFactory.Ripple(new RgbColor(0x12, 0x34, 0x56), 100),
            default);

        await port.PulseReactiveKeyAsync(new ReactiveKeyPulse(53), default);
        await writer.WaitForWriteCountAsync(2, TimeSpan.FromSeconds(2));

        Assert.Equal(LightingRendererOwnershipState.Ripple, port.OwnershipState);
        Assert.Equal(new MatrixRgb(0x12, 0x34, 0x56), writer.Reports[^1].Frame[53]);
    }

    [Fact]
    public async Task BrightnessOnlyChange_RetainsRendererAndSkipsTransitionHandshake()
    {
        var session = new LightingSession(initialBrightness: 0xFF);
        var writer = new MatrixWriter();
        await using var port = CreatePort(session, writer, admitted: true);
        RgbColor color = new(0x12, 0x34, 0x56);
        await port.SetLightingAsync(LightingSettingsFactory.Static(color, 100), default);
        int commandsBefore = session.Commands.Count;
        int framesBefore = writer.Reports.Count;

        await port.SetLightingAsync(LightingSettingsFactory.Static(color, 50), default);

        Assert.Equal(framesBefore, writer.Reports.Count);
        Assert.Equal(
            [
                (0x03, 0x83, "010500"),
                (0x03, 0x03, "010580"),
                (0x03, 0x83, "010500"),
            ],
            session.Commands.Skip(commandsBefore).Select(command => (
                (int)command.ClassId,
                (int)command.CommandId,
                Convert.ToHexString(command.Arguments.Span))).ToArray());
    }

    [Fact]
    public async Task BrightnessOnlyReadbackFailure_RestoresBaselineAndRetainsRenderer()
    {
        var session = new LightingSession(initialBrightness: 0xFF);
        var writer = new MatrixWriter();
        await using var port = CreatePort(session, writer, admitted: true);
        RgbColor color = new(0x12, 0x34, 0x56);
        await port.SetLightingAsync(LightingSettingsFactory.Static(color, 100), default);
        int framesBefore = writer.Reports.Count;
        session.BrightnessQueryOverrides.Enqueue(0xFF);
        session.BrightnessQueryOverrides.Enqueue(0x81);

        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetLightingAsync(LightingSettingsFactory.Static(color, 50), default));

        Assert.Equal(WriteSubmissionState.Indeterminate, error.SubmissionState);
        Assert.Equal(0xFF, session.Brightness);
        Assert.Equal(framesBefore, writer.Reports.Count);
        Assert.Equal(LightingRendererOwnershipState.Static, port.OwnershipState);
        Assert.Null(await port.ReadStateAsync(default));
    }

    [Fact]
    public async Task FailedTransitionAndBrightnessRestore_IsIndeterminate()
    {
        var session = new LightingSession(initialBrightness: 0xFF);
        var writer = new MatrixWriter();
        await using var port = CreatePort(session, writer, admitted: true);
        LightingSettings previous = LightingSettingsFactory.Static(
            new RgbColor(0, 0xFF, 0),
            100);
        await port.SetLightingAsync(previous, default);
        writer.Failures.Enqueue(HidSessionFailureKind.ConflictBlocked);
        session.FailBrightnessSetOnCall = session.BrightnessSetCalls + 2;

        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetLightingAsync(
                LightingSettingsFactory.Static(new RgbColor(0xFF, 0, 0), 50),
                default));

        Assert.Equal(WriteSubmissionState.Indeterminate, error.SubmissionState);
        Assert.IsType<BladeWriteException>(error.InnerException);
        Assert.Equal(
            WriteSubmissionState.KnownNotApplied,
            ((BladeWriteException)error.InnerException!).SubmissionState);
        Assert.Equal(LightingRendererOwnershipState.Static, port.OwnershipState);
        Assert.Equal(new MatrixRgb(0, 0xFF, 0), writer.Reports[^1].Frame[1]);
    }

    [Fact]
    public async Task FailedTransitionAndPriorEffectRestore_IsIndeterminate()
    {
        var session = new LightingSession(initialBrightness: 0xFF);
        var writer = new MatrixWriter();
        await using var port = CreatePort(session, writer, admitted: true);
        await port.SetLightingAsync(
            LightingSettingsFactory.Static(new RgbColor(0, 0xFF, 0), 100),
            default);
        writer.Failures.Enqueue(HidSessionFailureKind.ConflictBlocked);
        writer.Failures.Enqueue(HidSessionFailureKind.ProtocolFailure);

        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetLightingAsync(
                LightingSettingsFactory.Static(new RgbColor(0xFF, 0, 0), 100),
                default));

        Assert.Equal(WriteSubmissionState.Indeterminate, error.SubmissionState);
        Assert.IsType<BladeWriteException>(error.InnerException);
        Assert.Equal(
            WriteSubmissionState.KnownNotApplied,
            ((BladeWriteException)error.InnerException!).SubmissionState);
        Assert.Equal(LightingRendererOwnershipState.None, port.OwnershipState);
    }

    [Fact]
    public async Task FailedEffectTransition_RestartsThePreviousOwnedEffect()
    {
        var session = new LightingSession(initialBrightness: 0xFF);
        var writer = new MatrixWriter();
        await using var port = CreatePort(session, writer, admitted: true);
        LightingSettings previous = LightingSettingsFactory.Static(
            new RgbColor(0, 0xFF, 0),
            100);
        await port.SetLightingAsync(previous, default);
        writer.FailNextWrite();

        await Assert.ThrowsAsync<BladeWriteException>(() => port.SetLightingAsync(
            LightingSettingsFactory.Static(new RgbColor(0xFF, 0, 0), 100),
            default));

        Assert.Equal(new MatrixRgb(0, 0xFF, 0), writer.Reports[^1].Frame[1]);
        Assert.Equal(LightingRendererOwnershipState.Static, port.OwnershipState);
        Assert.Null(await port.ReadStateAsync(default));
    }

    [Fact]
    public async Task AnimatedStreamFault_IsExposedAsFaultedOwnership()
    {
        var session = new LightingSession(initialBrightness: 0xFF);
        var writer = new MatrixWriter();
        var time = new ManualTimeProvider();
        await using var port = CreatePort(
            session,
            writer,
            admitted: true,
            timeProvider: time);
        await port.SetLightingAsync(
            LightingSettingsFactory.BreathingSingle(new RgbColor(0, 0xFF, 0), 100),
            default);
        Assert.Equal(LightingRendererOwnershipState.Breathing, port.OwnershipState);
        Task stream = Assert.IsAssignableFrom<Task>(typeof(ProductionLightingPort)
            .GetField("streamTask", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(port));

        writer.FailNextWrite();
        time.Advance(Pid02E0LightingTiming.AnimatedFrameInterval);
        await stream.WaitAsync(TestDeadlockGuard);

        LightingRendererOwnershipState invalidated = await port.InvalidateAsync();

        Assert.Equal(LightingRendererOwnershipState.Faulted, invalidated);
        Assert.Equal(LightingRendererOwnershipState.None, port.OwnershipState);
    }

    [Fact]
    public async Task Disposal_ClearsOwnership()
    {
        var session = new LightingSession(initialBrightness: 0xFF);
        var writer = new MatrixWriter();
        var port = CreatePort(session, writer, admitted: true);
        await port.SetLightingAsync(
            LightingSettingsFactory.Static(new RgbColor(1, 2, 3), 100),
            default);

        LightingRendererOwnershipState invalidated = await port.InvalidateAsync();

        Assert.Equal(LightingRendererOwnershipState.Static, invalidated);
        Assert.Equal(LightingRendererOwnershipState.None, port.OwnershipState);
    }

    [Fact]
    public async Task ConcurrentCloseCallersAwaitTheSameCleanup()
    {
        var session = new LightingSession(initialBrightness: 0xFF);
        var writer = new MatrixWriter();
        var port = CreatePort(session, writer, admitted: true);
        writer.BlockNextWrite();
        Task write = port.SetLightingAsync(
            LightingSettingsFactory.Static(new RgbColor(1, 2, 3), 100),
            default);
        await writer.WaitForWriteCountAsync(1, TimeSpan.FromSeconds(2));

        Task<LightingRendererOwnershipState> first = port.InvalidateAsync();
        Task<LightingRendererOwnershipState> second = port.InvalidateAsync();
        await Task.Delay(50);

        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);

        writer.ReleaseBlockedWrite();
        await write;
        LightingRendererOwnershipState[] results = await Task.WhenAll(first, second);

        Assert.Equal(results[0], results[1]);
        Assert.Equal(LightingRendererOwnershipState.None, port.OwnershipState);
        await port.DisposeAsync();
    }

    [Fact]
    public async Task DisposeWithinCleanupDeadline_ReturnsTrueWhenTheCloseDrains()
    {
        var registry = new PendingDisposalRegistry();
        var port = CreatePort(
            new LightingSession(initialBrightness: 0xFF),
            new MatrixWriter(),
            admitted: true,
            pendingDisposals: registry);

        bool drained = await port.DisposeWithinCleanupDeadlineAsync();

        Assert.True(drained);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task DisposeWithinCleanupDeadline_ReturnsFalseAndRetainsWhenTheCloseStalls()
    {
        var time = new ManualTimeProvider();
        var registry = new PendingDisposalRegistry();
        var port = CreatePort(
            new LightingSession(initialBrightness: 0xFF),
            new MatrixWriter(),
            admitted: true,
            timeProvider: time,
            cleanupDrainTimeout: TimeSpan.FromSeconds(1),
            pendingDisposals: registry);
        SemaphoreSlim gate = OperationGate(port);
        await gate.WaitAsync();

        Task<bool> disposal = port.DisposeWithinCleanupDeadlineAsync();
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.False(await disposal);
        Assert.Equal(1, registry.Count);

        gate.Release();
        await registry.WhenEmptyAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task DisposeWithinCleanupDeadline_ReturnsFalseWhenTheCloseFaults()
    {
        var session = new LightingSession(initialBrightness: 0xFF)
        {
            DisposeFailure = new IOException("Synthetic close failure."),
        };
        var registry = new PendingDisposalRegistry();
        var port = CreatePort(
            session,
            new MatrixWriter(),
            admitted: true,
            pendingDisposals: registry);

        bool drained = await port.DisposeWithinCleanupDeadlineAsync();

        Assert.False(drained);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task DisposeWithinCleanupDeadline_IsIdempotentAcrossRepeatCalls()
    {
        var time = new ManualTimeProvider();
        var registry = new PendingDisposalRegistry();
        var port = CreatePort(
            new LightingSession(initialBrightness: 0xFF),
            new MatrixWriter(),
            admitted: true,
            timeProvider: time,
            cleanupDrainTimeout: TimeSpan.FromSeconds(1),
            pendingDisposals: registry);
        SemaphoreSlim gate = OperationGate(port);
        await gate.WaitAsync();

        Task<bool> first = port.DisposeWithinCleanupDeadlineAsync();
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(await first);

        Task<bool> second = port.DisposeWithinCleanupDeadlineAsync();
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(await second);
        Assert.Equal(1, registry.Count);

        gate.Release();
        await registry.WhenEmptyAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(await port.DisposeWithinCleanupDeadlineAsync());
    }

    [Fact]
    public async Task AnimatedStream_DoesNotPassItsStopTokenIntoNativeWrites()
    {
        var session = new LightingSession(initialBrightness: 0xFF);
        var writer = new MatrixWriter();
        await using var port = CreatePort(session, writer, admitted: true);

        await port.SetLightingAsync(
            LightingSettingsFactory.BreathingSingle(new RgbColor(0, 0xFF, 0), 100),
            default);
        await writer.WaitForWriteCountAsync(2, TimeSpan.FromSeconds(2));

        Assert.False(writer.CancellationTokens[1].CanBeCanceled);
    }

    [Fact]
    public async Task UnadmittedPortRejectsWriteBeforeAnyIo()
    {
        var session = new LightingSession(initialBrightness: 0xFF);
        await using var port = CreatePort(session, writer: null, admitted: false);

        CapabilityUnavailableException error =
            await Assert.ThrowsAsync<CapabilityUnavailableException>(() =>
                port.SetLightingAsync(
                    LightingSettingsFactory.Static(new RgbColor(1, 2, 3), 50),
                    default));

        Assert.Contains("not admitted", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(session.Commands);
    }

    private static ProductionLightingBuildIdentity TestBuildIdentity() => new(
        "0.1.0+403aa01b11e969a6351595154569ef56f8878efa",
        "403aa01b11e969a6351595154569ef56f8878efa",
        "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF");

    private static ProductionLightingExactBuildValidationEvidence ValidEvidence()
    {
        LightingSettings baseline = LightingSettingsFactory.Static(
            new RgbColor(0, byte.MaxValue, 0),
            60) with
        {
            LogoMode = LogoLightingMode.Static,
        };
        (string Name, LightingSettings Settings)[] settings =
        [
            ("off", LightingSettingsFactory.Off()),
            ("static", LightingSettingsFactory.Static(new RgbColor(0, 128, 255), 60)),
            ("spectrum", LightingSettingsFactory.SpectrumCycling(60)),
            ("breathingSingle", LightingSettingsFactory.BreathingSingle(
                new RgbColor(0, 255, 0), 60)),
            ("breathingDual", LightingSettingsFactory.BreathingDual(
                new RgbColor(255, 0, 0), new RgbColor(0, 0, 255), 60)),
            ("breathingRandom", LightingSettingsFactory.BreathingRandom(60)),
            ("reactiveShort", LightingSettingsFactory.Reactive(
                new RgbColor(0, 255, 0), 60, ReactiveLightingDuration.Short)),
            ("reactiveMedium", LightingSettingsFactory.Reactive(
                new RgbColor(0, 255, 0), 60, ReactiveLightingDuration.Medium)),
            ("reactiveLong", LightingSettingsFactory.Reactive(
                new RgbColor(0, 255, 0), 60, ReactiveLightingDuration.Long)),
            ("ripple", LightingSettingsFactory.Ripple(
                new RgbColor(0, 255, 0), 60)),
            ("tidalRightFixed", LightingSettingsFactory.Tidal(
                new RgbColor(0, 255, 0),
                new RgbColor(0, 0, 255),
                randomColors: false,
                LightingDirection.Rightward,
                brightness: 60)),
            ("tidalLeftFixed", LightingSettingsFactory.Tidal(
                new RgbColor(0, 255, 0),
                new RgbColor(0, 0, 255),
                randomColors: false,
                LightingDirection.Leftward,
                brightness: 60)),
            ("tidalRandom", LightingSettingsFactory.Tidal(
                new RgbColor(0, 255, 0),
                new RgbColor(0, 0, 255),
                randomColors: true,
                LightingDirection.Rightward,
                brightness: 60)),
            ("starlightSingleShort", LightingSettingsFactory.StarlightSingle(
                new RgbColor(0, 255, 0),
                60,
                StarlightLightingDuration.Short)),
            ("starlightSingleMedium", LightingSettingsFactory.StarlightSingle(
                new RgbColor(0, 255, 0),
                60,
                StarlightLightingDuration.Medium)),
            ("starlightSingleLong", LightingSettingsFactory.StarlightSingle(
                new RgbColor(0, 255, 0),
                60,
                StarlightLightingDuration.Long)),
            ("starlightDual", LightingSettingsFactory.StarlightDual(
                new RgbColor(255, 0, 0),
                new RgbColor(0, 0, 255),
                60,
                StarlightLightingDuration.Medium)),
            ("starlightRandom", LightingSettingsFactory.StarlightRandom(
                60,
                StarlightLightingDuration.Medium)),
            ("waveRightward", LightingSettingsFactory.Wave(
                LightingDirection.Rightward,
                60)),
            ("waveLeftward", LightingSettingsFactory.Wave(
                LightingDirection.Leftward,
                60)),
            ("wheelClockwise", LightingSettingsFactory.Wheel(
                LightingRotationDirection.Clockwise,
                60)),
            ("wheelCounterclockwise", LightingSettingsFactory.Wheel(
                LightingRotationDirection.Counterclockwise,
                60)),
            ("logoBreathing", LightingSettingsFactory.Static(
                new RgbColor(0, 255, 0), 60) with
            {
                LogoMode = LogoLightingMode.Breathing,
            }),
            ("logoOff", LightingSettingsFactory.Static(
                new RgbColor(0, 255, 0), 60) with
            {
                LogoMode = LogoLightingMode.Off,
            }),
            ("logoStaticRestore", LightingSettingsFactory.Static(
                new RgbColor(0, 255, 0), 60) with
            {
                LogoMode = LogoLightingMode.Static,
            }),
        ];
        ProductionLightingValidationActionEvidence[] actions = settings
            .Select(entry =>
            {
                int raw = LightingBrightnessProtocol.PercentToRaw(entry.Settings.Brightness);
                bool keyTriggered = entry.Settings.Effect is
                    LightingEffect.Reactive or LightingEffect.Ripple;
                return new ProductionLightingValidationActionEvidence(
                    entry.Name,
                    entry.Settings,
                    StartedAtUtc: DateTimeOffset.Parse("2026-07-18T07:56:00Z"),
                    CompletedAtUtc: DateTimeOffset.Parse("2026-07-18T07:57:00Z"),
                    ApplySucceeded: true,
                    ExpectedBrightnessRaw: raw,
                    ObservedBrightnessRaw: raw,
                    BrightnessMatched: true,
                    ReactivePulseRequired: keyTriggered,
                    ReactivePulseApplied: keyTriggered,
                    VisuallyConfirmed: true,
                    Failure: null);
            })
            .ToArray();
        ProductionLightingBuildIdentity identity = TestBuildIdentity();
        int baselineRaw = LightingBrightnessProtocol.PercentToRaw(baseline.Brightness);
        return new(
            SchemaVersion: 2,
            EvidenceKind: "openBladeExactBuildHardwareValidation",
            Device: new(
                TargetBladeDevice.Model,
                TargetBladeDevice.VendorId,
                TargetBladeDevice.ProductId,
                TargetBladeDevice.BiosVersion),
            identity.InformationalVersion,
            identity.CommitHash,
            identity.ProtocolAssemblySha256,
            StartedAtUtc: DateTimeOffset.Parse("2026-07-18T07:55:00Z"),
            ValidatedAtUtc: DateTimeOffset.Parse("2026-07-18T08:00:00Z"),
            KnownBaseline: new(
                baseline,
                baselineRaw,
                baselineRaw,
                BrightnessMatched: true,
                VisuallyConfirmed: true),
            Actions: actions,
            AllSupportedEffectsApplied: true,
            BrightnessIndependentReadback: true,
            TransitionAcknowledged: true,
            MatrixWriteVisuallyConfirmed: true,
            MatrixReadbackAvailable: false,
            BaselineRestoreAttempted: true,
            BaselineRestoreObservedBrightnessRaw: baselineRaw,
            BaselineRestoreBrightnessMatched: true,
            BaselineRestoreVisuallyConfirmed: true,
            BaselineRestored: true,
            RestoredStateKind: "knownStaticGreenBaseline",
            UnknownPreTestStateRestored: false,
            Failure: null);
    }

    private static ProductionLightingPort CreatePort(
        LightingSession session,
        MatrixWriter? writer,
        bool admitted,
        TimeProvider? timeProvider = null,
        TimeSpan? cleanupDrainTimeout = null,
        PendingDisposalRegistry? pendingDisposals = null,
        Func<bool>? driverModeWriteAllowed = null) => new(
            new ValidationActor(session, new BatteryCommands(), new TemporaryCommands()),
            writer,
            admitted,
            timeProvider ?? TimeProvider.System,
            TimeSpan.FromSeconds(1),
            cleanupDrainTimeout ?? TimeSpan.FromSeconds(1),
            pendingDisposals ?? new PendingDisposalRegistry(),
            driverModeWriteAllowed);

    private static IProductionLightingPort CreateOwnedRendererReadPort(
        LightingSession session,
        TimeProvider timeProvider,
        bool rz090528,
        MatrixWriter? writer = null) => rz090528
        ? new Rz090528BrightnessLightingPort(
            new ValidationActor(
                session,
                new BatteryCommands(),
                new TemporaryCommands()),
            TimeSpan.FromSeconds(1),
            staticEffectAdmitted: true,
            spectrumEffectAdmitted: true,
            timeProvider: timeProvider)
        : CreatePort(
            session,
            writer ?? new MatrixWriter(),
            admitted: true,
            timeProvider: timeProvider);

    private static SemaphoreSlim OperationGate(ProductionLightingPort port) =>
        Assert.IsType<SemaphoreSlim>(typeof(ProductionLightingPort)
            .GetField("operationGate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(port));

    private static SemaphoreSlim Rz090528OperationGate(
        Rz090528BrightnessLightingPort port) =>
        Assert.IsType<SemaphoreSlim>(typeof(Rz090528BrightnessLightingPort)
            .GetField("operationGate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(port));

    private static (IProductionLightingPort Port, MatrixWriter? Writer)
        CreateDriverModeCleanupPort(LightingSession session, bool rz090528,
            Func<bool>? driverModeWriteAllowed = null)
    {
        if (rz090528)
        {
            return (
                new Rz090528BrightnessLightingPort(
                    new ValidationActor(
                        session,
                        new BatteryCommands(),
                        new TemporaryCommands()),
                    TimeSpan.FromSeconds(1), driverModeWriteAllowed: driverModeWriteAllowed),
                null);
        }

        var writer = new MatrixWriter();
        return (CreatePort(session, writer, admitted: true,
            driverModeWriteAllowed: driverModeWriteAllowed), writer);
    }

    private static bool OwnsDriverMode(IProductionLightingPort port) =>
        Assert.IsType<bool>(port.GetType()
            .GetField("ownsDriverMode", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(port));

    private sealed class NonAdvancingRz090528Effect : IRz090528LightingEffect
    {
        public bool RequiresContinuousRendering => true;

        public TimeSpan? GetNextFrameDelay(TimeSpan elapsed) => TimeSpan.Zero;

        public Rz090528MatrixFrame RenderFrame(TimeSpan elapsed) =>
            Rz090528MatrixFrame.Uniform(default);
    }

    private static (int ClassId, int CommandId, string Arguments) CommandTuple(
        RazerCommandSpec command) => (
            command.ClassId,
            command.CommandId,
            Convert.ToHexString(command.Arguments.Span));

    private sealed class LegacyLightingPort : IProductionLightingPort
    {
        public DeviceCapabilities Capabilities => DeviceCapabilities.Unavailable();

        public LightingRendererOwnershipState OwnershipState =>
            LightingRendererOwnershipState.Static;

        public Task<LightingSettings?> ReadStateAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No ordinary read is allowed during cleanup.");

        public Task SetLightingAsync(LightingSettings settings, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No ordinary setter is allowed during cleanup.");

        public Task<byte> StepBrightnessAsync(
            KeyboardBrightnessDirection direction,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task PulseReactiveKeyAsync(
            ReactiveKeyPulse pulse,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task PushAmbientLightingFrameAsync(
            AmbientLightingFrame frame,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SetPlaybackMuteIndicatorAsync(
            bool muted,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<LightingRendererOwnershipState> RelinquishRendererAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<LightingRendererOwnershipState> InvalidateAsync() =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class LightingSession(byte initialBrightness) :
        IRazerCommandSession,
        IRazerSetFeatureCommandSession
    {
        internal List<RazerCommandSpec> Commands { get; } = [];

        internal ConcurrentQueue<RazerCommandSpec> SubmittedCommands { get; } = [];

        internal int SubmittedBatchCount { get; private set; }

        internal Queue<byte> BrightnessQueryOverrides { get; } = [];

        internal int BrightnessQueryCalls { get; private set; }

        internal int? FailBrightnessQueryOnCall { get; set; }

        internal Action? BrightnessQueried { get; set; }

        internal byte Brightness { get; set; } = initialBrightness;

        internal bool LogoEnabled { get; set; } = true;

        internal byte LogoEffect { get; set; } = LogoLightingProtocol.StaticEffect;

        internal int BrightnessSetCalls { get; private set; }

        internal int? FailBrightnessSetOnCall { get; set; }

        internal HidSessionResult<ReadOnlyMemory<byte>>? TransitionFailure { get; set; }

        internal byte[]? Transition84Response { get; set; }

        internal Exception? DisposeFailure { get; init; }

        internal RazerDeviceMode DeviceMode { get; set; } = RazerDeviceMode.Driver;

        internal Queue<RazerDeviceMode> DeviceModeQueryOverrides { get; } = [];

        internal int DeviceModeQueryCalls { get; private set; }

        internal Action? DeviceModeQueried { get; set; }

        internal Func<Task>? DeviceModeQueryBoundary { get; set; }

        internal int? FailDeviceModeQueryOnCall { get; init; }

        internal int DeviceModeSetCalls { get; private set; }

        public RazerCommandSessionDisposition Disposition { get; private set; } =
            RazerCommandSessionDisposition.Healthy;

        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(
            RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId,
            TimeSpan exchangeTimeout,
            CancellationToken cancellationToken)
        {
            if ((command.ClassId, command.CommandId) == (0x00, 0x84)
                && DeviceModeQueryBoundary is { } boundary)
            {
                DeviceModeQueryBoundary = null;
                return ExecuteAfterModeQueryBoundaryAsync(boundary, command,
                    assignTransactionId, exchangeTimeout, cancellationToken);
            }
            _ = assignTransactionId();
            Commands.Add(command);
            if ((command.ClassId, command.CommandId) == (0x00, 0x86)
                && TransitionFailure is { } transitionFailure)
            {
                TransitionFailure = null;
                return Task.FromResult(transitionFailure);
            }

            if ((command.ClassId, command.CommandId) == (0x03, 0x03))
            {
                BrightnessSetCalls++;
                if (FailBrightnessSetOnCall == BrightnessSetCalls)
                {
                    return Task.FromResult(
                        new HidSessionResult<ReadOnlyMemory<byte>>(
                            default,
                            HidSessionFailureKind.ProtocolFailure));
                }
            }

            if ((command.ClassId, command.CommandId) == (0x03, 0x83))
            {
                BrightnessQueryCalls++;
                BrightnessQueried?.Invoke();
                if (FailBrightnessQueryOnCall == BrightnessQueryCalls)
                {
                    return Task.FromResult(
                        new HidSessionResult<ReadOnlyMemory<byte>>(
                            default,
                            HidSessionFailureKind.ProtocolFailure));
                }
            }

            if ((command.ClassId, command.CommandId) == (0x00, 0x84))
            {
                DeviceModeQueryCalls++;
                DeviceModeQueried?.Invoke();
                if (FailDeviceModeQueryOnCall == DeviceModeQueryCalls)
                {
                    return Task.FromResult(
                        new HidSessionResult<ReadOnlyMemory<byte>>(
                            default,
                            HidSessionFailureKind.ProtocolFailure));
                }
            }

            byte[] response = (command.ClassId, command.CommandId) switch
            {
                (0x03, 0x83) =>
                    [0x01, 0x05, BrightnessQueryOverrides.TryDequeue(out byte raw)
                        ? raw
                        : Brightness],
                (0x03, 0x03) => SetBrightness(command.Arguments.Span),
                (0x03, 0x80) =>
                    [command.Arguments.Span[0], 0x04,
                        LogoEnabled ? (byte)0x01 : (byte)0x00],
                (0x03, 0x82) =>
                    [command.Arguments.Span[0], 0x04, LogoEffect],
                (0x03, 0x00) => SetLogoState(command.Arguments.Span),
                (0x03, 0x02) => SetLogoEffect(command.Arguments.Span),
                (0x00, 0x86) => [0x01, 0x00, 0x00],
                (0x00, 0x04) => SetDeviceMode(command.Arguments.Span),
                (0x00, 0x84) => Transition84Response
                    ?? DeviceModeProtocol.CreateSetArguments(
                        DeviceModeQueryOverrides.TryDequeue(out RazerDeviceMode mode)
                            ? mode
                            : DeviceMode),
                (0x03, 0x0A) => [0x05, 0x00],
                (0x03, 0x0B) => command.Arguments.ToArray(),
                _ => throw new InvalidOperationException(),
            };
            if (command.PayloadValidator?.Invoke(response) == false)
            {
                return Task.FromResult(
                    new HidSessionResult<ReadOnlyMemory<byte>>(
                        default,
                        HidSessionFailureKind.ProtocolFailure)
                    {
                        Diagnostic = new(
                            HidSessionFailurePhase.ProtocolValidation,
                            HidSessionFailureKind.ProtocolFailure,
                            null,
                            0)
                        {
                            ProtocolValidationFailure =
                                HidProtocolValidationFailure.PayloadValidator,
                        },
                    });
            }
            return Task.FromResult(
                new HidSessionResult<ReadOnlyMemory<byte>>(response, null));
        }

        private async Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAfterModeQueryBoundaryAsync(
            Func<Task> boundary,
            RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId,
            TimeSpan exchangeTimeout,
            CancellationToken cancellationToken)
        {
            await boundary();
            return await ExecuteAsync(command, assignTransactionId, exchangeTimeout, cancellationToken);
        }

        public Task<HidSessionResult<ReadOnlyMemory<byte>>> SubmitAsync(
            RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId,
            TimeSpan exchangeTimeout,
            CancellationToken cancellationToken)
        {
            _ = assignTransactionId();
            SubmittedCommands.Enqueue(command);

            return Task.FromResult(
                new HidSessionResult<ReadOnlyMemory<byte>>(
                    ReadOnlyMemory<byte>.Empty,
                    null));
        }

        public Task<HidSessionResult<ReadOnlyMemory<byte>>> SubmitBatchAsync(
            IReadOnlyList<RazerCommandSpec> commands,
            Func<TransactionIdAssignment> assignTransactionId,
            TimeSpan exchangeTimeout,
            CancellationToken cancellationToken)
        {
            SubmittedBatchCount++;
            foreach (RazerCommandSpec command in commands)
            {
                _ = assignTransactionId();
                SubmittedCommands.Enqueue(command);
            }

            return Task.FromResult(
                new HidSessionResult<ReadOnlyMemory<byte>>(
                    ReadOnlyMemory<byte>.Empty,
                    null));
        }

        public ValueTask DisposeAsync()
        {
            Disposition = RazerCommandSessionDisposition.FullyDisposed;
            return DisposeFailure is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(DisposeFailure);
        }

        private byte[] SetBrightness(ReadOnlySpan<byte> arguments)
        {
            Brightness = arguments[2];
            return arguments.ToArray();
        }

        private byte[] SetLogoState(ReadOnlySpan<byte> arguments)
        {
            LogoEnabled = arguments[2] == 0x01;
            return arguments.ToArray();
        }

        private byte[] SetLogoEffect(ReadOnlySpan<byte> arguments)
        {
            LogoEffect = arguments[2];
            return arguments.ToArray();
        }

        private byte[] SetDeviceMode(ReadOnlySpan<byte> arguments)
        {
            if (!DeviceModeProtocol.TryDecodeQuery(arguments, out RazerDeviceMode mode))
            {
                throw new InvalidOperationException();
            }

            DeviceModeSetCalls++;
            DeviceMode = mode;
            return arguments.ToArray();
        }
    }

    private sealed class LightingActorFactory(LightingSession session)
        : IValidationActorFactory
    {
        public ValueTask<HidSessionResult<ValidationActor>> OpenAsync(
            ValidationRunContext context)
        {
            _ = context;
            return ValueTask.FromResult(
                new HidSessionResult<ValidationActor>(
                    new ValidationActor(
                        session,
                        new BatteryCommands(),
                        new TemporaryCommands()),
                    null));
        }
    }

    private sealed class FixedLightingActorFactory(
        HidSessionResult<ValidationActor> result) : IValidationActorFactory
    {
        public ValueTask<HidSessionResult<ValidationActor>> OpenAsync(
            ValidationRunContext context)
        {
            _ = context;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class MatrixWriter : IPid02E0MatrixFeatureWriter
    {
        private readonly object sync = new();
        private TaskCompletionSource writeObserved = NewWriteObserved();
        private TaskCompletionSource<HidSessionResult<HidUnit>>? blockedWrite;
        private bool blockNextWrite;
        private bool failNext;

        internal List<Pid02E0MatrixReport> Reports { get; } = [];

        internal List<CancellationToken> CancellationTokens { get; } = [];

        internal Queue<HidSessionFailureKind> Failures { get; } = [];

        internal HidSessionFailureDiagnostic? NextDiagnostic { get; set; }

        public RazerCommandSessionDisposition Disposition { get; private set; } =
            RazerCommandSessionDisposition.Healthy;

        public Task<HidSessionResult<HidUnit>> WriteAsync(
            Pid02E0MatrixReport report,
            CancellationToken cancellationToken)
        {
            bool fail;
            lock (sync)
            {
                Reports.Add(report);
                CancellationTokens.Add(cancellationToken);
                fail = failNext || Failures.Count > 0;
                failNext = false;
                TaskCompletionSource observed = writeObserved;
                writeObserved = NewWriteObserved();
                observed.TrySetResult();
                if (blockNextWrite)
                {
                    blockNextWrite = false;
                    blockedWrite = new TaskCompletionSource<HidSessionResult<HidUnit>>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    return blockedWrite.Task;
                }
            }

            HidSessionFailureKind failure = Failures.TryDequeue(out HidSessionFailureKind queued)
                ? queued
                : HidSessionFailureKind.ProtocolFailure;
            if (!fail)
            {
                return Task.FromResult(new HidSessionResult<HidUnit>(new HidUnit(), null));
            }

            HidSessionFailureDiagnostic? diagnostic = NextDiagnostic;
            NextDiagnostic = null;
            return Task.FromResult(new HidSessionResult<HidUnit>(default, failure)
            {
                Diagnostic = diagnostic,
            });
        }

        internal void BlockNextWrite()
        {
            lock (sync)
            {
                Assert.Null(blockedWrite);
                blockNextWrite = true;
            }
        }

        internal void FailNextWrite()
        {
            lock (sync)
            {
                failNext = true;
            }
        }

        internal void ReleaseBlockedWrite()
        {
            TaskCompletionSource<HidSessionResult<HidUnit>> release;
            lock (sync)
            {
                release = Assert.IsType<TaskCompletionSource<HidSessionResult<HidUnit>>>(
                    blockedWrite);
                blockedWrite = null;
            }

            release.TrySetResult(new HidSessionResult<HidUnit>(new HidUnit(), null));
        }

        internal async Task WaitForWriteCountAsync(int count, TimeSpan timeout)
        {
            using var cancellation = new CancellationTokenSource(timeout);
            while (true)
            {
                Task observed;
                lock (sync)
                {
                    if (Reports.Count >= count)
                    {
                        return;
                    }

                    observed = writeObserved.Task;
                }

                await observed.WaitAsync(cancellation.Token);
            }
        }

        public ValueTask DisposeAsync()
        {
            Disposition = RazerCommandSessionDisposition.FullyDisposed;
            return ValueTask.CompletedTask;
        }

        private static TaskCompletionSource NewWriteObserved() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UnixEpoch;
        private readonly List<Timer> timers = [];

        public override DateTimeOffset GetUtcNow() => now;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => now.Ticks;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new Timer(this, callback, state, now + dueTime);
            timers.Add(timer);
            return timer;
        }

        internal void Advance(TimeSpan amount)
        {
            now += amount;
            foreach (Timer timer in timers.ToArray())
            {
                timer.Fire(now);
            }
        }

        private sealed class Timer(
            ManualTimeProvider owner,
            TimerCallback callback,
            object? state,
            DateTimeOffset due) : ITimer
        {
            private bool disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                due = owner.now + dueTime;
                return true;
            }

            public void Dispose() => disposed = true;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            internal void Fire(DateTimeOffset observed)
            {
                if (!disposed && observed >= due)
                {
                    disposed = true;
                    callback(state);
                }
            }
        }
    }

    private sealed class BatteryCommands : IBatteryCommandClient
    {
        public Task<HidSessionResult<BatteryValidationSetting>> QueryAsync(
            byte transactionId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<HidSessionResult<HidUnit>> SetAsync(
            byte transactionId,
            BatteryValidationSetting setting,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class NoopMatrixWriteTimingSink : IMatrixWriteTimingSink
    {
        public void Record(MatrixWritePhaseTiming timing)
        {
        }
    }

    private sealed class TemporaryCommands : ITemporaryFullChargeCommandClient
    {
        public Task<HidSessionResult<bool>> QueryAsync(
            byte transactionId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<HidSessionResult<HidUnit>> SetAsync(
            byte transactionId,
            bool enabled,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
