using System.ComponentModel;
using System.Runtime.InteropServices;

namespace OpenBlade.Platform.Tests;

public sealed class PawnIoRuntimeAdmissionTests
{
    [Theory]
    [InlineData("0000")]
    [InlineData("0001")]
    public void ReinstallSelectsOnlyPresentInstanceAndIgnoresDisconnectedRegistrations(string active)
    {
        List<string> queried = [];
        uint Locate(string id, out uint node)
        {
            queried.Add(id);
            node = id.EndsWith(active, StringComparison.Ordinal) ? 42u : 0u;
            return node == 42 ? 0u : 0x0Du;
        }
        var selected = PawnIoRuntimeAdmission.SelectPresentDevice(["0000", "0001"], Locate, default);
        Assert.Equal((active, 42u), selected);
        Assert.Equal([@"ROOT\PawnIO\0000", @"ROOT\PawnIO\0001"], queried);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(2, 2)]
    public void MissingOrMultiplePresentDevicesRemainRejected(int registered, int present)
    {
        int calls = 0;
        uint Locate(string id, out uint node)
        {
            node = (uint)++calls;
            return calls <= present ? 0u : 0x0Du;
        }
        string[] instances = Enumerable.Range(0, registered).Select(i => i.ToString("D4")).ToArray();
        var error = Assert.Throws<InvalidDataException>(() =>
            PawnIoRuntimeAdmission.SelectPresentDevice(instances, Locate, default));
        Assert.Contains($"present: {present}; registered: {registered}", error.Message);
        Assert.Equal(registered, calls);
    }

    [Theory]
    [InlineData(0x05u)]
    [InlineData(0x17u)]
    [InlineData(0x33u)]
    public void PresenceQueryErrorsCannotBeTreatedAsDisconnectedDevices(uint failure)
    {
        int calls = 0;
        uint Locate(string id, out uint node)
        {
            node = (uint)++calls;
            return calls == 1 ? 0u : failure;
        }
        var error = Assert.Throws<InvalidDataException>(() =>
            PawnIoRuntimeAdmission.SelectPresentDevice(["0000", "0001", "0002"], Locate, default));
        Assert.Contains($"ConfigManager {failure}", error.Message);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CanceledPresenceInspectionCannotPublishASelectedDevice(bool canceledBeforeQuery)
    {
        using CancellationTokenSource cancellation = new();
        int calls = 0;
        uint Locate(string id, out uint node)
        {
            calls++;
            node = 42;
            cancellation.Cancel();
            return 0;
        }
        if (canceledBeforeQuery) { cancellation.Cancel(); }
        Assert.Throws<OperationCanceledException>(() =>
            PawnIoRuntimeAdmission.SelectPresentDevice(["0000"], Locate, cancellation.Token));
        Assert.Equal(canceledBeforeQuery ? 0 : 1, calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ServiceAbsentWithOnlyDisconnectedResiduePermitsFreshInstallation(int registered)
    {
        int calls = 0;
        uint Locate(string id, out uint node) { calls++; node = 0; return 0x0D; }
        string[] instances = Enumerable.Range(0, registered).Select(i => i.ToString("D4")).ToArray();
        PawnIoRuntimeAdmission.VerifyNoInstallation(false, instances, Locate, default);
        Assert.Equal(registered, calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ServiceAbsentStillPreservesRealPartialInstallEvidence(bool uninstallRegistration)
    {
        int calls = 0;
        uint Locate(string id, out uint node) { calls++; node = 42; return 0; }
        Assert.Throws<InvalidOperationException>(() =>
            PawnIoRuntimeAdmission.VerifyNoInstallation(uninstallRegistration, ["0000"], Locate, default));
        Assert.Equal(uninstallRegistration ? 0 : 1, calls);
    }

    [Fact]
    public void ServiceAbsentCannotTurnFailedPresenceQueryIntoPermissionToInstall()
    {
        uint Locate(string id, out uint node) { node = 0; return 0x33; }
        Assert.Throws<InvalidDataException>(() =>
            PawnIoRuntimeAdmission.VerifyNoInstallation(false, ["0000"], Locate, default));
    }

    [Fact]
    public void InspectionValidatesSelectedLiveBindingAndQueriesThatSameDevnode()
    {
        List<string> steps = [];
        uint Locate(string id, out uint node)
        {
            node = id.EndsWith("0001", StringComparison.Ordinal) ? 42u : 0u;
            return node == 42 ? 0u : 0x0Du;
        }
        PawnIoRuntimeAdmission.InspectDevice(["0000", "0001"], Locate,
            selected => { Assert.Equal("0001", selected); steps.Add("binding"); },
            node => { Assert.Equal(42u, node); steps.Add("status"); return 0; }, default);
        Assert.Equal(["binding", "status"], steps);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SelectedBindingOrStatusFailureStopsWithoutTryingAnotherInstance(bool bindingFails)
    {
        int bindings = 0, statuses = 0;
        uint Locate(string id, out uint node) { node = 42; return 0; }
        var error = Assert.Throws<InvalidDataException>(() =>
            PawnIoRuntimeAdmission.InspectDevice(["0001"], Locate,
                _ => { bindings++; if (bindingFails) { throw new InvalidDataException("untrusted-binding"); } },
                _ => { statuses++; return 0x33; }, default));
        Assert.Equal(1, bindings);
        Assert.Equal(bindingFails ? 0 : 1, statuses);
        Assert.Contains(bindingFails ? "untrusted-binding" : "ConfigManager 51", error.Message);
    }

    [Fact]
    public void CancellationAfterBindingCheckStopsBeforeDeviceStatusRead()
    {
        using CancellationTokenSource cancellation = new();
        int statuses = 0;
        uint Locate(string id, out uint node) { node = 42; return 0; }
        Assert.Throws<OperationCanceledException>(() =>
            PawnIoRuntimeAdmission.InspectDevice(["0001"], Locate, _ => cancellation.Cancel(),
                _ => { statuses++; return 0; }, cancellation.Token));
        Assert.Equal(0, statuses);
    }

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
