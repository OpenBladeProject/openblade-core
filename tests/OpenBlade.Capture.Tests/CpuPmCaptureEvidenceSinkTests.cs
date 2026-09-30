using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using OpenBlade.Capture;

namespace OpenBlade.Capture.Tests;

public sealed class CpuPmCaptureEvidenceSinkTests
{
    [Fact]
    public void MissingAuthenticatedRunDeniesBeforeFilesystemOrNativeCalls()
    {
        var files = new Files(); var sink = new CpuPmCaptureEvidenceSink(null, files);
        Assert.False(sink.BeginFreshRun(Guid.NewGuid())); Assert.Equal(0, files.Begins);
        Assert.Throws<InvalidOperationException>(() => new WindowsCpuPmCaptureEvidenceFiles(null));
    }
    [Fact]
    public void ExactContiguousTenSlotsBoundIncludesReportAndCannotReuse()
    {
        var run = new Run(); var files = new Files(); var sink = new CpuPmCaptureEvidenceSink(run, files);
        Assert.True(sink.BeginFreshRun(run.RunId));
        for (int i = 0; i < 3; i++)
        {
            Assert.True(sink.TryWriteImmutable(File($"sample-{i:00}.reply.bin", 0xD54)));
            Assert.True(sink.TryWriteImmutable(File($"sample-{i:00}.payload.bin", 0xD50)));
            Assert.True(sink.TryWriteImmutable(File($"sample-{i:00}.json", 8192)));
        }
        Assert.True(sink.TryWriteImmutable(File("capture-result.json", 32768)));
        Assert.Equal(10, sink.ReservedSlots); Assert.Equal(77804, sink.ReservedBytes);
        Assert.False(sink.TryWriteImmutable(File("capture-result.json", 1)));
        Assert.False(sink.BeginFreshRun(run.RunId)); Assert.Equal(10, files.Stored.Count);
    }
    [Theory]
    [InlineData("../sample-00.reply.bin", 0xD54)]
    [InlineData("sample-01.reply.bin", 0xD54)]
    [InlineData("sample-00.payload.bin", 0xD50)]
    [InlineData("sample-00.reply.bin", 0xD50)]
    [InlineData("capture-result.json", 32769)]
    public void InvalidSlotOrGeometryIrreversiblyRefuses(string name, int size)
    {
        var run = new Run(); var files = new Files(); var sink = new CpuPmCaptureEvidenceSink(run, files);
        Assert.True(sink.BeginFreshRun(run.RunId)); Assert.False(sink.TryWriteImmutable(File(name, size)));
        Assert.False(sink.TryWriteImmutable(File("sample-00.reply.bin", 0xD54))); Assert.Empty(files.Stored);
    }
    [Fact]
    public void PartialSampleCannotBeFinalizedAsReport()
    {
        var run = new Run(); var files = new Files(); var sink = new CpuPmCaptureEvidenceSink(run, files);
        Assert.True(sink.BeginFreshRun(run.RunId)); Assert.True(sink.TryWriteImmutable(File("sample-00.reply.bin", 0xD54)));
        Assert.False(sink.TryWriteImmutable(File("capture-result.json", 1))); Assert.Single(files.Stored);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void IncompleteReturnedBatchMayReportOnlyAtWholeSampleBoundary(int count)
    {
        var run = new Run(); var files = new Files(); var sink = new CpuPmCaptureEvidenceSink(run, files);
        Assert.True(sink.BeginFreshRun(run.RunId));
        for (int i = 0; i < count; i++)
        {
            Assert.True(sink.TryWriteImmutable(File($"sample-{i:00}.reply.bin", 0xD54)));
            Assert.True(sink.TryWriteImmutable(File($"sample-{i:00}.payload.bin", 0xD50)));
            Assert.True(sink.TryWriteImmutable(File($"sample-{i:00}.json", 1)));
        }
        Assert.True(sink.TryWriteImmutable(File("capture-result.json", 1))); Assert.Equal(count * 3 + 1, sink.ReservedSlots);
    }
    [Theory]
    [InlineData("collision")]
    [InlineData("short-write")]
    [InlineData("hash-readback")]
    [InlineData("reparse")]
    [InlineData("extra-entry")]
    [InlineData("rename")]
    public void AnyFilesystemFailurePreservesReservedPartialAndNeverRetries(string failure)
    {
        var run = new Run(); var files = new Files { Failure = failure }; var sink = new CpuPmCaptureEvidenceSink(run, files);
        Assert.True(sink.BeginFreshRun(run.RunId)); Assert.False(sink.TryWriteImmutable(File("sample-00.reply.bin", 0xD54)));
        Assert.Equal(1, sink.ReservedSlots); Assert.Equal(0xD54, sink.ReservedBytes); Assert.Equal(1, files.Attempts);
        Assert.False(sink.TryWriteImmutable(File("sample-00.reply.bin", 0xD54))); Assert.Equal(1, files.Attempts);
        Assert.Equal(failure, files.RetainedPartial);
    }
    [Fact]
    public void PinLossAndUnexpectedCallbackFailureAreStickyAndNoCancellationReleaseExists()
    {
        var run = new Run(); var files = new Files(); var sink = new CpuPmCaptureEvidenceSink(run, files);
        Assert.True(sink.BeginFreshRun(run.RunId)); run.PinLost = true;
        Assert.False(sink.TryWriteImmutable(File("capture-result.json", 1))); run.PinLost = false;
        Assert.False(sink.TryWriteImmutable(File("capture-result.json", 1))); Assert.Equal(0, files.Attempts);
        Assert.Throws<InvalidOperationException>(() => sink.ReleaseAfterWorkerTerminal()); Assert.Equal(0, files.Releases);
        run.Terminal = true; sink.ReleaseAfterWorkerTerminal(); Assert.Equal(1, files.Releases);
    }
    [Fact]
    public void MarkerFailureOrMismatchedRunCannotBeRetried()
    {
        var run = new Run(); var files = new Files { MarkerFailure = true }; var sink = new CpuPmCaptureEvidenceSink(run, files);
        Assert.False(sink.BeginFreshRun(run.RunId)); files.MarkerFailure = false;
        Assert.False(sink.BeginFreshRun(run.RunId)); Assert.Equal(1, files.Begins);
        var mismatch = new CpuPmCaptureEvidenceSink(run, files); Assert.False(mismatch.BeginFreshRun(Guid.NewGuid()));
        Assert.False(mismatch.BeginFreshRun(run.RunId)); Assert.Equal(1, files.Begins);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectedBeginStillReleasesConstructedPinsOnceAfterWorkerTerminal(bool initialPinFailure)
    {
        var run = new Run(); var files = new Files(); var sink = new CpuPmCaptureEvidenceSink(run, files);
        run.PinLost = initialPinFailure;
        Assert.False(sink.BeginFreshRun(initialPinFailure ? run.RunId : Guid.NewGuid()));
        Assert.Equal(0, files.Begins);
        Assert.False(sink.TryWriteImmutable(File("capture-result.json", 1)));
        Assert.Throws<InvalidOperationException>(() => sink.ReleaseAfterWorkerTerminal());
        Assert.Equal(0, files.Releases);
        run.Terminal = true;
        sink.ReleaseAfterWorkerTerminal(); sink.ReleaseAfterWorkerTerminal();
        Assert.Equal(1, files.Releases); Assert.Equal(0, files.Attempts);
        run.PinLost = false;
        Assert.False(sink.BeginFreshRun(run.RunId));
    }
    [Fact]
    public void AuthenticatedRunIdentityDriftPermanentlyRefusesWritesAndRelease()
    {
        var run = new Run(); Guid original = run.RunId; var files = new Files(); var sink = new CpuPmCaptureEvidenceSink(run, files);
        Assert.True(sink.BeginFreshRun(original)); run.RunId = Guid.NewGuid();
        Assert.False(sink.TryWriteImmutable(File("capture-result.json", 1))); Assert.Equal(0, files.Attempts);
        run.Terminal = true; Assert.Throws<InvalidOperationException>(() => sink.ReleaseAfterWorkerTerminal());
        Assert.Equal(0, files.Releases); run.RunId = original;
        Assert.False(sink.TryWriteImmutable(File("capture-result.json", 1)));
    }
    [Fact]
    public void NativeAbiAndFixedNamesArePurelyInspectedWithoutInvokingBindings()
    {
        Assert.Equal(52, Marshal.SizeOf<WindowsCpuPmCaptureEvidenceFiles.FileInformation>());
        Assert.Equal(40, Marshal.OffsetOf<WindowsCpuPmCaptureEvidenceFiles.FileInformation>("NumberOfLinks").ToInt32());
        var method = typeof(WindowsCpuPmCaptureEvidenceFiles).GetMethod("MoveFileExW", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.Equal(typeof(bool), method.ReturnType);
        Assert.Equal([typeof(string), typeof(string), typeof(uint)], method.GetParameters().Select(p => p.ParameterType).ToArray());
        Assert.Equal(CharSet.Unicode, method.GetCustomAttribute<DllImportAttribute>()!.CharSet);
        Assert.Equal(8u, WindowsCpuPmCaptureEvidenceFiles.MoveWriteThrough);
        Assert.Equal(1u, WindowsCpuPmCaptureEvidenceFiles.CreateNew);
        Assert.False(WindowsCpuPmCaptureEvidenceFiles.AllowedName("sample-03.json"));
        Assert.False(WindowsCpuPmCaptureEvidenceFiles.AllowedName("sample-00.json.partial"));
    }
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void RunRootRequiresProtectedAclWhileDescendantsMayInheritPrivateAcl(bool requireProtected, bool protectedAcl, bool expected)
    {
        Assert.Equal(expected, WindowsCpuPmCaptureEvidenceFiles.IsPrivateDescriptor(Descriptor(protectedAcl), requireProtected));
    }
    [Fact]
    public void AclRejectsPublicTrusteeMissingSystemNullAclAndMalformedDescriptor()
    {
        Assert.False(WindowsCpuPmCaptureEvidenceFiles.IsPrivateDescriptor(Descriptor(true, publicTrustee: true), true));
        Assert.False(WindowsCpuPmCaptureEvidenceFiles.IsPrivateDescriptor(Descriptor(true, omitSystem: true), true));
        Assert.False(WindowsCpuPmCaptureEvidenceFiles.IsPrivateDescriptor([1, 2, 3], true));
        var raw = new RawSecurityDescriptor(ControlFlags.DiscretionaryAclProtected, new SecurityIdentifier("S-1-5-18"), null, null, null);
        byte[] bytes = new byte[raw.BinaryLength]; raw.GetBinaryForm(bytes, 0);
        Assert.False(WindowsCpuPmCaptureEvidenceFiles.IsPrivateDescriptor(bytes, true));
    }
    private static byte[] Descriptor(bool protect, bool publicTrustee = false, bool omitSystem = false)
    {
        var acl = new RawAcl(2, 3);
        if (!omitSystem) { acl.InsertAce(acl.Count, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, 0x1F01FF, new SecurityIdentifier("S-1-5-18"), false, null)); }
        acl.InsertAce(acl.Count, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, 0x1F01FF, new SecurityIdentifier("S-1-5-32-544"), false, null));
        if (publicTrustee) { acl.InsertAce(acl.Count, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, 0x1F01FF, new SecurityIdentifier("S-1-1-0"), false, null)); }
        var descriptor = new RawSecurityDescriptor(ControlFlags.DiscretionaryAclPresent | (protect ? ControlFlags.DiscretionaryAclProtected : 0), new SecurityIdentifier("S-1-5-18"), null, null, acl);
        byte[] bytes = new byte[descriptor.BinaryLength]; descriptor.GetBinaryForm(bytes, 0); return bytes;
    }
    private static CpuPmEvidenceFile File(string name, int size) => new(name, new byte[size]);
    private sealed class Run : ICpuPmAuthenticatedCaptureRun
    {
        public Guid RunId { get; set; } = Guid.NewGuid();
        internal bool PinLost, Terminal;
        public void AssertPinnedRun() { if (PinLost) { throw new FormatException("lost trusted pin"); } }
        public void AssertWorkerTerminal() { if (!Terminal) { throw new InvalidOperationException("Child pending"); } }
    }
    private sealed class Files : ICpuPmCaptureEvidenceFiles
    {
        internal int Begins, Attempts, Releases;
        internal bool MarkerFailure;
        internal string? Failure, RetainedPartial;
        internal List<string> Stored = [];
        public void BeginFreshRun(Guid id) { Begins++; if (MarkerFailure) { throw new IOException(); } }
        public void StoreImmutable(string name, byte[] bytes, string sha256)
        {
            Attempts++; if (Failure is not null) { RetainedPartial = Failure; throw new FormatException(Failure); }
            Stored.Add(name);
        }
        public void ReleaseAfterWorkerTerminal() { Releases++; }
    }
}
