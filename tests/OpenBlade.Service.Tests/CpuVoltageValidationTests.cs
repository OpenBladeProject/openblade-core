using System.Reflection;
using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed partial class CpuTuningRuntimeTests
{
    [Fact]
    public void VoltageValidationBuildIdentifiesItsActualVersionAndExactCapabilities()
    {
        var capabilities = BatteryValidationFactory.CreateWindowsRz090528ProductionBattery().Capabilities;
        Assert.Equal(DeviceFeatureAccess.ObservedOnly, capabilities.CpuPowerLimit!.Access);
        Assert.True(capabilities.CpuVoltageOptimizer!.SupportsEnableDisable);
        Assert.Equal(DeviceFeatureAccess.ReadWrite, capabilities.CpuVoltageOptimizer.Access);
        string version = OpenBladeProductVersion.Read(typeof(ServiceControlRuntime).Assembly);
        var metadata = typeof(ServiceControlRuntime).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(value => value.Key == "OpenBladeCpuVoltageValidation").Value;
#if OPENBLADE_CPU_VOLTAGE_VALIDATION
        Assert.Equal(new DeviceNumericRange(-1, -1, 1), capabilities.CpuVoltageOptimizer.Value);
        Assert.Equal("true", metadata);
        string? fullSource = typeof(ServiceControlRuntime).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(value => value.Key == "OpenBladeValidationSourceRevision").Value;
        Assert.Matches("^[0-9a-fA-F]{40}$", fullSource!);
        Assert.EndsWith("+voltage-validation." + fullSource![..8].ToLowerInvariant(), version);
        Assert.True(version.Length <= 64);
        Assert.Matches(@"^\d+\.\d+\.\d+(?:\.\d+)?\+voltage-validation\.[0-9a-f]{8}$", version);
#else
        Assert.Equal(new DeviceNumericRange(-30, -1, 1), capabilities.CpuVoltageOptimizer.Value);
        Assert.Equal("false", metadata);
        Assert.DoesNotContain("voltage-validation", version);
#endif
    }

    [Fact]
    public async Task VoltageValidationActualServiceAssemblyVersionRoundTripsStatusCodec()
    {
        using var backend = new ValidationCurveBackend(-1);
        var context = await CreateValidationContextAsync(backend);
        string version = OpenBladeProductVersion.Read(typeof(ServiceControlRuntime).Assembly);
        using var stream = new MemoryStream();
        await IpcMessageCodec.WriteServerFrameAsync(stream, new ServiceStatusEvent(OpenBladeIpc.ProtocolVersion,
            context.Runtime.GetStatus() with { ServiceVersion = version }), CancellationToken.None);
        stream.Position = 0;
        var decoded = Assert.IsType<ServiceStatusEvent>(await IpcMessageCodec.ReadServerFrameAsync(stream, CancellationToken.None));
        Assert.Equal(version, decoded.Status.ServiceVersion);
        Assert.Equal(0, backend.Sends);
    }

    [Fact]
    public async Task VoltageValidationMaximumLocalDevVersionRoundTripsStatusCodec()
    {
        using var backend = new ValidationCurveBackend(-1);
        var context = await CreateValidationContextAsync(backend);
        const string version = "65535.65534.65534.65534+voltage-validation.12345678";
        using var stream = new MemoryStream();
        await IpcMessageCodec.WriteServerFrameAsync(stream, new ServiceStatusEvent(OpenBladeIpc.ProtocolVersion,
            context.Runtime.GetStatus() with { ServiceVersion = version }), CancellationToken.None);
        stream.Position = 0;
        var decoded = Assert.IsType<ServiceStatusEvent>(await IpcMessageCodec.ReadServerFrameAsync(stream, CancellationToken.None));
        Assert.Equal(version, decoded.Status.ServiceVersion);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public async Task VoltageValidationRuntimeAndConcreteProviderGateWholeCycle(short original)
    {
        using var backend = new ValidationCurveBackend(original);
        var context = await CreateValidationContextAsync(backend);
        short target = original == -1 ? (short)0 : (short)-1;
        var applied = await context.Runtime.SetCpuTuningAsync(CpuTuningTarget.LiveNow,
            new(null, new(target != 0, -1)), () => true, default);
        // Both builds expose CO writes. The real trusted provider must still
        // admit every addressed core and its exact reverse before submission.
        Assert.Equal(ControlOperationStatus.Stored, applied.Status);
        Assert.Equal(12, backend.Sends);
        Assert.All(backend.Values, core => Assert.Equal(target, core.Offset));
        Assert.Equal(CpuTuningFeatures.CurveOptimizer, applied.CpuTuning!.LiveRecovery!.Features);
        var restored = await context.Runtime.RestoreCpuTuningAsync(CpuTuningTarget.LiveNow, () => true, default);
        Assert.Equal(ControlOperationStatus.Stored, restored.Status);
        Assert.Equal(24, backend.Sends);
        Assert.All(backend.Values, core => Assert.Equal(original, core.Offset));
        Assert.Equal(CpuTuningRecoveryState.None, restored.CpuTuning!.LiveRecovery!.State);
        Assert.True(backend.MetadataReads > 0);
        Assert.Equal(0, context.Source.Submissions);
    }

#if OPENBLADE_CPU_VOLTAGE_VALIDATION
    [Theory]
    [InlineData(-1, -2, false)]
    [InlineData(-2, -1, false)]
    [InlineData(-1, -1, true)]
    public async Task VoltageValidationRejectsUnsupportedForwardOriginalOrMixedRequestBeforeSubmission(
        short original, int target, bool ppt)
    {
        using var backend = new ValidationCurveBackend(original);
        // Deliberately broader advertised capabilities cannot broaden the profile.
        var context = await CreateValidationContextAsync(backend, broadCapabilities: true);
        var result = await context.Runtime.SetCpuTuningAsync(CpuTuningTarget.LiveNow,
            new(ppt ? 45 : null, new(true, target)), () => true, default);
        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.Equal(0, backend.Sends);
        Assert.Null(backend.Store.Journal);
        Assert.All(backend.Values, core => Assert.Equal(original, core.Offset));
    }

    [Fact]
    public async Task VoltageValidationRejectsPowerOnlyBeforeAnyBackendSubmission()
    {
        using var backend = new ValidationCurveBackend(-1);
        var context = await CreateValidationContextAsync(backend, broadCapabilities: true);
        var result = await context.Runtime.SetCpuTuningAsync(CpuTuningTarget.LiveNow,
            new(45, null), () => true, default);
        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.Equal(0, backend.Sends);
        Assert.Equal(0, backend.MetadataReads);
        Assert.Null(backend.Store.Journal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VoltageValidationConcreteProviderRejectsOutOfRangeEvenWithFreshBroadMetadata(
        bool restoring)
    {
        var purpose = restoring ? CpuLiveCurveCommandPurpose.Restore : CpuLiveCurveCommandPurpose.Forward;
        using var backend = new ValidationCurveBackend(-1);
        var context = await CreateValidationContextAsync(backend);
        bool checkedProvider = false;
        backend.OnObserve = current =>
        {
            Assert.True(CpuLiveCurveCommand.TryCreate(0, -2, 0xFF55, out var command));
            Assert.False(backend.Provider!.CheckWriteAdmission(current, command, purpose, default));
            var before = CpuLiveCurveSnapshot.TryCreate(new(0xFF55, 0x005D000B, 0xD50, backend.Values))!;
            Assert.Null(backend.Provider.CreateWriteProof(current, new(new(), command, purpose, before), default));
            // A valid addressed core cannot hide an unsupported different core in
            // the native request's before-vector, including restoration requests.
            var contaminated = backend.Values.ToArray();
            contaminated[1] = new(contaminated[1].PhysicalCoreId, -2);
            var unsupportedBefore = CpuLiveCurveSnapshot.TryCreate(new(0xFF55, 0x005D000B, 0xD50, contaminated))!;
            Assert.True(CpuLiveCurveCommand.TryCreate(0, 0, 0xFF55, out var allowed));
            Assert.Null(backend.Provider.CreateWriteProof(current, new(new(), allowed, purpose, unsupportedBefore), default));
            checkedProvider = true;
        };
        await context.Runtime.ObserveCpuTuningAsync(true, () => true, default);
        Assert.True(checkedProvider);
        Assert.Equal(0, backend.Sends);
        Assert.Equal(0, backend.MetadataReads);
    }

    [Fact]
    public async Task VoltageValidationRevocationAtNativeProofPreventsSetter()
    {
        using var backend = new ValidationCurveBackend(-1);
        var context = await CreateValidationContextAsync(backend);
        backend.BeforeProofRange = () => backend.UsbPresent = false;
        var result = await context.Runtime.SetCpuTuningAsync(CpuTuningTarget.LiveNow,
            new(null, new(false, -1)), () => true, default);
        Assert.NotEqual(ControlOperationStatus.Stored, result.Status);
        Assert.Equal(0, backend.Sends);
        Assert.All(backend.Values, core => Assert.Equal(-1, core.Offset));
    }

    [Fact]
    public async Task VoltageValidationStillRequiresFreshRangeToCoverImmediateReverse()
    {
        using var backend = new ValidationCurveBackend(-1) { MaximumMagnitude = 0 };
        var context = await CreateValidationContextAsync(backend);
        var result = await context.Runtime.SetCpuTuningAsync(CpuTuningTarget.LiveNow,
            new(null, new(false, -1)), () => true, default);
        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.Equal(0, backend.Sends);
        Assert.True(backend.MetadataReads > 0);
        Assert.Null(backend.Store.Journal);
    }
#endif

    private Task<TestContext> CreateValidationContextAsync(ValidationCurveBackend backend, bool broadCapabilities = false)
    {
        var actual = BatteryValidationFactory.CreateWindowsRz090528ProductionBattery().Capabilities;
        return CreateContextAsync(live: new LiveFixture(curve: true, power: false),
            configureCapabilities: capabilities => broadCapabilities ? capabilities : capabilities with
            { CpuVoltageOptimizer = actual.CpuVoltageOptimizer, CpuPowerLimit = actual.CpuPowerLimit },
            controllerFactory: source => backend.CreateController(source));
    }

    // Real runtime, controller, transaction and trusted provider. Only driver I/O,
    // boot/USB probes and WMI getters are fake; no native operation can execute.
    private sealed class ValidationCurveBackend(short original) : ICpuLiveCurveBackend, IDisposable
    {
        internal readonly CpuLiveCurveTuningTransactionTests.MemoryStore Store = new();
        internal readonly CpuLiveCoreOffset[] Values = CpuLiveCurveTuningTransactionTests.ValuesFixture()
            .Select(core => new CpuLiveCoreOffset(core.PhysicalCoreId, original)).ToArray();
        internal WindowsCpuLiveTuningTrustedNativeProvider? Provider;
        internal Action<CpuLiveCurveContext>? OnObserve = null;
        internal int Sends, MetadataReads;
        internal bool UsbPresent = true;
        internal Action? BeforeProofRange = null;
        internal uint MaximumMagnitude = 50;
        private readonly WindowsCpuLiveTuningTrustedNativeProviderTests.Platform platform = new();

        internal CpuTuningController CreateController(ICpuTuningObservationSource source)
        {
            CpuTuningController? controller = null;
            Provider = new(WindowsRz090528CpuNativeSession.ForPlatform(platform), () => controller, source,
                () => UsbPresent ? new("RZ09-0528", 0x1532, 0x02C6, "Unknown") : null,
                _ => new(CpuLiveCurveBootSequenceSource.WindowsKuserSharedData, 42),
                new WindowsCpuLiveCurveRangeMetadata(_ => { MetadataReads++; return new ValidationMetadata(this); }));
            return controller = new(source, curveBackend: this, curveStore: Store);
        }

        public Task<CpuLiveCurveBackendObservation> ObserveAsync(CancellationToken token)
        {
            var context = Provider!.ReadFreshContext(token);
            if (context is null || Provider.CreateInstalledQueryApproval(context, token) is null)
            { return Task.FromResult(new CpuLiveCurveBackendObservation(null, null)); }
            OnObserve?.Invoke(context);
            return Task.FromResult(new CpuLiveCurveBackendObservation(context, new(0xFF55, 0x005D000B, 0xD50, Values.ToArray())));
        }

        public Task<bool> CheckWriteAdmissionAsync(CpuLiveCurveContext context, CpuLiveCurveCommand command,
            CpuLiveCurveCommandPurpose purpose, CancellationToken token) =>
            Task.FromResult(Provider!.CheckWriteAdmission(context, command, purpose, token));

        public Task<CpuLiveCurveSubmission> SendAsync(CpuLiveCurveContext context, CpuLiveCurveCommand command,
            CpuLiveCurveCommandPurpose purpose, CpuLiveCurveSnapshot expectedBefore, CancellationToken token)
        {
            Assert.Equal(CpuLiveCurveOwnershipState.PossibleSubmission, Store.Journal!.State);
            Assert.Equal(Values, expectedBefore.CoreOffsets);
            var request = new CpuLiveCurveNativeRequest(new(), command, purpose, expectedBefore);
            var proof = Provider!.CreateWriteProof(context, request, token);
            if (proof is null || !proof.Consume(request))
            { return Task.FromResult(CpuLiveCurveSubmission.NotSubmitted); }
            int priorReads = MetadataReads;
            BeforeProofRange?.Invoke();
            if (proof.ReadFreshRange(request)?.AllowsOperation(request) != true)
            { return Task.FromResult(CpuLiveCurveSubmission.NotSubmitted); }
            Assert.True(MetadataReads > priorReads); // This exact native proof requires a fresh getter pair.
            Sends++;
            int index = Array.FindIndex(Values, core => core.PhysicalCoreId == command.PhysicalCoreId);
            Values[index] = new(command.PhysicalCoreId, command.Offset);
            return Task.FromResult(CpuLiveCurveSubmission.Acknowledged);
        }
        public void Dispose() => Provider?.Dispose();
    }

    private sealed class ValidationMetadata(ValidationCurveBackend backend) : IWindowsCpuCurveRangeSession
    {
        public IWindowsCpuCurveRangeObject? QueryVersion(Action checkpoint) => new ValidationOutput(new() { ["version"] = 7u });
        public IWindowsCpuCurveRangeObject? GetCurveMinMax(Action checkpoint) => new ValidationOutput(new()
        {
            ["pack"] = new ValidationOutput(new()
            {
                ["Current"] = 0u,
                ["Min"] = 0u,
                ["Max"] = backend.MaximumMagnitude,
                ["Stepping"] = 1u,
                ["Value"] = 0x5000Au,
            }),
        });
        public void Dispose() { }
    }
    private sealed class ValidationOutput(Dictionary<string, object?> values) : IWindowsCpuCurveRangeObject
    {
        public IEnumerable<string> Names => values.Keys;
        public object? Get(string name) => values[name];
        public void Dispose() { }
    }
}
