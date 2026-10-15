using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class ExperimentalThermalExpansionTests
{
    [Theory]
    [InlineData(0x029F, true, true, true)]
    [InlineData(0x029D, false, false, true)]
    [InlineData(0x02C5, false, false, false)]
    [InlineData(0x0255, false, false, false)]
    [InlineData(0x02A0, false, true, false)]
    public void Capabilities_AdmitOnlyExactModelCommands(int pid, bool boost, bool maximum, bool fixedFans)
    {
        DeviceCapabilities caps = ExperimentalBladePort.CreateCapabilities(Model(pid), true);
        Assert.True(caps.CustomPerformance!.IsWellFormed);
        Assert.Contains(PerformancePreset.Custom, caps.PerformancePresets);
        Assert.Equal(boost, caps.CustomPerformance!.Supports(new(CustomPerformanceLevel.Boost, CustomPerformanceLevel.High)));
        Assert.Equal(boost, caps.CustomPerformance.Supports(new(CustomPerformanceLevel.High, CustomPerformanceLevel.High)));
        Assert.Equal(maximum, caps.CustomPerformance.SupportsMaximumFans);
        Assert.Equal(fixedFans, caps.FanModes.Contains(FanMode.Fixed));
        Assert.Equal(fixedFans, caps.ReportsPerformanceWithManualFans);
        Assert.DoesNotContain(FanMode.ManualCurve, caps.FanModes);
        Assert.False(ExperimentalBladePort.CreateCapabilities(Model(pid), false).PerformancePresets.Any());
        Assert.False(ExperimentalBladePort.CreateCapabilities(Model(pid), false).ReportsPerformanceWithManualFans);
    }

    [Fact]
    public async Task Blade16_CustomBoostHighAndMaximumFans_RoundTripAndPreserveFlagOnLevelChange()
    {
        var session = new Session { Mode = 4, MaximumFans = true };
        await using var port = Create(0x029F, session);
        var target = new CustomPerformanceSettings(CustomPerformanceLevel.Boost, CustomPerformanceLevel.High)
        {
            MaximumFans = true,
        };
        await port.SetCustomPerformanceAsync(target, CancellationToken.None);
        Assert.Equal(target, (await port.QueryPerformanceFanStateAsync(CancellationToken.None)).CustomPerformance);
        Assert.DoesNotContain(session.Commands, item => item.ClassId == 7 && item.CommandId == 0x0F);
        await port.SetCustomPerformanceAsync(target with { MaximumFans = false }, CancellationToken.None);
        Assert.False(session.MaximumFans);
        Assert.Single(session.Commands, item => item.ClassId == 7 && item.CommandId == 0x0F);
    }

    [Theory]
    [InlineData(0x029D)]
    [InlineData(0x02C5)]
    [InlineData(0x0255)]
    [InlineData(0x02A0)]
    public async Task OtherModels_RejectBoostAndHighHighBeforeTraffic(int pid)
    {
        var session = new Session();
        await using var port = Create(pid, session);
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() => port.SetCustomPerformanceAsync(
            new(CustomPerformanceLevel.Boost, CustomPerformanceLevel.High), CancellationToken.None));
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() => port.SetCustomPerformanceAsync(
            new(CustomPerformanceLevel.High, CustomPerformanceLevel.High), CancellationToken.None));
        Assert.Empty(session.Commands);
    }

    [Theory]
    [InlineData(0x029D)]
    [InlineData(0x02C5)]
    [InlineData(0x0255)]
    public async Task NewCustomProfiles_ConfirmStoredLevelsEntryAndEachChange(int pid)
    {
        var session = new Session();
        await using var port = Create(pid, session);
        await port.SetCustomPerformanceAsync(CustomPerformanceSettings.Default, CancellationToken.None);
        Assert.Equal(4, session.Mode);
        Assert.Equal(1, session.Cpu);
        Assert.Equal(2, session.Gpu);
        Assert.DoesNotContain(session.Commands, item => item.ClassId == 7);
    }

    [Fact]
    public async Task Gaming_UsesCodeOneOnBlade15()
    {
        var session = new Session();
        await using var port = Create(0x0255, session);
        await port.SetPerformanceAsync(PerformancePreset.Performance, CancellationToken.None);
        Assert.Equal(1, session.Mode);
        Assert.All(session.Commands.Where(item => item.CommandId == 2), item => Assert.Equal(1, item.Arguments.Span[2]));
    }

    [Theory]
    [InlineData(PerformancePreset.BatterySaver, 3)]
    [InlineData(PerformancePreset.BatteryBalanced, 6)]
    public async Task BatteryModes_UseExactBlade14Codes(PerformancePreset preset, byte wire)
    {
        var session = new Session { Mode = 6 };
        await using var port = Create(0x02C5, session, power: () => PowerSource.Battery);
        await port.SetPerformanceAsync(preset, CancellationToken.None);
        Assert.Equal(wire, session.Mode);
    }

    [Fact]
    public async Task BatteryMode_PowerLossAtNativeSubmissionStopsWithoutWrite()
    {
        var session = new Session { Mode = 6 };
        PowerSource power = PowerSource.Battery;
        session.BeforeSubmission = command =>
        {
            if (command.CommandId == 2)
            {
                power = PowerSource.PluggedIn;
            }
        };
        var policy = new ExperimentalFeaturesPolicy(Model(0x02C5));
        await using var port = Create(0x02C5, session, policy, () => power);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetPerformanceAsync(PerformancePreset.BatterySaver, CancellationToken.None));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.Empty(session.Writes);
        Assert.True(policy.WritesAllowed);
    }

    [Theory]
    [InlineData(0x029F, 2000)]
    [InlineData(0x029D, 2200)]
    public async Task FixedFans_PreserveCustomAndReadTargetsSeparatelyFromActualRpm(int pid, int minimum)
    {
        var session = new Session { Mode = 4 };
        await using var port = Create(pid, session);
        await port.SetFanTargetsAsync(minimum, 5000, CancellationToken.None);
        Assert.Equal(4, session.Mode);
        Assert.Equal(1, session.Fan);
        Assert.Equal(minimum / 100, session.FirstTarget);
        Assert.Equal(50, session.SecondTarget);
        Assert.DoesNotContain(session.Commands, item => item.CommandId == 0x88);
        await port.SetFanModeAsync(FanMode.Automatic, CancellationToken.None);
        Assert.Equal(4, session.Mode);
        Assert.Equal(0, session.Fan);
    }

    [Fact]
    public async Task IgnoredTarget_StopsBeforeOtherZoneAndRequiresReadOnlyRecovery()
    {
        var session = new Session { Fan = 1, IgnoreFanTargets = true };
        var policy = new ExperimentalFeaturesPolicy(Model(0x029F));
        await using var port = Create(0x029F, session, policy);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetFanTargetsAsync(3000, 4000, CancellationToken.None));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.Single(session.Writes);
        Assert.False(policy.WritesAllowed);
        int writes = session.Writes.Count();
        await port.QueryPerformanceFanStateAsync(CancellationToken.None);
        Assert.Equal(writes, session.Writes.Count());
    }

    [Theory]
    [InlineData(0x029F, 4)]
    [InlineData(0x029D, 4)]
    [InlineData(0x029F, 5)]
    [InlineData(0x029D, 0)]
    public async Task CustomLevels_PreserveAdmittedFixedFlagAndTargets(int pid, byte initialMode)
    {
        var session = new Session { Mode = initialMode, Fan = 1, FirstTarget = 30, SecondTarget = 40 };
        await using var port = Create(pid, session);
        await port.SetCustomPerformanceAsync(CustomPerformanceSettings.Default, CancellationToken.None);

        ProductionPerformanceFanState confirmed = await port.QueryPerformanceFanStateAsync(CancellationToken.None);
        Assert.Equal(PerformancePreset.Custom, confirmed.Performance);
        Assert.Equal(FanMode.Fixed, confirmed.Fans);
        Assert.Equal(CustomPerformanceSettings.Default, confirmed.CustomPerformance);
        Assert.Equal(30, session.FirstTarget);
        Assert.Equal(40, session.SecondTarget);
        RazerCommandSpec[] modeWrites = session.Writes.Where(item => item.CommandId == 2).ToArray();
        Assert.Equal(initialMode == 4 ? 0 : 2, modeWrites.Length);
        Assert.All(modeWrites, item => Assert.Equal(1, item.Arguments.Span[3]));
        Assert.DoesNotContain(session.Writes, item => item.CommandId == 1);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public async Task MaximumFans_FixedBaselineRejectsBeforeAnyWrite(byte initialMode)
    {
        var session = new Session { Mode = initialMode, Fan = 1 };
        var policy = new ExperimentalFeaturesPolicy(Model(0x029F));
        await using var port = Create(0x029F, session, policy);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetCustomPerformanceAsync(CustomPerformanceSettings.Default with { MaximumFans = true }, CancellationToken.None));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.Empty(session.Writes);
        Assert.True(policy.WritesAllowed);
    }

    [Fact]
    public async Task CustomEntry_UnexpectedTargetChangePausesWithoutChangingLevels()
    {
        var session = new Session { Fan = 1, ResetTargetsOnModeWrite = true };
        var policy = new ExperimentalFeaturesPolicy(Model(0x029F));
        await using var port = Create(0x029F, session, policy);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetCustomPerformanceAsync(CustomPerformanceSettings.Default, CancellationToken.None));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.False(policy.WritesAllowed);
        Assert.DoesNotContain(session.Writes, item => item.CommandId == 7);
    }

    [Fact]
    public async Task MaximumFlagEchoWithoutApplication_PausesWrites()
    {
        var session = new Session { Mode = 4, IgnoreMaximum = true };
        var policy = new ExperimentalFeaturesPolicy(Model(0x02A0));
        await using var port = Create(0x02A0, session, policy);
        await Assert.ThrowsAsync<BladeWriteException>(() => port.SetCustomPerformanceAsync(
            new(CustomPerformanceLevel.Low, CustomPerformanceLevel.Low) { MaximumFans = true }, CancellationToken.None));
        Assert.False(policy.WritesAllowed);
        Assert.Single(session.Writes);
    }

    [Fact]
    public async Task MaximumFans_BlocksConflictingFixedTargetBeforeWrite()
    {
        var session = new Session { Mode = 4, MaximumFans = true };
        await using var port = Create(0x029F, session);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetFanTargetsAsync(3000, 3000, CancellationToken.None));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.Empty(session.Writes);
    }

    private static ExperimentalBladeModel Model(int pid) => ExperimentalBladeModels.All.Single(item => item.ProductId == pid);

    private static ExperimentalBladePort Create(int pid, Session session, ExperimentalFeaturesPolicy? policy = null,
        Func<PowerSource>? power = null) => new(session, new(), Model(pid), policy ?? new(Model(pid)),
            () => true, power ?? (() => PowerSource.PluggedIn));

    private sealed class Session : IRazerCommandSession
    {
        internal byte Mode { get; set; }
        internal byte Fan { get; set; }
        internal byte Cpu { get; set; }
        internal byte Gpu { get; set; }
        internal byte FirstTarget { get; set; } = 25;
        internal byte SecondTarget { get; set; } = 25;
        internal bool MaximumFans { get; set; }
        internal bool IgnoreFanTargets { get; init; }
        internal bool IgnoreMaximum { get; init; }
        internal bool ResetTargetsOnModeWrite { get; init; }
        internal Action<RazerCommandSpec>? BeforeSubmission { get; set; }
        internal List<RazerCommandSpec> Commands { get; } = [];
        internal IEnumerable<RazerCommandSpec> Writes => Commands.Where(item => item.CommandId < 0x80);
        public RazerCommandSessionDisposition Disposition => RazerCommandSessionDisposition.Healthy;

        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId, TimeSpan exchangeTimeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeforeSubmission?.Invoke(command);
            if (command.SubmissionAllowed?.Invoke() == false)
            {
                return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(default, HidSessionFailureKind.IdentityBlocked)
                {
                    Diagnostic = new(HidSessionFailurePhase.Guard, HidSessionFailureKind.IdentityBlocked, null, null),
                });
            }
            Commands.Add(command);
            _ = assignTransactionId();
            byte[] payload = command.Arguments.ToArray();
            switch ((command.ClassId, command.CommandId))
            {
                case (0x0D, 0x82): payload = [0, payload[1], Mode, Fan]; break;
                case (0x0D, 0x87): payload = [0, payload[1], payload[1] == 1 ? Cpu : Gpu]; break;
                case (0x0D, 0x81): payload = [0, payload[1], payload[1] == 1 ? FirstTarget : SecondTarget]; break;
                case (0x07, 0x8F): payload = [MaximumFans ? (byte)2 : (byte)0]; break;
                case (0x0D, 0x02):
                    Mode = payload[2]; Fan = payload[3];
                    if (ResetTargetsOnModeWrite) { FirstTarget = 30; }
                    break;
                case (0x0D, 0x07):
                    if (payload[1] == 1) { Cpu = payload[2]; } else { Gpu = payload[2]; }
                    break;
                case (0x07, 0x0F): if (!IgnoreMaximum) { MaximumFans = payload[0] == 2; } break;
                case (0x0D, 0x01):
                    if (!IgnoreFanTargets)
                    {
                        if (payload[1] == 1) { FirstTarget = payload[2]; } else { SecondTarget = payload[2]; }
                    }
                    break;
                default: throw new InvalidOperationException($"Unexpected command {command.ClassId:X2}/{command.CommandId:X2}.");
            }
            return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(payload, null));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
