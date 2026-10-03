using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class WindowsCpuLiveTuningTrustedNativeProviderTests
{
    [Fact]
    public async Task ProviderReportsFirstIdentityRejectionWithoutRetainingBackend()
    {
        using var fixture = new Fixture { Usb = null };
        var diagnostic = new CpuSavedAdmissionDiagnostic();
        await fixture.RunRaw(context => Assert.Null(context), diagnostic);
        Assert.Equal("provider matching USB identity", diagnostic.FirstFailure);
        Assert.Equal("curve transaction ObservationUnavailable", diagnostic.LiveObservationReason);
        Assert.Equal(0, fixture.Platform.Opens); Assert.Equal(0, fixture.Platform.Pins);
    }
    [Fact]
    public async Task PowerObservationSharesRetainedDriverWithCurveAndCannotManufactureAttendedAuthority()
    {
        using var fixture = new Fixture();
        var power = (ICpuLivePowerTrustedNativeProvider)fixture.Provider;
        Assert.Null(power.ReadFreshContext(default));
        CpuPmInstalledApproval? captured = null;
        await fixture.Run(curve =>
        {
            var context = Assert.IsType<CpuLivePowerContext>(power.ReadFreshContext(default));
            Assert.Equal(curve.Identity, context.Identity); Assert.Equal(curve.Backend, context.Backend);
            Assert.Equal(curve.RuntimeLifetime, context.RuntimeLifetime);
            Assert.Null(power.CreateObservationApproval(context, default));
            Assert.Null(power.ReadFreshObservationLifetime(context, default));
            captured = power.CreateInstalledObservationApproval(context, default);
            Assert.NotNull(captured); Assert.True(captured.IsCurrent());
            Assert.NotNull(power.CreateObservationMetadata(context, default));
            Assert.Equal(0, fixture.MetadataOpens);
            // Physical PPT admission is still pending, including restoration.
            Assert.False(power.CheckWriteAdmission(context, default, CpuLivePowerCommandPurpose.Restore, null!, null!, default));
            Assert.Null(power.CreateWriteProof(context, null!, default));
        });
        Assert.False(captured!.IsCurrent());
        await fixture.Run(_ =>
        {
            var context = power.ReadFreshContext(default)!;
            Assert.False(captured.IsCurrent());
            Assert.True(power.CreateInstalledObservationApproval(context, default)!.IsCurrent());
        });
        Assert.Equal(1, fixture.Platform.Opens); Assert.Equal(1, fixture.Platform.Pins);
    }

    [Fact]
    public async Task CurvePeerProofAllowsUnownedObservationButCannotAdmitPendingPptRestoration()
    {
        using var fixture = new Fixture();
        CpuLiveCurveWriteProof? captured = null; CpuLiveCurveNativeRequest? capturedRequest = null;
        var expected = CpuLivePowerTuningTransactionTests.PairFixture(45000, 45000);
        await fixture.Run(context =>
        {
            var single = Request(-2);
            capturedRequest = new(single.Scope, single.Command, single.Purpose, single.Before, new(expected, null));
            captured = fixture.Provider.CreateWriteProof(context, capturedRequest, default);
            Assert.NotNull(captured);
            var cap = CpuLivePowerFusedCap.TryDecode(4, 7u << 28)!;
            Assert.True(captured.AdmitPowerPeer(capturedRequest, 6, cap));
            var owned = new CpuLiveCurveNativeRequest(single.Scope, single.Command, single.Purpose, single.Before,
                new(expected, CpuLivePowerTuningTransactionTests.PairFixture(100123, 80765)));
            var ownedProof = fixture.Provider.CreateWriteProof(context, owned, default)!;
            Assert.False(ownedProof.AdmitPowerPeer(owned, 6, cap));
            Assert.False(captured.AdmitPowerPeer(owned, 6, cap));
            Assert.False(captured.Consume(owned)); // Values/correlation never rebind the exact native proof.
        });
        Assert.False(captured!.AdmitPowerPeer(capturedRequest!, 6, CpuLivePowerFusedCap.TryDecode(4, 7u << 28)!));
    }

    [Fact]
    public async Task PowerObservationRejectsChangedRuntimeContextAndUsbReturnCannotRenewOldApproval()
    {
        using var fixture = new Fixture();
        var power = (ICpuLivePowerTrustedNativeProvider)fixture.Provider;
        await fixture.Run(_ =>
        {
            var context = power.ReadFreshContext(default)!;
            var changed = context with { RuntimeLifetime = context.RuntimeLifetime! with { PowerSourceGeneration = 99 } };
            Assert.Null(power.CreateInstalledObservationApproval(changed, default));
            Assert.Null(power.CreateObservationMetadata(changed, default));
            var approval = power.CreateInstalledObservationApproval(context, default)!;
            var usb = fixture.Usb; fixture.Usb = null;
            Assert.False(approval.IsCurrent());
            fixture.Usb = usb;
            Assert.False(approval.IsCurrent());
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrMismatchedPresentUsbCannotAcquireInstalledDevice(bool mismatch)
    {
        using var fixture = new Fixture();
        fixture.Usb = mismatch ? new("RZ09-0581", 0x1532, 0x02E0, "Unknown") : null;
        await fixture.RunRaw(context => Assert.Null(context));
        Assert.Equal(0, fixture.Platform.Opens); Assert.Equal(0, fixture.MetadataOpens);
    }

    [Fact]
    public async Task UsbDriftIrreversiblyRevokesCapturedApprovalAndProof()
    {
        using var fixture = new Fixture();
        await fixture.Run(context =>
        {
            var approval = fixture.Provider.CreateInstalledQueryApproval(context, default)!;
            var request = Request(-2);
            var proof = fixture.Provider.CreateWriteProof(context, request, default)!;
            Assert.True(approval.IsCurrent()); Assert.True(proof.Current(request));
            var usb = fixture.Usb; fixture.Usb = null;
            Assert.False(approval.IsCurrent()); Assert.False(proof.Current(request));
            fixture.Usb = usb;
            Assert.False(approval.IsCurrent()); Assert.False(proof.Current(request));
            Assert.Null(proof.ReadFreshRange(request));
        });
    }

    [Fact]
    public async Task RetainedDeviceSurvivesRequestsButOldApprovalAndWriteProofExpire()
    {
        using var fixture = new Fixture();
        Assert.Null(fixture.Provider.ReadFreshContext(default));
        Assert.Equal(0, fixture.Platform.Opens);
        CpuQueryInstalledApproval? approval = null; CpuLiveCurveWriteProof? proof = null;
        var request = Request(-2);
        await fixture.Run(context =>
        {
            approval = fixture.Provider.CreateInstalledQueryApproval(context, default);
            Assert.NotNull(approval); Assert.True(approval.IsCurrent());
            Assert.Null(fixture.Provider.CreateQueryApproval(context, default));
            proof = fixture.Provider.CreateWriteProof(context, request, default);
            Assert.NotNull(proof); Assert.True(proof.Current(request));
            Assert.NotNull(proof.ReadFreshRange(request));
            Assert.NotNull(proof.ReadFreshRange(request));
            Assert.Equal(2, fixture.MetadataOpens); // Preflight and this exact native request.
        });
        Assert.False(approval!.IsCurrent()); Assert.False(proof!.Current(request));
        Assert.Null(proof.ReadFreshRange(request));
        await fixture.Run(context =>
        {
            Assert.False(approval.IsCurrent()); Assert.False(proof.Current(request));
            Assert.True(fixture.Provider.CreateInstalledQueryApproval(context, default)!.IsCurrent());
            Assert.True(fixture.Provider.CheckWriteAdmission(context, request.Command, request.Purpose, default));
        });
        Assert.Equal(1, fixture.Platform.Opens); Assert.Equal(1, fixture.Platform.Pins);
        Assert.Equal(3, fixture.MetadataOpens);
    }

    [Fact]
    public async Task GetterRevocationCannotProduceWriteProofOrRenewOnLaterRequest()
    {
        using var fixture = new Fixture();
        fixture.OnMinMax = () => fixture.Admitted = false;
        await fixture.Run(context => Assert.Null(fixture.Provider.CreateWriteProof(context, Request(-2), default)));
        Assert.Equal(1, fixture.MetadataOpens);
        fixture.Admitted = true; fixture.OnMinMax = null;
        await fixture.Run(context => Assert.NotNull(fixture.Provider.CreateWriteProof(context, Request(-2), default)));
        Assert.Equal(2, fixture.MetadataOpens);
        Assert.Equal(1, fixture.Platform.Opens);
    }

    [Fact]
    public async Task NarrowRangeRequiresExactImmediateReverseBeforeWriteProof()
    {
        using var fixture = new Fixture { Minimum = 2, Maximum = 5 };
        await fixture.Run(context =>
        {
            Assert.True(fixture.Provider.CheckWriteAdmission(context, Request(-2).Command, CpuLiveCurveCommandPurpose.Forward, default));
            // Original -1 falls outside this fresh range. No inverse guess.
            Assert.Null(fixture.Provider.CreateWriteProof(context, Request(-2), default));
        });
    }

    [Fact]
    public async Task InstalledConfigurationDriftRejectsFurtherContextAndApproval()
    {
        using var fixture = new Fixture();
        await fixture.Run(context =>
        {
            var approval = fixture.Provider.CreateInstalledQueryApproval(context, default);
            fixture.Platform.Running = false;
            Assert.False(approval!.IsCurrent());
            Assert.Null(fixture.Provider.ReadFreshContext(default));
        });
        Assert.Equal(1, fixture.Platform.Opens);
    }

    private static CpuLiveCurveNativeRequest Request(short offset)
    {
        Assert.True(CpuLiveCurveCommand.TryCreate(0, offset, 0xFF55, out var command));
        var before = CpuLiveCurveSnapshot.TryCreate(new(0xFF55, 0x005D000B, 0xD50,
            CpuLiveCurveTuningTransactionTests.ValuesFixture()))!;
        return new(new(), command, CpuLiveCurveCommandPurpose.Forward, before);
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly Platform Platform = new();
        internal readonly WindowsCpuLiveTuningTrustedNativeProvider Provider;
        private readonly ProbeBackend backend = new();
        private readonly CpuTuningController controller;
        internal int MetadataOpens;
        internal uint Minimum, Maximum = 50;
        internal bool Admitted = true;
        internal DeviceIdentity? Usb = new("RZ09-0528", 0x1532, 0x02C6, "Unknown");
        internal Action? OnMinMax;
        internal Fixture()
        {
            CpuTuningController? captured = null;
            Provider = new(WindowsRz090528CpuNativeSession.ForPlatform(Platform), () => captured, new Source(), () => Usb,
                _ => new(CpuLiveCurveBootSequenceSource.WindowsKuserSharedData, 42),
                new WindowsCpuLiveCurveRangeMetadata(_ => { MetadataOpens++; return new Metadata(this); }));
            controller = captured = new(new Source(), curveBackend: backend,
                curveStore: new CpuLiveCurveTuningTransactionTests.MemoryStore());
        }
        internal async Task Run(Action<CpuLiveCurveContext> check)
            => await RunRaw(context => check(Assert.IsType<CpuLiveCurveContext>(context)));

        internal async Task RunRaw(Action<CpuLiveCurveContext?> check, CpuSavedAdmissionDiagnostic? diagnostic = null)
        {
            backend.Check = token =>
            {
                var context = Provider.ReadFreshContext(token);
                check(context);
                return new(context, new(0xFF55, 0x005D000B, 0xD50, CpuLiveCurveTuningTransactionTests.ValuesFixture()));
            };
            await controller.ObserveLiveAsync(_ => Admitted, default, 2, 3, diagnostic);
        }
        public void Dispose() => Provider.Dispose();
    }

    private sealed class ProbeBackend : ICpuLiveCurveBackend
    {
        internal Func<CancellationToken, CpuLiveCurveBackendObservation> Check = _ => throw new InvalidOperationException();
        public Task<CpuLiveCurveBackendObservation> ObserveAsync(CancellationToken token) => Task.FromResult(Check(token));
        public Task<bool> CheckWriteAdmissionAsync(CpuLiveCurveContext context, CpuLiveCurveCommand command,
            CpuLiveCurveCommandPurpose purpose, CancellationToken token) => throw new InvalidOperationException();
        public Task<CpuLiveCurveSubmission> SendAsync(CpuLiveCurveContext context, CpuLiveCurveCommand command,
            CpuLiveCurveCommandPurpose purpose, CpuLiveCurveSnapshot expectedBefore, CancellationToken token) => throw new InvalidOperationException();
    }

    private sealed class Source : ICpuTuningObservationSource
    {
        public DeviceIdentity? ReadIdentity() => new("RZ09-0528", 0x1532, 0x02C6, "Unknown");
        public SavedCpuFirmwareReadResult ReadSaved(DeviceIdentity identity) => throw new InvalidOperationException();
        public CpuTuningBackendStatus InspectInstalled(DeviceIdentity identity, bool useInstalledBackend) => throw new InvalidOperationException();
    }

    internal sealed class Platform : ICpuQueryWindowsPlatform, ICpuQueryRetainableDevice, ICpuQueryMutex
    {
        internal int Opens, Pins;
        internal bool Running = true;
        public HostIdentityReadResult ReadHost() => new(new("Razer", "Blade 16 - RZ09-0528", "RZ09-05289EN4", "Unknown"), null);
        public CpuQueryProcessorIdentity ReadProcessor() => new("AuthenticAMD", 0x1A, 0x24, 0);
        public bool ControllersAbsent() => true;
        public IDisposable PinBackendFiles() { Pins++; return new Cleanup(); }
        public InstalledCpuBackendSnapshot ReadBackend() => new(Rz090528InstalledCpuBackendInspector.ExpectedHashes.Select(pair =>
            new CpuBackendFileMetadata(pair.Key, pair.Value, true, Rz090528InstalledCpuBackendInspector.SignerThumbprint)).ToArray(),
            true, @"\??\" + Rz090528InstalledCpuBackendInspector.DriverPath, 1, 3, Running);
        public ICpuQueryDevice OpenExistingDevice() { Opens++; return this; }
        public ICpuQueryDevice Duplicate() => throw new InvalidOperationException("Provider tests must not invoke the device.");
        public CpuQueryIoctlReply Invoke(uint code, byte[] input, int outputLength) => throw new InvalidOperationException();
        public ICpuQueryMutex CreateSharedMutex() => this;
        public CpuQueryMutexWait Wait(TimeSpan timeout, CancellationToken cancellationToken) => CpuQueryMutexWait.Acquired;
        public void Release() { }
        public void Dispose() { }
        private sealed class Cleanup : IDisposable { public void Dispose() { } }
    }

    private sealed class Metadata(Fixture fixture) : IWindowsCpuCurveRangeSession
    {
        public IWindowsCpuCurveRangeObject? QueryVersion(Action checkpoint) => new Output(new() { ["version"] = (uint)7 });
        public IWindowsCpuCurveRangeObject? GetCurveMinMax(Action checkpoint)
        {
            fixture.OnMinMax?.Invoke();
            return new Output(new()
            {
                ["pack"] = new Output(new()
                {
                    ["Current"] = (uint)0,
                    ["Min"] = fixture.Minimum,
                    ["Max"] = fixture.Maximum,
                    ["Stepping"] = (uint)1,
                    ["Value"] = (uint)0x5000A,
                })
            });
        }
        public void Dispose() { }
    }
    private sealed class Output(Dictionary<string, object?> values) : IWindowsCpuCurveRangeObject
    {
        public IEnumerable<string> Names => values.Keys;
        public object? Get(string name) => values[name];
        public void Dispose() { }
    }
}
