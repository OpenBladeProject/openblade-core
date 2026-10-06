using System.Diagnostics;
using System.Runtime.InteropServices;
using OpenBlade.Capture;

namespace OpenBlade.Capture.Tests;

// Fake process probes and in-memory files, plus pure native layout checks.
public sealed class WindowsCpuPmRetainedLaunchProcessTests
{
    private const string Image = @"C:\pinned\OpenBlade.Capture.exe";
    [Fact]
    public void ActualQuickExitCanBeRetainedForVoltageCompletion()
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) { return; }
        string image = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        using var files = new NativeImageFiles(image);
        var pin = files.PinFile(image, CpuPmLaunchSchema.MaximumFileBytes, true);
        using var actual = Process.Start(new ProcessStartInfo(image, "/d /c exit 0") { UseShellExecute = false, CreateNoWindow = true })!;
        Assert.True(actual.WaitForExit(10000)); Assert.Equal(0, actual.ExitCode);
        var value = CpuPmRetainedLaunchProcess.RetainForCompletion(WindowsCpuPmProcessProbe.FromLaunchedHandle(actual.SafeHandle), files, image, pin.Sha256, pin);
        Assert.Equal(actual.Id, value.Identity.ProcessId);
        Assert.Equal(actual.StartTime.ToUniversalTime().ToFileTimeUtc(), value.Identity.CreationFileTimeUtc);
        Assert.Equal(CpuPmChildState.Terminal, value.ReadState()); value.ReleaseAfterTerminal();
    }
    private static (CpuPmRetainedLaunchProcess Value, Probe Probe, CpuPmLaunchBindingTests.Files Files) Retain()
    {
        var files = new CpuPmLaunchBindingTests.Files(); files.Data[Image] = [1, 2, 3]; var pin = files.PinFile(Image, 100, true);
        var probe = new Probe(); return (CpuPmRetainedLaunchProcess.Retain(probe, files, Image, pin.Sha256), probe, files);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VoltageRetentionAdmitsTerminalFirstObservationOrExitDuringGuard(bool alreadyTerminal)
    {
        var files = new CpuPmLaunchBindingTests.Files(); files.Data[Image] = [1, 2, 3];
        var pin = files.PinFile(Image, 100, true); var probe = new Probe();
        if (alreadyTerminal) { probe.State = CpuPmChildState.Terminal; }
        else { probe.AfterRead = () => probe.State = CpuPmChildState.Terminal; }
        var child = CpuPmRetainedLaunchProcess.RetainForCompletion(probe, files, Image, pin.Sha256, pin);
        Assert.Equal(CpuPmChildState.Terminal, child.ReadState()); Assert.Equal(child.Identity, child.ReadFreshIdentity());
        child.ReleaseAfterTerminal(); Assert.False(files.Disposed);
    }
    [Theory]
    [InlineData("pid")]
    [InlineData("birth")]
    [InlineData("image")]
    [InlineData("terminal-image")]
    [InlineData("unknown")]
    [InlineData("pins")]
    [InlineData("reviewed-hash")]
    [InlineData("prelaunch-pin")]
    public void VoltageRetentionRejectsUnknownDriftAndUnreviewedPins(string fault)
    {
        var files = new CpuPmLaunchBindingTests.Files(); files.Data[Image] = [1, 2, 3];
        var pin = files.PinFile(Image, 100, true); var probe = new Probe(); string hash = pin.Sha256;
        if (fault == "pid") { probe.AfterRead = () => probe.Pid++; }
        if (fault == "birth") { probe.AfterRead = () => probe.Created++; }
        if (fault is "image" or "terminal-image") { probe.Image = @"C:\wrong.exe"; }
        if (fault == "terminal-image") { probe.ExitDuringRead = true; }
        if (fault == "unknown") { probe.State = CpuPmChildState.Unknown; }
        if (fault == "pins") { files.OnAssertPins = () => files.PinsValid = false; }
        if (fault == "reviewed-hash") { hash = new('F', 64); }
        if (fault == "prelaunch-pin") { files.Data[Image] = [4, 5, 6]; hash = files.PinFile(Image, 100, true).Sha256; }
        Assert.Throws<IOException>(() => CpuPmRetainedLaunchProcess.RetainForCompletion(probe, files, Image, hash, pin));
        Assert.True(probe.Disposed); Assert.False(files.Disposed);
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
        internal Action? AfterRead; internal bool ExitDuringRead;
        public CpuPmProcessObservation Read(bool includeLiveImage)
        {
            if (Throw) { throw new IOException(); }
            var value = new CpuPmProcessObservation(Pid, Created, ExitDuringRead ? CpuPmChildState.Terminal : State,
                State == CpuPmChildState.Running && includeLiveImage ? Image : null);
            AfterRead?.Invoke(); return value;
        }
        public void Dispose() { Disposed = true; Disposals++; }
    }
    // Real read/share-read executable pin for harmless native process tests;
    // private reviewed package ACL admission is tested separately.
    internal sealed class NativeImageFiles : ICpuPmLaunchFiles, ICpuPmLaunchFilePin
    {
        private readonly string image;
        private readonly FileStream stream;
        internal NativeImageFiles(string image)
        {
            this.image = image; stream = new FileStream(image, FileMode.Open, FileAccess.Read, FileShare.Read);
            Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
        }
        public long Length => stream.Length;
        public string Sha256 { get; }
        public ICpuPmLaunchFilePin PinFile(string path, long maximumBytes, bool privateAcl)
        { Assert.Equal(image, path); Assert.True(Length <= maximumBytes); AssertAllPins(); return this; }
        public void AssertAllPins() => Assert.False(stream.SafeFileHandle.IsClosed);
        public void Dispose() => stream.Dispose();
        public byte[] ReadBounded(int maximumBytes) => throw new NotSupportedException();
        public void PinDirectory(string path, bool privateAcl, bool protectedAcl) => throw new NotSupportedException();
        public IEnumerable<CpuPmLaunchEntry> Enumerate(string path) => throw new NotSupportedException();
    }
}
