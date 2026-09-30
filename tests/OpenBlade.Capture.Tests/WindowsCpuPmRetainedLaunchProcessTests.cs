using System.Runtime.InteropServices;
using OpenBlade.Capture;

namespace OpenBlade.Capture.Tests;

// Fake process probes and in-memory files, plus pure native layout checks.
public sealed class WindowsCpuPmRetainedLaunchProcessTests
{
    private const string Image = @"C:\pinned\OpenBlade.Capture.exe";
    private static (CpuPmRetainedLaunchProcess Value, Probe Probe, CpuPmLaunchBindingTests.Files Files) Retain()
    {
        var files = new CpuPmLaunchBindingTests.Files(); files.Data[Image] = [1, 2, 3]; var pin = files.PinFile(Image, 100, true);
        var probe = new Probe(); return (CpuPmRetainedLaunchProcess.Retain(probe, files, Image, pin.Sha256), probe, files);
    }
    [Fact]
    public void RetainedChildIdentityRemainsProvableAfterTerminalWithoutLiveImageQuery()
    {
        var (value, probe, files) = Retain(); var original = value.Identity;
        Assert.Equal(original, value.ReadFreshIdentity()); Assert.Equal(CpuPmChildState.Running, value.ReadState());
        Assert.Throws<InvalidOperationException>(value.ReleaseAfterTerminal); Assert.False(probe.Disposed);
        probe.State = CpuPmChildState.Terminal; probe.Image = null;
        Assert.Equal(original, value.ReadFreshIdentity()); Assert.Equal(CpuPmChildState.Terminal, value.ReadState());
        value.ReleaseAfterTerminal(); value.ReleaseAfterTerminal(); Assert.True(probe.Disposed); Assert.Equal(1, probe.Disposals);
        Assert.False(files.Disposed); Assert.Null(value.ReadFreshIdentity());
    }
    [Theory]
    [InlineData("pid")]
    [InlineData("created")]
    [InlineData("unknown")]
    [InlineData("image")]
    [InlineData("pins")]
    [InlineData("probe-error")]
    public void ProcessReuseDriftOrUnknownBlocksIdentityAndTerminalRelease(string fault)
    {
        var (value, probe, files) = Retain();
        if (fault == "pid") { probe.Pid++; }
        if (fault == "created") { probe.Created++; }
        if (fault == "unknown") { probe.State = CpuPmChildState.Unknown; }
        if (fault == "image") { probe.Image = @"C:\other.exe"; }
        if (fault == "pins") { files.PinsValid = false; }
        if (fault == "probe-error") { probe.Throw = true; }
        Assert.Null(value.ReadFreshIdentity()); Assert.Equal(CpuPmChildState.Unknown, value.ReadState());
        Assert.Throws<InvalidOperationException>(value.ReleaseAfterTerminal); Assert.False(probe.Disposed);
    }
    [Theory]
    [InlineData("terminal")]
    [InlineData("wrong-image")]
    [InlineData("wrong-hash")]
    public void InitialBindingRequiresObservedLiveExactImage(string fault)
    {
        var files = new CpuPmLaunchBindingTests.Files(); files.Data[Image] = [1, 2, 3]; string hash = files.PinFile(Image, 100, true).Sha256;
        var probe = new Probe();
        if (fault == "terminal") { probe.State = CpuPmChildState.Terminal; }
        if (fault == "wrong-image") { probe.Image = @"C:\other.exe"; }
        if (fault == "wrong-hash") { hash = new('F', 64); }
        Assert.Throws<IOException>(() => CpuPmRetainedLaunchProcess.Retain(probe, files, Image, hash)); Assert.True(probe.Disposed);
    }
    [Fact]
    public void ToolhelpLayoutUsesNativePointerAndUnicodeFilenameWidths()
    {
        Assert.Equal(IntPtr.Size == 8 ? 568 : 556, Marshal.SizeOf<WindowsCpuPmProcessProbe.ProcessEntry>());
        Assert.Equal(8, Marshal.OffsetOf<WindowsCpuPmProcessProbe.ProcessEntry>("ProcessId").ToInt32());
        Assert.Equal(IntPtr.Size == 8 ? 16 : 12, Marshal.OffsetOf<WindowsCpuPmProcessProbe.ProcessEntry>("DefaultHeap").ToInt32());
        Assert.Equal(IntPtr.Size == 8 ? 32 : 24, Marshal.OffsetOf<WindowsCpuPmProcessProbe.ProcessEntry>("ParentProcessId").ToInt32());
        Assert.Equal(IntPtr.Size == 8 ? 44 : 36, Marshal.OffsetOf<WindowsCpuPmProcessProbe.ProcessEntry>("Executable").ToInt32());
    }
    private sealed class Probe : ICpuPmRetainedProcessProbe
    {
        internal int Pid = 32; internal long Created = 200; internal string? Image = WindowsCpuPmRetainedLaunchProcessTests.Image;
        internal CpuPmChildState State = CpuPmChildState.Running; internal bool Disposed, Throw; internal int Disposals;
        public CpuPmProcessObservation Read(bool includeLiveImage) => Throw ? throw new IOException() : new(Pid, Created, State, State == CpuPmChildState.Running && includeLiveImage ? Image : null);
        public void Dispose() { Disposed = true; Disposals++; }
    }
}
