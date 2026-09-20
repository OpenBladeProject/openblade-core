using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;
using static OpenBlade.Service.Tests.ProductionLightingSessionFixture;
using static OpenBlade.Service.Tests.VerifiedBladeProviderFixture;

namespace OpenBlade.Service.Tests;

public sealed class ProductionLightingSessionTests
{
    private static readonly TimeSpan TestDeadlockGuard = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ReadLightingState_DoesNotOpenBatteryPort()
    {
        var batteryFactory = new FakeQueryFactory();
        var lightingPort = new FakeLightingPort();
        var lightingFactory = new FakeLightingFactory(lightingPort);
        using var provider = new WindowsVerifiedBladeDeviceProvider(
            () => Probe(BatteryValidationProbeClassification.Ready),
            Adapt(batteryFactory),
            UnavailableFanRpmFactory(),
            lightingFactory);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));
        LightingSettings expected = LightingSettingsFactory.Static(
            new RgbColor(0x47, 0xE1, 0x0C),
            50);
        await device.SetLightingAsync(expected, default);

        ConfirmedDeviceState state = await device.ReadLightingStateAsync(default);

        Assert.Equal(expected, state.Lighting);
        Assert.Null(state.Battery);
        Assert.Null(state.Performance);
        Assert.Null(state.Fans);
        Assert.Equal(0, batteryFactory.OpenCount);
        Assert.Equal(1, lightingFactory.OpenCount);
    }

    [Fact]
    public async Task PowerTransitionStateRead_UsesOwnedRendererStateRead()
    {
        var readOrder = new List<string>();
        var batteryFactory = new FakeQueryFactory(
            FakeQueryPort.Success(BatteryValidationSetting.Limit80, readOrder));
        var lightingPort = new FakeLightingPort
        {
            StateReadResult = LightingSettingsFactory.Off(),
            OwnershipState = LightingRendererOwnershipState.Static,
            ReadOrder = readOrder,
        };
        var lightingFactory = new FakeLightingFactory(lightingPort);
        using var provider = new WindowsVerifiedBladeDeviceProvider(
            () => Probe(BatteryValidationProbeClassification.Ready),
            Adapt(batteryFactory),
            UnavailableFanRpmFactory(),
            lightingFactory);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));

        PowerTransitionDeviceStateReadResult result =
            await device.ReadStateForPowerTransitionAsync(default);

        Assert.Equal(LightingEffect.Off, result.State.Lighting?.Effect);
        Assert.False(result.LightingOffConfirmationUnavailable);
        Assert.Equal(1, lightingPort.ReadStateCalls);
        Assert.Equal(
            LightingRendererOwnershipState.Static,
            lightingPort.OwnershipState);
        Assert.Equal(1, batteryFactory.OpenCount);
        Assert.Equal(["Lighting", "Battery"], readOrder);
    }

    [Fact]
    public async Task PowerTransitionStateRead_LightingFailureIsScopedAndRetainsPort()
    {
        var batteryFactory = new FakeQueryFactory(
            FakeQueryPort.Success(BatteryValidationSetting.Limit80),
            FakeQueryPort.Success(BatteryValidationSetting.Limit80));
        var lightingPort = new FakeLightingPort
        {
            ReadFailure = new IOException("brightness unavailable"),
            OwnershipState = LightingRendererOwnershipState.Static,
            LogoState = LogoLightingMode.Static,
        };
        var lightingFactory = new FakeLightingFactory(lightingPort);
        using var provider = new WindowsVerifiedBladeDeviceProvider(
            () => Probe(BatteryValidationProbeClassification.Ready),
            Adapt(batteryFactory),
            UnavailableFanRpmFactory(),
            lightingFactory);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));

        PowerTransitionDeviceStateReadResult result =
            await device.ReadStateForPowerTransitionAsync(default);

        Assert.Null(result.State.Lighting);
        Assert.Equal(LogoLightingMode.Static, result.State.LogoLighting);
        Assert.True(result.LightingOffConfirmationUnavailable);
        Assert.NotNull(result.State.Battery);
        Assert.Equal(1, lightingPort.ReadStateCalls);
        Assert.Equal(1, lightingPort.ReadLogoStateCalls);
        Assert.Equal(1, batteryFactory.OpenCount);
        Assert.Equal(1, lightingFactory.OpenCount);

        lightingPort.ReadFailure = null;
        PowerTransitionDeviceStateReadResult recovered =
            await device.ReadStateForPowerTransitionAsync(default);

        Assert.False(recovered.LightingOffConfirmationUnavailable);
        Assert.Equal(1, lightingFactory.OpenCount);
        Assert.Equal(2, batteryFactory.OpenCount);
    }

    [Fact]
    public async Task PowerTransitionStateRead_FaultedPortIsRetiredAndReopened()
    {
        var batteryFactory = new FakeQueryFactory(
            FakeQueryPort.Success(BatteryValidationSetting.Limit80),
            FakeQueryPort.Success(BatteryValidationSetting.Limit80));
        var faultedPort = new FakeLightingPort
        {
            ReadFailure = new IOException("brightness unavailable"),
            OwnershipState = LightingRendererOwnershipState.Faulted,
        };
        var recoveredPort = new FakeLightingPort();
        var lightingFactory = new FakeLightingFactory(faultedPort, recoveredPort);
        using var provider = new WindowsVerifiedBladeDeviceProvider(
            () => Probe(BatteryValidationProbeClassification.Ready),
            Adapt(batteryFactory),
            UnavailableFanRpmFactory(),
            lightingFactory);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));

        PowerTransitionDeviceStateReadResult failed =
            await device.ReadStateForPowerTransitionAsync(default);
        PowerTransitionDeviceStateReadResult recovered =
            await device.ReadStateForPowerTransitionAsync(default);

        Assert.True(failed.LightingOffConfirmationUnavailable);
        Assert.False(recovered.LightingOffConfirmationUnavailable);
        Assert.Equal(1, faultedPort.DisposeCount);
        Assert.Equal(2, lightingFactory.OpenCount);
        Assert.Equal(1, recoveredPort.ReadStateCalls);
    }

    [Fact]
    public async Task KeyboardBrightnessStep_UsesRecoveredSupportedPort()
    {
        var lightingPort = new FakeLightingPort
        {
            BrightnessStepResult = 42,
        };
        var lightingFactory = FakeLightingFactory.FromResults(
            new HidSessionResult<IProductionLightingPort>(
                Value: null,
                HidSessionFailureKind.NativeIoFailure),
            new HidSessionResult<IProductionLightingPort>(
                lightingPort,
                Failure: null));
        using var provider = new WindowsVerifiedBladeDeviceProvider(
            () => Probe(BatteryValidationProbeClassification.Ready),
            Adapt(new FakeQueryFactory()),
            UnavailableFanRpmFactory(),
            lightingFactory);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));

        await Assert.ThrowsAsync<BladeWriteException>(
            () => device.StepKeyboardBrightnessAsync(
                KeyboardBrightnessDirection.Increase,
                CancellationToken.None));

        byte brightness = await device.StepKeyboardBrightnessAsync(
            KeyboardBrightnessDirection.Increase,
            CancellationToken.None);

        Assert.Equal((byte)42, brightness);
        Assert.Equal(
            [KeyboardBrightnessDirection.Increase],
            lightingPort.BrightnessSteps);
        Assert.Equal(2, lightingFactory.OpenCount);
    }

    [Fact]
    public async Task KeyboardBrightnessStep_RejectsOpenedPortWithoutCapability()
    {
        var lightingPort = new FakeLightingPort(supportsKeyboardBrightness: false);
        var lightingFactory = new FakeLightingFactory(lightingPort);
        using var provider = new WindowsVerifiedBladeDeviceProvider(
            () => Probe(BatteryValidationProbeClassification.Ready),
            Adapt(new FakeQueryFactory()),
            UnavailableFanRpmFactory(),
            lightingFactory);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));

        BladeWriteException exception = await Assert.ThrowsAsync<BladeWriteException>(
            () => device.StepKeyboardBrightnessAsync(
                KeyboardBrightnessDirection.Decrease,
                CancellationToken.None));

        Assert.Equal(WriteSubmissionState.KnownNotApplied, exception.SubmissionState);
        Assert.Equal(
            "The exact-device lighting session does not support keyboard brightness.",
            exception.Message);
        Assert.Empty(lightingPort.BrightnessSteps);
        Assert.Equal(1, lightingFactory.OpenCount);
    }

    [Fact]
    public async Task LightingWriteOpenFailuresKeepUserMessagesPlainAndLogEachOperationOnce()
    {
        var history = new DiagnosticHistory();
        var logger = new ForwardingLogger<WindowsVerifiedBladeDeviceProvider>(
            history.CreateLogger(typeof(WindowsVerifiedBladeDeviceProvider).FullName!));
        var diagnostic = new HidSessionFailureDiagnostic(
            HidSessionFailurePhase.SetSubmit,
            HidSessionFailureKind.NativeIoFailure,
            NativeWin32Error: 5,
            TransferredByteCount: 0);
        var failedOpen = new HidSessionResult<IProductionLightingPort>(
            null,
            HidSessionFailureKind.NativeIoFailure)
        {
            Diagnostic = diagnostic,
        };
        var lightingFactory = FakeLightingFactory.FromResults(failedOpen);
        using var provider = new WindowsVerifiedBladeDeviceProvider(
            () => Probe(BatteryValidationProbeClassification.Ready),
            Adapt(new FakeQueryFactory()),
            UnavailableFanRpmFactory(),
            lightingFactory,
            logger: logger);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));

        BladeWriteException lighting = await Assert.ThrowsAsync<BladeWriteException>(
            () => device.SetLightingAsync(
                LightingSettingsFactory.Static(new RgbColor(1, 2, 3), 50),
                CancellationToken.None));
        BladeWriteException keyboard = await Assert.ThrowsAsync<BladeWriteException>(
            () => device.StepKeyboardBrightnessAsync(
                KeyboardBrightnessDirection.Increase,
                CancellationToken.None));
        BladeWriteException keyboardRepeat = await Assert.ThrowsAsync<BladeWriteException>(
            () => device.StepKeyboardBrightnessAsync(
                KeyboardBrightnessDirection.Increase,
                CancellationToken.None));
        BladeWriteException ambient = await Assert.ThrowsAsync<BladeWriteException>(
            () => device.PushAmbientLightingFrameAsync(
                new AmbientLightingFrame(new RgbColor[AmbientLightingFrame.SlotCount]),
                CancellationToken.None));
        BladeWriteException playback = await Assert.ThrowsAsync<BladeWriteException>(
            () => device.SetPlaybackMuteIndicatorAsync(true, CancellationToken.None));

        Assert.All(
            [lighting, keyboard, keyboardRepeat, ambient, playback],
            error => Assert.Equal(
                WriteSubmissionState.KnownNotApplied,
                error.SubmissionState));
        Assert.Equal(
            "The lighting change could not open an exact-device lighting session.",
            lighting.Message);
        Assert.Equal(
            "The keyboard brightness change could not open an exact-device lighting session.",
            keyboard.Message);
        Assert.Equal(keyboard.Message, keyboardRepeat.Message);
        Assert.Equal(
            "The ambient lighting frame could not open an exact-device lighting session.",
            ambient.Message);
        Assert.Equal(
            "The playback mute indication could not open an exact-device lighting session.",
            playback.Message);

        string[] logLines = history.Snapshot().LogLines.ToArray();
        Assert.Equal(4, logLines.Length);
        Assert.Contains(logLines, line => line.Contains(
            "The lighting change could not open",
            StringComparison.Ordinal));
        Assert.Contains(logLines, line => line.Contains(
            "The ambient lighting frame could not open",
            StringComparison.Ordinal));
        Assert.Contains(logLines, line => line.Contains(
            "The playback mute indication could not open",
            StringComparison.Ordinal));
        string keyboardLog = Assert.Single(
            logLines,
            line => line.Contains(
                "The keyboard brightness change could not open",
                StringComparison.Ordinal));
        Assert.Contains("origin=returned", keyboardLog, StringComparison.Ordinal);
        Assert.Contains("failure=NativeIoFailure", keyboardLog, StringComparison.Ordinal);
        Assert.Contains("diagnosticFailure=NativeIoFailure", keyboardLog, StringComparison.Ordinal);
        Assert.Contains("phase=SetSubmit", keyboardLog, StringComparison.Ordinal);
        Assert.Contains("nativeError=5", keyboardLog, StringComparison.Ordinal);
        Assert.Contains("bytes=0", keyboardLog, StringComparison.Ordinal);
        Assert.DoesNotContain("validation=", keyboardLog, StringComparison.Ordinal);
        Assert.DoesNotContain("None", keyboardLog, StringComparison.Ordinal);
        Assert.Equal(5, lightingFactory.OpenCount);
    }

    [Fact]
    public async Task LightingWriteOpenExceptionLogsSafeOriginWithoutExceptionMessage()
    {
        const string exceptionSecret = "machine-specific-native-message";
        var history = new DiagnosticHistory();
        var logger = new ForwardingLogger<WindowsVerifiedBladeDeviceProvider>(
            history.CreateLogger(typeof(WindowsVerifiedBladeDeviceProvider).FullName!));
        var lightingFactory = new ThrowingLightingFactory(
            new InvalidOperationException(exceptionSecret));
        using var provider = new WindowsVerifiedBladeDeviceProvider(
            () => Probe(BatteryValidationProbeClassification.Ready),
            Adapt(new FakeQueryFactory()),
            UnavailableFanRpmFactory(),
            lightingFactory,
            logger: logger);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));

        BladeWriteException first = await Assert.ThrowsAsync<BladeWriteException>(
            () => device.StepKeyboardBrightnessAsync(
                KeyboardBrightnessDirection.Decrease,
                CancellationToken.None));
        BladeWriteException second = await Assert.ThrowsAsync<BladeWriteException>(
            () => device.StepKeyboardBrightnessAsync(
                KeyboardBrightnessDirection.Decrease,
                CancellationToken.None));

        Assert.Equal(
            "The keyboard brightness change could not open an exact-device lighting session.",
            first.Message);
        Assert.Equal(first.Message, second.Message);
        string logLine = Assert.Single(history.Snapshot().LogLines);
        Assert.Contains("origin=threw", logLine, StringComparison.Ordinal);
        Assert.Contains("failure=NativeIoFailure", logLine, StringComparison.Ordinal);
        Assert.Contains("exceptionType=InvalidOperationException", logLine, StringComparison.Ordinal);
        Assert.DoesNotContain("diagnosticFailure=", logLine, StringComparison.Ordinal);
        Assert.DoesNotContain("phase=", logLine, StringComparison.Ordinal);
        Assert.DoesNotContain(exceptionSecret, logLine, StringComparison.Ordinal);
        Assert.Equal(2, lightingFactory.OpenCount);
    }

    [Fact]
    public async Task DriverModeOpenFailuresLogReturnedDiagnosticsOncePerOperation()
    {
        var history = new DiagnosticHistory();
        var logger = new ForwardingLogger<WindowsVerifiedBladeDeviceProvider>(
            history.CreateLogger(typeof(WindowsVerifiedBladeDeviceProvider).FullName!));
        var diagnostic = new HidSessionFailureDiagnostic(
            HidSessionFailurePhase.ProtocolValidation,
            HidSessionFailureKind.ProtocolFailure,
            NativeWin32Error: null,
            TransferredByteCount: 90)
        {
            ProtocolValidationFailure = HidProtocolValidationFailure.Status,
        };
        var failedOpen = new HidSessionResult<IProductionLightingPort>(
            null,
            HidSessionFailureKind.ProtocolFailure)
        {
            Diagnostic = diagnostic,
        };
        var lightingFactory = FakeLightingFactory.FromResults(failedOpen);
        using var provider = new WindowsVerifiedBladeDeviceProvider(
            () => Probe(BatteryValidationProbeClassification.Ready),
            Adapt(new FakeQueryFactory()),
            UnavailableFanRpmFactory(),
            lightingFactory,
            logger: logger);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));

        await device.EnsureDriverModeAsync(CancellationToken.None);
        Assert.Null(await device.ReadDriverModeAsync(CancellationToken.None));
        Assert.Null(await device.MaintainDriverModeAsync(CancellationToken.None));
        Assert.Null(await device.MaintainDriverModeAsync(CancellationToken.None));
        Assert.Null(await device.ReleaseDriverModeAsync(CancellationToken.None));

        string[] logLines = history.Snapshot().LogLines.ToArray();
        Assert.Equal(4, logLines.Length);
        Assert.Contains(logLines, line => line.Contains(
            "keyboard Driver-mode acquisition",
            StringComparison.Ordinal));
        Assert.Contains(logLines, line => line.Contains(
            "keyboard Driver-mode read",
            StringComparison.Ordinal));
        Assert.Contains(logLines, line => line.Contains(
            "keyboard Driver-mode release",
            StringComparison.Ordinal));
        string maintenanceLog = Assert.Single(
            logLines,
            line => line.Contains(
                "keyboard Driver-mode maintenance",
                StringComparison.Ordinal));
        Assert.Contains("origin=returned", maintenanceLog, StringComparison.Ordinal);
        Assert.Contains("failure=ProtocolFailure", maintenanceLog, StringComparison.Ordinal);
        Assert.Contains(
            "diagnosticFailure=ProtocolFailure",
            maintenanceLog,
            StringComparison.Ordinal);
        Assert.Contains("phase=ProtocolValidation", maintenanceLog, StringComparison.Ordinal);
        Assert.Contains("bytes=90", maintenanceLog, StringComparison.Ordinal);
        Assert.Contains("validation=Status", maintenanceLog, StringComparison.Ordinal);
        Assert.DoesNotContain("nativeError=", maintenanceLog, StringComparison.Ordinal);
        Assert.Equal(5, lightingFactory.OpenCount);
    }

    [Fact]
    public async Task SuccessfulLightingOpenEndsFailureEpisodeForEveryOperation()
    {
        var history = new DiagnosticHistory();
        var logger = new ForwardingLogger<WindowsVerifiedBladeDeviceProvider>(
            history.CreateLogger(typeof(WindowsVerifiedBladeDeviceProvider).FullName!));
        var diagnostic = new HidSessionFailureDiagnostic(
            HidSessionFailurePhase.SetSubmit,
            HidSessionFailureKind.NativeIoFailure,
            NativeWin32Error: 5,
            TransferredByteCount: 0);
        var failedOpen = new HidSessionResult<IProductionLightingPort>(
            null,
            HidSessionFailureKind.NativeIoFailure)
        {
            Diagnostic = diagnostic,
        };
        var recoveredPort = new FakeLightingPort();
        var recoveredOpen = new HidSessionResult<IProductionLightingPort>(
            recoveredPort,
            Failure: null);
        var lightingFactory = FakeLightingFactory.FromResults(
            failedOpen,
            failedOpen,
            recoveredOpen,
            failedOpen);
        using var provider = new WindowsVerifiedBladeDeviceProvider(
            () => Probe(BatteryValidationProbeClassification.Ready),
            Adapt(new FakeQueryFactory()),
            UnavailableFanRpmFactory(),
            lightingFactory,
            logger: logger);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));

        await Assert.ThrowsAsync<BladeWriteException>(
            () => device.StepKeyboardBrightnessAsync(
                KeyboardBrightnessDirection.Increase,
                CancellationToken.None));
        await Assert.ThrowsAsync<BladeWriteException>(
            () => device.StepKeyboardBrightnessAsync(
                KeyboardBrightnessDirection.Increase,
                CancellationToken.None));
        await device.SetLightingAsync(
            LightingSettingsFactory.Static(new RgbColor(1, 2, 3), 50),
            CancellationToken.None);
        await provider.InvalidateLightingRendererAsync(CancellationToken.None);
        await Assert.ThrowsAsync<BladeWriteException>(
            () => device.StepKeyboardBrightnessAsync(
                KeyboardBrightnessDirection.Increase,
                CancellationToken.None));

        Assert.Equal(
            2,
            history.Snapshot().LogLines.Count(line => line.Contains(
                "keyboard brightness change",
                StringComparison.Ordinal)));
        Assert.Equal(4, lightingFactory.OpenCount);
        Assert.Equal(1, recoveredPort.DisposeCount);
    }

    [Fact]
    public async Task RelinquishLightingRenderer_RetainsPortAndDriverModeSession()
    {
        var port = new FakeLightingPort();
        var lightingFactory = new FakeLightingFactory(port);
        using var provider = new WindowsVerifiedBladeDeviceProvider(
            () => Probe(BatteryValidationProbeClassification.Ready),
            Adapt(new FakeQueryFactory()),
            UnavailableFanRpmFactory(),
            lightingFactory);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));
        LightingSettings first = LightingSettingsFactory.Static(
            new RgbColor(1, 2, 3),
            50);
        await device.SetLightingAsync(first, CancellationToken.None);

        LightingRendererOwnershipState relinquished =
            await provider.RelinquishLightingRendererAsync(CancellationToken.None);
        LightingSettings second = LightingSettingsFactory.Static(
            new RgbColor(4, 5, 6),
            60);
        await device.SetLightingAsync(second, CancellationToken.None);

        Assert.Equal(LightingRendererOwnershipState.Static, relinquished);
        Assert.Equal(1, port.RelinquishCount);
        Assert.Equal(0, port.DisposeCount);
        Assert.Equal([first, second], port.Writes);
        Assert.Equal(1, lightingFactory.OpenCount);
    }

    [Fact]
    public async Task LowPowerCleanup_WithoutRetainedPortDoesNotProbeOrAcquireHardware()
    {
        int probeCount = 0;
        var port = new FakeLightingPort();
        var queryFactory = new FakeQueryFactory();
        var lightingFactory = new FakeLightingFactory(port);
        using var provider = new WindowsVerifiedBladeDeviceProvider(
            () =>
            {
                probeCount++;
                return Probe(BatteryValidationProbeClassification.Ready);
            },
            Adapt(queryFactory),
            UnavailableFanRpmFactory(),
            lightingFactory);
        Assert.True(provider.TryGetVerifiedDevice(out _));

        Assert.Null(await provider.TurnOffRetainedLightingForLowPowerAsync(
            () => true, default));
        Assert.Null(await provider.ReleaseRetainedDriverModeForLowPowerAsync(
            () => true, default));

        Assert.Equal(1, probeCount);
        Assert.Equal(0, lightingFactory.OpenCount);
        Assert.Equal(0, queryFactory.OpenCount);
        Assert.Equal(0, port.DriverModeEnsureCount);
        Assert.Equal(0, port.DriverModeReleaseCount);
        Assert.Equal(0, port.LowPowerOffCount);
    }

    [Fact]
    public async Task LowPowerCleanup_FreshlyAdmitsRetainedPortWithoutReplayingOtherWrites()
    {
        int probeCount = 0;
        var port = new FakeLightingPort(
            supportedLightingRefreshRates: new HashSet<LightingRefreshRate>
            {
                LightingRefreshRate.ThirtyHz,
                LightingRefreshRate.SixtyHz,
            });
        var lightingFactory = new FakeLightingFactory(port);
        using var provider = new WindowsVerifiedBladeDeviceProvider(
            () =>
            {
                probeCount++;
                return Probe(BatteryValidationProbeClassification.Ready);
            },
            Adapt(new FakeQueryFactory()),
            UnavailableFanRpmFactory(),
            lightingFactory);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));
        LightingSettings intent = LightingSettingsFactory.Static(new RgbColor(1, 2, 3), 50);
        await device.SetLightingAsync(intent, default);
        int probesBeforeCleanup = probeCount;
        int readsBeforeCleanup = port.ReadStateCalls;
        int muteWritesBeforeCleanup = port.PlaybackMuteIndicatorStates.Count;
        LightingRefreshRate[] appliedRefreshRates = port.LightingRefreshRates.ToArray();
        provider.SetLightingRefreshRate(LightingRefreshRate.SixtyHz);

        LightingLowPowerOffResult result = Assert.IsType<LightingLowPowerOffResult>(
            await provider.TurnOffRetainedLightingForLowPowerAsync(() => true, default));
        Assert.False(await provider.ReleaseRetainedDriverModeForLowPowerAsync(
            () => true, default));

        Assert.Equal(LightingLowPowerOffOutcome.ConfirmedOff, result.Outcome);
        Assert.Equal(LightingRendererOwnershipState.Static, result.OwnershipBefore);
        Assert.Equal(probesBeforeCleanup + 2, probeCount);
        Assert.Equal(1, port.LowPowerOffCount);
        Assert.Equal(1, port.DriverModeReleaseCount);
        Assert.Equal(0, port.DriverModeEnsureCount);
        Assert.Equal(1, lightingFactory.OpenCount);
        Assert.Equal(0, port.DisposeCount);
        Assert.Equal([intent], port.Writes);
        Assert.Equal(readsBeforeCleanup, port.ReadStateCalls);
        Assert.Equal(muteWritesBeforeCleanup, port.PlaybackMuteIndicatorStates.Count);
        Assert.Equal(appliedRefreshRates, port.LightingRefreshRates);
    }

    [Theory]
    [InlineData(BatteryValidationProbeClassification.EnumerationFailed, false)]
    [InlineData(BatteryValidationProbeClassification.ConflictBlocked, false)]
    [InlineData(BatteryValidationProbeClassification.Ready, true)]
    public async Task LowPowerCleanup_FreshProbeFailureCannotUseRecentReadyFallback(
        BatteryValidationProbeClassification failure,
        bool probeThrows)
    {
        bool rejectProbe = false;
        int rejectedProbeCount = 0;
        var port = new FakeLightingPort();
        var lightingFactory = new FakeLightingFactory(port);
        using var provider = new WindowsVerifiedBladeDeviceProvider(
            () =>
            {
                if (!rejectProbe)
                {
                    return Probe(BatteryValidationProbeClassification.Ready);
                }

                rejectedProbeCount++;
                return probeThrows
                    ? throw new IOException("Synthetic probe failure.")
                    : Probe(failure);
            },
            Adapt(new FakeQueryFactory()),
            UnavailableFanRpmFactory(),
            lightingFactory);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));
        LightingSettings intent = LightingSettingsFactory.Static(new RgbColor(1, 2, 3), 50);
        await device.SetLightingAsync(intent, default);
        rejectProbe = true;

        LightingLowPowerOffResult result = Assert.IsType<LightingLowPowerOffResult>(
            await provider.TurnOffRetainedLightingForLowPowerAsync(() => true, default));
        Assert.Null(await provider.ReleaseRetainedDriverModeForLowPowerAsync(
            () => true, default));

        Assert.Equal(LightingLowPowerOffOutcome.KnownNotApplied, result.Outcome);
        Assert.Equal(LightingRendererOwnershipState.Static, result.OwnershipBefore);
        Assert.Equal(2, rejectedProbeCount);
        Assert.Equal(0, port.LowPowerOffCount);
        Assert.Equal(0, port.DriverModeReleaseCount);
        Assert.Equal(0, port.DriverModeEnsureCount);
        Assert.Equal(1, lightingFactory.OpenCount);
        Assert.Equal([intent], port.Writes);
    }

    [Fact]
    public async Task LowPowerCleanup_BoundaryClearingDuringFreshProbePreventsSubmission()
    {
        bool expireBoundary = false;
        bool stillSuspending = true;
        var port = new FakeLightingPort();
        var lightingFactory = new FakeLightingFactory(port);
        using var provider = new WindowsVerifiedBladeDeviceProvider(
            () =>
            {
                if (expireBoundary)
                {
                    stillSuspending = false;
                }

                return Probe(BatteryValidationProbeClassification.Ready);
            },
            Adapt(new FakeQueryFactory()),
            UnavailableFanRpmFactory(),
            lightingFactory);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));
        await device.SetLightingAsync(
            LightingSettingsFactory.Static(new RgbColor(1, 2, 3), 50), default);
        expireBoundary = true;

        LightingLowPowerOffResult result = Assert.IsType<LightingLowPowerOffResult>(
            await provider.TurnOffRetainedLightingForLowPowerAsync(
                () => stillSuspending, default));
        stillSuspending = true;
        Assert.Null(await provider.ReleaseRetainedDriverModeForLowPowerAsync(
            () => stillSuspending, default));

        Assert.Equal(LightingLowPowerOffOutcome.KnownNotApplied, result.Outcome);
        Assert.Equal(0, port.LowPowerOffCount);
        Assert.Equal(0, port.DriverModeReleaseCount);
        Assert.Equal(0, port.DriverModeEnsureCount);
        Assert.Equal(1, lightingFactory.OpenCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LightingWriteQueuedBeforeLowPower_DoesNotOpenOrWriteAfterBoundaryEnters(
        bool retainPort)
    {
        var lowPowerGate = new LowPowerGate();
        var port = new FakeLightingPort();
        var lightingFactory = new FakeLightingFactory(port);
        using var provider = new WindowsVerifiedBladeDeviceProvider(
            () => Probe(BatteryValidationProbeClassification.Ready),
            Adapt(new FakeQueryFactory()),
            UnavailableFanRpmFactory(),
            lightingFactory,
            lowPowerGate: lowPowerGate);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));
        LightingSettings intent = LightingSettingsFactory.Static(new RgbColor(1, 2, 3), 50);
        if (retainPort)
        {
            await device.SetLightingAsync(intent, default);
        }

        var hardwareGate = Assert.IsType<SemaphoreSlim>(device.GetType()
            .GetField(
                "hardwareOperationGate",
                System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(device));
        await hardwareGate.WaitAsync();
        Task queued;
        try
        {
            queued = device.SetLightingAsync(intent, default);
            Assert.False(queued.IsCompleted);
            lowPowerGate.Enter();
        }
        finally
        {
            hardwareGate.Release();
        }

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(
            () => queued.WaitAsync(TestDeadlockGuard));

        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.Equal(retainPort ? 1 : 0, lightingFactory.OpenCount);
        Assert.Equal(retainPort ? 1 : 0, port.Writes.Count);
        Assert.Equal(0, port.DriverModeEnsureCount);
    }

    [Fact]
    public async Task LightingFactory_ReceivesTheSharedPowerGateForRetainedModeWrites()
    {
        var gate = new LowPowerGate();
        var factory = new FakeLightingFactory(new FakeLightingPort());
        using var provider = new WindowsVerifiedBladeDeviceProvider(
            () => Probe(BatteryValidationProbeClassification.Ready), Adapt(new FakeQueryFactory()),
            UnavailableFanRpmFactory(), factory, lowPowerGate: gate);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));
        await device.ReadDriverModeAsync(default);

        Assert.NotNull(factory.DriverModeWriteAllowed);
        Assert.True(factory.DriverModeWriteAllowed());
        gate.Enter();
        Assert.False(factory.DriverModeWriteAllowed());
    }

    [Fact]
    public async Task CanceledLowPowerClose_BlocksReplacementUntilRetainedCloseCompletes()
    {
        var gate = new LowPowerGate();
        var first = new FakeLightingPort { BlockLowPowerClose = true };
        var second = new FakeLightingPort();
        var factory = new FakeLightingFactory(first, second);
        using var provider = new WindowsVerifiedBladeDeviceProvider(
            () => Probe(BatteryValidationProbeClassification.Ready), Adapt(new FakeQueryFactory()),
            UnavailableFanRpmFactory(), factory, lowPowerGate: gate);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));
        LightingSettings intent = LightingSettingsFactory.Static(new RgbColor(1, 2, 3), 50);
        await device.SetLightingAsync(intent, default);
        gate.Enter();
        using var cancellation = new CancellationTokenSource();
        Task<LightingRendererInvalidationResult> closing =
            provider.InvalidateLightingRendererForLowPowerAsync(cancellation.Token);
        await first.LowPowerCloseStarted.Task.WaitAsync(TestDeadlockGuard);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => closing);
        Assert.Equal(LightingRendererOwnershipState.Static, first.OwnershipState);
        gate.Exit();

        using var retryCancellation = new CancellationTokenSource();
        Task write = device.SetLightingAsync(intent, retryCancellation.Token);
        Assert.False(write.IsCompleted);
        Assert.Equal(1, factory.OpenCount);
        await retryCancellation.CancelAsync();
        BladeWriteException canceledWrite = await Assert.ThrowsAsync<BladeWriteException>(() => write);
        Assert.Equal(WriteSubmissionState.KnownNotApplied, canceledWrite.SubmissionState);
        Assert.Equal(1, factory.OpenCount);
        first.LowPowerCloseRelease.TrySetResult();
        await device.SetLightingAsync(intent, default).WaitAsync(TestDeadlockGuard);

        Assert.Equal(2, factory.OpenCount);
        Assert.Equal(1, first.DisposeCount);
        Assert.Single(second.Writes);
    }

    [Fact]
    public async Task RetainedLightingPort_ReconcilesChangedRefreshRateBeforeReuse()
    {
        var port = new FakeLightingPort(
            supportedLightingRefreshRates:
            new HashSet<LightingRefreshRate>
            {
                LightingRefreshRate.ThirtyHz,
                LightingRefreshRate.SixtyHz,
            });
        var lightingFactory = new FakeLightingFactory(port);
        using var provider = new WindowsVerifiedBladeDeviceProvider(
            () => Probe(BatteryValidationProbeClassification.Ready),
            Adapt(new FakeQueryFactory()),
            UnavailableFanRpmFactory(),
            lightingFactory);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));
        await device.ReadCapabilitiesAsync(CancellationToken.None);
        await device.SetLightingAsync(
            LightingSettingsFactory.Static(new RgbColor(1, 2, 3), 50),
            CancellationToken.None);

        provider.SetLightingRefreshRate(LightingRefreshRate.SixtyHz);
        await provider.RelinquishLightingRendererAsync(CancellationToken.None);
        await device.ReadCapabilitiesAsync(CancellationToken.None);
        await device.ReadCapabilitiesAsync(CancellationToken.None);

        Assert.Equal(
            [LightingRefreshRate.ThirtyHz, LightingRefreshRate.SixtyHz],
            port.LightingRefreshRates);
        Assert.Equal(1, port.RelinquishCount);
        Assert.Equal(0, port.DisposeCount);
        Assert.Equal(1, lightingFactory.OpenCount);
    }

    [Fact]
    public async Task RetainedLightingPort_CanceledReconcileRemainsRetryable()
    {
        var port = new FakeLightingPort(
            supportedLightingRefreshRates:
            new HashSet<LightingRefreshRate>
            {
                LightingRefreshRate.ThirtyHz,
                LightingRefreshRate.SixtyHz,
            });
        var lightingFactory = new FakeLightingFactory(port);
        using var provider = new WindowsVerifiedBladeDeviceProvider(
            () => Probe(BatteryValidationProbeClassification.Ready),
            Adapt(new FakeQueryFactory()),
            UnavailableFanRpmFactory(),
            lightingFactory);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));
        await device.ReadCapabilitiesAsync(CancellationToken.None);
        await device.SetLightingAsync(
            LightingSettingsFactory.Static(new RgbColor(1, 2, 3), 50),
            CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        port.BlockNextLightingRefreshRate();
        provider.SetLightingRefreshRate(LightingRefreshRate.SixtyHz);

        Task<DeviceCapabilities> reconcile = device.ReadCapabilitiesAsync(
            cancellation.Token);
        await port.LightingRefreshRateStarted.Task.WaitAsync(TestDeadlockGuard);
        await cancellation.CancelAsync();
        OperationCanceledException error = await Assert.ThrowsAnyAsync<
            OperationCanceledException>(() => reconcile);

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(0, port.DisposeCount);
        Assert.Equal(LightingRendererOwnershipState.Static, port.OwnershipState);
        Assert.Equal(1, lightingFactory.OpenCount);

        await device.ReadCapabilitiesAsync(CancellationToken.None);

        Assert.Equal(
            [LightingRefreshRate.ThirtyHz, LightingRefreshRate.SixtyHz],
            port.LightingRefreshRates);
        Assert.Equal(0, port.DisposeCount);
        Assert.Equal(1, lightingFactory.OpenCount);
    }

    [Fact]
    public async Task LightingWrite_RejectsSequenceForDifferentLayoutBeforeOpeningPort()
    {
        var lightingPort = new FakeLightingPort();
        var lightingFactory = new FakeLightingFactory(lightingPort);
        using var provider = new WindowsVerifiedBladeDeviceProvider(
            () => Probe(BatteryValidationProbeClassification.Ready),
            Adapt(new FakeQueryFactory()),
            UnavailableFanRpmFactory(),
            lightingFactory);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));
        string timeline = KeyboardLightingSequenceCodec.Encode(
        [
            new KeyboardLightingSequenceFrame(
                100,
                new RgbColor[AmbientLightingFrame.SlotCount]),
        ]);

        CapabilityUnavailableException exception =
            await Assert.ThrowsAsync<CapabilityUnavailableException>(() =>
                device.SetLightingAsync(
                    LightingSettingsFactory.Sequence(
                        KeyboardLightingLayoutIds.Rz09_0528,
                        timeline,
                        loop: true,
                        brightness: 75),
                    CancellationToken.None));

        Assert.Contains("not admitted", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, lightingFactory.OpenCount);
        Assert.Empty(lightingPort.Writes);
    }

    [Fact]
    public async Task LightingWrite_RejectsSequenceWhenExactSessionLayoutChanged()
    {
        var lightingPort = new FakeLightingPort(
            keyboardLightingLayoutId: KeyboardLightingLayoutIds.Rz09_0528);
        var lightingFactory = new FakeLightingFactory(lightingPort);
        using var provider = new WindowsVerifiedBladeDeviceProvider(
            () => Probe(BatteryValidationProbeClassification.Ready),
            Adapt(new FakeQueryFactory()),
            UnavailableFanRpmFactory(),
            lightingFactory);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));
        string timeline = KeyboardLightingSequenceCodec.Encode(
        [
            new KeyboardLightingSequenceFrame(
                100,
                new RgbColor[AmbientLightingFrame.SlotCount]),
        ]);

        BladeWriteException exception = await Assert.ThrowsAsync<BladeWriteException>(() =>
            device.SetLightingAsync(
                LightingSettingsFactory.Sequence(
                    KeyboardLightingLayoutIds.Rz09_0581,
                    timeline,
                    loop: true,
                    brightness: 75),
                CancellationToken.None));

        Assert.Equal(WriteSubmissionState.KnownNotApplied, exception.SubmissionState);
        Assert.Contains("keyboard layout", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, lightingFactory.OpenCount);
        Assert.Empty(lightingPort.Writes);
    }

    [Fact]
    public async Task LightingWriter_IsOpenedOnceKeptAliveAndDisposedWithProvider()
    {
        var lightingPort = new FakeLightingPort
        {
            DriverModeRestoreOutcome = DriverModeRestoreOutcome.Released,
        };
        var lightingFactory = new FakeLightingFactory(lightingPort);
        var provider = new WindowsVerifiedBladeDeviceProvider(
            () => Probe(BatteryValidationProbeClassification.Ready),
            Adapt(new FakeQueryFactory()),
            UnavailableFanRpmFactory(),
            lightingFactory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);
        LightingSettings first = LightingSettingsFactory.Static(new RgbColor(1, 2, 3), 50);
        LightingSettings second = LightingSettingsFactory.Reactive(
            new RgbColor(4, 5, 6),
            75,
            ReactiveLightingDuration.Medium);

        await device.SetLightingAsync(first, default);
        await device.SetLightingAsync(second, default);
        await device.PulseReactiveKeyAsync(new ReactiveKeyPulse(77), default);

        Assert.Equal(
            LightingRendererOwnershipState.Reactive,
            await device.ReadLightingRendererOwnershipAsync(default));

        Assert.Equal(1, lightingFactory.OpenCount);
        Assert.Equal([first, second], lightingPort.Writes);
        Assert.Equal([77], lightingPort.Pulses.Select(pulse => (int)pulse.MatrixSlot));
        Assert.Equal(0, lightingPort.DisposeCount);

        LightingRendererInvalidationResult invalidated =
            await provider.InvalidateLightingRendererAsync(default);
        Assert.Equal(LightingRendererOwnershipState.Reactive, invalidated.Ownership);
        Assert.Equal(DriverModeRestoreOutcome.Released, invalidated.DriverModeRestoreOutcome);
        Assert.Equal(1, lightingPort.DisposeCount);
        Assert.Equal(1, lightingFactory.OpenCount);
        Assert.Equal(
            LightingRendererOwnershipState.None,
            await device.ReadLightingRendererOwnershipAsync(default));

        provider.Dispose();
        Assert.Equal(1, lightingPort.DisposeCount);
    }

    [Fact]
    public async Task ReactivePulse_DoesNotWaitBehindUnrelatedHardwareObservationGate()
    {
        var lightingPort = new FakeLightingPort();
        var lightingFactory = new FakeLightingFactory(lightingPort);
        using var provider = new WindowsVerifiedBladeDeviceProvider(
            () => Probe(BatteryValidationProbeClassification.Ready),
            Adapt(new FakeQueryFactory()),
            UnavailableFanRpmFactory(),
            lightingFactory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);
        await device.SetLightingAsync(
            LightingSettingsFactory.Reactive(
                new RgbColor(0, 255, 0),
                77,
                ReactiveLightingDuration.Medium),
            default);
        var gate = Assert.IsType<SemaphoreSlim>(device.GetType()
            .GetField(
                "hardwareOperationGate",
                System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(device));
        await gate.WaitAsync();
        try
        {
            Task pulse = device.PulseReactiveKeyAsync(new ReactiveKeyPulse(77), default);
            Assert.True(pulse.IsCompleted);
            await pulse;
        }
        finally
        {
            gate.Release();
        }

        Assert.Equal([77], lightingPort.Pulses.Select(value => (int)value.MatrixSlot));
    }

    [Fact]
    public async Task OwnedReactiveRenderer_IsReturnedWithoutRepeatingDeviceProbePerKey()
    {
        int probeCount = 0;
        var lightingPort = new FakeLightingPort();
        using var provider = new WindowsVerifiedBladeDeviceProvider(
            () =>
            {
                probeCount++;
                return Probe(BatteryValidationProbeClassification.Ready);
            },
            Adapt(new FakeQueryFactory()),
            UnavailableFanRpmFactory(),
            new FakeLightingFactory(lightingPort));
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));
        await device.SetLightingAsync(
            LightingSettingsFactory.Reactive(
                new RgbColor(0, 255, 0),
                77,
                ReactiveLightingDuration.Medium),
            default);

        Assert.True(provider.TryGetOwnedKeyTriggeredLightingDevice(out IBladeDevice owned));
        Assert.Same(device, owned);
        Assert.Equal(1, probeCount);
    }

    [Fact]
    public async Task DisablingUntestedFirmware_BlocksOwnedRendererImmediately()
    {
        using var provider = new WindowsVerifiedBladeDeviceProvider(
            () => Probe(
                BatteryValidationProbeClassification.Ready,
                BatteryValidationFirmwareCompatibility.UntestedAllowed),
            Adapt(new FakeQueryFactory()),
            UnavailableFanRpmFactory(),
            new FakeLightingFactory(new FakeLightingPort()));
        provider.SetAllowUntestedFirmware(true);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));
        await device.SetLightingAsync(
            LightingSettingsFactory.Reactive(
                new RgbColor(0, 255, 0),
                77,
                ReactiveLightingDuration.Medium),
            default);
        Assert.True(provider.TryGetOwnedKeyTriggeredLightingDevice(out _));

        provider.SetAllowUntestedFirmware(false);

        Assert.Equal(
            FirmwareCompatibilityStatus.UntestedBlocked,
            provider.FirmwareCompatibility);
        Assert.False(provider.TryGetOwnedKeyTriggeredLightingDevice(out _));
    }

    [Fact]
    public async Task DisablingUnusedOverride_PreservesTestedFirmwareRenderer()
    {
        using var provider = new WindowsVerifiedBladeDeviceProvider(
            () => Probe(BatteryValidationProbeClassification.Ready),
            Adapt(new FakeQueryFactory()),
            UnavailableFanRpmFactory(),
            new FakeLightingFactory(new FakeLightingPort()));
        provider.SetAllowUntestedFirmware(true);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));
        await device.SetLightingAsync(
            LightingSettingsFactory.Reactive(
                new RgbColor(0, 255, 0),
                77,
                ReactiveLightingDuration.Medium),
            default);

        provider.SetAllowUntestedFirmware(false);

        Assert.Equal(
            FirmwareCompatibilityStatus.Tested,
            provider.FirmwareCompatibility);
        Assert.True(provider.TryGetOwnedKeyTriggeredLightingDevice(out _));
    }

    [Fact]
    public async Task UnknownProbeAfterUntestedAdmission_FailsClosedAndDisablesCleanly()
    {
        int probeCount = 0;
        using var provider = new WindowsVerifiedBladeDeviceProvider(
            () => ++probeCount == 1
                ? Probe(
                    BatteryValidationProbeClassification.Ready,
                    BatteryValidationFirmwareCompatibility.UntestedAllowed)
                : Probe(
                    BatteryValidationProbeClassification.EnumerationFailed,
                    BatteryValidationFirmwareCompatibility.Unknown),
            Adapt(new FakeQueryFactory()),
            UnavailableFanRpmFactory(),
            new FakeLightingFactory(new FakeLightingPort()));
        provider.SetAllowUntestedFirmware(true);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));
        await device.SetLightingAsync(
            LightingSettingsFactory.Reactive(
                new RgbColor(0, 255, 0),
                77,
                ReactiveLightingDuration.Medium),
            default);

        Assert.True(provider.TryGetVerifiedDevice(out _));
        Assert.Equal(
            FirmwareCompatibilityStatus.Unknown,
            provider.FirmwareCompatibility);
        Assert.False(provider.TryGetOwnedKeyTriggeredLightingDevice(out _));

        provider.SetAllowUntestedFirmware(false);

        Assert.Equal(
            FirmwareCompatibilityStatus.UntestedBlocked,
            provider.FirmwareCompatibility);
        Assert.False(provider.TryGetOwnedKeyTriggeredLightingDevice(out _));
    }

    [Fact]
    public async Task EnsureDriverMode_ReusesTheSerializedLightingPort()
    {
        var lightingPort = new FakeLightingPort();
        var lightingFactory = new FakeLightingFactory(lightingPort);
        using var provider = new WindowsVerifiedBladeDeviceProvider(
            () => Probe(BatteryValidationProbeClassification.Ready),
            Adapt(new FakeQueryFactory()),
            UnavailableFanRpmFactory(),
            lightingFactory);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));

        await device.EnsureDriverModeAsync(default);
        await device.EnsureDriverModeAsync(default);
        Assert.True(await device.ReadDriverModeAsync(default));

        Assert.Equal(1, lightingFactory.OpenCount);
        Assert.Equal(2, lightingPort.DriverModeEnsureCount);
        Assert.Equal(1, lightingPort.DriverModeReadCount);
    }

    [Fact]
    public async Task PlaybackMuteIndicator_IsReappliedWhenLightingPortReopens()
    {
        var first = new FakeLightingPort();
        var second = new FakeLightingPort();
        using var provider = new WindowsVerifiedBladeDeviceProvider(
            () => Probe(BatteryValidationProbeClassification.Ready),
            Adapt(new FakeQueryFactory()),
            UnavailableFanRpmFactory(),
            new FakeLightingFactory(first, second));
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));

        await device.SetPlaybackMuteIndicatorAsync(true, default);
        await device.SetLightingAsync(
            LightingSettingsFactory.Static(new RgbColor(0, 0xFF, 0), 100),
            default);
        await provider.InvalidateLightingRendererAsync(default);
        await device.SetLightingAsync(
            LightingSettingsFactory.Static(new RgbColor(0, 0, 0xFF), 100),
            default);

        Assert.Contains(true, first.PlaybackMuteIndicatorStates);
        Assert.Equal([true], second.PlaybackMuteIndicatorStates);
    }

    [Fact]
    public async Task PlaybackMuteIndicator_FaultedKnownNotAppliedPortIsReopened()
    {
        var first = new FakeLightingPort
        {
            PlaybackMuteFailure = new BladeWriteException(
                WriteSubmissionState.KnownNotApplied,
                "Blocked"),
        };
        var second = new FakeLightingPort();
        var lightingFactory = new FakeLightingFactory(first, second);
        using var provider = new WindowsVerifiedBladeDeviceProvider(
            () => Probe(BatteryValidationProbeClassification.Ready),
            Adapt(new FakeQueryFactory()),
            UnavailableFanRpmFactory(),
            lightingFactory);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(
            () => device.SetPlaybackMuteIndicatorAsync(true, default));
        await device.SetPlaybackMuteIndicatorAsync(true, default);

        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(2, lightingFactory.OpenCount);
        Assert.Contains(true, second.PlaybackMuteIndicatorStates);
    }

    [Fact]
    public async Task ReleaseDriverMode_ReusesTheSerializedLightingPort()
    {
        var lightingPort = new FakeLightingPort();
        var lightingFactory = new FakeLightingFactory(lightingPort);
        using var provider = new WindowsVerifiedBladeDeviceProvider(
            () => Probe(BatteryValidationProbeClassification.Ready),
            Adapt(new FakeQueryFactory()),
            UnavailableFanRpmFactory(),
            lightingFactory);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));

        await device.EnsureDriverModeAsync(default);
        Assert.False(await device.ReleaseDriverModeAsync(default));

        Assert.Equal(1, lightingFactory.OpenCount);
        Assert.Equal(1, lightingPort.DriverModeEnsureCount);
        Assert.Equal(1, lightingPort.DriverModeReleaseCount);
    }

    [Fact]
    public async Task OwnedAmbientRenderer_ReusesPortWithoutProbeOrHardwareObservationGate()
    {
        int probeCount = 0;
        var lightingPort = new FakeLightingPort();
        using var provider = new WindowsVerifiedBladeDeviceProvider(
            () =>
            {
                probeCount++;
                return Probe(BatteryValidationProbeClassification.Ready);
            },
            Adapt(new FakeQueryFactory()),
            UnavailableFanRpmFactory(),
            new FakeLightingFactory(lightingPort));
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));
        await device.SetLightingAsync(
            LightingSettingsFactory.AmbientAwareness(
                AmbientAwarenessRegion.Full,
                77),
            default);
        Assert.True(provider.TryGetOwnedAmbientLightingDevice(out IBladeDevice owned));
        Assert.Same(device, owned);

        var gate = Assert.IsType<SemaphoreSlim>(device.GetType()
            .GetField(
                "hardwareOperationGate",
                System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(device));
        await gate.WaitAsync();
        try
        {
            Task frame = owned.PushAmbientLightingFrameAsync(
                new AmbientLightingFrame(
                    new RgbColor[AmbientLightingFrame.SlotCount]),
                default);
            Assert.True(frame.IsCompleted);
            await frame;
        }
        finally
        {
            gate.Release();
        }

        Assert.Equal(1, probeCount);
        Assert.Single(lightingPort.AmbientFrames);
    }

    [Fact]
    public async Task LightingBrightnessReadFailure_PreservesHealthyRenderer()
    {
        var lightingPort = new FakeLightingPort
        {
            ReadFailure = new IOException("transient brightness read failure"),
        };
        var lightingFactory = new FakeLightingFactory(lightingPort);
        using var provider = new WindowsVerifiedBladeDeviceProvider(
            () => Probe(BatteryValidationProbeClassification.Ready),
            Adapt(new FakeQueryFactory(
                FakeQueryPort.Success(BatteryValidationSetting.Limit80))),
            UnavailableFanRpmFactory(),
            lightingFactory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);
        LightingSettings requested = LightingSettingsFactory.SpectrumCycling(75);
        await device.SetLightingAsync(requested, default);

        ConfirmedDeviceState observed = await device.ReadStateAsync(default);

        Assert.Null(observed.Lighting);
        Assert.Equal(0, lightingPort.DisposeCount);
        Assert.Equal(
            LightingRendererOwnershipState.Spectrum,
            await device.ReadLightingRendererOwnershipAsync(default));
    }

    [Fact]
    public async Task InvalidateLightingRenderer_ReturnsLiveFaultBeforeRetiringPort()
    {
        var lightingPort = new FakeLightingPort();
        var lightingFactory = new FakeLightingFactory(lightingPort);
        using var provider = new WindowsVerifiedBladeDeviceProvider(
            () => Probe(BatteryValidationProbeClassification.Ready),
            Adapt(new FakeQueryFactory()),
            UnavailableFanRpmFactory(),
            lightingFactory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);
        await device.SetLightingAsync(
            LightingSettingsFactory.Static(new RgbColor(1, 2, 3), 50),
            default);
        lightingPort.OwnershipState = LightingRendererOwnershipState.Faulted;

        LightingRendererInvalidationResult invalidated =
            await provider.InvalidateLightingRendererAsync(default);

        Assert.Equal(LightingRendererOwnershipState.Faulted, invalidated.Ownership);
        Assert.Equal(1, lightingPort.DisposeCount);
        Assert.Equal(1, lightingFactory.OpenCount);
        Assert.Equal(
            LightingRendererOwnershipState.None,
            await device.ReadLightingRendererOwnershipAsync(default));
    }

    [Fact]
    public async Task FaultedLightingObservation_RetiresCachedPortAndNextWriteReopens()
    {
        var faultedPort = new FakeLightingPort();
        var replacementPort = new FakeLightingPort();
        var lightingFactory = new FakeLightingFactory(faultedPort, replacementPort);
        using var provider = new WindowsVerifiedBladeDeviceProvider(
            () => Probe(BatteryValidationProbeClassification.Ready),
            Adapt(new FakeQueryFactory()),
            UnavailableFanRpmFactory(),
            lightingFactory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);
        LightingSettings requested = LightingSettingsFactory.Static(
            new RgbColor(4, 5, 6),
            75);

        await device.SetLightingAsync(
            LightingSettingsFactory.Static(new RgbColor(1, 2, 3), 50),
            default);
        faultedPort.OwnershipState = LightingRendererOwnershipState.Faulted;

        LightingRendererOwnershipState observed =
            await device.ReadLightingRendererOwnershipAsync(default);
        await device.SetLightingAsync(requested, default);

        Assert.Equal(LightingRendererOwnershipState.Faulted, observed);
        Assert.Equal(1, faultedPort.DisposeCount);
        Assert.Equal(2, lightingFactory.OpenCount);
        Assert.Equal([requested], replacementPort.Writes);
    }
}
