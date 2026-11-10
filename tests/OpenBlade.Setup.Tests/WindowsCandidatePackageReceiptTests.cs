#if OPENBLADE_CANDIDATE_RECEIPT_FIXTURE
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using OpenBlade.Deployment;
using OpenBlade.DeploymentBroker;

namespace OpenBlade.Setup.Tests;

public sealed class WindowsCandidatePackageReceiptTests
{
    private const string PackageHash = "4079c110978af3008ef929fac309b9637e9e77b0c68a660dd8d956758449359f";
    private const string Revision = "af61a8c842429bc0564390dee39b466ee0da9023";
    private const string Version = "65535.1.2.3";

    [Fact]
    public async Task ReceiverReopensAfterBrokerBindsItsProcessAndConsumesReceiptOnce()
    {
        using var fixture = await Fixture.CreateAsync();
        Guid request = Guid.NewGuid();
        var issued = await fixture.PrepareAsync(request);
        byte[] pending = File.ReadAllBytes(fixture.Pending(request));
        byte[] beforeLaunch = File.ReadAllBytes(fixture.Paths.InFlightPath);

        await fixture.RunReceiverAsync(process =>
            new BrokerInFlightStore(fixture.Paths).BindInstallerProcess(request, process.Id, DateTimeOffset.UtcNow),
            expectedExit: 0);

        Assert.NotEqual(beforeLaunch, File.ReadAllBytes(fixture.Paths.InFlightPath));
        Assert.False(File.Exists(fixture.Pending(request)));
        Assert.Equal(pending, File.ReadAllBytes(fixture.Claimed(request)));
        issued.VerifyCommitted(fixture.Tree.Install, default);
        byte[] committed = File.ReadAllBytes(fixture.Committed);

        await fixture.RunReceiverAsync(_ => { }, expectedExit: 3, "no active launch binding");
        Assert.Equal(committed, File.ReadAllBytes(fixture.Committed));
    }

    [Theory]
    [InlineData("unbound", "no active launch binding")]
    [InlineData("other-pid", "no active launch binding")]
    [InlineData("old-generation", "launch generations differ")]
    [InlineData("terminal", "no active launch binding")]
    [InlineData("immutable-state", "pending candidate receipt differs")]
    [InlineData("enrollment", "pending candidate receipt differs")]
    [InlineData("sequence", "differs from the issued record")]
    [InlineData("signature", "deployment signature is invalid")]
    [InlineData("package", "installer size does not match")]
    [InlineData("pending-role", "pending candidate receipt differs")]
    [InlineData("other-request", "pending candidate receipt differs")]
    [InlineData("pending-missing", "receipt path could not be retained")]
    [InlineData("claimed", "already contains the fixed name")]
    public async Task ReceiverRejectsMissingStaleOrReplacedEvidence(string change, string diagnostic)
    {
        using var fixture = await Fixture.CreateAsync();
        Guid request = Guid.NewGuid();
        await fixture.PrepareAsync(request);
        fixture.Tree.WriteInstall(WindowsCandidatePackageReceipt.CommittedName, "prior proof"u8.ToArray());
        var state = new BrokerInFlightStore(fixture.Paths);
        await fixture.RunReceiverAsync(process =>
        {
            if (change == "unbound")
            {
                return;
            }
            state.BindInstallerProcess(request, change == "other-pid" ? Environment.ProcessId : process.Id,
                change == "old-generation" ? process.StartTime.ToUniversalTime().AddTicks(-1) : DateTimeOffset.UtcNow);
            switch (change)
            {
                case "terminal":
                    state.BindInstallerResult(request, 0, DateTimeOffset.UtcNow);
                    break;
                case "immutable-state":
                    var bound = BrokerInFlightDeploymentCodec.ParseCanonical(File.ReadAllBytes(fixture.Paths.InFlightPath));
                    File.WriteAllBytes(fixture.Paths.InFlightPath, BrokerInFlightDeploymentCodec.SerializeCanonical(
                        bound with { PreviousPayloadSha256 = new string('1', 64) }));
                    break;
                case "enrollment":
                    var enrollment = BrokerEnrollmentCodec.ParseCanonical(File.ReadAllBytes(fixture.Paths.EnrollmentPath));
                    File.WriteAllBytes(fixture.Paths.EnrollmentPath,
                        BrokerEnrollmentCodec.SerializeCanonical(enrollment with { BrokerVersion = "0.58.5" }));
                    break;
                case "sequence":
                    File.WriteAllText(fixture.Paths.AcceptedSequencePath, "2");
                    break;
                case "signature":
                    File.WriteAllBytes(Path.Combine(fixture.StageDirectory(request), FixedLocalDevInboxSource.SignatureFileName),
                        new byte[FixedLocalDevInboxSource.SignatureBytes]);
                    break;
                case "package":
                    using (var package = File.OpenWrite(Path.Combine(fixture.StageDirectory(request),
                        FixedLocalDevInboxSource.InstallerFileName)))
                    {
                        package.SetLength(package.Length - 1);
                    }
                    break;
                case "pending-role":
                    string pending = fixture.Pending(request);
                    File.WriteAllText(pending, File.ReadAllText(pending).Replace(
                        "6235dfd6b3aa304f3be525d51de05733d3678cf006547a1f94cfed367a00885c",
                        new string('0', 64), StringComparison.Ordinal));
                    break;
                case "other-request":
                    string receipt = fixture.Pending(request);
                    File.WriteAllText(receipt, File.ReadAllText(receipt).Replace(
                        request.ToString("D"), Guid.NewGuid().ToString("D"), StringComparison.Ordinal));
                    break;
                case "pending-missing":
                    File.Delete(fixture.Pending(request));
                    break;
                case "claimed":
                    File.Copy(fixture.Pending(request), fixture.Claimed(request));
                    break;
            }
        }, expectedExit: 3, diagnostic);
        Assert.Equal("prior proof", File.ReadAllText(fixture.Committed));
        Assert.Equal(change == "claimed", File.Exists(fixture.Claimed(request)));
        Assert.Equal(change != "pending-missing", File.Exists(fixture.Pending(request)));
    }

    [Fact]
    public async Task CommitsExactRolesAndKeepsInstalledProofAfterBrokerCleanup()
    {
        using var fixture = await Fixture.CreateAsync();
        Guid request = Guid.NewGuid();
        var handoff = await fixture.PrepareAsync(request);
        var transaction = await fixture.StageAsync(handoff, request);
        Assert.False(File.Exists(fixture.Pending(request)));
        Assert.True(File.Exists(fixture.Claimed(request)));
        await transaction.ActivateAsync(default);
        Assert.False(File.Exists(fixture.Committed));
        await transaction.CommitAsync(default);

        byte[] committed = File.ReadAllBytes(fixture.Committed);
        using var json = JsonDocument.Parse(committed);
        JsonElement root = json.RootElement;
        Assert.Equal("SetupPayloadCommitted", root.GetProperty("phase").GetString());
        Assert.Equal(fixture.Installation, root.GetProperty("installationId").GetGuid());
        Assert.Equal(request, root.GetProperty("requestId").GetGuid());
        Assert.Equal(PackageHash, root.GetProperty("packageSha256").GetString());
        Assert.Equal(Version, root.GetProperty("version").GetString());
        Assert.Equal(Revision, root.GetProperty("sourceRevision").GetString());
        CheckRole(root.GetProperty("service"), @"payload\OpenBlade.Service.exe", 337920,
            "6235dfd6b3aa304f3be525d51de05733d3678cf006547a1f94cfed367a00885c");
        CheckRole(root.GetProperty("sessionAgent"), @"payload\OpenBlade.SessionAgent.exe", 144896,
            "ddd3112e117dd8ae4c8969aba0e91b7768a69e95bdb5bb0be54974a71efe38da");

        await Assert.ThrowsAnyAsync<IOException>(() => fixture.StageAsync(handoff, request));
        await fixture.ReconcileAsync(handoff, request);
        Assert.False(Directory.Exists(fixture.StageDirectory(request)));
        Assert.False(File.Exists(fixture.Paths.InFlightPath));
        handoff.VerifyCommitted(fixture.Tree.Install, default);
        Assert.Equal(committed, File.ReadAllBytes(fixture.Committed));
    }

    [Fact]
    public async Task RejectsOtherRequestInstallationRoleAndExistingEvidence()
    {
        using var fixture = await Fixture.CreateAsync();
        Guid request = Guid.NewGuid();
        Directory.CreateDirectory(fixture.StageDirectory(request));
        byte[] prior = "pre-existing request evidence"u8.ToArray();
        File.WriteAllBytes(fixture.Pending(request), prior);
        await Assert.ThrowsAnyAsync<IOException>(() => fixture.PrepareAsync(request));
        Assert.Equal(prior, File.ReadAllBytes(fixture.Pending(request)));
        Assert.False(File.Exists(fixture.Paths.InFlightPath));

        request = Guid.NewGuid();
        var handoff = await fixture.PrepareAsync(request);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.StageAsync(handoff, Guid.NewGuid()));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.StageAsync(handoff, request, Guid.NewGuid()));
        string agent = Path.Combine(fixture.Tree.Payload, "OpenBlade.SessionAgent.exe");
        byte[] bytes = File.ReadAllBytes(agent);
        bytes[^1] ^= 0x01;
        File.WriteAllBytes(agent, bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.StageAsync(handoff, request));
        Assert.True(File.Exists(fixture.Pending(request)));
        Assert.False(File.Exists(fixture.Claimed(request)));
        Assert.False(Directory.Exists(fixture.Tree.Install));
    }

    [Fact]
    public async Task RejectsDifferentSignedPackageAndLeavesPriorInstallAlone()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Tree.WriteInstall(WindowsCandidatePackageReceipt.CommittedName, "prior evidence"u8.ToArray());
        fixture.WriteSignedInbox(sequence: 1, changePackage: true);
        Guid request = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.PrepareAsync(request));
        Assert.False(Directory.Exists(fixture.StageDirectory(request)));
        Assert.False(File.Exists(fixture.Paths.InFlightPath));
        Assert.Equal("prior evidence", File.ReadAllText(fixture.Committed));
    }

    [Fact]
    public async Task RefusesStaleReceiptAndAclThenRestoresPriorProofAfterLostClaim()
    {
        using var fixture = await Fixture.CreateAsync();
        Guid first = Guid.NewGuid();
        var old = await fixture.PrepareAsync(first);
        byte[] stale = File.ReadAllBytes(fixture.Pending(first));
        var original = await fixture.StageAsync(old, first);
        await original.ActivateAsync(default);
        await original.CommitAsync(default);
        byte[] priorProof = File.ReadAllBytes(fixture.Committed);
        await fixture.ReconcileAsync(old, first);

        fixture.WriteSignedInbox(sequence: 2);
        Guid second = Guid.NewGuid();
        var current = await fixture.PrepareAsync(second);
        string pending = fixture.Pending(second);
        byte[] issued = File.ReadAllBytes(pending);
        File.WriteAllBytes(pending, stale);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.StageAsync(current, second));
        File.WriteAllBytes(pending, issued);

        var file = new FileInfo(pending);
        FileSecurity originalAcl = file.GetAccessControl();
        FileSecurity modifiedAcl = file.GetAccessControl();
        modifiedAcl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.ReadData, AccessControlType.Allow));
        file.SetAccessControl(modifiedAcl);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.StageAsync(current, second));
        // Restore the exact inherited DACL via recreation under the protected directory.
        File.Delete(pending);
        File.WriteAllBytes(pending, issued);
        Assert.Equal(originalAcl.GetSecurityDescriptorSddlForm(AccessControlSections.Access),
            file.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access));

        var update = await fixture.StageAsync(current, second);
        await update.ActivateAsync(default);
        File.Delete(fixture.Claimed(second));
        await Assert.ThrowsAnyAsync<IOException>(() => update.CommitAsync(default));
        Assert.False(File.Exists(fixture.Committed));
        await update.RollbackAsync(default);
        Assert.Equal(priorProof, File.ReadAllBytes(fixture.Committed));
        old.VerifyCommitted(fixture.Tree.Install, default);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.ReconcileAsync(current, second));
        Assert.True(File.Exists(fixture.Paths.InFlightPath));
    }

    [Fact]
    public async Task RetainsCommitAndInFlightStateIfBrokerCleanupFails()
    {
        using var fixture = await Fixture.CreateAsync();
        Guid request = Guid.NewGuid();
        var handoff = await fixture.PrepareAsync(request);
        var transaction = await fixture.StageAsync(handoff, request);
        await transaction.ActivateAsync(default);
        await transaction.CommitAsync(default);
        using (File.Open(Path.Combine(fixture.StageDirectory(request), FixedLocalDevInboxSource.InstallerFileName),
            FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Assert.ThrowsAnyAsync<IOException>(() => fixture.ReconcileAsync(handoff, request));
            Assert.True(File.Exists(fixture.Paths.InFlightPath));
            handoff.VerifyCommitted(fixture.Tree.Install, default);
        }
        await fixture.ReconcileAsync(handoff, request);
        handoff.VerifyCommitted(fixture.Tree.Install, default);
    }

    [Fact]
    public async Task ReconcileRejectsReplacedInFlightBindingWithSameRequestAndPackage()
    {
        using var fixture = await Fixture.CreateAsync();
        Guid request = Guid.NewGuid();
        var handoff = await fixture.PrepareAsync(request);
        var transaction = await fixture.StageAsync(handoff, request);
        await transaction.ActivateAsync(default);
        await transaction.CommitAsync(default);
        var binding = BrokerInFlightDeploymentCodec.ParseCanonical(File.ReadAllBytes(fixture.Paths.InFlightPath));
        byte[] replacement = BrokerInFlightDeploymentCodec.SerializeCanonical(binding with { TargetSequence = 9 });
        File.WriteAllBytes(fixture.Paths.InFlightPath, replacement);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.ReconcileAsync(handoff, request));
        Assert.Equal(replacement, File.ReadAllBytes(fixture.Paths.InFlightPath));
        Assert.True(File.Exists(fixture.Claimed(request)));
        handoff.VerifyCommitted(fixture.Tree.Install, default);
    }

    [Fact]
    public async Task ActivationRechecksTheLiveRoleBeforeReturningAndRemainsRollbackable()
    {
        using var fixture = await Fixture.CreateAsync();
        Guid request = Guid.NewGuid();
        var handoff = await fixture.PrepareAsync(request);
        string? staged = null;
        var transaction = await fixture.StageAsync(handoff, request, beforeMove: () =>
        {
            if (staged is { } directory)
            {
                staged = null;
                File.WriteAllText(Path.Combine(directory, "OpenBlade.Service.exe"), "substituted");
            }
        });
        staged = transaction.StagedPayloadDirectory;
        await Assert.ThrowsAsync<InvalidDataException>(() => transaction.ActivateAsync(default));
        Assert.True(transaction.IsActivated);
        await transaction.RollbackAsync(default);
        Assert.False(Directory.Exists(fixture.Tree.Install));
    }

    private static void CheckRole(JsonElement role, string path, int length, string digest)
    {
        Assert.Equal(path, role.GetProperty("PayloadPath").GetString());
        Assert.Equal(length, role.GetProperty("Length").GetInt32());
        Assert.Equal(digest, role.GetProperty("Sha256").GetString());
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ECDsa signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly byte[] package;
        private readonly BrokerCandidateReceiptStage broker;
        private readonly Guid scratchId;
        private readonly string keyId;

        private Fixture(string bundle)
        {
            package = File.ReadAllBytes(bundle);
            Assert.Equal(PackageHash, Convert.ToHexStringLower(SHA256.HashData(package)));
            var storage = WindowsCandidateReceiptStorage.CreateScratch();
            scratchId = storage.ScratchId;
            Tree = new(storage.Root);
            Paths = BrokerPaths.CreateForTests(storage.Root);
            broker = new(storage);
            keyId = OpenBladeSignedReleaseVerifier.ComputeSigningKeyId(signingKey.ExportSubjectPublicKeyInfo());
        }

        internal static async Task<Fixture> CreateAsync()
        {
            string bundle = Environment.GetEnvironmentVariable("OPENBLADE_PHASE_9X_LOCALDEV")
                ?? throw new InvalidOperationException("Supply the exact offline Phase 9x LocalDev bundle.");
            var fixture = new Fixture(bundle);
            try
            {
                using WindowsIdentity identity = WindowsIdentity.GetCurrent();
                await new BrokerEnrollmentStore(fixture.Paths).CreateOnceAsync(new(
                    BrokerEnrollmentCodec.CurrentSchemaVersion, fixture.Installation, identity.User!.Value,
                    "openblade-local-dev-v1", "0.58.4", fixture.keyId,
                    Convert.ToBase64String(fixture.signingKey.ExportSubjectPublicKeyInfo()), DateTimeOffset.UtcNow));
                fixture.Tree.CreateCompletePayload();
                string roles = Path.Combine(Path.GetDirectoryName(bundle)!, "payload", "WixAttachedContainer", "payload");
                foreach (string name in new[] { "OpenBlade.Service.exe", "OpenBlade.SessionAgent.exe" })
                {
                    File.Copy(Path.Combine(roles, name), Path.Combine(fixture.Tree.Payload, name), overwrite: true);
                }
                fixture.WriteSignedInbox(sequence: 1);
                return fixture;
            }
            catch
            {
                fixture.Dispose();
                throw;
            }
        }

        internal Guid Installation { get; } = Guid.NewGuid();
        internal BrokerPaths Paths { get; }
        internal SetupPayloadTransactionTests.TestTree Tree { get; }
        internal string Committed => Path.Combine(Tree.Install, WindowsCandidatePackageReceipt.CommittedName);
        internal string StageDirectory(Guid request) => Path.Combine(Paths.StagingDirectory, request.ToString("N"));
        internal string Pending(Guid request) => Path.Combine(StageDirectory(request), WindowsCandidatePackageReceipt.PendingName);
        internal string Claimed(Guid request) => Path.Combine(StageDirectory(request), WindowsCandidatePackageReceipt.ClaimedName);

        internal Task<WindowsCandidatePackageReceipt> PrepareAsync(Guid request) =>
            broker.PrepareAsync(request, BrokerInstalledState.NotInstalled, default);

        internal Task<ISetupPayloadTransaction> StageAsync(WindowsCandidatePackageReceipt receipt, Guid request,
            Guid? installation = null, Action? beforeMove = null) =>
            SetupPayloadTransaction.StageCandidateAsync(Tree.Paths, Tree.Payload, new WindowsSetupPayloadFileSystem(),
                receipt, installation ?? Installation, request, beforeMove ?? (() => { }), default);

        internal Task ReconcileAsync(WindowsCandidatePackageReceipt receipt, Guid request) =>
            broker.ReconcileCommittedAsync(receipt, Installation, request, default);

        internal async Task RunReceiverAsync(Action<Process> bind, int expectedExit, string? expectedDiagnostic = null)
        {
            string testDirectory = Path.GetDirectoryName(typeof(WindowsCandidatePackageReceiptTests).Assembly.Location)!;
            string image = Path.Combine(testDirectory, "OpenBlade.CandidateReceipt.Scratch.exe");
            using var child = Process.Start(new ProcessStartInfo(image)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardError = true,
            }) ?? throw new InvalidOperationException("Unable to start the scratch receiver.");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(75));
            try
            {
                bind(child);
                await child.StandardInput.BaseStream.WriteAsync(scratchId.ToByteArray(), deadline.Token);
                child.StandardInput.Close();
                var error = child.StandardError.ReadToEndAsync(deadline.Token);
                await child.WaitForExitAsync(deadline.Token);
                string diagnostics = await error;
                Assert.True(child.ExitCode == expectedExit,
                    $"Scratch receiver exit {child.ExitCode}, expected {expectedExit}. {diagnostics}");
                if (expectedDiagnostic is not null)
                {
                    Assert.Contains(expectedDiagnostic, diagnostics, StringComparison.Ordinal);
                }
            }
            finally
            {
                if (!child.HasExited)
                {
                    child.Kill();
                    await child.WaitForExitAsync(CancellationToken.None);
                }
            }
        }

        internal void WriteSignedInbox(long sequence, bool changePackage = false)
        {
            byte[] bytes = package;
            if (changePackage)
            {
                bytes = package.ToArray();
                bytes[^1] ^= 0x01;
            }
            byte[] manifest = OpenBladeReleaseManifestCodec.SerializeCanonical(new()
            {
                SchemaVersion = OpenBladeReleaseManifestCodec.CurrentSchemaVersion,
                SignatureAlgorithm = OpenBladeReleaseManifestCodec.SignatureAlgorithm,
                SigningKeyCurve = OpenBladeReleaseManifestCodec.SigningKeyCurve,
                SigningKeyId = keyId,
                InstallationId = Installation,
                Sequence = sequence,
                Channel = OpenBladeReleaseManifestCodec.LocalDevChannel,
                Product = OpenBladeReleaseManifestCodec.Product,
                Version = Version,
                SourceRevision = Revision,
                Architecture = OpenBladeReleaseManifestCodec.Architecture,
                MinimumWindowsBuild = OpenBladeReleaseManifestCodec.MinimumWindowsBuild,
                InstallerFileName = FixedLocalDevInboxSource.InstallerFileName,
                BundleId = OpenBladeReleaseManifestCodec.BundleId,
                ProviderKey = OpenBladeReleaseManifestCodec.ProviderKey,
                UpgradeCode = OpenBladeReleaseManifestCodec.UpgradeCode,
                InstallerSize = bytes.Length,
                InstallerSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            });
            byte[] signature = signingKey.SignHash(OpenBladeSignedReleaseVerifier.ComputeSigningDigest(manifest),
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            Directory.CreateDirectory(Paths.InboxDirectory);
            File.WriteAllBytes(Path.Combine(Paths.InboxDirectory, FixedLocalDevInboxSource.InstallerFileName), bytes);
            File.WriteAllBytes(Path.Combine(Paths.InboxDirectory, FixedLocalDevInboxSource.ManifestFileName), manifest);
            File.WriteAllBytes(Path.Combine(Paths.InboxDirectory, FixedLocalDevInboxSource.SignatureFileName), signature);
        }

        public void Dispose()
        {
            signingKey.Dispose();
            Tree.Dispose();
        }
    }
}
#endif
