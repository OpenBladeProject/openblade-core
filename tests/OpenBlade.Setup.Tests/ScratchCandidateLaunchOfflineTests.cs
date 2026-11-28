#if OPENBLADE_CANDIDATE_RECEIPT_FIXTURE
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text.Json;
using OpenBlade.Deployment;
using OpenBlade.DeploymentBroker;

namespace OpenBlade.Setup.Tests;

// Data-only private NTFS tests. No child, IPC, new certificate or installed path.
public sealed class ScratchCandidateLaunchOfflineTests
{
    internal static byte[] VectorImage()
    {
        using var v = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "scratch-native-receiver-v2.json")));
        return Convert.FromHexString(v.RootElement.GetProperty("imageHex").GetString()!);
    }

    [Fact]
    public void RetainedNativeBytesExcludeExistingAndNewWritersAndReleaseWithoutAclChanges()
    {
        using var fixture = new NativeFiles();
        var info = new DirectoryInfo(fixture.Directory);
        byte[] before = info.GetAccessControl().GetSecurityDescriptorBinaryForm();
        using (var writer = new FileStream(fixture.Image, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            Assert.ThrowsAny<IOException>(() => ScratchReceiverClosure.Retain(fixture.Storage));
        }
        using (var closure = ScratchReceiverClosure.Retain(fixture.Storage))
        {
            Assert.Equal(fixture.Image, closure.ImagePath);
            Assert.ThrowsAny<IOException>(() => File.WriteAllBytes(fixture.Image, [1]));
            Assert.ThrowsAny<IOException>(() => File.Delete(fixture.Image));
            Assert.ThrowsAny<IOException>(() => File.Move(fixture.Image, fixture.Image + ".replacement"));
            Assert.Equal(before, info.GetAccessControl().GetSecurityDescriptorBinaryForm());
            // No ACL authority is installed or later accepted. Owner cleanup
            // rights survive interrupted disposal; only ordinary handles remain.
            var security = info.GetAccessControl();
            info.SetAccessControl(security);
            Assert.Equal(32, closure.RequireUnchanged().Length);
        }
        Assert.Equal(before, info.GetAccessControl().GetSecurityDescriptorBinaryForm());
        File.WriteAllBytes(fixture.Image, [1]);
    }

    [Theory]
    [InlineData("unexpected.dll")]
    [InlineData("OpenBlade.CandidateReceipt.Scratch.runtimeconfig.dev.json")]
    [InlineData("OpenBlade.CandidateReceipt.Scratch.deps.json")]
    public void ObsoleteOrUnexpectedMembersRefuseWithoutSealingNamespace(string name)
    {
        using var fixture = new NativeFiles();
        using (var closure = ScratchReceiverClosure.Retain(fixture.Storage))
        {
            File.WriteAllText(Path.Combine(fixture.Directory, name), "data only");
            Assert.Throws<InvalidDataException>(() => closure.RequireUnchanged());
        }
        Assert.Throws<InvalidDataException>(() => ScratchReceiverClosure.Retain(fixture.Storage));
    }

    [Fact]
    public void ChangedNativeCodeChangesAuthenticatedEvidence()
    {
        using var fixture = new NativeFiles();
        using var current = Process.GetCurrentProcess();
        byte[] issued;
        using (var closure = ScratchReceiverClosure.Retain(fixture.Storage))
        {
            issued = ScratchCandidateLaunch.Encode("active"u8.ToArray(), current, current, closure, new byte[32]);
        }
        byte[] bytes = File.ReadAllBytes(fixture.Image);
        bytes[440] ^= 1;
        File.WriteAllBytes(fixture.Image, bytes);
        using var changed = ScratchReceiverClosure.Retain(fixture.Storage);
        Assert.NotEqual(issued, ScratchCandidateLaunch.Encode("active"u8.ToArray(), current, current, changed, new byte[32]));
    }

    [Fact]
    public void MissingNativeImageAndGenericFddApphostRefuse()
    {
        using var fixture = new NativeFiles();
        File.Delete(fixture.Image);
        Assert.ThrowsAny<IOException>(() => ScratchReceiverClosure.Retain(fixture.Storage));
        byte[] generic = VectorImage();
        generic[264] = 0; generic[265] = 0;
        Assert.False(ScratchReceiverClosure.AcceptNativeImage(generic));
        File.WriteAllBytes(fixture.Image, generic);
        Assert.Throws<InvalidDataException>(() => ScratchReceiverClosure.Retain(fixture.Storage));
    }

    [Fact]
    public void WrongLiveImageCannotPublishOrChangeActiveState()
    {
        using var fixture = new NativeFiles();
        Guid request = Guid.NewGuid();
        string stage = Path.Combine(fixture.Storage.Root, "staging", request.ToString("N"));
        Directory.CreateDirectory(stage);
        string active = Path.Combine(fixture.Storage.Root, "unresolved-deployment.json");
        File.WriteAllText(active, "original");
        using var closure = ScratchReceiverClosure.Retain(fixture.Storage);
        using var current = Process.GetCurrentProcess();
        Assert.Throws<InvalidDataException>(() => ScratchCandidateLaunch.Bind(fixture.Storage, request, current, closure, new byte[32]));
        Assert.Equal("original", File.ReadAllText(active));
        Assert.False(File.Exists(Path.Combine(stage, ScratchCandidateLaunch.EvidenceName)));
    }

    [Fact]
    public void PolicyRefusalsAndExactChildEnvironmentAreBounded()
    {
        Assert.True(ScratchNativeReceiverLaunch.AcceptPolicies(1, 1, 4));
        foreach (uint value in new uint[] { 0, 2, 3, 5, uint.MaxValue })
        {
            Assert.False(ScratchNativeReceiverLaunch.AcceptPolicies(value, 1, 4));
            Assert.False(ScratchNativeReceiverLaunch.AcceptPolicies(1, value, 4));
            Assert.False(ScratchNativeReceiverLaunch.AcceptPolicies(1, 1, value));
        }
        string environment = ScratchNativeReceiverLaunch.CreateEnvironment(@"C:\private\receiver", @"C:\private\child.data");
        Assert.EndsWith("\0\0", environment, StringComparison.Ordinal);
        Assert.Equal(["LOCALAPPDATA", "SystemRoot", "TEMP", "TMP"], environment.Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(x => x[..x.IndexOf('=')]));
        Assert.Contains("TEMP=C:\\private\\child.data\0TMP=C:\\private\\child.data\0\0", environment, StringComparison.Ordinal);
        foreach (string data in new[] { @"C:\private\receiver", @"C:\private\receiver\temp", @"C:\private\receiver\..\receiver" })
        {
            Assert.Throws<InvalidDataException>(() => ScratchNativeReceiverLaunch.CreateEnvironment(@"C:\private\receiver", data));
        }
    }

    [Fact]
    public void ConcurrentExitDuringTerminationRequiresOriginalHandleReadback()
    {
        var observations = new Queue<uint>([258, 0]);
        List<uint> waits = [];
        int submissions = 0;
        ScratchNativeReceiverLaunch.Stop(timeout => { waits.Add(timeout); return observations.Dequeue(); },
            () => { submissions++; return 5; });
        Assert.Equal([0u, 0u], waits);
        Assert.Equal(1, submissions);
        Assert.Empty(observations);
    }

    [Fact]
    public void UnconfirmedTerminationFailureRemainsVisibleWithoutReplay()
    {
        foreach (uint readback in new[] { 258u, uint.MaxValue })
        {
            var observations = new Queue<uint>([258, readback]);
            int submissions = 0;
            var error = Assert.Throws<System.ComponentModel.Win32Exception>(() => ScratchNativeReceiverLaunch.Stop(
                _ => observations.Dequeue(), () => { submissions++; return 5; }));
            Assert.Equal(5, error.NativeErrorCode);
            Assert.Equal(1, submissions);
            Assert.Empty(observations);
        }
        List<uint> waits = [];
        Assert.Throws<IOException>(() => ScratchNativeReceiverLaunch.Stop(
            timeout => { waits.Add(timeout); return 258; }, () => null));
        Assert.Equal([0u, 5000u], waits);
    }

    [Fact]
    public void ConfirmedExitAndFailedInitialObservationNeverSubmitTermination()
    {
        ScratchNativeReceiverLaunch.Stop(_ => 0, () => throw new InvalidOperationException("must not terminate"));
        Assert.Throws<IOException>(() => ScratchNativeReceiverLaunch.Stop(
            _ => uint.MaxValue, () => throw new InvalidOperationException("must not terminate")));
    }

    [Fact]
    public void CleanupAttemptsEveryHandleAfterFailure()
    {
        List<int> attempted = [];
        var error = Assert.Throws<AggregateException>(() => ScratchReceiverClosure.DisposeRetainedResources([
            new Cleanup(() => { attempted.Add(1); throw new IOException(); }), new Cleanup(() => attempted.Add(2))]));
        Assert.Single(error.InnerExceptions);
        Assert.Equal([1, 2], attempted);
    }
    private sealed class Cleanup(Action action) : IDisposable { public void Dispose() => action(); }
    internal sealed class NativeFiles : IDisposable
    {
        internal WindowsCandidateReceiptStorage Storage { get; } = WindowsCandidateReceiptStorage.CreateScratch();
        internal string Directory => Path.GetDirectoryName(ScratchCandidateLaunch.ReceiverPath(Storage))!;
        internal string Image => ScratchCandidateLaunch.ReceiverPath(Storage);
        internal NativeFiles() { System.IO.Directory.CreateDirectory(Directory); File.WriteAllBytes(Image, VectorImage()); }
        public void Dispose() => System.IO.Directory.Delete(Storage.Root, recursive: true);
    }
}
#endif
