using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class ProductionBatteryFactoryIntegrationTests
{
    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(1);

    [Fact]
    public void Rz090581CapabilityDiagnosticsAdmission_RequiresExactModelEvidence()
    {
        ProductionBatteryReadEvidence admitted =
            ProductionBatteryFactory.GetRz090581ProductionBatteryReadEvidence(
                ExactRz090581Identity("3.01"));
        Assert.True(admitted.SupportsRz090581CapabilityDiagnosticsQuery);
        Assert.True(ProductionBatteryFactory.GetRz090581ProductionBatteryReadEvidence(
            ExactRz090581Identity("99.99", HostFirmwareCompatibility.ModelScoped))
            .SupportsRz090581CapabilityDiagnosticsQuery);

        HostIdentityReadResult[] rejected =
        [
            ExactRz090581Identity(
                "3.01",
                HostFirmwareCompatibility.UntestedBlocked),
            new(
                new HostIdentityEvidence(
                    "Razer",
                    "Blade 16 - RZ09-0528",
                    "RZ09-05289EN4",
                    "3.01"),
                null,
                HostFirmwareCompatibility.Tested),
            new(
                new HostIdentityEvidence(
                    "Razer",
                    "Blade 16 - RZ09-0581",
                    "RZ09-05819EN3",
                    "3.01"),
                null,
                HostFirmwareCompatibility.Tested),
            new(null, HidSessionFailureKind.IdentityBlocked),
        ];

        foreach (HostIdentityReadResult identity in rejected)
        {
            ProductionBatteryReadEvidence evidence =
                ProductionBatteryFactory.GetRz090581ProductionBatteryReadEvidence(
                    identity);
            Assert.False(evidence.SupportsRz090581CapabilityDiagnosticsQuery);
            Assert.True(evidence.SupportsMcuVersionQuery);
        }
    }

    [Fact]
    public void Rz090581GpuOverclockPolicy_RequiresExactIdentityButNotSpecificBios()
    {
        HostIdentityReadResult bios301 = ExactRz090581Identity("3.01");
        HostIdentityReadResult bios400 = ExactRz090581Identity("4.00");
        HostIdentityReadResult futureBios = ExactRz090581Identity(
            "99.99",
            HostFirmwareCompatibility.ModelScoped);

        GpuOverclockCapabilities policy = Assert.IsType<GpuOverclockCapabilities>(
            ProductionBatteryFactory.GetRz090581GpuOverclockCapabilities(bios301));

        Assert.Equal(DeviceFeatureAccess.ReadWrite, policy.Access);
        Assert.Equal(new DeviceNumericRange(-1000, 400, null), policy.CoreOffsetMegahertz);
        Assert.Equal(new DeviceNumericRange(-1000, 3000, null), policy.MemoryOffsetMegahertz);
        Assert.True(policy.SupportsEnableDisable);
        Assert.Equal(
            policy,
            ProductionBatteryFactory.GetRz090581GpuOverclockCapabilities(bios400));
        Assert.Equal(
            policy,
            ProductionBatteryFactory.GetRz090581GpuOverclockCapabilities(futureBios));
        Assert.Null(ProductionBatteryFactory.GetRz090581GpuOverclockCapabilities(
            new HostIdentityReadResult(null, HidSessionFailureKind.IdentityBlocked)));
        Assert.Null(ProductionBatteryFactory.GetRz090581GpuOverclockCapabilities(
            ExactRz090581Identity(
                "99.99",
                HostFirmwareCompatibility.UntestedBlocked)));
    }

    [Fact]
    public void Rz090581PadlessHyperBoostPolicy_AdmitsReviewedExactModelEvidence()
    {
        Assert.Equal(
            DeviceFeatureAccess.ReadWrite,
            ProductionBatteryFactory.GetRz090581HyperBoostWithoutCoolingPadAccess(
                ExactRz090581Identity("4.00"),
                HyperBoostWithoutCoolingPadEvidenceGates.Current.WriteAdmitted));
        Assert.Equal(
            DeviceFeatureAccess.ReadWrite,
            ProductionBatteryFactory.GetRz090581HyperBoostWithoutCoolingPadAccess(
                ExactRz090581Identity(
                    "99.99",
                    HostFirmwareCompatibility.ModelScoped),
                HyperBoostWithoutCoolingPadEvidenceGates.Current.WriteAdmitted));
        Assert.Null(
            ProductionBatteryFactory.GetRz090581HyperBoostWithoutCoolingPadAccess(
                new HostIdentityReadResult(
                    null,
                    HidSessionFailureKind.IdentityBlocked),
                HyperBoostWithoutCoolingPadEvidenceGates.Current.WriteAdmitted));
    }

    [Fact]
    public void Rz090581AdapterAdmission_ScopesOptionalClassesToExactEvidence()
    {
        AdapterPowerState[] modelScopedExpected =
        [
            AdapterPowerState.UsbCPower100W,
            AdapterPowerState.FullPower280W,
            AdapterPowerState.FullPower330W,
            AdapterPowerState.UsbCPower45W,
            AdapterPowerState.UsbCPower60W,
            AdapterPowerState.UsbCPower65W,
            AdapterPowerState.UsbCPower80W,
            AdapterPowerState.UsbCPower90W,
        ];

        Assert.Equal(
            modelScopedExpected,
            ProductionBatteryFactory.GetRz090581AdmittedAdapterPowerStates(
                ExactRz090581Identity("3.01")).Order().ToArray());
        Assert.Equal(
            modelScopedExpected,
            ProductionBatteryFactory.GetRz090581AdmittedAdapterPowerStates(
                ExactRz090581Identity("4.00")).Order().ToArray());
        Assert.Equal(
            modelScopedExpected,
            ProductionBatteryFactory.GetRz090581AdmittedAdapterPowerStates(
                ExactRz090581Identity(
                    "99.99",
                    HostFirmwareCompatibility.ModelScoped)).Order().ToArray());
        Assert.DoesNotContain(
            AdapterPowerState.FullPower330W,
            ProductionBatteryFactory.GetRz090581AdmittedAdapterPowerStates(new(
                new HostIdentityEvidence(
                    "Razer",
                    "Blade 16 - RZ09-0528",
                    "RZ09-05289EN4",
                    "4.00"),
                null,
                HostFirmwareCompatibility.Tested)));
        Assert.DoesNotContain(
            AdapterPowerState.FullPower330W,
            ProductionBatteryFactory.GetRz090581AdmittedAdapterPowerStates(
                new HostIdentityReadResult(
                    null,
                    HidSessionFailureKind.IdentityBlocked)));
    }

    [Fact]
    public void Rz090581AdapterAdmission_UntrustedManifestPreservesReadOnlyFallback()
    {
        IReadOnlySet<AdapterPowerState> states =
            ProductionBatteryFactory.GetRz090581AdmittedAdapterPowerStates(
                ExactRz090581Identity("4.00"),
                static () => throw new UnvalidatedCommandException("invalid manifest"));

        Assert.Equal(
            [
                AdapterPowerState.UsbCPower100W,
                AdapterPowerState.FullPower280W,
                AdapterPowerState.UsbCPower45W,
                AdapterPowerState.UsbCPower60W,
                AdapterPowerState.UsbCPower65W,
                AdapterPowerState.UsbCPower80W,
                AdapterPowerState.UsbCPower90W,
            ],
            states.Order().ToArray());
    }

    private static HostIdentityReadResult ExactRz090581Identity(
        string biosVersion,
        HostFirmwareCompatibility compatibility = HostFirmwareCompatibility.Tested) => new(
            new HostIdentityEvidence(
                "Razer",
                "Blade 16 - RZ09-0581",
                "RZ09-05819EN4",
                biosVersion),
            null,
            compatibility);

    [Fact]
    public async Task ProductionCompositionPassesThirtyMillisecondSettleToOpenedActorFactory()
    {
        TimeSpan? configuredSettle = null;
        var session = new ControlledSession(Immediate(QueryLimit80()));
        var actors = new RecordingActorFactory(session);
        ProductionBatteryFactory factory = Factory(settleDelay =>
        {
            configuredSettle = settleDelay;
            return actors;
        });

        await using IProductionBatteryPort port = Assert.IsAssignableFrom<IProductionBatteryPort>(
            (await factory.OpenAsync(default)).Value);
        Assert.Equal(80, (await port.QueryProtectionAsync(default)).ChargeLimitPercent);

        Assert.Equal(TimeSpan.FromMilliseconds(30), configuredSettle);
        Assert.Equal(1, actors.OpenCount);
    }

    [Fact]
    public async Task FactoryPortUsesExactActorSessionAndFreshIdentityContextForEveryOpen()
    {
        var first = new ControlledSession(Immediate(QueryLimit80()));
        var second = new ControlledSession(Immediate(QueryLimit80()));
        var actors = new RecordingActorFactory(first, second);
        List<ValidationRunContext> contexts = [];
        ProductionBatteryFactory factory = Factory(
            actors,
            () =>
            {
                var context = new ValidationRunContext();
                contexts.Add(context);
                return context;
            });

        HidSessionResult<IProductionBatteryPort> firstOpen = await factory.OpenAsync(default);
        await using (IProductionBatteryPort firstPort = Assert.IsAssignableFrom<IProductionBatteryPort>(firstOpen.Value))
        {
            Assert.Equal(80, (await firstPort.QueryProtectionAsync(default)).ChargeLimitPercent);
        }

        HidSessionResult<IProductionBatteryPort> secondOpen = await factory.OpenAsync(default);
        await using (IProductionBatteryPort secondPort = Assert.IsAssignableFrom<IProductionBatteryPort>(secondOpen.Value))
        {
            Assert.Equal(80, (await secondPort.QueryProtectionAsync(default)).ChargeLimitPercent);
        }

        Assert.Equal(2, actors.OpenCount);
        Assert.Equal(2, contexts.Count);
        Assert.NotSame(contexts[0], contexts[1]);
        Assert.Same(contexts[0], actors.Contexts[0]);
        Assert.Same(contexts[1], actors.Contexts[1]);
        Assert.Single(first.Commands);
        Assert.Single(second.Commands);
        Assert.Equal((0x07, 0x92), (first.Commands[0].ClassId, first.Commands[0].CommandId));
        Assert.Equal((0x07, 0x92), (second.Commands[0].ClassId, second.Commands[0].CommandId));
    }

    [Fact]
    public async Task OpenedPortPreservesObservedOnlyPerformanceCapabilities()
    {
        var cpuPowerLimit = new CpuPowerLimitCapabilities(
            new DeviceNumericRange(45, 95, 1),
            DeviceFeatureAccess.ObservedOnly);
        var cpuVoltageOptimizer = new CpuVoltageOptimizerCapabilities(
            new DeviceNumericRange(-30, -1, 1),
            DeviceFeatureAccess.ObservedOnly)
        {
            SupportsEnableDisable = true,
        };
        var gpuOverclock = new GpuOverclockCapabilities(
            new DeviceNumericRange(-1000, 400, null),
            new DeviceNumericRange(-1000, 3000, null),
            DeviceFeatureAccess.ObservedOnly)
        {
            SupportsEnableDisable = true,
        };
        PowerSourceBehaviorObservation[] sourceBehaviors =
        [
            new(
                PowerSource.UsbC,
                PerformancePreset.Balanced,
                FanMode.Automatic,
                FixedFanControlAvailable: false,
                PerformanceModeAvailable: false,
                CustomModeAvailable: false,
                InadequatePowerWarningVisible: true),
        ];
        ProductionBatteryFactory factory = Factory(
            new RecordingActorFactory(new ControlledSession()),
            cpuPowerLimitCapabilities: cpuPowerLimit,
            cpuVoltageOptimizerCapabilities: cpuVoltageOptimizer,
            gpuOverclockCapabilities: gpuOverclock,
            observedPowerSourceBehaviors: sourceBehaviors);

        await using IProductionBatteryPort port = Assert.IsAssignableFrom<IProductionBatteryPort>(
            (await factory.OpenAsync(default)).Value);

        Assert.Equal(factory.Capabilities.CpuPowerLimit, port.Capabilities.CpuPowerLimit);
        Assert.Equal(
            factory.Capabilities.CpuVoltageOptimizer,
            port.Capabilities.CpuVoltageOptimizer);
        Assert.Equal(factory.Capabilities.GpuOverclock, port.Capabilities.GpuOverclock);
        Assert.Equal(
            factory.Capabilities.ObservedPowerSourceBehaviors,
            port.Capabilities.ObservedPowerSourceBehaviors);
    }

    [Fact]
    public async Task CancellationAfterActorOpenDisposesExactActorBeforeReturningCanceled()
    {
        using var cancellation = new CancellationTokenSource();
        var session = new ControlledSession();
        var actors = new RecordingActorFactory(session)
        {
            AfterOpen = cancellation.Cancel,
        };
        ProductionBatteryFactory factory = Factory(actors);

        HidSessionResult<IProductionBatteryPort> opened = await factory.OpenAsync(cancellation.Token);

        Assert.Equal(HidSessionFailureKind.Canceled, opened.Failure);
        Assert.Null(opened.Value);
        Assert.Equal(1, actors.OpenCount);
        Assert.Equal(1, session.DisposeCount);
        Assert.Equal(RazerCommandSessionDisposition.FullyDisposed, session.Disposition);
    }

    [Fact]
    public async Task ActiveReadSerializesWithWriteAndActiveWritesSerializeWithEachOther()
    {
        var firstRelease = new TaskCompletionSource<HidSessionResult<ReadOnlyMemory<byte>>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRelease = new TaskCompletionSource<HidSessionResult<ReadOnlyMemory<byte>>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new ControlledSession(
            (_, _) => firstRelease.Task,
            (_, _) => secondRelease.Task,
            Immediate(WriteLimit80()));
        ProductionBatteryFactory factory = Factory(
            new RecordingActorFactory(session),
            catalog: Limit80Catalog());
        await using IProductionBatteryPort port = Assert.IsAssignableFrom<IProductionBatteryPort>(
            (await factory.OpenAsync(default)).Value);

        Task<OpenBlade.Contracts.ConfirmedBatteryState> read = port.QueryProtectionAsync(default);
        await session.WaitForExecutionsAsync(1);
        Task firstWrite = port.SetChargeLimitAsync(80, default);
        Task secondWrite = port.SetChargeLimitAsync(80, default);
        await Task.Yield();
        Assert.Equal(1, session.ExecuteCount);

        firstRelease.SetResult(QueryLimit80());
        await session.WaitForExecutionsAsync(2);
        Assert.Equal(2, session.ExecuteCount);
        Assert.False(secondWrite.IsCompleted);

        secondRelease.SetResult(WriteLimit80());
        await firstWrite;
        await secondWrite;
        Assert.Equal(3, session.ExecuteCount);
        Assert.Equal(
            [(0x07, 0x92), (0x07, 0x12), (0x07, 0x12)],
            session.Commands.Select(command => (command.ClassId, command.CommandId)).ToArray());
        Assert.Equal(80, (await read).ChargeLimitPercent);
    }

    [Fact]
    public async Task DisposalClosesAdmissionImmediatelyThenDrainsActiveOperationAndActor()
    {
        var release = new TaskCompletionSource<HidSessionResult<ReadOnlyMemory<byte>>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new ControlledSession((_, _) => release.Task);
        ProductionBatteryFactory factory = Factory(new RecordingActorFactory(session));
        IProductionBatteryPort port = Assert.IsAssignableFrom<IProductionBatteryPort>(
            (await factory.OpenAsync(default)).Value);

        Task<OpenBlade.Contracts.ConfirmedBatteryState> active = port.QueryProtectionAsync(default);
        await session.WaitForExecutionsAsync(1);
        ValueTask disposing = port.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => port.QueryProtectionAsync(default));
        CapabilityUnavailableException blockedWrite =
            await Assert.ThrowsAsync<CapabilityUnavailableException>(
            () => port.SetChargeLimitAsync(80, default));
        Assert.Contains("not admitted", blockedWrite.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(disposing.IsCompleted);
        Assert.Equal(0, session.DisposeCount);

        release.SetResult(QueryLimit80());
        Assert.Equal(80, (await active).ChargeLimitPercent);
        await disposing;
        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public async Task PoisonNeverRetriesAndFreshOpenRevalidatesWithFreshContextAndSession()
    {
        var poisoned = new ControlledSession(
            Immediate(new(default, HidSessionFailureKind.Timeout)),
            Immediate(QueryLimit80()));
        var recovered = new ControlledSession(Immediate(QueryLimit80()));
        var actors = new RecordingActorFactory(poisoned, recovered);
        List<ValidationRunContext> contexts = [];
        ProductionBatteryFactory factory = Factory(
            actors,
            () =>
            {
                var context = new ValidationRunContext();
                contexts.Add(context);
                return context;
            });

        IProductionBatteryPort first = Assert.IsAssignableFrom<IProductionBatteryPort>(
            (await factory.OpenAsync(default)).Value);
        await Assert.ThrowsAsync<TimeoutException>(() => first.QueryProtectionAsync(default));
        await Assert.ThrowsAsync<IOException>(() => first.QueryProtectionAsync(default));
        Assert.Equal(1, poisoned.ExecuteCount);
        await first.DisposeAsync();

        await using IProductionBatteryPort second = Assert.IsAssignableFrom<IProductionBatteryPort>(
            (await factory.OpenAsync(default)).Value);
        Assert.Equal(80, (await second.QueryProtectionAsync(default)).ChargeLimitPercent);

        Assert.Equal(2, actors.OpenCount);
        Assert.Equal(2, contexts.Count);
        Assert.NotSame(contexts[0], contexts[1]);
        Assert.Equal(1, recovered.ExecuteCount);
    }

    private static ProductionBatteryFactory Factory(
        IValidationActorFactory actors,
        Func<ValidationRunContext>? createContext = null,
        DeviceCommandCatalog? catalog = null,
        CpuPowerLimitCapabilities? cpuPowerLimitCapabilities = null,
        CpuVoltageOptimizerCapabilities? cpuVoltageOptimizerCapabilities = null,
        GpuOverclockCapabilities? gpuOverclockCapabilities = null,
        IReadOnlyList<PowerSourceBehaviorObservation>? observedPowerSourceBehaviors = null) =>
        Factory(
            _ => actors,
            createContext,
            catalog,
            cpuPowerLimitCapabilities,
            cpuVoltageOptimizerCapabilities,
            gpuOverclockCapabilities,
            observedPowerSourceBehaviors);

    private static ProductionBatteryFactory Factory(
        Func<TimeSpan, IValidationActorFactory> createActors,
        Func<ValidationRunContext>? createContext = null,
        DeviceCommandCatalog? catalog = null,
        CpuPowerLimitCapabilities? cpuPowerLimitCapabilities = null,
        CpuVoltageOptimizerCapabilities? cpuVoltageOptimizerCapabilities = null,
        GpuOverclockCapabilities? gpuOverclockCapabilities = null,
        IReadOnlyList<PowerSourceBehaviorObservation>? observedPowerSourceBehaviors = null) => new(
            catalog ?? DeviceCommandCatalog.Empty,
            ProductionBatteryReadEvidence.Production,
            createContext ?? (static () => new ValidationRunContext()),
            createActors,
            TimeProvider.System,
            ExchangeTimeout,
            DrainTimeout,
            new PendingDisposalRegistry(),
            cpuPowerLimitCapabilities: cpuPowerLimitCapabilities,
            cpuVoltageOptimizerCapabilities: cpuVoltageOptimizerCapabilities,
            gpuOverclockCapabilities: gpuOverclockCapabilities,
            observedPowerSourceBehaviors: observedPowerSourceBehaviors);

    private static DeviceCommandCatalog Limit80Catalog() => DeviceCommandCatalog.Create(
        CapturedFixtureFile.TargetDevice,
        [new(
            CapturedFixtureFile.TargetDevice,
            ProductionBatteryPort.Limit80Action,
            0x07,
            0x12,
            [0xD0],
            true,
            true,
            true,
            true,
            true)]);

    private static Func<RazerCommandSpec, CancellationToken, Task<HidSessionResult<ReadOnlyMemory<byte>>>>
        Immediate(HidSessionResult<ReadOnlyMemory<byte>> result) =>
            (_, _) => Task.FromResult(result);

    private static HidSessionResult<ReadOnlyMemory<byte>> QueryLimit80() =>
        new(new byte[] { BatteryValidationProtocol.Encode(BatteryValidationSetting.Limit80) }, null);

    private static HidSessionResult<ReadOnlyMemory<byte>> WriteLimit80() =>
        new(new byte[] { 0xD0 }, null);

    private sealed class RecordingActorFactory(params ControlledSession[] sessions)
        : IValidationActorFactory
    {
        private readonly Queue<ControlledSession> sessions = new(sessions);

        public int OpenCount { get; private set; }

        public List<ValidationRunContext> Contexts { get; } = [];

        public Action? AfterOpen { get; init; }

        public ValueTask<HidSessionResult<ValidationActor>> OpenAsync(ValidationRunContext context)
        {
            OpenCount++;
            Contexts.Add(context);
            ControlledSession session = sessions.Dequeue();
            var actor = new ValidationActor(
                session,
                new BatteryCommandClient(session, ExchangeTimeout));
            AfterOpen?.Invoke();
            return ValueTask.FromResult(new HidSessionResult<ValidationActor>(actor, null));
        }
    }

    private sealed class ControlledSession(
        params Func<RazerCommandSpec, CancellationToken,
            Task<HidSessionResult<ReadOnlyMemory<byte>>>>[] operations)
        : IRazerCommandSession
    {
        private readonly Queue<Func<RazerCommandSpec, CancellationToken,
            Task<HidSessionResult<ReadOnlyMemory<byte>>>>> operations = new(operations);
        private readonly TaskCompletionSource executionChanged =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RazerCommandSessionDisposition Disposition { get; private set; } =
            RazerCommandSessionDisposition.Healthy;

        public int ExecuteCount { get; private set; }

        public int DisposeCount { get; private set; }

        public List<RazerCommandSpec> Commands { get; } = [];

        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(
            RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId,
            TimeSpan exchangeTimeout,
            CancellationToken cancellationToken)
        {
            ExecuteCount++;
            Commands.Add(command);
            _ = assignTransactionId();
            executionChanged.TrySetResult();
            return operations.Dequeue()(command, cancellationToken);
        }

        public async Task WaitForExecutionsAsync(int count)
        {
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (ExecuteCount < count)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    throw new TimeoutException($"Expected {count} command executions.");
                }

                await Task.Yield();
            }
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            Disposition = RazerCommandSessionDisposition.FullyDisposed;
            return ValueTask.CompletedTask;
        }
    }
}
