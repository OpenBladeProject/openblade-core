using System.Runtime.InteropServices;
using OpenBlade.Capture;

namespace OpenBlade.Capture.Tests;

// ABI/layout/early-validation only; never creates a native pipe or process handle.
public sealed class WindowsCpuPmAuthorityListenerTests
{
    [Fact]
    public void NativeSecurityAttributesUseDocumentedPointerAlignmentAndWin32Bool()
    {
        Assert.Equal(IntPtr.Size == 8 ? 24 : 12, Marshal.SizeOf<WindowsCpuPmAuthorityListener.SecurityAttributes>());
        Assert.Equal(0, Marshal.OffsetOf<WindowsCpuPmAuthorityListener.SecurityAttributes>("Length").ToInt32());
        Assert.Equal(IntPtr.Size, Marshal.OffsetOf<WindowsCpuPmAuthorityListener.SecurityAttributes>("Descriptor").ToInt32());
        Assert.Equal(IntPtr.Size * 2, Marshal.OffsetOf<WindowsCpuPmAuthorityListener.SecurityAttributes>("InheritHandle").ToInt32());
        Assert.Equal(0x40080003u, WindowsCpuPmAuthorityListener.OpenMode);
        Assert.Equal(8u, WindowsCpuPmAuthorityListener.PipeMode);
        Assert.Equal("D:P(A;;GA;;;SY)(A;;GA;;;BA)", WindowsCpuPmAuthorityListener.ProtectedAcl);
    }
    [Fact]
    public void InvalidIdentityAndMissingClockFailBeforeNativeAllocation()
    {
        Assert.Throws<ArgumentException>(() => WindowsCpuPmAuthorityListener.Create(Guid.Empty, Guid.NewGuid(), new Clock()));
        Assert.Throws<ArgumentException>(() => WindowsCpuPmAuthorityListener.Create(Guid.NewGuid(), Guid.Empty, new Clock()));
        Assert.Throws<ArgumentNullException>(() => WindowsCpuPmAuthorityListener.Create(Guid.NewGuid(), Guid.NewGuid(), null!));
    }
    private sealed class Clock : ICpuPmAuthorityClock { public TimeSpan Now => TimeSpan.Zero; }
}
