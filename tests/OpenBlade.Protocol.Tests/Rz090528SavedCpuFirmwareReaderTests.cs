using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class Rz090528SavedCpuFirmwareReaderTests
{
    private static readonly DeviceIdentity Exact = new("RZ09-0528", 0x1532, 0x02C6, "2.02");

    [Fact]
    public void ReadsOnlyExactVariablesWithBoundedBuffer()
    {
        var native = new FakeReader();
        var result = new Rz090528SavedCpuFirmwareReader(native).Read(Exact);
        Assert.Equal(SavedCpuFirmwareReadStatus.Observed, result.Status);
        Assert.True(result.HasRestoreBaseline);
        Assert.Equal(new[] { "AodSetupStx", "AodSetupStxLast" }, native.Names);
        Assert.All(native.Guids, guid => Assert.Equal(Rz090528SavedCpuFirmwareReader.VendorGuid, guid));
        Assert.All(native.MaximumSizes, size => Assert.Equal(32768, size));
    }

    [Fact]
    public void UnsupportedIdentityOrDecoderBiosSkipsAllReads()
    {
        var native = new FakeReader();
        foreach (var identity in new[]
        {
            Exact with { Model = "RZ09-0581" }, Exact with { VendorId = 1 },
            Exact with { ProductId = 0x02E0 }, Exact with { BiosVersion = "2.03" },
        })
        {
            var result = new Rz090528SavedCpuFirmwareReader(native).Read(identity);
            Assert.Equal(SavedCpuFirmwareReadStatus.UnsupportedIdentity, result.Status);
            Assert.False(result.HasRestoreBaseline);
        }
        Assert.Empty(native.Names);
    }

    [Theory]
    [InlineData(0, 7)]
    [InlineData(799, 7)]
    [InlineData(801, 7)]
    [InlineData(32769, 7)]
    [InlineData(800, 3)]
    [InlineData(800, 15)]
    public void UnobservedSizeOrAttributesCannotBecomeBaseline(int size, uint attributes)
    {
        var native = new FakeReader { Current = new(new byte[size], attributes, 0) };
        var result = new Rz090528SavedCpuFirmwareReader(native).Read(Exact);
        Assert.Equal(SavedCpuFirmwareReadStatus.InvalidData, result.Status);
        Assert.Null(result.Current);
        Assert.NotNull(result.Last);
        Assert.False(result.HasRestoreBaseline);
    }

    [Fact]
    public void PrivilegeFailureRemainsTypedUnavailableNotZeroConfiguration()
    {
        var native = new FakeReader { Current = new(null, 0, 1314), Last = new(null, 0, 1314) };
        var result = new Rz090528SavedCpuFirmwareReader(native).Read(Exact);
        Assert.Equal(SavedCpuFirmwareReadStatus.Unavailable, result.Status);
        Assert.Equal(1314, result.CurrentError);
        Assert.Equal(1314, result.LastError);
        Assert.Null(result.Current);
        Assert.Null(result.Last);
        Assert.False(result.HasRestoreBaseline);
    }

    [Fact]
    public void PartialFailureRetainsSuccessfulSavedObservation()
    {
        var native = new FakeReader { Last = new(null, 0, 203) };
        var result = new Rz090528SavedCpuFirmwareReader(native).Read(Exact);
        Assert.Equal(SavedCpuFirmwareReadStatus.Unavailable, result.Status);
        Assert.NotNull(result.Current);
        Assert.Null(result.Last);
        Assert.Equal(203, result.LastError);
        Assert.False(result.HasRestoreBaseline);
    }

    [Fact]
    public void MirrorMismatchPreservesBothObservationsWithoutRestoreAuthority()
    {
        byte[] last = new byte[800];
        last[799] = 17; // An unrelated field mismatch matters too.
        var result = new Rz090528SavedCpuFirmwareReader(new FakeReader { Last = new(last, 7, 0) }).Read(Exact);
        Assert.Equal(SavedCpuFirmwareReadStatus.MirrorMismatch, result.Status);
        Assert.Equal(0, result.Current!.CopyBytes()[799]);
        Assert.Equal(17, result.Last!.CopyBytes()[799]);
        Assert.False(result.HasRestoreBaseline);
    }

    [Fact]
    public void BaselinesDoNotAliasNativeBuffersOrReturnedCopies()
    {
        byte[] bytes = Enumerable.Range(0, 800).Select(index => (byte)index).ToArray();
        byte[] expected = (byte[])bytes.Clone();
        var native = new FakeReader { Current = new(bytes, 7, 0), Last = new(bytes, 7, 0) };
        var result = new Rz090528SavedCpuFirmwareReader(native).Read(Exact);
        Array.Clear(bytes);
        Array.Clear(result.Current!.CopyBytes());
        Assert.Equal(expected, result.Current.CopyBytes());
        Assert.Equal(expected, result.Last!.CopyBytes());
        Assert.True(result.HasRestoreBaseline);
    }

    private sealed class FakeReader : ICpuFirmwareVariableReader
    {
        public CpuFirmwareVariableRead Current { get; init; } = new(new byte[800], 7, 0);
        public CpuFirmwareVariableRead Last { get; init; } = new(new byte[800], 7, 0);
        public List<string> Names { get; } = [];
        public List<string> Guids { get; } = [];
        public List<int> MaximumSizes { get; } = [];

        public CpuFirmwareVariableRead Read(string name, string vendorGuid, int maximumBytes)
        {
            Names.Add(name);
            Guids.Add(vendorGuid);
            MaximumSizes.Add(maximumBytes);
            return name == "AodSetupStx" ? Current : Last;
        }
    }
}
