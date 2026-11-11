#if OPENBLADE_CANDIDATE_RECEIPT_FIXTURE
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using OpenBlade.Deployment;
using OpenBlade.DeploymentBroker;

namespace OpenBlade.Setup.Tests;

// Scratch files and the existing test process only. Never launches or opens IPC.
public sealed class ScratchCandidateLaunchOfflineTests
{
    [Fact]
    public void IssuerRefusesCurrentProcessAtWrongImageWithoutPublishingOrMutatingReceipt()
    {
        using var fixture = new ClosureFiles();
        Guid request = Guid.NewGuid();
        string stage = Path.Combine(fixture.Storage.Root, "staging", request.ToString("N"));
        Directory.CreateDirectory(stage);
        string pending = Path.Combine(stage, WindowsCandidatePackageReceipt.PendingName);
        string active = Path.Combine(fixture.Storage.Root, "unresolved-deployment.json");
        File.WriteAllText(pending, "original pending receipt");
        File.WriteAllText(active, "original in-flight state");
        byte[] pendingBefore = File.ReadAllBytes(pending);
        byte[] activeBefore = File.ReadAllBytes(active);
        string[] filesBefore = Directory.GetFiles(stage);
        using var retained = ScratchReceiverClosure.Retain(fixture.Storage);
        using var current = Process.GetCurrentProcess();

        var error = Assert.Throws<InvalidDataException>(() => ScratchCandidateLaunch.Bind(
            fixture.Storage, request, current, retained, new byte[ScratchCandidateLaunch.SecretLength]));

        Assert.Contains("live scratch receiver image differs", error.Message, StringComparison.Ordinal);
        Assert.Equal(pendingBefore, File.ReadAllBytes(pending));
        Assert.Equal(activeBefore, File.ReadAllBytes(active));
        Assert.Equal(filesBefore, Directory.GetFiles(stage));
        Assert.False(File.Exists(Path.Combine(stage, ScratchCandidateLaunch.EvidenceName)));
        Assert.False(File.Exists(Path.Combine(stage, WindowsCandidatePackageReceipt.ClaimedName)));
    }

    [Theory]
    [InlineData("OpenBlade.CandidateReceipt.Scratch.exe")]
    [InlineData("OpenBlade.CandidateReceipt.Scratch.dll")]
    [InlineData("OpenBlade.Setup.dll")]
    [InlineData("OpenBlade.CandidateReceipt.Scratch.deps.json")]
    [InlineData("OpenBlade.CandidateReceipt.Scratch.runtimeconfig.json")]
    public void CompleteClosureRetainsEveryDeclaredMemberUntilDisposed(string name)
    {
        using var fixture = new ClosureFiles();
        string path = Path.Combine(fixture.Directory, name);
        using (var closure = ScratchReceiverClosure.Retain(fixture.Storage))
        {
            byte[] digest = closure.RequireUnchanged();
            Assert.ThrowsAny<IOException>(() => File.WriteAllText(path, "replacement"));
            Assert.ThrowsAny<IOException>(() => File.Delete(path));
            Assert.ThrowsAny<IOException>(() => File.Move(path, path + ".replacement"));
            Assert.Equal(digest, closure.RequireUnchanged());
        }
        File.WriteAllText(path, "replacement after release");
        Assert.Equal("replacement after release", File.ReadAllText(path));
    }

    [Theory]
    [InlineData("OpenBlade.CandidateReceipt.Scratch.dll")]
    [InlineData("OpenBlade.Setup.dll")]
    [InlineData("OpenBlade.CandidateReceipt.Scratch.deps.json")]
    [InlineData("OpenBlade.CandidateReceipt.Scratch.runtimeconfig.json")]
    public void ReceiverEvidenceRejectsChangedCodeOrLoaderWithUnchangedApphost(string name)
    {
        using var fixture = new ClosureFiles();
        byte[] apphost = File.ReadAllBytes(Path.Combine(fixture.Directory, ScratchCandidateLaunch.ReceiverName));
        using var current = Process.GetCurrentProcess();
        byte[] secret = new byte[ScratchCandidateLaunch.SecretLength];
        byte[] issued;
        using (var held = ScratchReceiverClosure.Retain(fixture.Storage))
        {
            issued = ScratchCandidateLaunch.Encode("active"u8.ToArray(), current, current, held, secret);
        }
        string changed = Path.Combine(fixture.Directory, name);
        File.AppendAllText(changed, name.EndsWith(".json", StringComparison.Ordinal) ? " " : "changed code");
        using var reopened = ScratchReceiverClosure.Retain(fixture.Storage);
        byte[] observed = ScratchCandidateLaunch.Encode("active"u8.ToArray(), current, current, reopened, secret);
        Assert.Equal(apphost, File.ReadAllBytes(Path.Combine(fixture.Directory, ScratchCandidateLaunch.ReceiverName)));
        Assert.False(CryptographicOperations.FixedTimeEquals(issued, observed));
        string evidence = Path.Combine(fixture.Storage.Root, ScratchCandidateLaunch.EvidenceName);
        fixture.Storage.Publish(evidence, issued);
        using var record = fixture.Storage.OpenFile(evidence);
        Assert.Throws<InvalidDataException>(() => WindowsCandidateReceiptStorage.RequireExact(record, observed));
    }

    [Theory]
    [InlineData("OpenBlade.CandidateReceipt.Scratch.dll")]
    [InlineData("OpenBlade.Setup.dll")]
    [InlineData("OpenBlade.CandidateReceipt.Scratch.deps.json")]
    [InlineData("OpenBlade.CandidateReceipt.Scratch.runtimeconfig.json")]
    public void MissingMemberRefusesBeforeEvidence(string name)
    {
        using var fixture = new ClosureFiles();
        File.Delete(Path.Combine(fixture.Directory, name));
        Assert.ThrowsAny<IOException>(() => ScratchReceiverClosure.Retain(fixture.Storage));
        Assert.False(File.Exists(Path.Combine(fixture.Storage.Root, ScratchCandidateLaunch.EvidenceName)));
    }

    [Theory]
    [InlineData("injected.dll")]
    [InlineData("OpenBlade.CandidateReceipt.Scratch.runtimeconfig.dev.json")]
    public void UnexpectedMemberRefusesAtCaptureAndDuringRetainedLifetime(string name)
    {
        using var fixture = new ClosureFiles();
        string path = Path.Combine(fixture.Directory, name);
        using (var closure = ScratchReceiverClosure.Retain(fixture.Storage))
        {
            Assert.Throws<UnauthorizedAccessException>(() => File.WriteAllText(path, "transient loader input"));
            Assert.Throws<UnauthorizedAccessException>(() => System.IO.Directory.CreateDirectory(path + ".dir"));
            closure.RequireUnchanged();
        }
        File.WriteAllText(path, "unexpected loader input");
        Assert.Throws<InvalidDataException>(() => ScratchReceiverClosure.Retain(fixture.Storage));
    }

    [Fact]
    public void BuildOutputDeclaresACompleteSupportedClosureWithoutLaunchingIt()
    {
        string source = AppContext.BaseDirectory;
        string[] required = ScratchReceiverClosure.RequiredFiles(File.ReadAllBytes(
            Path.Combine(source, ScratchReceiverClosure.Stem + ".deps.json")));
        Assert.Contains("OpenBlade.Setup.dll", required);
        Assert.Contains("OpenBlade.Deployment.dll", required);
        Assert.Contains("OpenBlade.DeploymentBroker.dll", required);
        using var fixture = new ClosureFiles();
        foreach (string old in Directory.GetFiles(fixture.Directory))
        {
            File.Delete(old);
        }
        foreach (string name in required)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(fixture.Directory, name))!);
            File.Copy(Path.Combine(source, name), Path.Combine(fixture.Directory, name));
        }
        using var closure = ScratchReceiverClosure.Retain(fixture.Storage);
        Assert.Equal(32, closure.RequireUnchanged().Length);
    }

    [Fact]
    public void NamespaceSealCannotBeReopenedForAclChangesAndRestoresExactAccess()
    {
        using var fixture = new ClosureFiles();
        var directory = new DirectoryInfo(fixture.Directory);
        byte[] original = directory.GetAccessControl().GetSecurityDescriptorBinaryForm();
        using (var closure = ScratchReceiverClosure.Retain(fixture.Storage))
        {
            var security = directory.GetAccessControl();
            security.SetSecurityDescriptorBinaryForm(original, AccessControlSections.Access);
            Assert.Throws<UnauthorizedAccessException>(() => directory.SetAccessControl(security));
            using var receiver = ScratchReceiverClosure.Retain(fixture.Storage);
            Assert.Equal(closure.RequireUnchanged(), receiver.RequireUnchanged());
        }
        var before = new RawSecurityDescriptor(original, 0);
        var after = new RawSecurityDescriptor(directory.GetAccessControl().GetSecurityDescriptorBinaryForm(), 0);
        Assert.Equal(before.Owner, after.Owner);
        Assert.Equal(before.Group, after.Group);
        Assert.Equal(before.ControlFlags & ControlFlags.DiscretionaryAclProtected,
            after.ControlFlags & ControlFlags.DiscretionaryAclProtected);
        // Windows can set AUTO_INHERITED while preserving the exact DACL ACEs.
        Assert.Equal(AccessBytes(before.DiscretionaryAcl!), AccessBytes(after.DiscretionaryAcl!));
        File.WriteAllText(Path.Combine(fixture.Directory, "after-release"), "restored");
    }

    [Fact]
    public void InMemoryCleanupAttemptsEveryRetainedResourceAndReportsEveryFailure()
    {
        List<int> attempted = [];
        var first = new IOException("first restore failed");
        var second = new UnauthorizedAccessException("second restore failed");
        var failure = Assert.Throws<AggregateException>(() => ScratchReceiverClosure.DisposeRetainedResources(
        [
            new CleanupAction(() => { attempted.Add(1); throw first; }),
            new CleanupAction(() => { attempted.Add(2); throw second; }),
            new CleanupAction(() => attempted.Add(3)),
        ]));
        Assert.Equal([1, 2, 3], attempted);
        Assert.Equal([first, second], failure.InnerExceptions);
    }

    private static byte[] AccessBytes(RawAcl acl)
    {
        byte[] bytes = new byte[acl.BinaryLength];
        acl.GetBinaryForm(bytes, 0);
        return bytes;
    }

    private sealed class CleanupAction(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    private sealed class ClosureFiles : IDisposable
    {
        internal WindowsCandidateReceiptStorage Storage { get; } = WindowsCandidateReceiptStorage.CreateScratch();
        internal string Directory => Path.GetDirectoryName(ScratchCandidateLaunch.ReceiverPath(Storage))!;

        internal ClosureFiles()
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(Path.Combine(Directory, ScratchCandidateLaunch.ReceiverName), "apphost");
            File.WriteAllText(Path.Combine(Directory, ScratchReceiverClosure.Stem + ".dll"), "receiver");
            File.WriteAllText(Path.Combine(Directory, "OpenBlade.Setup.dll"), "setup");
            File.WriteAllText(Path.Combine(Directory, ScratchReceiverClosure.Stem + ".deps.json"),
                """{"targets":{"fixture":{"receiver/1":{"runtime":{"OpenBlade.CandidateReceipt.Scratch.dll":{}}},"setup/1":{"runtime":{"OpenBlade.Setup.dll":{}}}}}}""");
            File.WriteAllText(Path.Combine(Directory, ScratchReceiverClosure.Stem + ".runtimeconfig.json"),
                """{"runtimeOptions":{"tfm":"net10.0"}}""");
        }

        public void Dispose() => System.IO.Directory.Delete(Storage.Root, recursive: true);
    }
}
#endif
