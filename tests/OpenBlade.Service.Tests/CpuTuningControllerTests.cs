using System.Buffers.Binary;
using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class CpuTuningControllerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceAdmittedPairedObservationRequiresJournalAndDoesNotPromiseARestorableBaseline(bool hasJournal)
    {
        // Per-core is observable but has no all-core typed reverse. Backend
        // availability must not pretend this particular mutation is admitted.
        var source = new FakeObservationSource { Saved = Observed(DecodeSaved(2, 41)) };
        CpuTuningStatus status = await new CpuTuningController(source, hasJournal ? new EmptyStore() : null)
            .ObserveAsync(false, default);
        Assert.Equal(hasJournal, status.Firmware.CanWrite);
        Assert.Equal(CpuTuningBackendAvailability.Available, status.Firmware.Availability);
        Assert.Null(status.LiveState);
        Assert.False(status.Live.CanWrite);
        Assert.False(status.RestorePending);
        Assert.Null(CpuSavedTuningPlan.Create(source.Saved.Current!.CopyBytes(), new(null, new(true, -1))));
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("bios")]
    [InlineData("status")]
    [InlineData("error")]
    [InlineData("missing-last")]
    [InlineData("mirror")]
    [InlineData("geometry")]
    public async Task SourceAdmissionDoesNotPromoteInvalidOrUnsupportedPairedObservations(string fault)
    {
        SavedCpuFirmwareReadResult saved = Observed(DecodeSaved(1, 1));
        saved = fault switch
        {
            "status" => saved with { Status = SavedCpuFirmwareReadStatus.UnsupportedIdentity },
            "error" => saved with { LastError = 5 },
            "missing-last" => saved with { Last = null },
            "mirror" => saved with { Last = DecodeSaved(1, 2) },
            "geometry" => saved with { Current = new(saved.Current!.CopyBytes()[..799]), Last = new(saved.Last!.CopyBytes()[..799]) },
            _ => saved,
        };
        var source = new FakeObservationSource
        {
            Saved = saved,
            Identity = fault switch
            {
                "identity" => FakeObservationSource.ExactIdentity with { ProductId = 0x02E0 },
                "bios" => FakeObservationSource.ExactIdentity with { BiosVersion = "2.03" },
                _ => FakeObservationSource.ExactIdentity,
            },
        };
        CpuTuningStatus status = await new CpuTuningController(source, new EmptyStore()).ObserveAsync(false, default);
        Assert.False(status.Firmware.CanWrite);
        Assert.NotEqual(CpuTuningBackendAvailability.Available, status.Firmware.Availability);
        Assert.False(status.RestorePending);
    }

    [Fact]
    public async Task MissingExactIdentitySkipsSavedReadsAndInstalledBackendInspection()
    {
        var source = new FakeObservationSource { Identity = null };

        CpuTuningStatus status = await new CpuTuningController(source)
            .ObserveAsync(true, CancellationToken.None);

        Assert.Equal(1, source.IdentityReads);
        Assert.Equal(0, source.SavedReads);
        Assert.Equal(0, source.Inspections);
        Assert.Null(status.SavedConfiguration);
        Assert.Equal(CpuTuningBackendAvailability.Unavailable, status.Firmware.Availability);
        Assert.Equal(CpuTuningBackendAvailability.Unavailable, status.Live.Availability);
        AssertUnconfirmed(status);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public async Task ObservedAllCoreSavedValueDoesNotConfirmLiveStateOrBootActivation(int magnitude)
    {
        Rz090528SavedCpuConfiguration saved = DecodeSaved(curveMode: 1, magnitude: (ushort)magnitude);
        var source = new FakeObservationSource { Saved = Observed(saved) };

        CpuTuningStatus status = await new CpuTuningController(source)
            .ObserveAsync(false, CancellationToken.None);

        CpuSavedConfiguration configuration = Assert.IsType<CpuSavedConfiguration>(status.SavedConfiguration);
        Assert.Equal(65000u, configuration.PowerLimitMilliwatts);
        Assert.True(configuration.CustomPowerLimitEnabled);
        CpuCurveOptimizerState curve = Assert.IsType<CpuCurveOptimizerState>(configuration.CurveOptimizer);
        Assert.Equal(CpuCurveOptimizerMode.AllCore, curve.Mode);
        Assert.Equal(-magnitude, curve.AllCoreValue);
        Assert.Empty(curve.CoreValues);
        Assert.Equal(CpuTuningBackendAvailability.Available, status.Firmware.Availability);
        Assert.Equal(FakeObservationSource.ExactIdentity, source.SavedIdentity);
        Assert.Equal(FakeObservationSource.ExactIdentity, source.InspectedIdentity);
        AssertUnconfirmed(status);
    }

    [Fact]
    public async Task DisabledSavedPowerZeroIsNotAZeroWattLiveMeasurement()
    {
        var source = new FakeObservationSource
        {
            Saved = Observed(DecodeSaved(curveMode: 1, magnitude: 0, powerMilliwatts: 0, powerMode: 0xFF)),
        };

        CpuTuningStatus status = await new CpuTuningController(source)
            .ObserveAsync(false, CancellationToken.None);

        Assert.Equal(0u, status.SavedConfiguration!.PowerLimitMilliwatts);
        Assert.False(status.SavedConfiguration.CustomPowerLimitEnabled);
        AssertUnconfirmed(status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(255)]
    public async Task UnknownOrPerCoreSavedModeRetainsRawBaselineWithoutInventingAllCoreZero(byte mode)
    {
        Rz090528SavedCpuConfiguration saved = DecodeSaved(curveMode: mode, magnitude: 41);
        byte[] before = saved.CopyBytes();
        SavedCpuFirmwareReadResult read = Observed(saved);
        var source = new FakeObservationSource { Saved = read };

        CpuTuningStatus status = await new CpuTuningController(source)
            .ObserveAsync(false, CancellationToken.None);

        Assert.NotNull(status.SavedConfiguration);
        Assert.Null(status.SavedConfiguration.CurveOptimizer);
        Assert.True(read.HasRestoreBaseline);
        Assert.Equal(before, read.Current!.CopyBytes());
        Assert.Equal(before, read.Last!.CopyBytes());
        Assert.Equal(mode, read.Current.CurveMode);
        if (mode == 2)
        {
            Assert.Equal(-41, read.Current.GetCoreCurveOptimizer(0));
            Assert.Equal(17, read.Current.GetCoreCurveOptimizer(1));
        }
        AssertUnconfirmed(status);
    }

    [Fact]
    public async Task UnknownSavedSignDoesNotInventDisabledCurveState()
    {
        var source = new FakeObservationSource { Saved = Observed(DecodeSaved(1, 41, sign: 2)) };

        CpuTuningStatus status = await new CpuTuningController(source)
            .ObserveAsync(false, CancellationToken.None);

        Assert.Null(status.SavedConfiguration!.CurveOptimizer);
        AssertUnconfirmed(status);
    }

    [Fact]
    public async Task FailedCurrentReadCannotInferSavedOrLiveZeroFromMirror()
    {
        var source = new FakeObservationSource
        {
            Saved = new(SavedCpuFirmwareReadStatus.Unavailable, null, DecodeSaved(1, 0), 5, 0),
        };

        CpuTuningStatus status = await new CpuTuningController(source)
            .ObserveAsync(false, CancellationToken.None);

        Assert.Null(status.SavedConfiguration);
        Assert.False(source.Saved.HasRestoreBaseline);
        Assert.Equal(CpuTuningBackendAvailability.Unavailable, status.Firmware.Availability);
        AssertUnconfirmed(status);
    }

    [Fact]
    public async Task MirrorMismatchReportsFailedAndCannotGrantWriteOrRestoreOwnership()
    {
        var source = new FakeObservationSource
        {
            Saved = new(SavedCpuFirmwareReadStatus.MirrorMismatch, DecodeSaved(1, 1), DecodeSaved(1, 0), 0, 0),
        };

        CpuTuningStatus status = await new CpuTuningController(source)
            .ObserveAsync(false, CancellationToken.None);

        Assert.Equal(-1, status.SavedConfiguration!.CurveOptimizer!.AllCoreValue);
        Assert.Equal(CpuTuningBackendAvailability.Failed, status.Firmware.Availability);
        Assert.False(source.Saved.HasRestoreBaseline);
        AssertUnconfirmed(status);
    }

    [Theory]
    [InlineData(false, DeviceFeatureAccess.ObservedOnly, CpuTuningBackendAvailability.Unavailable)]
    [InlineData(true, DeviceFeatureAccess.ReadOnly, CpuTuningBackendAvailability.Available)]
    [InlineData(true, DeviceFeatureAccess.ReadWrite, CpuTuningBackendAvailability.Available)]
    [InlineData(true, DeviceFeatureAccess.ObservedOnly, CpuTuningBackendAvailability.MissingDependency)]
    public async Task InstalledBackendInspectionRemainsObservationOnly(bool useInstalledBackend,
        DeviceFeatureAccess access, CpuTuningBackendAvailability availability)
    {
        var source = new FakeObservationSource { Installed = new(access, availability) };

        CpuTuningStatus status = await new CpuTuningController(source)
            .ObserveAsync(useInstalledBackend, CancellationToken.None);

        Assert.Equal(1, source.Inspections);
        Assert.Equal(useInstalledBackend, source.UseInstalledBackend);
        Assert.Equal(availability, status.Live.Availability);
        Assert.Equal(DeviceFeatureAccess.ObservedOnly, status.Live.Access);
        AssertUnconfirmed(status);
    }

    [Fact]
    public async Task CancellationBeforeObservationSkipsAllSourceCalls()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var source = new FakeObservationSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new CpuTuningController(source).ObserveAsync(true, cancellation.Token));

        Assert.Equal(0, source.IdentityReads);
        Assert.Equal(0, source.SavedReads);
        Assert.Equal(0, source.Inspections);
    }

    [Fact]
    public async Task CancellationDuringSavedReadIsObservedBeforeBackendInspection()
    {
        using var cancellation = new CancellationTokenSource();
        var source = new FakeObservationSource { AfterSavedRead = cancellation.Cancel };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new CpuTuningController(source).ObserveAsync(true, cancellation.Token));

        Assert.Equal(1, source.SavedReads);
        Assert.Equal(0, source.Inspections);
    }

    [Fact]
    public async Task CancellationDuringBackendInspectionDoesNotReturnObservation()
    {
        using var cancellation = new CancellationTokenSource();
        var source = new FakeObservationSource { AfterInspection = cancellation.Cancel };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new CpuTuningController(source).ObserveAsync(true, cancellation.Token));

        Assert.Equal(1, source.SavedReads);
        Assert.Equal(1, source.Inspections);
    }

    private static void AssertUnconfirmed(CpuTuningStatus status)
    {
        Assert.True(status.IsWellFormed);
        Assert.Null(status.LiveState);
        Assert.Equal(CpuTuningPendingBoot.Unknown, status.PendingBoot);
        Assert.False(status.RestorePending);
        Assert.False(status.Firmware.CanWrite);
        Assert.False(status.Live.CanWrite);
    }

    private static SavedCpuFirmwareReadResult Observed(Rz090528SavedCpuConfiguration saved) =>
        new(SavedCpuFirmwareReadStatus.Observed, saved,
            Rz090528CpuFirmwareCodec.DecodeSaved(saved.CopyBytes()), 0, 0);

    private static Rz090528SavedCpuConfiguration DecodeSaved(byte curveMode, ushort magnitude,
        uint powerMilliwatts = 65000, byte powerMode = 2, byte sign = 1)
    {
        byte[] bytes = new byte[Rz090528CpuFirmwareCodec.SavedConfigurationLength];
        bytes[0x94] = 2;
        bytes[0x95] = powerMode;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x96), powerMilliwatts);
        bytes[0xC0] = curveMode;
        bytes[0xC1] = sign;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0xC2), magnitude);
        bytes[0xC4] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x104), magnitude);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x106), 17);
        return Rz090528CpuFirmwareCodec.DecodeSaved(bytes);
    }

    private sealed class EmptyStore : ICpuTuningOwnershipJournalStore
    {
        public Task<CpuTuningOwnershipJournalLoadResult> LoadAsync(CancellationToken token) =>
            Task.FromResult(new CpuTuningOwnershipJournalLoadResult(null, false, null));
        public Task<string?> TryWriteAsync(string? expectedSha256, CpuTuningOwnershipJournal journal, CancellationToken token) =>
            throw new InvalidOperationException("Passive observation must not prepare ownership.");
    }

    private sealed class FakeObservationSource : ICpuTuningObservationSource
    {
        internal static readonly DeviceIdentity ExactIdentity = new("RZ09-0528", 0x1532, 0x02C6, "2.02");
        public DeviceIdentity? Identity { get; init; } = ExactIdentity;
        public SavedCpuFirmwareReadResult Saved { get; init; } = Observed(DecodeSaved(1, 1));
        public CpuTuningBackendStatus Installed { get; init; } =
            new(DeviceFeatureAccess.ObservedOnly, CpuTuningBackendAvailability.Unavailable);
        public Action? AfterSavedRead { get; init; }
        public Action? AfterInspection { get; init; }
        public int IdentityReads { get; private set; }
        public int SavedReads { get; private set; }
        public int Inspections { get; private set; }
        public DeviceIdentity? SavedIdentity { get; private set; }
        public DeviceIdentity? InspectedIdentity { get; private set; }
        public bool UseInstalledBackend { get; private set; }

        public DeviceIdentity? ReadIdentity()
        {
            IdentityReads++;
            return Identity;
        }

        public SavedCpuFirmwareReadResult ReadSaved(DeviceIdentity identity)
        {
            SavedReads++;
            SavedIdentity = identity;
            AfterSavedRead?.Invoke();
            return Saved;
        }

        public CpuTuningBackendStatus InspectInstalled(DeviceIdentity identity, bool useInstalledBackend)
        {
            Inspections++;
            InspectedIdentity = identity;
            UseInstalledBackend = useInstalledBackend;
            AfterInspection?.Invoke();
            return Installed;
        }
    }
}
