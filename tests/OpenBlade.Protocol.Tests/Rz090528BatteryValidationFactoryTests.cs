using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class Rz090528BatteryValidationFactoryTests
{
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

        Assert.Equal([50, 55, 60, 65, 70, 75, 80], factory.Capabilities.ChargeLimits);
        Assert.Equal(
            [
                PerformancePreset.Balanced,
                PerformancePreset.Silent,
                PerformancePreset.Performance,
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
                DeviceFeatureAccess.ObservedOnly)
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
        Assert.True(factory.Capabilities.OneShotLaptopControls.SetEquals(
            [
                OneShotLaptopControl.StartupAnimation,
                OneShotLaptopControl.FunctionKeyPrimary,
                OneShotLaptopControl.GamingMode,
            ]));
        Assert.True(productionFactory.AdmittedAdapterPowerStates!.SetEquals(
            [
                AdapterPowerState.Battery,
                AdapterPowerState.UsbCPower100W,
                AdapterPowerState.FullPower280W,
            ]));
    }

    [Fact]
    public void ProductionReadEvidence_AdmitsOnlyExactValidatedQueries()
    {
        ProductionBatteryReadEvidence evidence =
            BatteryValidationFactory.CreateRz090528ProductionBatteryReadEvidence();

        Assert.True(evidence.SupportsTemporaryFullChargeQuery);
        Assert.True(evidence.SupportsAdapterPowerQuery);
        Assert.True(evidence.SupportsMcuVersionQuery);
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

        public Task<HidSessionResult<HidUnit>> SetAsync(
            byte transactionId,
            bool enabled,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
