using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace OpenBlade.Deployment.Tests;

// Offline only: authenticated inert package bytes and private filesystem,
// with own-SID ACL metadata. No native role, SCM, process or pipe probes.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class NativeMetadataRecoveryTests
{
    [Fact]
    public void SharedV2RoleCapabilitiesAndFullCombinedProvenance()
    {
        using var data = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "native-capabilities-v2.json")));
        foreach (var item in data.RootElement.GetProperty("cases").EnumerateArray())
        {
            byte[] image = Convert.FromBase64String(item.GetProperty("image").GetString()!);
            string role = item.GetProperty("role").GetString()!;
            if (!item.GetProperty("accepted").GetBoolean())
            { Assert.Throws<InvalidDataException>(() => NativeArtifact.Inspect([.. image], role, new string('a', 40))); continue; }
            var artifact = NativeArtifact.Inspect([.. image], role, new string('a', 40));
            Assert.Equal(Convert.FromBase64String(item.GetProperty("record").GetString()!), artifact.Encode());
            Assert.Equal(2, artifact.Schema);
            if (role == "agent")
            {
                Assert.Contains(item.GetProperty("name").GetString() == "temporary-combined-agent" ? "service-temporary-charge-v1" : "service-metadata-only-v1", artifact.Features);
                Assert.False(artifact.TemporaryChargeMutation);
                Assert.Equal("none", artifact.Capabilities);
                Assert.False(artifact.MetadataOnly);
                Assert.False(artifact.PrivateCustody);
            }
        }
    }

    [Fact]
    public void LegacyRecordAndMarkerStayByteExact()
    {
        using var data = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "native-package-v1.json")));
        foreach (string role in new[] { "service", "agent" })
        {
            byte[] image = Convert.FromBase64String(data.RootElement.GetProperty(role + "Image").GetString()!);
            byte[] expected = Convert.FromBase64String(data.RootElement.GetProperty(role + "Record").GetString()!);
            var artifact = NativeArtifact.Inspect([.. image], role, new string('a', 40));
            Assert.Equal(expected, artifact.Encode());
            Assert.Equal(1, artifact.Schema);
            Assert.False(artifact.MetadataOnly);
            Assert.False(artifact.PrivateCustody);
        }
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public async Task OnlyAuthenticatedOldMetadataCanAbandonAndPreservesAllHistory(int schema, bool allowed)
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var package = await WindowsNativeEpochTests.Package(files, schema: schema);
        string directory;
        using (var tx = WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid()))
        {
            directory = Path.Combine(files.Root, tx.Record.Epoch);
            tx.MarkStartupSubmitted();
            NativeRecoveryTests.AddAttempt(files, tx.Record, 17, 1, true);
            NativeRecoveryTests.AddAttempt(files, tx.Record, 18, 2, false);
            tx.CommitAfterBoundReadiness(new string('e', 64));
        }
        var before = Snapshot(Path.Combine(directory, "attempts"));
        // An incoming v2 package grants no privilege to this OLD selection.
        using var incoming = await WindowsNativeEpochTests.Package(files, nonce: 'c', schema: 2);
        using var selection = await WindowsNativeInstalledSelection.OpenForSetupAsync(files, CancellationToken.None);
        if (!allowed)
        { Assert.Equal(NativeRecoveryReason.MissingAttemptDrain, Assert.Throws<NativeRecoveryException>(() => selection!.RetireRecoverableSelection()).Reason); }
        else
        {
            selection!.RetireRecoverableSelection();
            Assert.Equal(NativeEpochPhase.Retired, selection.Record.Phase);
            string proof = Encoding.ASCII.GetString(File.ReadAllBytes(Directory.GetFiles(Path.Combine(directory, "retirement-proofs"), "*.bin").Single()));
            Assert.StartsWith("OpenBlade.NativeRetirementProof.v2\n", proof);
            Assert.Contains("metadata-attempts-classified", proof);
            Assert.False(File.Exists(Path.Combine(directory, "attempts", "drain-18-2.bin")));
        }
        AssertSnapshot(before, Path.Combine(directory, "attempts"));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public async Task AuthenticatedLegacyPublicCleanupKeepsDescriptorAndV2Refuses(int schema, bool refuses)
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var package = await WindowsNativeEpochTests.Package(files, schema: schema);
        string path;
        using (var tx = WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid()))
        {
            tx.MarkStartupSubmitted();
            NativeRecoveryTests.AddAttempt(files, tx.Record, 17, 1, true);
            path = Path.Combine(files.Root, tx.Record.Epoch, "attempts", "custody.lock");
        }
        var acl = new FileInfo(path).GetAccessControl();
        acl.SetAccessRuleProtection(false, true);
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-32-545"), FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(acl);
        byte[] descriptor = new FileInfo(path).GetAccessControl().GetSecurityDescriptorBinaryForm();
        byte[] contents = File.ReadAllBytes(path);
        var history = Snapshot(Path.GetDirectoryName(path)!);
        Assert.Throws<UnauthorizedAccessException>(() => files.AcquireServiceCustody(path));
        using var selection = await WindowsNativeInstalledSelection.OpenForSetupAsync(files, CancellationToken.None);
        using (var publicReader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        { Assert.Equal(NativeRecoveryReason.ActiveOrUnknownCustody, Assert.Throws<NativeRecoveryException>(() => selection!.RetireRecoverableSelection()).Reason); }
        if (refuses)
        { Assert.Equal(NativeRecoveryReason.ActiveOrUnknownCustody, Assert.Throws<NativeRecoveryException>(() => selection!.RetireRecoverableSelection()).Reason); }
        else { selection!.RetireRecoverableSelection(); Assert.Equal(NativeEpochPhase.Retired, selection.Record.Phase); }
        Assert.Equal(descriptor, new FileInfo(path).GetAccessControl().GetSecurityDescriptorBinaryForm());
        Assert.Equal(contents, File.ReadAllBytes(path));
        AssertSnapshot(history, Path.GetDirectoryName(path)!);
    }

    [Theory]
    [InlineData("proof")]
    [InlineData("Retired.bin")]
    [InlineData("frontier")]
    [InlineData("current.bin")]
    public async Task EveryPublicationBoundaryRetainsOwnerWriterAndInterruptedRecoveryReacquiresIt(string boundary)
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var package = await WindowsNativeEpochTests.Package(files, schema: 2);
        string directory;
        using (var tx = WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid()))
        {
            directory = Path.Combine(files.Root, tx.Record.Epoch);
            tx.MarkStartupSubmitted();
            NativeRecoveryTests.AddAttempt(files, tx.Record, 17, 1, false);
            tx.CommitAfterBoundReadiness(new string('e', 64));
        }
        var history = Snapshot(Path.Combine(directory, "attempts"));
        bool triggered = false;
        files.PublishedForTests = path =>
        {
            bool match = boundary switch
            {
                "proof" => path.Contains("retirement-proofs"),
                "frontier" => Path.GetFileName(path).StartsWith("selection-") && path.EndsWith("-3.bin"),
                _ => Path.GetFileName(path) == boundary
            };
            if (!match || triggered) { return; }
            triggered = true;
            Assert.Throws<IOException>(() => files.AcquireOwnerWriter());
            throw new IOException("injected publication interruption");
        };
        using (var selection = await WindowsNativeInstalledSelection.OpenForSetupAsync(files, CancellationToken.None))
        { Assert.Equal(NativeRecoveryReason.RetirementPublicationFailed, Assert.Throws<NativeRecoveryException>(() => selection!.RetireRecoverableSelection()).Reason); }
        Assert.True(triggered);
        files.PublishedForTests = null;
        // A concurrent reservation blocks fresh interrupted retirement admission.
        using (var writer = files.AcquireOwnerWriter())
        {
            if (boundary == "frontier")
            { Assert.Equal(NativeRecoveryReason.FirmwareOwnerUnresolved, (await Assert.ThrowsAsync<NativeRecoveryException>(() => WindowsNativeInstalledSelection.OpenForSetupAsync(files, CancellationToken.None))).Reason); }
            if (boundary == "Retired.bin")
            {
                using var pending = await WindowsNativeInstalledSelection.OpenForSetupAsync(files, CancellationToken.None);
                Assert.Equal(NativeRecoveryReason.FirmwareOwnerUnresolved, Assert.Throws<NativeRecoveryException>(() => pending!.RetireRecoverableSelection()).Reason);
            }
        }
        using (var selection = await WindowsNativeInstalledSelection.OpenForSetupAsync(files, CancellationToken.None))
        { selection!.RetireRecoverableSelection(); Assert.Equal(NativeEpochPhase.Retired, selection.Record.Phase); }
        AssertSnapshot(history, Path.Combine(directory, "attempts"));
        using var released = files.AcquireOwnerWriter();
    }

    [Theory]
    [InlineData("missing-lock")]
    [InlineData("occupied-lock")]
    [InlineData("Reserved")]
    [InlineData("Active")]
    [InlineData("Retiring")]
    [InlineData("malformed")]
    [InlineData("orphan-drain")]
    [InlineData("mismatched-drain")]
    public async Task MetadataCapabilityDoesNotBypassIndependentOwnerOrMalformedHistory(string fault)
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var package = await WindowsNativeEpochTests.Package(files, schema: 2);
        string directory;
        NativeEpochRecord record;
        using (var tx = WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid()))
        {
            directory = Path.Combine(files.Root, tx.Record.Epoch); record = tx.Record;
            tx.MarkStartupSubmitted();
            NativeRecoveryTests.AddAttempt(files, tx.Record, 17, 1, false);
        }
        string ownerLock = Path.Combine(files.Root, "owner", "writer.lock");
        if (fault == "missing-lock") { File.Delete(ownerLock); }
        if (fault is "Reserved" or "Active" or "Retiring")
        {
            string? prior = null;
            var phases = new[] { "Reserved", "Active", "Retiring" };
            for (int i = 0; i <= Array.IndexOf(phases, fault); i++)
            {
                byte[] bytes = NativeCanonical.Lines("OpenBlade.NativeOwner.v1", phases[i], (i + 1).ToString(), prior ?? new string('0', 64),
                    record.Installation, record.Epoch, record.ServiceId, "17:1", new string('d', 64));
                files.Publish(Path.Combine(files.Root, "owner", $"owner-{i + 1:D20}.bin"), bytes); prior = NativeCanonical.Digest(bytes);
            }
        }
        if (fault == "malformed") { File.WriteAllText(Path.Combine(directory, "attempts", "launch-18-2.bin"), "torn"); }
        if (fault is "orphan-drain" or "mismatched-drain")
        { File.WriteAllText(Path.Combine(directory, "attempts", fault == "orphan-drain" ? "drain-18-2.bin" : "drain-17-1.bin"), "torn"); }
        var before = Snapshot(directory);
        using var busy = fault == "occupied-lock" ? files.AcquireOwnerWriter() : null;
        using var selection = await WindowsNativeInstalledSelection.OpenForSetupAsync(files, CancellationToken.None);
        Assert.Throws<NativeRecoveryException>(() => selection!.RetireRecoverableSelection());
        AssertSnapshot(before, directory);
        Assert.False(File.Exists(Path.Combine(directory, "Retired.bin")));
        if (fault == "missing-lock") { Assert.False(File.Exists(ownerLock)); }
    }

    [Fact]
    public async Task CancellationAfterProofPublicationReleasesAllLeasesWithoutDrainFabrication()
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var package = await WindowsNativeEpochTests.Package(files, schema: 2);
        string directory;
        using (var tx = WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid()))
        {
            directory = Path.Combine(files.Root, tx.Record.Epoch);
            tx.MarkStartupSubmitted(); NativeRecoveryTests.AddAttempt(files, tx.Record, 17, 1, false);
        }
        using var cancel = new CancellationTokenSource();
        files.PublishedForTests = path => { if (path.Contains("retirement-proofs")) { cancel.Cancel(); throw new OperationCanceledException(cancel.Token); } };
        using (var selection = await WindowsNativeInstalledSelection.OpenForSetupAsync(files, CancellationToken.None))
        { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Run(() => selection!.RetireRecoverableSelection())); }
        files.PublishedForTests = null;
        using var custody = files.AcquireServiceCustody(Path.Combine(directory, "attempts", "custody.lock"));
        using var owner = files.AcquireOwnerWriter();
        Assert.False(File.Exists(Path.Combine(directory, "Retired.bin")));
        Assert.Single(Directory.GetFiles(Path.Combine(directory, "retirement-proofs"), "*.bin"));
        Assert.False(File.Exists(Path.Combine(directory, "attempts", "drain-17-1.bin")));
    }

    [Fact]
    public async Task LegacyPublicInterruptedFrontierRecoversWithoutChangingCustodyOrHistory()
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var package = await WindowsNativeEpochTests.Package(files);
        string directory;
        using (var tx = WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid()))
        {
            directory = Path.Combine(files.Root, tx.Record.Epoch);
            tx.MarkStartupSubmitted(); NativeRecoveryTests.AddAttempt(files, tx.Record, 17, 1, true);
            tx.CommitAfterBoundReadiness(new string('e', 64));
        }
        string path = Path.Combine(directory, "attempts", "custody.lock");
        var security = new FileInfo(path).GetAccessControl();
        security.SetAccessRuleProtection(false, true);
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-32-545"), FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
        byte[] descriptor = new FileInfo(path).GetAccessControl().GetSecurityDescriptorBinaryForm();
        var history = Snapshot(Path.Combine(directory, "attempts"));
        files.PublishedForTests = name => { if (Path.GetFileName(name).StartsWith("selection-") && name.EndsWith("-3.bin")) { throw new IOException("frontier renamed"); } };
        using (var selection = await WindowsNativeInstalledSelection.OpenForSetupAsync(files, CancellationToken.None))
        { Assert.Throws<NativeRecoveryException>(() => selection!.RetireRecoverableSelection()); }
        files.PublishedForTests = null;
        using (var selection = await WindowsNativeInstalledSelection.OpenForSetupAsync(files, CancellationToken.None))
        { Assert.Equal(NativeEpochPhase.Retired, selection!.Record.Phase); }
        Assert.Equal(descriptor, new FileInfo(path).GetAccessControl().GetSecurityDescriptorBinaryForm());
        AssertSnapshot(history, Path.Combine(directory, "attempts"));
    }

    [Theory]
    [InlineData("retired", true)]
    [InlineData("bad-artifact", false)]
    [InlineData("bad-process", false)]
    [InlineData("bad-sequence", false)]
    [InlineData("missing-record", false)]
    public async Task RetainedCompleteOwnerChainIsIndependentOfMetadataDisposition(string fault, bool allowed)
    {
        using var files = WindowsNativeEpochFiles.CreatePrivateFixture();
        using var package = await WindowsNativeEpochTests.Package(files, schema: 2);
        NativeEpochRecord record;
        using (var tx = WindowsNativeEpochTransaction.Begin(files, package, Guid.NewGuid()))
        {
            record = tx.Record; tx.MarkStartupSubmitted();
            NativeRecoveryTests.AddAttempt(files, tx.Record, 17, 1, false);
        }
        string? previous = null;
        string[] phases = ["Reserved", "Active", "Retiring", "Retired"];
        for (int i = 0; i < phases.Length; i++)
        {
            byte[] bytes = NativeCanonical.Lines("OpenBlade.NativeOwner.v1", phases[i], fault == "bad-sequence" && i == 0 ? "01" : (i + 1).ToString(),
                previous ?? new string('0', 64), record.Installation, record.Epoch,
                fault == "bad-artifact" ? "malformed-service" : record.ServiceId, fault == "bad-process" ? "017:1" : "17:1", new string('d', 64));
            if (fault != "missing-record" || i != 1) { files.Publish(Path.Combine(files.Root, "owner", $"owner-{i + 1:D20}.bin"), bytes); }
            previous = NativeCanonical.Digest(bytes);
        }
        var original = Snapshot(Path.Combine(files.Root, "owner"));
        using var selection = await WindowsNativeInstalledSelection.OpenForSetupAsync(files, CancellationToken.None);
        if (allowed) { selection!.RetireRecoverableSelection(); Assert.Equal(NativeEpochPhase.Retired, selection.Record.Phase); }
        else { Assert.Equal(NativeRecoveryReason.FirmwareOwnerUnresolved, Assert.Throws<NativeRecoveryException>(() => selection!.RetireRecoverableSelection()).Reason); }
        AssertSnapshot(original, Path.Combine(files.Root, "owner"));
    }

    private static Dictionary<string, byte[]> Snapshot(string directory) => Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(directory, path), File.ReadAllBytes);
    private static void AssertSnapshot(Dictionary<string, byte[]> expected, string directory)
    { foreach (var (path, bytes) in expected) { Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(directory, path))); } }
}
