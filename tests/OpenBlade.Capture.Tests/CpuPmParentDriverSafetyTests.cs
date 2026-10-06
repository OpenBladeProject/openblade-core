using System.Security.Cryptography;
using System.Text.Json;
using OpenBlade.Capture;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class CpuPmParentDriverSafetyTests
{
    [Fact]
    public void RepeatedProofChecksUseHeldPinsAndCallbacksWithoutFullHostOrClosureReads()
    {
        var fixture = new Fixture(); var value = fixture.Create(); Assert.NotNull(value);
        Assert.False(value.ReadFreshProof().Admits);
        var owner = NewOwner(value); value.RetainUntilVerifiedCleanup(owner);
        int fullReads = fixture.Source.Reads, closureReads = fixture.Files.Enumerations;
        fixture.Source.OnRead = () => throw new IOException("Full check must not run here.");
        fixture.Files.DenyEnumeration = true;
        for (int i = 0; i < 1000; i++) { Assert.True(value.ReadFreshProof().Admits); }
        Assert.Equal(fullReads, fixture.Source.Reads); Assert.Equal(closureReads, fixture.Files.Enumerations);
        Assert.Equal(1, fixture.Vendor.Initializations);
        // Safety metadata and owner binding cannot create a live-driver proof.
        Assert.Null(owner.ReadFresh(Run, Lifetime));
        Assert.False(value.ReadFresh().Admits);
        Assert.False(fixture.Files.Disposed); Assert.False(fixture.Vendor.Disposed);
    }
    [Theory]
    [InlineData("vendor")]
    [InlineData("payload")]
    [InlineData("callback")]
    public void CheapProofDriftPermanentlyDeniesWithoutReleasingResources(string fault)
    {
        var fixture = new Fixture(); var value = fixture.Create(); Assert.NotNull(value);
        value.RetainUntilVerifiedCleanup(NewOwner(value));
        if (fault == "vendor") { fixture.Vendor.Fail = true; }
        if (fault == "payload") { fixture.Files.FailPins = true; }
        if (fault == "callback") { fixture.Clock.Suspend!(4); }
        Assert.False(value.ReadFreshProof().Admits);
        fixture.Vendor.Fail = fixture.Files.FailPins = false;
        Assert.False(value.ReadFreshProof().Admits); Assert.False(value.ReadFresh().Admits);
        Assert.False(fixture.Vendor.Disposed); Assert.False(fixture.Files.Disposed);
        Assert.Equal(0, fixture.Clock.Unregisters);
    }
    [Fact]
    public void FreshSafetyRequiresHeldVendorPayloadAndLiveCallbackScopeWithoutDriverState()
    {
        var fixture = new Fixture(); var value = fixture.Create();
        Assert.NotNull(value); Assert.True(value.ReadFresh().Admits);
        Assert.Equal(1, fixture.Vendor.Initializations); Assert.True(fixture.Source.Reads >= 2);
        Assert.Throws<IOException>(() => value.ReleaseAfterVerifiedCleanup());
        Assert.False(fixture.Files.Disposed); Assert.False(fixture.Vendor.Disposed);
    }
    [Theory]
    [InlineData("host")]
    [InlineData("usb")]
    [InlineData("controllers")]
    [InlineData("vendor")]
    [InlineData("payload")]
    [InlineData("callback")]
    [InlineData("late-clock")]
    [InlineData("passive-error")]
    public void DriftLatchesDenialAndPreservesPinsRatherThanCleaningUp(string failure)
    {
        var fixture = new Fixture(); var value = fixture.Create(); Assert.NotNull(value);
        switch (failure)
        {
            case "host": fixture.Source.Value = fixture.Source.Value with { ExactHostAndPackage = false }; break;
            case "usb": fixture.Source.Value = fixture.Source.Value with { HealthyUniqueUsb = false }; break;
            case "controllers": fixture.Source.Value = fixture.Source.Value with { ControllersAbsent = false }; break;
            case "vendor": fixture.Vendor.Fail = true; break;
            case "payload": fixture.Files.FailPins = true; break;
            case "callback": fixture.Source.OnRead = () => fixture.Clock.Suspend!(4); break;
            case "late-clock": fixture.Source.OnRead = () => fixture.Clock.Value = fixture.Clock.Value with { TickMilliseconds = 121000 }; break;
            case "passive-error": fixture.Source.OnRead = () => throw new IOException(); break;
        }
        Assert.False(value.ReadFresh().Admits);
        fixture.Source.Value = new(true, true, true); fixture.Source.OnRead = null;
        fixture.Files.FailPins = false; fixture.Clock.Value = CpuPmParentContinuityTests.FakePlatform.Initial;
        Assert.False(value.ReadFresh().Admits);
        Assert.False(fixture.Files.Disposed); Assert.False(fixture.Vendor.Disposed); Assert.Equal(0, fixture.Clock.Unregisters);
    }
    [Fact]
    public void MissingOperatorConsentAndLateCancellationNeverReturnAdmittedProvider()
    {
        var fixture = new Fixture(); Assert.Null(fixture.Create(false)); Assert.Equal(0, fixture.Vendor.Initializations);
        fixture = new Fixture(); using var token = new CancellationTokenSource();
        fixture.Source.OnRead = token.Cancel;
        Assert.Null(fixture.Create(true, token.Token)); Assert.False(fixture.Vendor.Disposed);
    }
    [Fact]
    public void FileMetadataAdmissionIsExactAndIndependentOfScmState()
    {
        var files = Rz090528InstalledCpuBackendInspector.ExpectedHashes.Select(pair =>
            new CpuBackendFileMetadata(pair.Key, pair.Value, true, Rz090528InstalledCpuBackendInspector.SignerThumbprint)).ToArray();
        Assert.True(WindowsRz090528CpuQueryAdapter.FilesExact(files));
        Assert.False(WindowsRz090528CpuQueryAdapter.FilesExact(files[..2]));
        Assert.False(WindowsRz090528CpuQueryAdapter.FilesExact([files[0], files[0], files[2]]));
        Assert.False(WindowsRz090528CpuQueryAdapter.FilesExact([files[0] with { SignatureValid = false }, files[1], files[2]]));
        Assert.False(WindowsRz090528CpuQueryAdapter.FilesExact([files[0] with { Sha256 = new('0', 64) }, files[1], files[2]]));
        Assert.False(WindowsRz090528CpuQueryAdapter.FilesExact([files[0] with { SignerThumbprint = "other" }, files[1], files[2]]));
    }
    private static readonly Guid Run = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Lifetime = new("22222222-2222-2222-2222-222222222222");
    private static CpuPmOwnedDriverLifecycle NewOwner(ICpuPmDriverSafety safety) => new(
        new(Run, Lifetime, new('A', 64), new('a', 64)), new NoOperations(), safety, new NoOperations(), new NoOperations());
    private sealed class NoOperations : ICpuPmDriverLifecyclePlatform, ICpuPmDriverJournal, ICpuPmAuthorityClock
    {
        public TimeSpan Now => TimeSpan.Zero;
        public ICpuPmDriverMutex AcquireMutex(CancellationToken token) => throw new InvalidOperationException();
        public CpuPmDriverAbsence ReadIndependentAbsence() => throw new InvalidOperationException();
        public ICpuPmCreatedDriver? CreateFixedDriver() => throw new InvalidOperationException();
        public bool AppendAndFlush(CpuPmDriverLifecycleRecord record) => throw new InvalidOperationException();
    }
    private sealed class Fixture
    {
        internal readonly Passive Source = new();
        internal readonly Vendor Vendor = new();
        internal readonly CpuPmParentContinuityTests.FakePlatform Clock = new();
        internal readonly Files Files = new();
        internal CpuPmParentDriverSafety? Create(bool approved = true, CancellationToken token = default) =>
            CpuPmParentDriverSafety.CreateForTesting(Files.Trusted, Files, Source, Clock, approved, Vendor, token);
    }
    private sealed class Passive : ICpuPmParentPassiveSource
    {
        internal CpuPmParentPassiveEvidence Value = new(true, true, true);
        internal int Reads;
        internal Action? OnRead;
        public CpuPmParentPassiveEvidence ReadFresh() { Reads++; OnRead?.Invoke(); return Value; }
    }
    private sealed class Vendor : ICpuPmParentVendorLease
    {
        internal int Initializations;
        internal bool Fail, Disposed;
        public void InitializeAndVerify(CancellationToken token) { token.ThrowIfCancellationRequested(); Initializations++; }
        public void AssertHeld() { if (Fail || Disposed) { throw new IOException(); } }
        public void BindOwner(CpuPmOwnedDriverLifecycle owner) { AssertHeld(); }
        public void ReleaseAfterVerifiedCleanup(CpuPmOwnedDriverLifecycle owner) => Disposed = true;
    }
    private sealed class Files : ICpuPmLaunchFiles
    {
        private static readonly string[] Names = ["OpenBlade.Capture.exe", "OpenBlade.PmSupervisor.exe"];
        private readonly byte[] manifest;
        internal readonly CpuPmReviewedLaunchBootstrap Trusted;
        internal bool Disposed, FailPins, DenyEnumeration;
        internal int Enumerations;
        internal Files()
        {
            string source = new('a', 40);
            manifest = JsonSerializer.SerializeToUtf8Bytes(new
            {
                version = 1,
                source,
                files = Names.Select(name => new { name, length = 1, sha256 = Hash([1]) })
            });
            Trusted = new(source, Hash(manifest));
        }
        private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
        public void PinDirectory(string path, bool privateAcl, bool protectedAcl) { Assert.False(Disposed); }
        public ICpuPmLaunchFilePin PinFile(string path, long maximumBytes, bool privateAcl) =>
            new Pin(path.EndsWith("manifest.json", StringComparison.Ordinal) ? manifest : [1]);
        public IEnumerable<CpuPmLaunchEntry> Enumerate(string path)
        {
            Enumerations++; if (DenyEnumeration) { throw new IOException(); }
            return Names.Append("manifest.json").Select(name => new CpuPmLaunchEntry(name, false));
        }
        public void AssertAllPins() { if (FailPins || Disposed) { throw new IOException(); } }
        public void Dispose() => Disposed = true;
        private sealed class Pin(byte[] bytes) : ICpuPmLaunchFilePin
        {
            public long Length => bytes.Length;
            public string Sha256 => Hash(bytes);
            public byte[] ReadBounded(int maximumBytes) => (byte[])bytes.Clone();
        }
    }
}
