using OpenBlade.Capture;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class CpuPmParentVendorLeaseTests
{
    [Fact]
    public void SignaturesReadOnceUnderThreeDenyWritePinsAndMetadataDefensivelyCopied()
    {
        var pins = new Pins(); int reads = 0;
        var metadata = Metadata();
        var lease = new CpuPmParentVendorLease(pins, () => { reads++; Assert.Equal(3, pins.Files); return metadata; });
        lease.InitializeAndVerify(CancellationToken.None);
        metadata[0] = metadata[0] with { SignatureValid = false };
        for (int i = 0; i < 100; i++) { lease.AssertHeld(); }
        Assert.Equal(1, reads); Assert.False(pins.Disposed);
        Assert.Throws<IOException>(() => lease.InitializeAndVerify(CancellationToken.None));
    }
    [Theory]
    [InlineData("hash")]
    [InlineData("signature")]
    [InlineData("signer")]
    [InlineData("late-cancel")]
    [InlineData("late-pins")]
    public void InitializationUncertaintyNeverReturnsHeldEvidenceOrDisposesPins(string failure)
    {
        var pins = new Pins { WrongHash = failure == "hash" }; using var token = new CancellationTokenSource();
        var lease = new CpuPmParentVendorLease(pins, () =>
        {
            var result = Metadata();
            if (failure == "signature") { result[0] = result[0] with { SignatureValid = false }; }
            if (failure == "signer") { result[0] = result[0] with { SignerThumbprint = "unknown" }; }
            if (failure == "late-cancel") { token.Cancel(); }
            if (failure == "late-pins") { pins.Fail = true; }
            return result;
        });
        Assert.ThrowsAny<Exception>(() => lease.InitializeAndVerify(token.Token));
        Assert.Throws<IOException>(lease.AssertHeld); Assert.False(pins.Disposed);
    }
    [Fact]
    public void LostPinIsPermanentEvenWhenFileAppearsHealthyAgain()
    {
        var pins = new Pins(); var lease = new CpuPmParentVendorLease(pins, Metadata);
        lease.InitializeAndVerify(CancellationToken.None); pins.Fail = true;
        Assert.Throws<IOException>(lease.AssertHeld); pins.Fail = false; Assert.Throws<IOException>(lease.AssertHeld);
        Assert.False(pins.Disposed);
    }
    private static CpuBackendFileMetadata[] Metadata() => Rz090528InstalledCpuBackendInspector.ExpectedHashes.Select(pair =>
        new CpuBackendFileMetadata(pair.Key, pair.Value, true, Rz090528InstalledCpuBackendInspector.SignerThumbprint)).ToArray();
    private sealed class Pins : ICpuPmLaunchFiles
    {
        internal int Files;
        internal bool WrongHash, Fail, Disposed;
        public void PinDirectory(string path, bool privateAcl, bool protectedAcl) { Assert.False(privateAcl); }
        public ICpuPmLaunchFilePin PinFile(string path, long maximumBytes, bool privateAcl)
        { Files++; Assert.False(privateAcl); return new Pin(WrongHash ? new('0', 64) : Rz090528InstalledCpuBackendInspector.ExpectedHashes[path]); }
        public IEnumerable<CpuPmLaunchEntry> Enumerate(string path) => throw new InvalidOperationException();
        public void AssertAllPins() { if (Fail || Disposed) { throw new IOException(); } }
        public void Dispose() => Disposed = true;
        private sealed class Pin(string hash) : ICpuPmLaunchFilePin
        {
            public long Length => 1;
            public string Sha256 => hash;
            public byte[] ReadBounded(int maximumBytes) => throw new InvalidOperationException();
        }
    }
}
