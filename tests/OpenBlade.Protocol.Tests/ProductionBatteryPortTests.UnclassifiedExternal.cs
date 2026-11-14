using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed partial class ProductionBatteryPortTests
{
    private sealed class OrdinaryAcSession : IRazerCommandSession
    {
        internal byte Mode;
        internal byte Manual;
        internal byte Cpu;
        internal byte Gpu;
        internal byte First = 40;
        internal byte Second = 40;
        internal HidSessionResult<ReadOnlyMemory<byte>> AdapterResult = new(default, HidSessionFailureKind.ProtocolFailure)
        {
            UnclassifiedAdapterResponseAvailable = true,
            Diagnostic = new(HidSessionFailurePhase.ProtocolValidation, HidSessionFailureKind.ProtocolFailure, 0, 90)
            {
                ProtocolValidationFailure = HidProtocolValidationFailure.Status,
            },
        };
        internal Action<RazerCommandSpec>? BeforeSubmission;
        internal List<RazerCommandSpec> Commands = [];
        internal IEnumerable<RazerCommandSpec> Writes => Commands.Where(x => x.CommandId < 0x80);
        public RazerCommandSessionDisposition Disposition => RazerCommandSessionDisposition.Healthy;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(RazerCommandSpec command,
            Func<TransactionIdAssignment> assign, TimeSpan timeout, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            BeforeSubmission?.Invoke(command);
            if (command.SubmissionAllowed?.Invoke() == false)
            {
                return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(default, HidSessionFailureKind.IdentityBlocked)
                {
                    Diagnostic = new(HidSessionFailurePhase.Guard, HidSessionFailureKind.IdentityBlocked, null, null),
                });
            }
            assign();
            Commands.Add(command);
            byte[] p = command.Arguments.ToArray();
            if (command.ClassId == 7 && command.CommandId == 0x8C) { return Task.FromResult(AdapterResult); }
            byte[] reply = (command.ClassId, command.CommandId) switch
            {
                (13, 0x82) => [p[0], p[1], Mode, Manual],
                (13, 0x87) => [p[0], p[1], p[1] == 1 ? Cpu : Gpu],
                (13, 0x81) => [p[0], p[1], p[1] == 1 ? First : Second],
                (13, 0x02) or (13, 0x07) or (13, 0x01) => p,
                _ => throw new InvalidOperationException($"{command.ClassId:X2}/{command.CommandId:X2}"),
            };
            if (command.CommandId == 0x02) { Mode = p[2]; Manual = p[3]; }
            if (command.CommandId == 0x07) { if (p[1] == 1) { Cpu = p[2]; } else { Gpu = p[2]; } }
            if (command.CommandId == 0x01) { if (p[1] == 1) { First = p[2]; } else { Second = p[2]; } }
            return Task.FromResult(Success(reply));
        }
    }

    private static ProductionBatteryPort OrdinaryAcPort(OrdinaryAcSession session, Func<PowerSource> source,
        int minimum = 3300, int maximum = 5400)
    {
        var catalog = Catalog([
            .. MaximumCapabilityFixtures(), .. CustomPerformanceFixtures(),
            Fixture(ProductionBatteryPort.PerformanceSilentFirstAction, 13, 2, [1, 1, 5, 0]),
            Fixture(ProductionBatteryPort.PerformanceSilentSecondAction, 13, 2, [1, 2, 5, 0]),
            Fixture(ProductionBatteryPort.PerformanceBoostFirstAction, 13, 2, [1, 1, 2, 0]),
            Fixture(ProductionBatteryPort.PerformanceBoostSecondAction, 13, 2, [1, 2, 2, 0]),
        ]);
        return CreatePort(session, catalog, AcceptedAdapterPowerReadEvidence(), new(minimum, maximum, 100),
            new HashSet<FanMode> { FanMode.Automatic, FanMode.Fixed, FanMode.ManualCurve, FanMode.Maximum },
            AdapterPowerProtocol.SynapseAdapterStates, supportsDirectManualFanTransitions: true,
            capturePowerSource: source, supportsOrdinaryThermalControlOnUnclassifiedExternalPower: true);
    }

    [Theory]
    [InlineData(2000, 5000)]
    [InlineData(3300, 5400)]
    public async Task PhysicalOrdinaryUnclassifiedAc_PerformanceFixedCurveAndCustomUseFreshIndependentAdmission(int minimum, int maximum)
    {
        var session = new OrdinaryAcSession();
        await using var port = OrdinaryAcPort(session, () => PowerSource.ExternalUnknown, minimum, maximum);
        Assert.True(port.Capabilities.SupportsOrdinaryThermalControlOnUnclassifiedExternalPower);
        Assert.Equal(AdapterPowerState.Unknown, await port.QueryAdapterPowerStateAsync(default));
        await port.SetPerformanceAsync(PerformancePreset.Silent, default);
        Assert.Equal((byte)5, session.Mode);
        await port.SetFanControlAsync(new(FanMode.Fixed, maximum - 100, ManualFanCurve.Default), default);
        Assert.Equal((byte)5, session.Mode);
        Assert.Equal(((maximum - 100) / 100, (maximum - 100) / 100), ((int)session.First, (int)session.Second));
        await port.SetFanModeAsync(FanMode.Automatic, default);
        await port.SetCustomPerformanceAsync(new(CustomPerformanceLevel.Medium, CustomPerformanceLevel.Medium), default);
        Assert.Equal((byte)4, session.Mode);
        Assert.Equal((byte)1, session.Cpu);
        Assert.Equal((byte)1, session.Gpu);
        await port.SetFanControlAsync(new(FanMode.ManualCurve, maximum, ManualFanCurve.Default), default);
        Assert.Equal((byte)4, session.Mode);
        Assert.Equal((maximum / 100, maximum / 100), ((int)session.First, (int)session.Second));
        Assert.All(session.Writes, command => Assert.Contains(session.Commands.Take(session.Commands.IndexOf(command)),
            earlier => earlier.CommandId == 0x8C));
        for (int i = 0; i < session.Commands.Count; i++)
        {
            if (session.Commands[i].CommandId < 0x80)
            {
                Assert.Equal(0x8C, session.Commands[i - 1].CommandId);
            }
        }
    }

    [Fact]
    public async Task PhysicalOrdinaryUnclassifiedAc_NativeQueueSourceChangeBlocksFirstSet()
    {
        var source = PowerSource.ExternalUnknown;
        var session = new OrdinaryAcSession();
        session.BeforeSubmission = command => { if (command.CommandId < 0x80) { source = PowerSource.Battery; } };
        await using var port = OrdinaryAcPort(session, () => source);
        await Assert.ThrowsAsync<BladeWriteException>(() => port.SetPerformanceAsync(PerformancePreset.Silent, default));
        Assert.Empty(session.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PhysicalOrdinaryAc_TransferFailureBetweenSelectorsStopsAfterFirstSet(bool classified)
    {
        var session = new OrdinaryAcSession();
        if (classified) { session.AdapterResult = Success([0x11, 0x11]); }
        session.BeforeSubmission = command =>
        {
            if (command.CommandId == 0x8C && session.Writes.Any())
            {
                session.AdapterResult = new(default, HidSessionFailureKind.NativeIoFailure)
                {
                    Diagnostic = new(HidSessionFailurePhase.GetCompletion, HidSessionFailureKind.NativeIoFailure, 3, 90),
                };
            }
        };
        await using var port = OrdinaryAcPort(session, () => classified ? PowerSource.PluggedIn : PowerSource.ExternalUnknown);
        var failed = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetPerformanceAsync(PerformancePreset.Silent, default));
        Assert.Equal(WriteSubmissionState.Indeterminate, failed.SubmissionState);
        Assert.Single(session.Writes);
        int count = session.Commands.Count;
        await Assert.ThrowsAsync<BladeWriteException>(() => port.SetPerformanceAsync(PerformancePreset.Balanced, default));
        Assert.Equal(count, session.Commands.Count);
    }

    [Fact]
    public async Task PhysicalOrdinaryUnclassifiedAc_IndirectHyperBoostExitRemainsBlocked()
    {
        var session = new OrdinaryAcSession { Mode = 7 };
        await using var port = OrdinaryAcPort(session, () => PowerSource.ExternalUnknown);
        await Assert.ThrowsAsync<BladeWriteException>(() => port.SetPerformanceAsync(PerformancePreset.Silent, default));
        Assert.Empty(session.Writes);
    }

    [Theory]
    [InlineData((PowerSource)255)]
    [InlineData(PowerSource.Battery)]
    [InlineData(PowerSource.UsbC)]
    public async Task PhysicalOrdinaryUnclassifiedAc_UnavailableClassificationRequiresPositiveRawAc(PowerSource source)
    {
        var session = new OrdinaryAcSession();
        await using var port = OrdinaryAcPort(session, () => source);
        await Assert.ThrowsAsync<BladeWriteException>(() => port.SetPerformanceAsync(PerformancePreset.Silent, default));
        Assert.Empty(session.Writes);
    }
    [Fact]
    public async Task PhysicalOrdinaryClassifiedAc_BatteryContradictionBetweenSelectorsStopsWithoutReplay()
    {
        var session = new OrdinaryAcSession { AdapterResult = Success([0x11, 0x11]) };
        session.BeforeSubmission = command =>
        {
            if (command.CommandId == 0x8C && session.Writes.Any())
            {
                session.AdapterResult = Success([0, 0x11]);
            }
        };
        await using var port = OrdinaryAcPort(session, () => PowerSource.PluggedIn);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetPerformanceAsync(PerformancePreset.Silent, default));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.Single(session.Writes);
        Assert.Equal(2, session.Commands.Count(command => command.CommandId == 0x8C));
    }

    [Fact]
    public async Task PhysicalOrdinaryPayloadScope_DoesNotExposeExperimentalGamingTurboOrMaximumFans()
    {
        var session = new OrdinaryAcSession();
        await using var port = OrdinaryAcPort(session, () => PowerSource.ExternalUnknown);
        Assert.DoesNotContain(PerformancePreset.Gaming, port.Capabilities.PerformancePresets);
        Assert.DoesNotContain(PerformancePreset.Turbo, port.Capabilities.PerformancePresets);
        Assert.False(port.Capabilities.CustomPerformance!.SupportsMaximumFans);
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() =>
            port.SetPerformanceAsync(PerformancePreset.Gaming, default));
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() =>
            port.SetPerformanceAsync(PerformancePreset.Turbo, default));
        await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetCustomPerformanceAsync(new(CustomPerformanceLevel.Medium, CustomPerformanceLevel.Medium)
            {
                MaximumFans = true,
            }, default));
        Assert.Empty(session.Commands);
    }

}
