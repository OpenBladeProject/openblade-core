using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class Rz090528InstalledCpuBackendInspectorTests
{
    private static readonly DeviceIdentity Exact = new("RZ09-0528", 0x1532, 0x02C6, "2.02");

    [Fact]
    public void NoOptInSkipsAllInstalledStackInspection()
    {
        var source = new FakeSource();
        var status = new Rz090528InstalledCpuBackendInspector(source).Inspect(false, Exact);
        Assert.Equal(0, source.Reads);
        Assert.Equal(CpuTuningBackendAvailability.Unavailable, status.Availability);
    }

    [Fact]
    public void WrongModelOrPidSkipsInspection()
    {
        var source = new FakeSource();
        var inspector = new Rz090528InstalledCpuBackendInspector(source);
        _ = inspector.Inspect(true, Exact with { Model = "RZ09-0581" });
        _ = inspector.Inspect(true, Exact with { ProductId = 0x02E0 });
        _ = inspector.Inspect(true, Exact with { VendorId = 0xFFFF });
        Assert.Equal(0, source.Reads);
    }

    [Fact]
    public void MissingFilesOrServiceAreMissingDependencies()
    {
        AssertAvailability(Valid() with { Files = [] }, CpuTuningBackendAvailability.MissingDependency);
        AssertAvailability(Valid() with { ServiceExists = false }, CpuTuningBackendAvailability.MissingDependency);
    }

    [Fact]
    public void SignedHashMatchedStackStillRefusesStoppedOrDisabledService()
    {
        AssertAvailability(Valid() with { Running = false }, CpuTuningBackendAvailability.Unavailable);
        AssertAvailability(Valid() with { Start = 4 }, CpuTuningBackendAvailability.Unavailable);
        AssertAvailability(Valid() with { Start = null }, CpuTuningBackendAvailability.Unavailable);
    }

    [Fact]
    public void ExactRunningStackCannotPromoteObservedOnlyOrEnableWrites()
    {
        var status = new Rz090528InstalledCpuBackendInspector(new FakeSource(Valid())).Inspect(true, Exact);
        Assert.Equal(CpuTuningBackendAvailability.Available, status.Availability);
        Assert.Equal(DeviceFeatureAccess.ObservedOnly, status.Access);
        Assert.False(status.CanWrite);
    }

    [Fact]
    public void EachFileNeedsHashAndValidPinnedSigner()
    {
        foreach (int index in Enumerable.Range(0, 3))
        {
            CpuBackendFileMetadata[] files = Valid().Files.ToArray();
            var original = files[index];
            foreach (var replacement in new[]
            {
                original with { Sha256 = new string('0', 64) },
                original with { SignatureValid = false },
                original with { SignerThumbprint = "DifferentSigner" },
                original with { Path = original.Path + ".other" },
            })
            {
                files[index] = replacement;
                AssertAvailability(Valid() with { Files = files }, CpuTuningBackendAvailability.Failed);
            }
        }
    }

    [Fact]
    public void WrongDriverPathTypeOrDuplicateFileFailsClosed()
    {
        AssertAvailability(Valid() with { ImagePath = @"\??\C:\Other\AMDRyzenMasterDriver.sys" },
            CpuTuningBackendAvailability.Failed);
        AssertAvailability(Valid() with { ServiceType = 2 }, CpuTuningBackendAvailability.Failed);
        AssertAvailability(Valid() with { Files = [Valid().Files[0], Valid().Files[0], Valid().Files[2]] },
            CpuTuningBackendAvailability.Failed);
    }

    [Fact]
    public void PassiveReadFailureDoesNotEscapeOrAuthorizeNativeWork()
    {
        var status = new Rz090528InstalledCpuBackendInspector(new FakeSource { Failure = true }).Inspect(true, Exact);
        Assert.Equal(CpuTuningBackendAvailability.Failed, status.Availability);
        Assert.False(status.CanWrite);
    }

    private static InstalledCpuBackendSnapshot Valid() => new(
        Rz090528InstalledCpuBackendInspector.ExpectedHashes.Select(pair =>
            new CpuBackendFileMetadata(pair.Key, pair.Value, true,
                Rz090528InstalledCpuBackendInspector.SignerThumbprint)).ToArray(),
        true, @"\??\" + Rz090528InstalledCpuBackendInspector.DriverPath, 1, 3, true);

    private static void AssertAvailability(InstalledCpuBackendSnapshot snapshot, CpuTuningBackendAvailability expected)
    {
        var status = Rz090528InstalledCpuBackendInspector.Evaluate(snapshot);
        Assert.Equal(expected, status.Availability);
        Assert.False(status.CanWrite);
    }

    private sealed class FakeSource(InstalledCpuBackendSnapshot? snapshot = null) : IInstalledCpuBackendSnapshotSource
    {
        public int Reads { get; private set; }
        public bool Failure { get; init; }

        public InstalledCpuBackendSnapshot Read()
        {
            Reads++;
            if (Failure) { throw new IOException("Passive metadata denied."); }
            return snapshot ?? Valid();
        }
    }
}
