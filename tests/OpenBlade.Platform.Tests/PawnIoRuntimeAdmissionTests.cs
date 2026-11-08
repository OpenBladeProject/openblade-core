using System.ComponentModel;
using System.Runtime.InteropServices;

namespace OpenBlade.Platform.Tests;

public sealed class PawnIoRuntimeAdmissionTests
{
    [Fact]
    public void ServiceConfigurationUsesDocumentedProbeAndExactBoundedReadInsteadOfOversizedRpcBuffer()
    {
        List<int> sizes = [];
        bool Query(nint buffer, int size, out int needed)
        {
            sizes.Add(size);
            needed = 256;
            if (buffer == 0 && size == 0)
            {
                Marshal.SetLastPInvokeError(122);
                return false;
            }
            Assert.NotEqual(0, buffer);
            // Windows rejects a size above 8192 with RPC_X_BAD_STUB_DATA (1783).
            Assert.InRange(size, 64, 8192);
            Assert.Equal(needed, size);
            Marshal.WriteInt32(buffer, 123);
            return true;
        }
        nint result = PawnIoRuntimeAdmission.ReadServiceConfigurationBuffer(Query);
        try
        {
            Assert.Equal(123, Marshal.ReadInt32(result));
            Assert.Equal([0, 256], sizes);
        }
        finally { Marshal.FreeHGlobal(result); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(8193)]
    [InlineData(16384)]
    public void InvalidNativeConfigurationSizeStopsBeforeTheDataQuery(int reported)
    {
        int calls = 0;
        bool Query(nint buffer, int size, out int needed)
        {
            calls++;
            needed = reported;
            Marshal.SetLastPInvokeError(122);
            return false;
        }
        Assert.Throws<InvalidDataException>(() => PawnIoRuntimeAdmission.ReadServiceConfigurationBuffer(Query));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void NativeConfigurationFailurePreservesItsObservedWin32ErrorWithoutRetry()
    {
        int calls = 0;
        bool Query(nint buffer, int size, out int needed)
        {
            calls++;
            needed = 256;
            Marshal.SetLastPInvokeError(buffer == 0 ? 122 : 5);
            return false;
        }
        Win32Exception error = Assert.Throws<Win32Exception>(() => PawnIoRuntimeAdmission.ReadServiceConfigurationBuffer(Query));
        Assert.Equal(5, error.NativeErrorCode);
        Assert.Equal(2, calls);
    }
    [Theory]
    [InlineData(@"\SystemRoot\System32\DriverStore\FileRepository\pawnio.inf_amd64_123\PawnIO.sys")]
    [InlineData(@"\??\C:\Windows\System32\DriverStore\FileRepository\pawnio.inf_amd64_123\PawnIO.sys")]
    [InlineData(@"C:\Windows\System32\DriverStore\FileRepository\pawnio.inf_amd64_123\PawnIO.sys")]
    public void OnlyCanonicalExpectedDriverStorePackagePathIsAdmitted(string path) =>
        Assert.Equal(@"C:\Windows\System32\DriverStore\FileRepository\pawnio.inf_amd64_123\PawnIO.sys",
            PawnIoRuntimeAdmission.NormalizeDriverPath(path, @"C:\Windows"));

    [Theory]
    [InlineData(@"C:\Users\User\PawnIO.sys")]
    [InlineData(@"C:\Windows\System32\drivers\PawnIO.sys")]
    [InlineData(@"C:\Windows\System32\DriverStore\FileRepository\other.inf_amd64_123\PawnIO.sys")]
    [InlineData(@"C:\Windows\System32\DriverStore\FileRepository\pawnio.inf_amd64_123\Other.sys")]
    [InlineData(@"C:\Windows\System32\DriverStore\FileRepository\pawnio.inf_amd64_123\nested\PawnIO.sys")]
    [InlineData(@"C:\Windows\System32\DriverStore\FileRepository\pawnio.inf_amd64_123\..\PawnIO.sys")]
    public void ArbitraryOrAmbiguousBindingPathsAreRejected(string path) =>
        Assert.Throws<InvalidDataException>(() => PawnIoRuntimeAdmission.NormalizeDriverPath(path, @"C:\Windows"));
}
