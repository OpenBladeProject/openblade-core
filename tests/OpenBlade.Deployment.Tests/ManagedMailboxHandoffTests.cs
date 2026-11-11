using OpenBlade.Deployment;

namespace OpenBlade.Deployment.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class ManagedMailboxHandoffTests
{
    [Fact]
    public void ActualInitializerFaultCutRepairsOnlyTheNeverSelectedEmptyBootstrap()
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        files.Publish(Path.Combine(files.Root, "root.bin"), NativeCanonical.Lines("OpenBlade.NativeRoot.v1", "legacy-store-absent"));
        files.CrashBoundaryForTests = NativeFileBoundary.OwnerDirectoryCreated;
        Assert.Throws<IOException>(files.EnsureOwnerStore);
        Assert.True(Directory.Exists(Path.Combine(files.Root, "owner")));
        Assert.False(File.Exists(Path.Combine(files.Root, "owner", "writer.lock")));
        files.CrashBoundaryForTests = null;
        files.RepairInterruptedOwnerBootstrap();
        using var writer = files.AcquireWriter();
        Assert.Equal(NativeInstallationState.VerifiedBootstrap, WindowsNativeRecoveryState.Inspect(files).State);
        Assert.Single(Directory.EnumerateFileSystemEntries(Path.Combine(files.Root, "owner")));
    }

    [Theory]
    [InlineData("current.bin")]
    [InlineData("selection-00000000000000000001-0.bin")]
    [InlineData("old.reserved")]
    [InlineData("owner/owner-00000000000000000001.bin")]
    [InlineData("owner/unknown.bin")]
    public void InterruptedBootstrapHistoryIsPreservedWithoutCreatingAReplacementLock(string history)
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        files.Publish(Path.Combine(files.Root, "root.bin"), NativeCanonical.Lines("OpenBlade.NativeRoot.v1", "legacy-store-absent"));
        files.CrashBoundaryForTests = NativeFileBoundary.OwnerDirectoryCreated;
        Assert.Throws<IOException>(files.EnsureOwnerStore);
        string path = Path.Combine(files.Root, history);
        byte[] bytes = "unknown-preserved"u8.ToArray();
        File.WriteAllBytes(path, bytes);
        Assert.Throws<IOException>(files.RepairInterruptedOwnerBootstrap);
        Assert.False(File.Exists(Path.Combine(files.Root, "owner", "writer.lock")));
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void InterruptedBootstrapRejectsWrongMarkerAndNonDirectoryOwnerShape()
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        File.WriteAllBytes(Path.Combine(files.Root, "owner"), [1]);
        files.Publish(Path.Combine(files.Root, "root.bin"), NativeCanonical.Lines("OpenBlade.NativeRoot.v1", "legacy-store-absent"));
        Assert.ThrowsAny<IOException>(files.RepairInterruptedOwnerBootstrap);
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(Path.Combine(files.Root, "owner")));
        using var other = WindowsNativeEpochFiles.CreatePrivateFixture();
        Directory.CreateDirectory(Path.Combine(other.Root, "owner"));
        other.Publish(Path.Combine(other.Root, "root.bin"), NativeCanonical.Lines("OpenBlade.NativeRoot.v1", "unrecognized-origin"));
        Assert.Throws<InvalidDataException>(other.RepairInterruptedOwnerBootstrap);
        Assert.False(File.Exists(Path.Combine(other.Root, "owner", "writer.lock")));
    }

    [Fact]
    public void InterruptedBootstrapRevalidatesTheActualOwnerAclBeforeCreatingItsLock()
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        files.Publish(Path.Combine(files.Root, "root.bin"), NativeCanonical.Lines("OpenBlade.NativeRoot.v1", "legacy-store-absent"));
        files.CrashBoundaryForTests = NativeFileBoundary.OwnerDirectoryCreated;
        Assert.Throws<IOException>(files.EnsureOwnerStore);
        var directory = new DirectoryInfo(Path.Combine(files.Root, "owner"));
        var original = System.IO.FileSystemAclExtensions.GetAccessControl(directory);
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var changed = new System.Security.AccessControl.DirectorySecurity();
        changed.SetOwner(identity.User!);
        changed.SetAccessRuleProtection(true, false);
        changed.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(identity.User!,
            System.Security.AccessControl.FileSystemRights.ReadAndExecute,
            System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
            System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
        System.IO.FileSystemAclExtensions.SetAccessControl(directory, changed);
        try
        {
            Assert.Throws<UnauthorizedAccessException>(files.RepairInterruptedOwnerBootstrap);
            Assert.False(File.Exists(Path.Combine(directory.FullName, "writer.lock")));
        }
        finally { System.IO.FileSystemAclExtensions.SetAccessControl(directory, original); }
    }

    [Fact]
    public void ActualInitializerDoesNotCreateAnOwnerDirectoryForSelectedOrUnknownHistory()
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        files.Publish(Path.Combine(files.Root, "root.bin"), NativeCanonical.Lines("OpenBlade.NativeRoot.v1", "legacy-store-absent"));
        File.WriteAllBytes(Path.Combine(files.Root, "current.bin"), "preserved-unknown-selection"u8.ToArray());
        Assert.Throws<IOException>(files.EnsureOwnerStore);
        Assert.False(Directory.Exists(Path.Combine(files.Root, "owner")));
    }

    [Fact]
    public void OriginalRunReceiptRequiresExclusionAndMapsToOnlyOneIncomingEpoch()
    {
        using var files = Fixture();
        var run = Run();
        using var owner = WindowsManagedMailboxRun.StartForTests(files.RetainFixtureView(), run);
        using var transfer = new WindowsManagedMailboxTransfer(files.RetainFixtureView(), run.Encode(), Request(run));
        // Stored/uncertain settings do not appear in this transport-only receipt.
        owner.PublishQuiescent();
        Assert.ThrowsAny<IOException>(transfer.ConfirmOriginalExited);
        owner.Dispose();
        transfer.ConfirmOriginalExited();
        using var epochs = WindowsNativeEpochFiles.CreatePrivateFixture();
        string epoch = new('e', 32);
        Directory.CreateDirectory(Path.Combine(epochs.Root, epoch));
        var request = Request(run);
        var record = new NativeEpochRecord(NativeEpochPhase.Provisional, epoch, new string('0', 64), 1,
            request.Installation, request.Package, request.Request, new string('a', 64) + "-service", new string('b', 64) + "-agent", new string('0', 64), new string('f', 64));
        transfer.BindEpoch(epochs, record);
        using var parity = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "managed-mailbox-handoff-v2.json")));
        Assert.Equal(parity.RootElement.GetProperty("run").GetString(), System.Text.Encoding.ASCII.GetString(run.Encode()));
        Assert.Equal(parity.RootElement.GetProperty("request").GetString(), System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(Path.Combine(files.Root, "handoff-" + request.Request + "-request.bin"))));
        Assert.Equal(parity.RootElement.GetProperty("receipt").GetString(), System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(Path.Combine(files.Root, "handoff-" + request.Request + "-receipt.bin"))));
        Assert.Equal(parity.RootElement.GetProperty("mapping").GetString(), System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(Path.Combine(files.Root, "handoff-" + request.Request + "-mapping.bin"))));
        Assert.ThrowsAny<IOException>(() => transfer.BindEpoch(epochs, record with { Epoch = new string('d', 32) }));
        Assert.ThrowsAny<IOException>(() => WindowsManagedMailboxRun.StartForTests(files.RetainFixtureView(), run with { Run = new string('9', 32) }));
    }

    [Fact]
    public void ExitWithoutReceiptAndWrongOriginalRunRefuseTransfer()
    {
        using var files = Fixture();
        var run = Run();
        using var owner = WindowsManagedMailboxRun.StartForTests(files.RetainFixtureView(), run);
        Assert.ThrowsAny<IOException>(() => new WindowsManagedMailboxTransfer(files, run.Encode(), Request(run) with { RunDigest = new string('0', 64) }));
        using var transfer = new WindowsManagedMailboxTransfer(files.RetainFixtureView(), run.Encode(), Request(run));
        owner.Dispose();
        Assert.ThrowsAny<IOException>(transfer.ConfirmOriginalExited);
    }

    [Fact]
    public void EnrollmentMaintenanceRefusesActiveManagedRunAndBlocksRestartThroughoutReplacement()
    {
        using var files = Fixture();
        var run = Run();
        using var owner = WindowsManagedMailboxRun.StartForTests(files.RetainFixtureView(), run);
        Assert.Throws<IOException>(() => WindowsNativeTrustLifecycle.AcquireManagedEnrollmentChangeCustody(files.RetainFixtureView()));
        owner.Dispose();
        using (var maintenance = WindowsNativeTrustLifecycle.AcquireManagedEnrollmentChangeCustody(files.RetainFixtureView()))
        {
            Assert.ThrowsAny<IOException>(() => WindowsManagedMailboxRun.StartForTests(files.RetainFixtureView(), run with { Run = new string('9', 32) }));
        }
        using var restarted = WindowsManagedMailboxRun.StartForTests(files.RetainFixtureView(), run with { Run = new string('a', 32) });
    }

    private static WindowsNativeEpochFiles Fixture()
    {
        var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        files.PublishCustody(Path.Combine(files.Root, "exclusion.lock"), NativeCanonical.Lines("OpenBlade.MailboxExclusion.v1"));
        return files;
    }
    private static ManagedMailboxRun Run() => new(new string('1', 32), "17:100", new string('2', 64), new string('3', 64), new string('4', 32));
    private static ManagedMailboxRequest Request(ManagedMailboxRun run) => new(NativeCanonical.Digest(run.Encode()), new string('5', 32), new string('6', 32), new string('7', 64));
}
