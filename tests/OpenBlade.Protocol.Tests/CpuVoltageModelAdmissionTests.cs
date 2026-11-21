using OpenBlade.Contracts;

namespace OpenBlade.Protocol.Tests;

public sealed class CpuVoltageModelAdmissionTests
{
    [Theory]
    [InlineData("RZ09-0508", 0x02B6)]
    [InlineData("RZ09-0482", 0x029D)]
    public void InstalledPhoenixBackendRequiresExactModelCompleteTrustedRecipeAndRunningService(string model, int product)
    {
        var identity = new DeviceIdentity(model, 0x1532, (ushort)product, "provenance");
        var contract = CpuLiveCurveContract.ForIdentity(identity)!;
        var recipe = CpuInstalledBackendProfile.ForContract(contract);
        var source = new Source(Valid(recipe));
        var inspector = new Rz090528InstalledCpuBackendInspector(source, recipe);
        Assert.Equal(CpuTuningBackendAvailability.Available, inspector.Inspect(true, identity).Availability);
        Assert.Equal(1, source.Reads);
        foreach (var invalid in new[]
        {
            Valid(recipe) with { Running = false },
            Valid(recipe) with { Start = 4 },
        })
        {
            source.Snapshot = invalid;
            Assert.Equal(CpuTuningBackendAvailability.Unavailable, inspector.Inspect(true, identity).Availability);
        }
        source.Snapshot = Valid(recipe) with { Files = Valid(recipe).Files.Select((file, i) => i == 0 ? file with { Sha256 = new string('0', 64) } : file).ToArray() };
        Assert.Equal(CpuTuningBackendAvailability.Failed, inspector.Inspect(true, identity).Availability);
        source.Snapshot = Valid(recipe) with { Files = Valid(recipe).Files.Select((file, i) => i == 0 ? file with { SignatureValid = false } : file).ToArray() };
        Assert.Equal(CpuTuningBackendAvailability.Failed, inspector.Inspect(true, identity).Availability);
        source.Snapshot = Valid(recipe) with { ImagePath = @"\??\C:\unreviewed\AMDRyzenMasterDriver.sys" };
        Assert.Equal(CpuTuningBackendAvailability.Failed, inspector.Inspect(true, identity).Availability);
        int reads = source.Reads;
        Assert.Equal(CpuTuningBackendAvailability.Unavailable, inspector.Inspect(false, identity).Availability);
        Assert.Equal(CpuTuningBackendAvailability.Unavailable,
            inspector.Inspect(true, identity with { ProductId = 0x02C6 }).Availability);
        Assert.Equal(reads, source.Reads);
    }

    [Theory]
    [InlineData("RZ09-0508", 0x02B6)]
    [InlineData("RZ09-0482", 0x029D)]
    public void VoltageCapabilitiesRemainSeparateFromPowerSettersAndRequireModelAdmission(string model, int product)
    {
        var profile = ExperimentalBladeModels.All.Single(value => value.ModelNumber == model);
        var caps = ExperimentalBladePort.CreateCapabilities(profile, true);
        Assert.True(caps.CpuVoltageOptimizer!.IsWellFormed);
        Assert.Equal(DeviceFeatureAccess.ReadWrite, caps.CpuVoltageOptimizer!.Access);
        // The Synapse CPU power limit is shown but never writable; voltage
        // admission does not lend it a write path.
        // RZ09-0482 shows no power limit: its Synapse path drives the V22 curve.
        Assert.Equal(product == 0x029D ? null : DeviceFeatureAccess.ObservedOnly, caps.CpuPowerLimit?.Access);
        Assert.Equal(DeviceFeatureAccess.ObservedOnly,
            ExperimentalBladePort.CreateCapabilities(profile, false).CpuVoltageOptimizer!.Access);
        var contract = CpuLiveCurveContract.ForIdentity(new(model, 0x1532, (ushort)product, "Unknown"))!;
        Assert.True(Rz090528CpuFreshUsbIdentity.IsExactUsbInstance($"USB\\VID_1532&PID_{product:X4}\\unit", contract));
        Assert.False(Rz090528CpuFreshUsbIdentity.IsExactUsbInstance("USB\\VID_1532&PID_02C6\\unit", contract));
        Assert.False(Rz090528CpuFreshUsbIdentity.IsExactUsbInstance($"USB\\VID_1532&PID_{product:X4}&MI_00\\unit", contract));
        Assert.Null(CpuLiveCurveContract.Resolve(null, null, new(model, 0x1532, (ushort)product, "Unknown")));
    }

    [Theory]
    [InlineData("RZ09-0508", 0x02B6)]
    [InlineData("RZ09-0482", 0x029D)]
    public void InitialUnknownIdentityCanRecoverAndOnlyConfirmedBindingIsRetained(string model, int product)
    {
        DeviceIdentity? identity = null;
        int reads = 0;
        var binding = new CpuInstalledBackendBinding(() => { reads++; return identity; });
        Assert.Throws<CpuVoltageHostUnavailableException>(() => binding.Get());
        identity = new(model, 0x1532, (ushort)product, "Unknown");
        var confirmed = binding.Get();
        Assert.Equal(model, confirmed.Contract.ModelNumber);
        identity = new("RZ09-0528", 0x1532, 0x02C6, "Unknown");
        Assert.Same(confirmed, binding.Get());
        Assert.Equal(2, reads);
    }

    [Fact]
    public void TenCoreBlade14IsObservedWithoutBorrowingTwelveCoreBackend()
    {
        var profile = ExperimentalBladeModels.All.Single(value => value.ModelNumber == "RZ09-0530");
        Assert.Equal(DeviceFeatureAccess.ObservedOnly,
            ExperimentalBladePort.CreateCapabilities(profile, true).CpuVoltageOptimizer!.Access);
        Assert.Null(CpuLiveCurveContract.ForIdentity(new("RZ09-0530", 0x1532, 0x02C5, "Unknown")));
    }

    private static InstalledCpuBackendSnapshot Valid(CpuInstalledBackendProfile recipe) =>
        new(recipe.ExpectedHashes.Select(pair => new CpuBackendFileMetadata(pair.Key, pair.Value, true, recipe.SignerThumbprint)).ToArray(),
            true, @"\??\" + recipe.DriverPath, 1, 3, true);
    private sealed class Source(InstalledCpuBackendSnapshot snapshot) : IInstalledCpuBackendSnapshotSource
    {
        internal InstalledCpuBackendSnapshot Snapshot { get; set; } = snapshot;
        internal int Reads { get; private set; }
        public InstalledCpuBackendSnapshot Read() { Reads++; return Snapshot; }
    }
}
