using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class ProductionLightingPortTests
{
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

    [Fact]
    public void ProductionAdmissionUsesOracleAndReviewedSemanticEvidence()
    {
        ProductionLightingOracleEvidence evidence =
            ProductionLightingEvidenceSource.ReadValidatedOracle();
        IProductionLightingPortFactory factory = ProductionLightingFactory.CreateWindows();

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
            Enum.GetValues<LightingEffect>().Order(),
            factory.Capabilities.LightingEffects.Order());
        Assert.Equal(
            DeviceFeatureAccess.ReadWrite,
            factory.Capabilities.SpecialKeyBindingsAccess);
        Assert.Equal(
            SpecialKeySlotSet.All,
            factory.Capabilities.WritableSpecialKeySlots);
        Assert.Null(factory.Capabilities.LampArrayLightingAccess);
        Assert.Empty(factory.Capabilities.PerformancePresets);
        Assert.Empty(factory.Capabilities.FanModes);
        Assert.Empty(factory.Capabilities.ChargeLimits);
        Assert.False(ProductionLightingEvidenceSource.HasCurrentExactBuildValidationArtifact(
            Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.json"),
            TestBuildIdentity()));
    }

    [Fact]
    public void Rz090528Capabilities_AdmitKeyboardOffBrightnessAndLogoAndExposeObservedQuickEffects()
    {
        DeviceCapabilities capabilities =
            Rz090528BrightnessLightingPort.CreateCapabilities();

        Assert.True(capabilities.SupportsKeyboardBrightness);
        Assert.Equal(
            DeviceFeatureAccess.ReadWrite,
            capabilities.SpecialKeyBindingsAccess);
        Assert.Equal(
            SpecialKeySlotSet.M3
                | SpecialKeySlotSet.M4
                | SpecialKeySlotSet.M5,
            capabilities.WritableSpecialKeySlots);
        Assert.Equal(
            DeviceFeatureAccess.ObservedOnly,
            capabilities.LampArrayLightingAccess);
        Assert.Equal([LightingEffect.Off], capabilities.LightingEffects);
        Assert.True(capabilities.ObservedLightingEffects.SetEquals(
        [
            LightingEffect.Spectrum,
            LightingEffect.Static,
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

    [Fact]
    public async Task ReadState_ExternalOffStopsAndRelinquishesOwnedRenderer()
    {
        var session = new LightingSession(initialBrightness: 0xFF);
        var writer = new MatrixWriter();
        await using var port = CreatePort(session, writer, admitted: true);
        await port.SetLightingAsync(
            LightingSettingsFactory.Static(new RgbColor(1, 2, 3), 100),
            default);
        session.Brightness = 0;

        LightingSettings? observed = await port.ReadStateAsync(default);

        Assert.Equal(LightingEffect.Off, observed?.Effect);
        Assert.Equal(LightingRendererOwnershipState.None, port.OwnershipState);
        BladeWriteException exception = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.PulseReactiveKeyAsync(new ReactiveKeyPulse(19), default));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, exception.SubmissionState);
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
        writer.FailNext = true;

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
        await using var port = CreatePort(session, writer, admitted: true);
        await port.SetLightingAsync(
            LightingSettingsFactory.BreathingSingle(new RgbColor(0, 0xFF, 0), 100),
            default);
        Assert.Equal(LightingRendererOwnershipState.Breathing, port.OwnershipState);

        writer.FailNext = true;
        await writer.WaitForWriteCountAsync(2, TimeSpan.FromSeconds(2));
        await WaitUntilAsync(
            () => port.OwnershipState == LightingRendererOwnershipState.Faulted,
            TimeSpan.FromSeconds(2));

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
        PendingDisposalRegistry? pendingDisposals = null) => new(
            new ValidationActor(session, new BatteryCommands(), new TemporaryCommands()),
            writer,
            admitted,
            timeProvider ?? TimeProvider.System,
            TimeSpan.FromSeconds(1),
            cleanupDrainTimeout ?? TimeSpan.FromSeconds(1),
            pendingDisposals ?? new PendingDisposalRegistry());

    private static SemaphoreSlim OperationGate(ProductionLightingPort port) =>
        Assert.IsType<SemaphoreSlim>(typeof(ProductionLightingPort)
            .GetField("operationGate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(port));

    private static (int ClassId, int CommandId, string Arguments) CommandTuple(
        RazerCommandSpec command) => (
            command.ClassId,
            command.CommandId,
            Convert.ToHexString(command.Arguments.Span));

    private sealed class LightingSession(byte initialBrightness) : IRazerCommandSession
    {
        internal List<RazerCommandSpec> Commands { get; } = [];

        internal Queue<byte> BrightnessQueryOverrides { get; } = [];

        internal byte Brightness { get; set; } = initialBrightness;

        internal bool LogoEnabled { get; set; } = true;

        internal byte LogoEffect { get; set; } = LogoLightingProtocol.StaticEffect;

        internal int BrightnessSetCalls { get; private set; }

        internal int? FailBrightnessSetOnCall { get; set; }

        internal HidSessionResult<ReadOnlyMemory<byte>>? TransitionFailure { get; set; }

        internal byte[]? Transition84Response { get; set; }

        internal RazerDeviceMode DeviceMode { get; set; } = RazerDeviceMode.Driver;

        public RazerCommandSessionDisposition Disposition { get; private set; } =
            RazerCommandSessionDisposition.Healthy;

        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(
            RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId,
            TimeSpan exchangeTimeout,
            CancellationToken cancellationToken)
        {
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
                    ?? DeviceModeProtocol.CreateSetArguments(DeviceMode),
                (0x03, 0x0A) => [0x05, 0x00],
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

        public ValueTask DisposeAsync()
        {
            Disposition = RazerCommandSessionDisposition.FullyDisposed;
            return ValueTask.CompletedTask;
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

    private sealed class MatrixWriter : IPid02E0MatrixFeatureWriter
    {
        private readonly object sync = new();
        private TaskCompletionSource writeObserved = NewWriteObserved();
        private TaskCompletionSource<HidSessionResult<HidUnit>>? blockedWrite;
        private bool blockNextWrite;

        internal List<Pid02E0MatrixReport> Reports { get; } = [];

        internal List<CancellationToken> CancellationTokens { get; } = [];

        internal bool FailNext { get; set; }

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
                fail = FailNext || Failures.Count > 0;
                FailNext = false;
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
