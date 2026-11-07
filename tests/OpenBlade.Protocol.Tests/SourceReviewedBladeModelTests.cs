using OpenBlade.Contracts;

namespace OpenBlade.Protocol.Tests;

public sealed class SourceReviewedBladeModelTests
{
    public static TheoryData<int> Products => new() { 0x02B6, 0x02B7, 0x02B8, 0x02C7 };

    private static ExperimentalBladeModel Model(int product) =>
        ExperimentalBladeModels.All.Single(model => model.ProductId == product);

    [Theory]
    [MemberData(nameof(Products))]
    public void Capabilities_AreExactAndBounded(int product)
    {
        ExperimentalBladeModel model = Model(product);
        DeviceCapabilities capabilities = ExperimentalBladePort.CreateCapabilities(model, true);
        Assert.Contains(PerformancePreset.Turbo, capabilities.PerformancePresets);
        Assert.Contains(PerformancePreset.BatteryBalanced, capabilities.PerformancePresets);
        Assert.Equal(product == 0x02C7, capabilities.PerformancePresets.Contains(PerformancePreset.MaximumPower));
        Assert.Equal(product == 0x02C7, capabilities.PerformancePresets.Contains(PerformancePreset.BatterySaver));
        Assert.DoesNotContain(PerformancePreset.Performance, capabilities.PerformancePresets);
        Assert.Equal(new[] { FanMode.Automatic }, capabilities.FanModes);
        Assert.Null(capabilities.FanSpeedRange);
        Assert.Empty(capabilities.LightingEffects);
        Assert.Empty(capabilities.LogoLightingModes);
        Assert.False(capabilities.SupportsKeyboardBrightness);
        Assert.False(capabilities.SupportsUniformLightingFrames);
        Assert.Null(capabilities.HyperBoostAccess);
        Assert.Null(capabilities.ThxAudio);
        Assert.Empty(capabilities.OneShotLaptopControls);
        Assert.Equal(new[] { 50, 55, 60, 65, 70, 75, 80, 100 }, capabilities.ChargeLimits.Order());
        CustomPerformanceCapabilities custom = Assert.IsType<CustomPerformanceCapabilities>(capabilities.CustomPerformance);
        bool cpuBoost = product is 0x02B6 or 0x02B7;
        Assert.Equal(cpuBoost ? 12 : 9, custom.AllowedCombinations.Count);
        Assert.All(custom.AllowedCombinations, pair =>
        {
            Assert.InRange((int)pair.Cpu, 0, cpuBoost ? 3 : 2);
            Assert.InRange((int)pair.Gpu, 0, 2);
        });
        Assert.Equal(product != 0x02C7, custom.SupportsMaximumFans);
        Assert.Contains(ExperimentalBladeModels.FeatureEvidenceFor(model),
            item => item.Basis.Contains("DynamicFanRanges", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Products))]
    public async Task Turbo_UsesOwnProfileBothZonesFreshAdapterAndIndependentReadback(int product)
    {
        var session = new Session { Mode = 5 };
        var policy = new ExperimentalFeaturesPolicy(Model(product));
        await using var port = Create(product, session, policy);
        await port.SetPerformanceAsync(PerformancePreset.Turbo, default);
        Assert.Equal((byte)1, session.Mode);
        RazerCommandSpec[] setters = session.Commands.Where(command => command.CommandId == 0x02).ToArray();
        Assert.Equal(2, setters.Length);
        Assert.Equal(new byte[] { 1, 1, 1, 0 }, setters[0].Arguments.ToArray());
        Assert.Equal(new byte[] { 1, 2, 1, 0 }, setters[1].Arguments.ToArray());
        AssertFreshAdapterBeforeSetters(session);
        Assert.All(session.Commands.Where(command => command.CommandId == 0x82),
            command => Assert.Equal(1, command.Arguments.Span[0]));
        Assert.Equal(PerformancePreset.Turbo, (await port.QueryPerformanceFanStateAsync(default)).Performance);
        Assert.Equal(product != 0x02C7, session.Commands.Any(command => command.CommandId == 0x8F));
    }

    [Theory]
    [MemberData(nameof(Products))]
    public async Task Custom_HighHighEntersDirectlyAndChangesOnlyNeededLevels(int product)
    {
        var session = new Session { Mode = 1, Cpu = 0, Gpu = 1 };
        var policy = new ExperimentalFeaturesPolicy(Model(product));
        await using var port = Create(product, session, policy);
        await port.SetCustomPerformanceAsync(new(CustomPerformanceLevel.High, CustomPerformanceLevel.High), default);
        Assert.Equal((byte)4, session.Mode);
        Assert.Equal((byte)2, session.Cpu);
        Assert.Equal((byte)2, session.Gpu);
        Assert.All(session.Commands.Where(command => command.CommandId == 0x02),
            command => Assert.Equal(4, command.Arguments.Span[2]));
        Assert.Equal(2, session.Commands.Count(command => command.CommandId == 0x07));
        Assert.All(session.Commands.Where(command => command.CommandId is 0x87 or 0x07),
            command => Assert.Equal(1, command.Arguments.Span[0]));
        AssertFreshAdapterBeforeSetters(session);
        if (product == 0x02C7)
        {
            Assert.DoesNotContain(session.Commands, command => command.CommandId is 0x8F or 0x0F);
        }
    }

    [Fact]
    public async Task MaximumPowerAndTurbo_AreDistinctReadbackStates()
    {
        var session = new Session { Mode = 1 };
        await using var port = Create(0x02C7, session);
        await port.SetPerformanceAsync(PerformancePreset.MaximumPower, default);
        Assert.Equal((byte)2, session.Mode);
        Assert.Equal(PerformancePreset.MaximumPower, (await port.QueryPerformanceFanStateAsync(default)).Performance);
        await port.SetPerformanceAsync(PerformancePreset.Turbo, default);
        Assert.Equal((byte)1, session.Mode);
    }

    [Theory]
    [MemberData(nameof(Products))]
    public async Task CpuBoost_UsesExactModelAdmissionAndIndependentLevelReadback(int product)
    {
        var session = new Session { Mode = 4, Cpu = 2, Gpu = 2 };
        await using var port = Create(product, session);
        var requested = new CustomPerformanceSettings(CustomPerformanceLevel.Boost, CustomPerformanceLevel.High);
        if (product is 0x02B6 or 0x02B7)
        {
            await port.SetCustomPerformanceAsync(requested, default);
            RazerCommandSpec setter = Assert.Single(session.Commands, command => command.CommandId == 0x07);
            Assert.Equal(new byte[] { 1, 1, 3 }, setter.Arguments.ToArray());
            Assert.Equal(requested, (await port.QueryPerformanceFanStateAsync(default)).CustomPerformance);
        }
        else
        {
            await Assert.ThrowsAsync<CapabilityUnavailableException>(() => port.SetCustomPerformanceAsync(requested, default));
            Assert.Empty(session.Commands);
        }
    }

    [Theory]
    [MemberData(nameof(Products))]
    public async Task Battery_StatusOnlyAckStillRequiresIndependentStateChange(int product)
    {
        var session = new Session { EmptyBatteryAck = true };
        var policy = new ExperimentalFeaturesPolicy(Model(product));
        await using var port = Create(product, session, policy);
        await port.SetChargeLimitAsync(75, default);
        Assert.Equal((byte)0xCB, session.Battery);
        Assert.Equal(new byte[] { 0x92, 0x12, 0x92 }, session.Commands.Select(command => command.CommandId));
        session.IgnoreWrites = true;
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetChargeLimitAsync(80, default));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.False(policy.WritesAllowed);
        Assert.Equal(2, session.Commands.Count(command => command.CommandId == 0x12));
    }

    [Theory]
    [MemberData(nameof(Products))]
    public async Task BatteryBalanced_UsesRawSixWithFreshBatteryAdapter(int product)
    {
        var session = new Session { Mode = 0, Adapter = [0, 0x11] };
        await using var port = Create(product, session, power: () => PowerSource.Battery);
        await port.SetPerformanceAsync(PerformancePreset.BatteryBalanced, default);
        Assert.Equal((byte)6, session.Mode);
        Assert.Equal(PerformancePreset.BatteryBalanced, (await port.QueryPerformanceFanStateAsync(default)).Performance);
        AssertFreshAdapterBeforeSetters(session);
    }

    [Theory]
    [MemberData(nameof(Products))]
    public async Task AutomaticHandback_PreservesPresetAndNeverQueriesOrWritesTargets(int product)
    {
        var session = new Session { Mode = 1, Manual = 1 };
        await using var port = Create(product, session);
        await port.SetFanModeAsync(FanMode.Automatic, default);
        Assert.Equal((byte)1, session.Mode);
        Assert.Equal((byte)0, session.Manual);
        Assert.All(session.Commands.Where(command => command.CommandId == 0x02),
            command => Assert.Equal(new byte[] { 1, command.Arguments.Span[1], 1, 0 }, command.Arguments.ToArray()));
        Assert.DoesNotContain(session.Commands, command => command.CommandId is 0x81 or 0x01 or 0x83);
        AssertFreshAdapterBeforeSetters(session);
    }

    [Theory]
    [MemberData(nameof(Products))]
    public async Task BatteryAutomaticHandback_PreservesConfirmedRawAcBalanced(int product)
    {
        var session = new Session { Mode = 0, Manual = 1, Adapter = [0, 0x11] };
        var policy = new ExperimentalFeaturesPolicy(Model(product));
        await using var port = Create(product, session, policy, power: () => PowerSource.Battery);
        await port.SetFanModeAsync(FanMode.Automatic, default);
        Assert.True(policy.WritesAllowed);
        Assert.Equal((byte)0, session.Mode);
        Assert.Equal((byte)0, session.Manual);
        Assert.All(session.Commands.Where(command => command.CommandId == 0x02),
            command => Assert.Equal(0, command.Arguments.Span[2]));
        Assert.DoesNotContain(session.Commands, command => command.CommandId is 0x81 or 0x01 or 0x83);
    }

    [Theory]
    [MemberData(nameof(Products))]
    public async Task ManualBaseline_BlocksPresetAndCustomUntilExplicitHandback(int product)
    {
        var session = new Session { Mode = 0, Manual = 1 };
        await using var port = Create(product, session);
        BladeWriteException preset = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetPerformanceAsync(PerformancePreset.Turbo, default));
        Assert.Contains("dynamic fan ranges", preset.Message);
        Assert.Equal(WriteSubmissionState.KnownNotApplied, preset.SubmissionState);
        await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetCustomPerformanceAsync(new(CustomPerformanceLevel.High, CustomPerformanceLevel.High), default));
        Assert.DoesNotContain(session.Commands, command => command.CommandId < 0x80);
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() => port.SetFanTargetsAsync(4500, 4500, default));
    }

    [Theory]
    [MemberData(nameof(Products))]
    public async Task Battery_DisableRetainsThresholdAndRequiresExactReadback(int product)
    {
        var session = new Session { Battery = 0xB7 };
        await using var port = Create(product, session);
        await port.SetProtectionOffAsync(default);
        Assert.Equal((byte)55, session.Battery);
        Assert.Equal(new byte[] { 0x92, 0x12, 0x92 }, session.Commands.Select(command => command.CommandId));
        await port.SetChargeLimitAsync(75, default);
        Assert.Equal((byte)0xCB, session.Battery);
        int before = session.Commands.Count;
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() => port.SetChargeLimitAsync(77, default));
        Assert.Equal(before, session.Commands.Count);
    }

    [Theory]
    [MemberData(nameof(Products))]
    public async Task ReadbackMismatch_PausesAllFutureWritesWithoutReplay(int product)
    {
        var session = new Session { Mode = 0, IgnoreWrites = true };
        var policy = new ExperimentalFeaturesPolicy(Model(product));
        await using var port = Create(product, session, policy);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetPerformanceAsync(PerformancePreset.Turbo, default));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.False(policy.WritesAllowed);
        Assert.Equal(2, session.Commands.Count(command => command.CommandId == 0x02));
        int before = session.Commands.Count;
        await Assert.ThrowsAsync<BladeWriteException>(() => port.SetChargeLimitAsync(80, default));
        Assert.Equal(before, session.Commands.Count);
    }

    [Theory]
    [MemberData(nameof(Products))]
    public async Task Recovery_QueriesOnlyAdmittedFeaturesAndPreservesUnknownFullCharge(int product)
    {
        var session = new Session { Mode = 4 };
        await using var port = Create(product, session);
        ConfirmedDeviceState recovered = await port.ReadRecoveryStateAsync(default);
        Assert.Null(recovered.Lighting);
        Assert.Null(recovered.LogoLighting);
        Assert.NotNull(recovered.Battery);
        Assert.Null(recovered.Battery.TemporaryFullCharge);
        Assert.All(session.Commands, command =>
            Assert.True(command.ClassId == 0x0D && command.CommandId is 0x82 or 0x87
                || command.ClassId == 0x07 && command.CommandId is 0x92 or 0x8F));
        if (product == 0x02C7)
        {
            Assert.DoesNotContain(session.Commands, command => command.CommandId == 0x8F);
        }
    }

    [Theory]
    [MemberData(nameof(Products))]
    public async Task LostIdentityAndLimitedAdapter_BlockBeforeSubmission(int product)
    {
        var session = new Session { Mode = 0 };
        await using (var port = Create(product, session, identity: () => false))
        {
            await Assert.ThrowsAsync<BladeWriteException>(() => port.SetPerformanceAsync(PerformancePreset.Turbo, default));
            Assert.Empty(session.Commands);
        }
        session.Adapter = [0, 0x11];
        await using var second = Create(product, session);
        await Assert.ThrowsAsync<BladeWriteException>(() => second.SetPerformanceAsync(PerformancePreset.Turbo, default));
        Assert.DoesNotContain(session.Commands, command => command.CommandId < 0x80);
    }

    [Theory]
    [MemberData(nameof(Products))]
    public async Task BatteryPreset_RejectsContradictoryFullPowerAdapter(int product)
    {
        var session = new Session();
        var policy = new ExperimentalFeaturesPolicy(Model(product));
        await using var port = Create(product, session, policy, power: () => PowerSource.Battery);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetPerformanceAsync(PerformancePreset.BatteryBalanced, default));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.True(policy.WritesAllowed);
        Assert.DoesNotContain(session.Commands, command => command.CommandId < 0x80);
    }

    [Fact]
    public async Task PowerChangesAtNativeBoundary_BlockWithoutSubmissionOrPause()
    {
        var session = new Session();
        PowerSource power = PowerSource.PluggedIn;
        session.BeforeSubmission = command =>
        {
            if (command.CommandId == 0x02)
            {
                power = PowerSource.UsbC;
            }
        };
        var policy = new ExperimentalFeaturesPolicy(Model(0x02C7));
        await using var port = Create(0x02C7, session, policy, power: () => power);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(
            () => port.SetPerformanceAsync(PerformancePreset.Turbo, default));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.True(policy.WritesAllowed);
        Assert.DoesNotContain(session.Commands, command => command.CommandId < 0x80);
    }

    [Fact]
    public async Task SharedPowerControl_RefusesOtherBitsBeforeAnyCustomWrites()
    {
        var session = new Session { Flags = 0x04 };
        await using var port = Create(0x02B8, session);
        await Assert.ThrowsAsync<BladeWriteException>(() => port.SetCustomPerformanceAsync(
            new(CustomPerformanceLevel.High, CustomPerformanceLevel.High) { MaximumFans = true }, default));
        Assert.DoesNotContain(session.Commands, command => command.CommandId < 0x80);
    }

    [Theory]
    [MemberData(nameof(Products))]
    public async Task Transactions_StayWithinExactVendorRange(int product)
    {
        var session = new Session();
        await using var port = Create(product, session);
        for (int index = 0; index < 20; index++)
        {
            await port.QueryPerformanceFanStateAsync(default);
        }
        Assert.All(session.Transactions, transaction => Assert.InRange(transaction, 1, 31));
        Assert.Equal(1, session.Transactions[31]);
    }

    private static ExperimentalBladePort Create(int product, Session session,
        ExperimentalFeaturesPolicy? policy = null, Func<bool>? identity = null, Func<PowerSource>? power = null) =>
        new(session, new(), Model(product), policy ?? new(Model(product)), identity ?? (() => true),
            power ?? (() => PowerSource.PluggedIn));

    private static void AssertFreshAdapterBeforeSetters(Session session)
    {
        for (int index = 0; index < session.Commands.Count; index++)
        {
            if (session.Commands[index].ClassId == 0x0D && session.Commands[index].CommandId < 0x80)
            {
                Assert.Equal(0x8C, session.Commands[index - 1].CommandId);
            }
        }
    }

    private sealed class Session : IRazerCommandSession
    {
        internal byte Mode;
        internal byte Manual;
        internal byte Cpu;
        internal byte Gpu;
        internal byte Flags;
        internal byte Battery = 0xD0;
        internal byte[] Adapter = [0x11, 0x11];
        internal bool IgnoreWrites;
        internal bool EmptyBatteryAck;
        internal Action<RazerCommandSpec>? BeforeSubmission;
        internal List<RazerCommandSpec> Commands { get; } = [];
        internal List<int> Transactions { get; } = [];
        public RazerCommandSessionDisposition Disposition => RazerCommandSessionDisposition.Healthy;

        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId, TimeSpan exchangeTimeout, CancellationToken cancellationToken)
        {
            BeforeSubmission?.Invoke(command);
            if (command.SubmissionAllowed?.Invoke() == false)
            {
                return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(default, HidSessionFailureKind.IdentityBlocked)
                {
                    Diagnostic = new(HidSessionFailurePhase.Guard, HidSessionFailureKind.IdentityBlocked, null, null),
                });
            }
            Commands.Add(command);
            Transactions.Add(assignTransactionId().TransactionId);
            byte[] arguments = command.Arguments.ToArray();
            byte[] payload = (command.ClassId, command.CommandId) switch
            {
                (0x07, 0x8C) => Adapter,
                (0x07, 0x8F) => [Flags],
                (0x07, 0x92) => [Battery],
                (0x0D, 0x82) => [1, arguments[1], Mode, Manual],
                (0x0D, 0x87) => [1, arguments[1], arguments[1] == 1 ? Cpu : Gpu],
                (0x07, 0x12) or (0x07, 0x0F) or (0x0D, 0x02) or (0x0D, 0x07) => arguments,
                _ => throw new InvalidOperationException($"Unexpected command {command.ClassId:X2}/{command.CommandId:X2}"),
            };
            if (!IgnoreWrites)
            {
                switch (command.ClassId, command.CommandId)
                {
                    case (0x07, 0x12): Battery = arguments[0]; break;
                    case (0x07, 0x0F): Flags = arguments[0]; break;
                    case (0x0D, 0x02): Mode = arguments[2]; Manual = arguments[3]; break;
                    case (0x0D, 0x07):
                        if (arguments[1] == 1)
                        {
                            Cpu = arguments[2];
                        }
                        else
                        {
                            Gpu = arguments[2];
                        }
                        break;
                }
            }
            // Exact Synapse thermal helpers accept successful status-only ACKs.
            if (command.ClassId == 0x0D && command.CommandId < 0x80
                || EmptyBatteryAck && command.ClassId == 0x07 && command.CommandId == 0x12)
            {
                payload = [];
            }
            return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(payload, null));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
