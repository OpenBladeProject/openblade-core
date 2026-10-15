using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class Rz090528BatteryValidationFactoryTests
{
    [Fact]
    public void HyperBoostFixture_RequiresReviewedHashAndExactTargetWithoutBalancedAliases()
    {
        byte[] bytes = HyperBoostFixtureSource.ReadRz090528()!.Value.ToArray();
        var target = new DeviceIdentity("RZ09-0528", 0x1532, 0x02C6, "2.02");
        DeviceCommandCatalog catalog = CapturedFixtureFile.LoadCatalog(
            bytes, TrustedFixtureHashRegistry.Rz090528HyperBoostProduction, target);
        Assert.False(ProductionBatteryPort.HasHyperBoostCommandSet(catalog));
        Assert.True(ProductionBatteryPort.HasHyperBoostCommandSet(catalog, catalog,
            new HyperBoostBaseModeCapabilities(DeviceFeatureAccess.ObservedOnly,
                DeviceFeatureAccess.ObservedOnly, DeviceFeatureAccess.ReadWrite)));
        Assert.Throws<UnvalidatedCommandException>(() => CapturedFixtureFile.LoadCatalog(
            bytes, TrustedFixtureHashRegistry.Rz090528HyperBoostProduction,
            target with { ProductId = 0x02E0 }));
        bytes[0] ^= 1;
        Assert.Throws<UnvalidatedCommandException>(() => CapturedFixtureFile.LoadCatalog(
            bytes, TrustedFixtureHashRegistry.Rz090528HyperBoostProduction, target));
    }

    [Fact]
    public void ProductionLightingFactory_AdmitsCapturedExactSynapseEffects()
    {
        IProductionLightingPortFactory factory =
            BatteryValidationFactory.CreateWindowsRz090528ProductionLighting();

        Assert.True(factory.Capabilities.LightingEffects.SetEquals(
        [
            LightingEffect.Off,
            LightingEffect.Static,
            LightingEffect.Spectrum,
            LightingEffect.Ripple,
            LightingEffect.Starlight,
            LightingEffect.Tidal,
            LightingEffect.Wave,
            LightingEffect.Wheel,
            LightingEffect.Breathing,
            LightingEffect.Reactive,
            LightingEffect.AmbientAwareness,
            LightingEffect.AudioMeter,
            LightingEffect.Fire,
            LightingEffect.Sequence,
        ]));
        Assert.Empty(factory.Capabilities.ObservedLightingEffects);
    }

    [Fact]
    public void IsolatedLightingValidator_RetainsSpectrumForEvidenceCollection()
    {
        IProductionLightingPortFactory factory =
            BatteryValidationFactory.CreateWindowsRz090528LightingValidation();

        Assert.True(factory.Capabilities.LightingEffects.SetEquals(
        [
            LightingEffect.Off,
            LightingEffect.Static,
            LightingEffect.Spectrum,
            LightingEffect.Ripple,
            LightingEffect.Starlight,
            LightingEffect.Tidal,
            LightingEffect.Wave,
            LightingEffect.Wheel,
            LightingEffect.Breathing,
            LightingEffect.Reactive,
            LightingEffect.AmbientAwareness,
            LightingEffect.AudioMeter,
            LightingEffect.Fire,
            LightingEffect.Sequence,
        ]));
        Assert.Empty(factory.Capabilities.ObservedLightingEffects);
    }

    [Fact]
    public void ProductionFactory_AdmitsCapturedPerformanceBatteryAndTemporaryFullCharge()
    {
        IProductionBatteryPortFactory factory =
            BatteryValidationFactory.CreateWindowsRz090528ProductionBattery();
        var productionFactory = Assert.IsType<ProductionBatteryFactory>(factory);

        Assert.Equal(DeviceFeatureAccess.ReadWrite, factory.Capabilities.HyperBoostAccess);
        Assert.Equal(
            DeviceFeatureAccess.ReadWrite,
            factory.Capabilities.HyperBoostWithoutCoolingPadAccess);
        Assert.Equal(new HyperBoostBaseModeCapabilities(
            DeviceFeatureAccess.ReadWrite, DeviceFeatureAccess.ReadWrite, DeviceFeatureAccess.ReadWrite)
        { Custom = DeviceFeatureAccess.ReadWrite },
            factory.Capabilities.HyperBoostBaseModes);
        Assert.Equal([50, 55, 60, 65, 70, 75, 80], factory.Capabilities.ChargeLimits);
        Assert.Equal(
            [
                PerformancePreset.Balanced,
                PerformancePreset.Silent,
                PerformancePreset.Performance,
                PerformancePreset.BatterySaver,
                PerformancePreset.BatteryBalanced,
                PerformancePreset.Custom,
            ],
            factory.Capabilities.PerformancePresets);
        Assert.True(factory.Capabilities.CustomPerformance?.IsWellFormed);
        Assert.True(factory.Capabilities.CustomPerformance!.Supports(
            new CustomPerformanceSettings(
                CustomPerformanceLevel.High,
                CustomPerformanceLevel.Low)));
        Assert.False(factory.Capabilities.CustomPerformance.Supports(
            new CustomPerformanceSettings(
                CustomPerformanceLevel.High,
                CustomPerformanceLevel.Medium)));
        Assert.Equal(
            new CpuPowerLimitCapabilities(
                new DeviceNumericRange(45, 95, 1),
                DeviceFeatureAccess.ObservedOnly),
            factory.Capabilities.CpuPowerLimit);
        Assert.Equal(
            new CpuVoltageOptimizerCapabilities(
                new DeviceNumericRange(-30, -1, 1),
                DeviceFeatureAccess.ObservedOnly)
            {
                SupportsEnableDisable = true,
            },
            factory.Capabilities.CpuVoltageOptimizer);
        Assert.Equal(
            new GpuOverclockCapabilities(
                new DeviceNumericRange(-1000, 400, null),
                new DeviceNumericRange(-1000, 3000, null),
                DeviceFeatureAccess.ReadWrite)
            {
                SupportsEnableDisable = true,
            },
            factory.Capabilities.GpuOverclock);
        Assert.Equal(
            [
                new PowerSourceBehaviorObservation(
                    PowerSource.Battery,
                    PerformancePreset.BatterySaver,
                    FanMode.Automatic,
                    FixedFanControlAvailable: false,
                    PerformanceModeAvailable: false,
                    CustomModeAvailable: false,
                    InadequatePowerWarningVisible: false),
                new PowerSourceBehaviorObservation(
                    PowerSource.UsbC,
                    PerformancePreset.Balanced,
                    FanMode.Automatic,
                    FixedFanControlAvailable: false,
                    PerformanceModeAvailable: false,
                    CustomModeAvailable: false,
                    InadequatePowerWarningVisible: true),
            ],
            factory.Capabilities.ObservedPowerSourceBehaviors);
        Assert.True(factory.Capabilities.FanModes.SetEquals(
            [
                FanMode.Automatic,
                FanMode.Fixed,
                FanMode.ManualCurve,
                FanMode.Maximum,
            ]));
        Assert.Equal(
            new FanSpeedRange(2000, 5000, 100),
            factory.Capabilities.FanSpeedRange);
        Assert.Empty(factory.Capabilities.LightingEffects);
        Assert.True(factory.Capabilities.SupportsBatteryProtectionOff);
        Assert.True(factory.Capabilities.SupportsTemporaryFullCharge);
        Assert.True(factory.Capabilities.SupportsDirectManualFanTransitions);
        Assert.False(factory.Capabilities.ReportsPerformanceWithManualFans);
        Assert.True(factory.Capabilities.OneShotLaptopControls.SetEquals(
            [
                OneShotLaptopControl.StartupAnimation,
                OneShotLaptopControl.FunctionKeyPrimary,
                OneShotLaptopControl.GamingMode,
            ]));
        Assert.True(productionFactory.AdmittedAdapterPowerStates!.SetEquals(
            AdapterPowerProtocol.SynapseAdapterStates));
    }

    [Fact]
    public void ProductionReadEvidence_AdmitsOnlyExactValidatedQueries()
    {
        ProductionBatteryReadEvidence evidence =
            BatteryValidationFactory.CreateRz090528ProductionBatteryReadEvidence();

        Assert.True(evidence.SupportsTemporaryFullChargeQuery);
        Assert.True(evidence.SupportsAdapterPowerQuery);
        Assert.True(evidence.SupportsMcuVersionQuery);
        Assert.False(evidence.SupportsRz090581CapabilityDiagnosticsQuery);
        Assert.True(evidence.SupportsStartupAnimationQuery);
        Assert.True(evidence.SupportsFunctionKeyPrimaryQuery);
        Assert.True(evidence.SupportsGamingModeQuery);
    }

    [Fact]
    public async Task Port_UsesTypedQueryAndSetterWithMonotonicTransactions()
    {
        var session = new ScriptedSession([0xD0]);
        var commands = new BatteryCommands();
        var actor = new ValidationActor(session, commands, new TemporaryCommands());
        var factory = new Rz090528BatteryValidationFactory(
            static () => new ValidationRunContext(),
            new ActorFactory(actor));

        HidSessionResult<IBatteryValidationPort> opened =
            await factory.OpenAsync(CancellationToken.None);
        await using IBatteryValidationPort port = Assert.IsAssignableFrom<IBatteryValidationPort>(
            opened.Value);

        HidSessionResult<BatteryValidationSetting> queried =
            await port.QueryAsync(CancellationToken.None);
        HidSessionResult<HidUnit> applied =
            await port.ApplyAsync(BatteryValidationSetting.Limit70, CancellationToken.None);

        Assert.Equal(BatteryValidationSetting.Limit80, queried.Value);
        Assert.Null(queried.Failure);
        Assert.Null(applied.Failure);
        Assert.Equal(
            [
                (TransactionId: (byte)1, Setting: (BatteryValidationSetting?)null),
                (TransactionId: (byte)2, Setting: (BatteryValidationSetting?)BatteryValidationSetting.Limit70),
            ],
            commands.Calls);
    }

    [Fact]
    public async Task RawQuery_UsesExactCommandAndReturnsUnmappedByte()
    {
        var session = new ScriptedSession([0xC6]);
        var actor = new ValidationActor(
            session,
            new BatteryCommands(),
            new TemporaryCommands());
        var factory = new Rz090528BatteryValidationFactory(
            static () => new ValidationRunContext(),
            new ActorFactory(actor));

        HidSessionResult<byte> result = await factory.RunRawAsync(CancellationToken.None);

        Assert.Equal(0xC6, result.Value);
        Assert.Null(result.Failure);
        RazerCommandSpec command = Assert.Single(session.Commands);
        Assert.Equal(0x07, command.ClassId);
        Assert.Equal(0x92, command.CommandId);
        Assert.Equal([0x00], command.Arguments.ToArray());
        Assert.Equal(1, command.ExpectedPayloadLength);
        Assert.Equal([0xC6], Assert.Single(session.Payloads));
    }

    [Theory]
    [InlineData(FanMode.Fixed, 4000, 0x28)]
    [InlineData(FanMode.Maximum, 5000, 0x32)]
    public async Task DirectFanEntry_UsesTheExactProductionCatalogAndModelRange(
        FanMode mode, int rpm, byte encodedRpm)
    {
        var session = new ScriptedSession(
            [1, 1, 2, 0], [1, 2, 2, 0],
            [1, 1, 0, 1], [1, 2, 0, 1],
            [1, 1, encodedRpm], [1, 2, encodedRpm]);
        await using IProductionBatteryPort port = await OpenProductionPortAsync(session);

        await port.SetFanControlAsync(new FanControlSettings(mode, rpm, ManualFanCurve.Default), default);

        Assert.True(port.Capabilities.SupportsDirectManualFanTransitions);
        Assert.False(port.Capabilities.ReportsPerformanceWithManualFans);
        Assert.Equal(new FanSpeedRange(2000, 5000, 100), port.Capabilities.FanSpeedRange);
        Assert.Equal(
            ["01010001", "01020001", $"0101{encodedRpm:X2}", $"0102{encodedRpm:X2}"],
            Writes(session));
    }

    [Fact]
    public async Task DirectManualTargets_UseOnlyTheRequestedTargetPair()
    {
        var session = new ScriptedSession(
            [1, 1, 0, 1], [1, 2, 0, 1], [1, 1, 31], [1, 2, 32]);
        await using IProductionBatteryPort port = await OpenProductionPortAsync(session);

        await port.SetFanTargetsAsync(3100, 3200, default);

        Assert.Equal(["01011F", "010220"], Writes(session));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectManualExit_UsesRequestedPerformanceOrCustomPair(bool custom)
    {
        byte mode = custom ? (byte)4 : (byte)2;
        var responses = new List<byte[]>
        {
            new byte[] { 1, 1, 0, 1 }, new byte[] { 1, 2, 0, 1 },
            new byte[] { 1, 1, mode, 0 }, new byte[] { 1, 2, mode, 0 },
        };
        if (custom)
        {
            responses.AddRange([
                new byte[] { 1, 1, 1 }, new byte[] { 1, 2, 0 },
                new byte[] { 1, 1, 4, 0 }, new byte[] { 1, 2, 4, 0 },
                new byte[] { 1, 1, 1 }, new byte[] { 1, 2, 0 },
            ]);
        }
        var session = new ScriptedSession(responses.ToArray());
        await using IProductionBatteryPort port = await OpenProductionPortAsync(session);

        if (custom)
        {
            await port.SetCustomPerformanceAsync(new CustomPerformanceSettings(
                CustomPerformanceLevel.Medium, CustomPerformanceLevel.Low), default);
        }
        else
        {
            await port.SetPerformanceAsync(PerformancePreset.Performance, default);
        }

        Assert.Equal(custom
            ? new[] { "01010400", "01020400", "010101", "010200" }
            : new[] { "01010200", "01020200" }, Writes(session));
    }

    [Theory]
    [InlineData(CoolingPadConnectionState.Absent, false)]
    [InlineData(CoolingPadConnectionState.PresentExact, true)]
    public async Task DirectManualHyperBoost_UsesExactMode7PairForTheSelectedPadPath(
        CoolingPadConnectionState padState, bool requiresCoolingPad)
    {
        var session = new ScriptedSession(
            [1, 1, 0, 1], [1, 2, 0, 1],
            [0x12, 0x11], [1, 1, 7, 0],
            [0x12, 0x11], [1, 2, 7, 0],
            [1, 1, 7, 0], [1, 2, 7, 0]);
        await using IProductionBatteryPort port = await OpenProductionPortAsync(session, padState);

        await port.SetHyperBoostAsync(true, PerformancePreset.Performance, requiresCoolingPad, default);

        Assert.Equal(["01010700", "01020700"], Writes(session));
        Assert.Equal([0x82, 0x82, 0x8C, 0x02, 0x8C, 0x02, 0x82, 0x82],
            session.Commands.Select(command => command.CommandId).ToArray());
    }

    private static string[] Writes(ScriptedSession session) => session.Commands
        .Where(command => command.ClassId == 0x0D && command.CommandId is 0x01 or 0x02 or 0x07)
        .Select(command => Convert.ToHexString(command.Arguments.Span)).ToArray();

    private static async Task<IProductionBatteryPort> OpenProductionPortAsync(
        ScriptedSession session, CoolingPadConnectionState padState = CoolingPadConnectionState.Absent)
    {
        var actor = new ValidationActor(session, new BatteryCommands(), new TemporaryCommands());
        IProductionBatteryPortFactory factory = BatteryValidationFactory.CreateWindowsRz090528ProductionBattery(
            compatibilityPolicy: null, createActorFactory: _ => new ActorFactory(actor),
            hyperBoostEligibility: new FixedPadSource(padState));
        HidSessionResult<IProductionBatteryPort> result = await factory.OpenAsync(default);
        Assert.Null(result.Failure);
        return Assert.IsAssignableFrom<IProductionBatteryPort>(result.Value);
    }

    private sealed class FixedPadSource(CoolingPadConnectionState state) : IHyperBoostEligibilitySource
    {
        public CoolingPadConnectionState ObserveCoolingPad() => state;
    }

    private sealed class ActorFactory(ValidationActor actor) : IValidationActorFactory
    {
        public ValueTask<HidSessionResult<ValidationActor>> OpenAsync(
            ValidationRunContext context) =>
            ValueTask.FromResult(new HidSessionResult<ValidationActor>(actor, null));
    }

    private sealed class ScriptedSession(params byte[][] payloads) : IRazerCommandSession
    {
        private readonly Queue<byte[]> remaining = new(payloads);

        internal List<RazerCommandSpec> Commands { get; } = [];
        internal List<byte[]> Payloads { get; } = [];
        public RazerCommandSessionDisposition Disposition { get; private set; } =
            RazerCommandSessionDisposition.Healthy;

        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(
            RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId,
            TimeSpan exchangeTimeout,
            CancellationToken cancellationToken)
        {
            _ = assignTransactionId();
            byte[] payload = remaining.Dequeue();
            Commands.Add(command);
            Payloads.Add(payload);
            return Task.FromResult(
                new HidSessionResult<ReadOnlyMemory<byte>>(payload, null));
        }

        public ValueTask DisposeAsync()
        {
            Disposition = RazerCommandSessionDisposition.FullyDisposed;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BatteryCommands : IBatteryCommandClient
    {
        internal List<(byte TransactionId, BatteryValidationSetting? Setting)> Calls { get; } = [];

        public Task<HidSessionResult<BatteryValidationSetting>> QueryAsync(
            byte transactionId,
            CancellationToken cancellationToken)
        {
            Calls.Add((transactionId, null));
            return Task.FromResult(
                new HidSessionResult<BatteryValidationSetting>(
                    BatteryValidationSetting.Limit80,
                    null));
        }

        public Task<HidSessionResult<HidUnit>> SetAsync(
            byte transactionId,
            BatteryValidationSetting setting,
            CancellationToken cancellationToken)
        {
            Calls.Add((transactionId, setting));
            return Task.FromResult(new HidSessionResult<HidUnit>(default, null));
        }
    }

    private sealed class TemporaryCommands : ITemporaryFullChargeCommandClient
    {
        public Task<HidSessionResult<bool>> QueryAsync(
            byte transactionId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<HidSessionResult<TemporaryFullChargeReading>> QueryReadingAsync(
            byte transactionId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<HidSessionResult<HidUnit>> SetAsync(
            byte transactionId,
            bool enabled,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
