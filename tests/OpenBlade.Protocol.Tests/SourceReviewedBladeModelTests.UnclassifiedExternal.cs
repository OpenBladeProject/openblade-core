using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed partial class SourceReviewedBladeModelTests
{
    private static HidSessionResult<ReadOnlyMemory<byte>> CompletedAdapterRejection() =>
        new(default, HidSessionFailureKind.ProtocolFailure)
        {
            UnclassifiedAdapterResponseAvailable = true,
            QueryResponseStatus = 5,
            Diagnostic = new(HidSessionFailurePhase.ProtocolValidation,
                HidSessionFailureKind.ProtocolFailure, 0, 90)
            {
                ProtocolValidationFailure = HidProtocolValidationFailure.Status,
            },
        };

    public static IEnumerable<object[]> UnclassifiedAcFanCases()
    {
        foreach (object[] row in DynamicFanPresetCases)
        {
            if ((PerformancePreset)row[1] == PerformancePreset.BatteryBalanced) { continue; }
            foreach (bool curve in new[] { false, true }) { yield return [.. row, curve]; }
        }
        foreach (int product in new[] { 0x029D, 0x029F })
        {
            foreach (PerformancePreset preset in new[] { PerformancePreset.Balanced, PerformancePreset.Silent,
                PerformancePreset.Performance, PerformancePreset.Custom })
            {
                if (product == 0x029D && preset == PerformancePreset.Performance) { continue; }
                foreach (bool curve in new[] { false, true })
                {
                    yield return [product, preset, product == 0x029D ? 2200 : 2000, 5000, curve];
                }
            }
        }
        foreach (bool curve in new[] { false, true })
        {
            yield return [0x02A0, PerformancePreset.Balanced, 2200, 3800, curve];
            yield return [0x02A0, PerformancePreset.Silent, 3700, 5300, curve];
        }
    }

    [Theory]
    [MemberData(nameof(UnclassifiedAcFanCases))]
    public async Task UnclassifiedAc_ExactFanPresetsUseFreshRangeAndCurrentMode(
        int product, PerformancePreset preset, int minimum, int maximum, bool curve)
    {
        byte mode = product == 0x029F && preset == PerformancePreset.Performance ? (byte)2 : FanPresetWireMode(preset);
        var session = new Session
        {
            Mode = mode,
            EchoThermalAck = product == 0x029D,
            Fan1 = (byte)(minimum / 100),
            Fan2 = (byte)(minimum / 100),
            DynamicFanRange = FanMetadata(minimum, maximum),
            AdapterResult = CompletedAdapterRejection(),
        };
        await using var port = Create(product, session, power: () => PowerSource.ExternalUnknown);
        Assert.True(port.Capabilities.SupportsOrdinaryThermalControlOnUnclassifiedExternalPower);
        Assert.Equal(AdapterPowerState.Unknown, await port.QueryAdapterPowerStateAsync(default));
        if (curve) { await port.SetFanModeAsync(FanMode.ManualCurve, default); }
        else { await port.SetFanTargetsAsync(maximum - 100, maximum - 100, default); }
        int target = curve ? maximum : maximum - 100;
        Assert.Equal((target / 100, target / 100), ((int)session.Fan1, (int)session.Fan2));
        Assert.Equal(mode, session.Mode);
        Assert.Equal((byte)1, session.Manual);
        Assert.All(session.Commands.Where(x => x.CommandId == 0x02),
            x => Assert.Equal(mode, x.Arguments.Span[2]));
        AssertFreshAdapterBeforeSetters(session);
        session.Commands.Clear();
        Assert.Equal((minimum, minimum), await port.SetCurveFanTargetsAsync(preset, minimum, minimum, default));
        Assert.Equal(mode, session.Mode);
        AssertFreshAdapterBeforeSetters(session);
        Assert.Equal(AdapterPowerState.Unknown, await port.QueryAdapterPowerStateAsync(default));
    }

    public static IEnumerable<object[]> UnclassifiedOrdinaryModels() =>
        ExperimentalBladeModels.All.Select(model => new object[] { (int)model.ProductId });

    [Theory]
    [MemberData(nameof(UnclassifiedOrdinaryModels))]
    public async Task UnclassifiedAc_EverySourceModelAdmitsOrdinaryPresetAndExistingCustomComponents(int product)
    {
        var capabilities = ExperimentalBladePort.CreateCapabilities(Model(product), true);
        var session = new Session { AdapterResult = CompletedAdapterRejection(), EchoThermalAck = product == 0x029D };
        await using var port = Create(product, session, power: () => PowerSource.ExternalUnknown);
        PerformancePreset alternate = capabilities.PerformancePresets.First(x =>
            x is not (PerformancePreset.Balanced or PerformancePreset.BatteryBalanced or PerformancePreset.BatterySaver));
        if (alternate == PerformancePreset.Custom)
        {
            await port.SetCustomPerformanceAsync(new(CustomPerformanceLevel.Medium, CustomPerformanceLevel.Medium), default);
        }
        else { await port.SetPerformanceAsync(alternate, default); }
        await port.SetPerformanceAsync(PerformancePreset.Balanced, default);
        Assert.Equal((byte)0, session.Mode);
        if (capabilities.CustomPerformance is { } custom)
        {
            var value = new CustomPerformanceSettings(CustomPerformanceLevel.Medium, CustomPerformanceLevel.Medium)
            {
                MaximumFans = custom.SupportsMaximumFans,
            };
            await port.SetCustomPerformanceAsync(value, default);
            Assert.Equal((byte)4, session.Mode);
            Assert.Equal((byte)1, session.Cpu);
            Assert.Equal((byte)1, session.Gpu);
            Assert.Equal(custom.SupportsMaximumFans ? (byte)2 : (byte)0, session.Flags);
            await port.SetPerformanceAsync(PerformancePreset.Balanced, default);
            Assert.Equal((byte)0, session.Flags);
        }
        AssertFreshAdapterBeforeSetters(session);
        var evidence = Assert.Single(ExperimentalBladeModels.FeatureEvidenceFor(Model(product)),
            item => item.Feature == "OrdinaryThermalControlsOnUnclassifiedExternalPower");
        Assert.Equal("SynapseSourceBackedWithoutPhysicalValidation", evidence.Basis);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnclassifiedAc_UnrecognizedSuccessfulLevelsRemainUnknown(bool curve)
    {
        var session = new Session
        {
            Adapter = [0xFF, 0xFF],
            AdapterResponseAvailable = true,
        };
        await using var port = Create(0x0245, session, power: () => PowerSource.ExternalUnknown);
        if (curve) { await port.SetFanModeAsync(FanMode.ManualCurve, default); }
        else { await port.SetFanTargetsAsync(4600, 4700, default); }
        Assert.Equal(AdapterPowerState.Unknown, await port.QueryAdapterPowerStateAsync(default));
        Assert.Equal((byte)1, session.Manual);
        AssertFreshAdapterBeforeSetters(session);
    }

    [Theory]
    [InlineData((PowerSource)255)]
    [InlineData(PowerSource.Battery)]
    [InlineData(PowerSource.UsbC)]
    public async Task UnclassifiedAc_RequiresPositiveUnclassifiedExternalPresence(PowerSource source)
    {
        var session = new Session { AdapterResult = CompletedAdapterRejection() };
        await using var port = Create(0x0245, session, power: () => source);
        await Assert.ThrowsAsync<BladeWriteException>(() => port.SetFanTargetsAsync(4600, 4700, default));
        Assert.DoesNotContain(session.Commands, x => x.CommandId < 0x80);
    }

    [Fact]
    public async Task UnclassifiedAc_NativeSourceChangeBeforeSubmissionBlocksTheWrite()
    {
        PowerSource source = PowerSource.ExternalUnknown;
        var session = new Session { Manual = 1, AdapterResult = CompletedAdapterRejection() };
        session.BeforeSubmission = command => { if (command.CommandId == 0x01) { source = PowerSource.Battery; } };
        await using var port = Create(0x0245, session, power: () => source);
        await Assert.ThrowsAsync<BladeWriteException>(() => port.SetFanTargetsAsync(4600, 4700, default));
        Assert.DoesNotContain(session.Commands, x => x.CommandId < 0x80);
    }

    [Fact]
    public async Task UnclassifiedAc_TransportFailureAfterFirstTargetStopsWithoutReplayAndPauses()
    {
        var session = new Session { Manual = 1, AdapterResult = CompletedAdapterRejection() };
        session.BeforeSubmission = command =>
        {
            if (command.CommandId == 0x8C && session.Commands.Any(x => x.CommandId == 0x01))
            {
                session.AdapterResult = new(default, HidSessionFailureKind.NativeIoFailure)
                {
                    Diagnostic = new(HidSessionFailurePhase.GetCompletion, HidSessionFailureKind.NativeIoFailure, 3, 90),
                };
            }
        };
        var policy = new ExperimentalFeaturesPolicy(Model(0x0245));
        await using var port = Create(0x0245, session, policy, power: () => PowerSource.ExternalUnknown);
        var failure = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetFanTargetsAsync(4600, 4700, default));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.Single(session.Commands, x => x.CommandId == 0x01);
        Assert.False(policy.WritesAllowed);
    }

    [Theory]
    [InlineData(0x029F)]
    [InlineData(0x029D)]
    public async Task UnclassifiedAc_OrdinaryPresetCannotUnlockIndirectHyperBoostExit(int product)
    {
        var session = new Session { Mode = 7, AdapterResult = CompletedAdapterRejection() };
        await using var port = Create(product, session, power: () => PowerSource.ExternalUnknown);
        await Assert.ThrowsAsync<BladeWriteException>(() => port.SetPerformanceAsync(PerformancePreset.Silent, default));
        Assert.DoesNotContain(session.Commands, x => x.CommandId < 0x80);
    }

    [Theory]
    [InlineData(0, 0x11)]
    [InlineData(2, 0x11)]
    public async Task UnclassifiedAc_KnownBatteryContradictionAndLimitedManualRulesRemainBlocking(int actual, int recommended)
    {
        var session = new Session { Adapter = [(byte)actual, (byte)recommended], AdapterResponseAvailable = true };
        await using var port = Create(0x0245, session, power: () => PowerSource.ExternalUnknown);
        await Assert.ThrowsAsync<BladeWriteException>(() => port.SetFanTargetsAsync(4600, 4700, default));
        Assert.DoesNotContain(session.Commands, x => x.CommandId < 0x80);
    }
}
