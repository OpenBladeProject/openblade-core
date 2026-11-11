#if OPENBLADE_CANDIDATE_RECEIPT_FIXTURE
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using OpenBlade.Contracts;
using OpenBlade.Deployment;
using OpenBlade.DeploymentBroker;

namespace OpenBlade.Setup.Tests;

public sealed class WindowsCandidatePackageReceiptTests
{
    private const string PackageHash = "4079c110978af3008ef929fac309b9637e9e77b0c68a660dd8d956758449359f";
    private const string Revision = "af61a8c842429bc0564390dee39b466ee0da9023";
    private const string Version = "65535.1.2.3";

#if OPENBLADE_CANDIDATE_RECEIPT_RUST_PROBE
    [Fact]
    public async Task RustReopensCommittedBytesAfterSetupExitsAndRefusesInvalidEvidence()
    {
        using var fixture = await Fixture.CreateAsync();
        Guid first = Guid.NewGuid();
        var original = await fixture.PrepareAsync(first);
        byte[] startup = [];
        byte[] observedCommitment = [];
        await fixture.RunReceiverAsync(first, expectedExit: 0, observeStartup: async commitment =>
        {
            startup = File.ReadAllBytes(fixture.Startup);
            observedCommitment = commitment;
            Assert.Equal(SHA256.HashData(startup), commitment[..32]);
            Assert.False(File.Exists(fixture.Committed));
            await fixture.RunRustReaderAsync(commitment[..32], expectedRefusal: "");
            await fixture.RunRustReaderAsync(observedCommitment, "invalid scratch receipt commitment");
            await fixture.VerifyUnrelatedPeerIsRejectedAsync(first, commitment);
        });
        original.VerifyCommitted(fixture.Tree.Install, default);
        byte[] committed = File.ReadAllBytes(fixture.Committed);
        byte[] expected = SHA256.HashData(committed);
        byte[] claimed = File.ReadAllBytes(fixture.Claimed(first));
        Assert.False(File.Exists(fixture.Startup));
        File.WriteAllBytes(fixture.Startup, startup);
        await fixture.RunRustReaderAsync(observedCommitment, "invalid scratch receipt commitment");
        File.Delete(fixture.Startup);
        await fixture.RunRustReaderAsync(expected);

        string json = System.Text.Encoding.UTF8.GetString(committed);
        foreach ((byte[] bytes, string diagnostic) in new[]
        {
            (claimed, "invalid canonical"),
            (startup, "invalid canonical"),
            (System.Text.Encoding.UTF8.GetBytes(json + " "), "invalid canonical"),
            (System.Text.Encoding.UTF8.GetBytes(json.Replace("\"schema\":2", "\"schema\":2,\"schema\":2")),
                "invalid canonical"),
            (new byte[4097], "invalid canonical"),
            (System.Text.Encoding.UTF8.GetBytes(json.Replace("\"Role\":0", "\"Role\":1")), "invalid canonical"),
            (System.Text.Encoding.UTF8.GetBytes(json.Replace(first.ToString("D"), Guid.NewGuid().ToString("D"))),
                "authenticated release binding"),
        })
        {
            string replacement = fixture.Committed + ".replacement";
            File.WriteAllBytes(replacement, bytes);
            File.Move(replacement, fixture.Committed, overwrite: true);
            await fixture.RunRustReaderAsync(expected, diagnostic);
        }
        File.WriteAllBytes(fixture.Committed, committed);

        string image = Path.Combine(fixture.Tree.Install, "OpenBlade.Service.exe");
        byte[] imageBytes = File.ReadAllBytes(image);
        File.WriteAllBytes(image, imageBytes[..^1]);
        await fixture.RunRustReaderAsync(expected, "Service length");
        byte[] modified = imageBytes.ToArray();
        modified[^1] ^= 1;
        File.WriteAllBytes(image, modified);
        await fixture.RunRustReaderAsync(expected, "authenticated build evidence");
        File.WriteAllBytes(image, imageBytes);

        using (File.Open(fixture.Committed, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await fixture.RunRustReaderAsync(expected, expectedRefusal: "");
        }
        var file = new FileInfo(fixture.Committed);
        FileSecurity modifiedAcl = file.GetAccessControl();
        modifiedAcl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.ReadData, AccessControlType.Allow));
        file.SetAccessControl(modifiedAcl);
        await fixture.RunRustReaderAsync(expected, "untrusted");
        File.Delete(fixture.Committed);
        File.WriteAllBytes(fixture.Committed, committed);
        await fixture.RunRustReaderAsync(expected);
    }

    [Theory]
    [InlineData("startup-receipt")]
    [InlineData("session-agent")]
    public async Task AuthenticatedScratchIssuerStillRequiresExactDiskReadback(string changed)
    {
        using var fixture = await Fixture.CreateAsync();
        Guid request = Guid.NewGuid();
        await fixture.PrepareAsync(request);
        string diagnostic = changed == "startup-receipt" ? "differs from the authenticated release binding"
            : "Session Agent verification failed";
        await fixture.RunReceiverAsync(request, expectedExit: 3, diagnostic, observeStartup: _ =>
        {
            string path = changed == "startup-receipt" ? fixture.Startup
                : Path.Combine(fixture.Tree.Install, "OpenBlade.SessionAgent.exe");
            byte[] bytes = File.ReadAllBytes(path);
            if (changed == "startup-receipt")
            {
                // Change canonical data while keeping the published digest stale.
                string json = System.Text.Encoding.UTF8.GetString(bytes);
                bytes = System.Text.Encoding.UTF8.GetBytes(json.Replace(request.ToString("D"),
                    Guid.NewGuid().ToString("D"), StringComparison.Ordinal));
            }
            else
            {
                bytes = bytes[..^1];
            }
            File.WriteAllBytes(path, bytes);
            return Task.CompletedTask;
        });
        Assert.False(File.Exists(fixture.Startup));
        Assert.False(File.Exists(fixture.Committed));
    }
#endif

#if OPENBLADE_CANDIDATE_RECEIPT_RUST_PROBE
    [Fact]
    public async Task ReceiverReopensAfterBrokerBindsItsProcessAndConsumesReceiptOnce()
    {
        using var fixture = await Fixture.CreateAsync();
        Guid request = Guid.NewGuid();
        var issued = await fixture.PrepareAsync(request);
        byte[] pending = File.ReadAllBytes(fixture.Pending(request));
        byte[] beforeLaunch = File.ReadAllBytes(fixture.Paths.InFlightPath);

        await fixture.RunReceiverAsync(request, expectedExit: 0);

        Assert.NotEqual(beforeLaunch, File.ReadAllBytes(fixture.Paths.InFlightPath));
        Assert.False(File.Exists(fixture.Pending(request)));
        Assert.Equal(pending, File.ReadAllBytes(fixture.Claimed(request)));
        issued.VerifyCommitted(fixture.Tree.Install, default);
        byte[] committed = File.ReadAllBytes(fixture.Committed);

        await fixture.RunReceiverAsync(request, expectedExit: 3, "no active launch binding", publishBinding: false);
        Assert.Equal(committed, File.ReadAllBytes(fixture.Committed));

        using var refused = await Fixture.CreateAsync();
        Guid oldRequest = Guid.NewGuid();
        var old = await refused.PrepareAsync(oldRequest);
        await using (var prior = await refused.StageAsync(old, oldRequest))
        {
            await prior.ActivateAsync(default);
            await prior.CommitAsync(default);
        }
        byte[] priorCommit = File.ReadAllBytes(refused.Committed);
        await refused.ReconcileAsync(old, oldRequest);
        refused.WriteSignedInbox(sequence: 2);
        Guid update = Guid.NewGuid();
        await refused.PrepareAsync(update);
        await refused.RunReceiverAsync(update, expectedExit: 3, "fixture build pin", observeStartup: _ =>
        {
            Assert.True(File.Exists(refused.Startup));
            Assert.False(File.Exists(refused.Committed));
            File.AppendAllText(refused.ReadinessImage, "changed");
            return Task.CompletedTask;
        });
        Assert.False(File.Exists(refused.Startup));
        Assert.Equal(priorCommit, File.ReadAllBytes(refused.Committed));
        old.VerifyCommitted(refused.Tree.Install, default);
    }
#endif

    [Theory]
    [InlineData("generic")]
    [InlineData("request")]
    [InlineData("deployment-request")]
    [InlineData("commitment")]
    [InlineData("step")]
    [InlineData("duplicate")]
    [InlineData("truncated")]
    public async Task ScratchReadinessRejectsGenericStaleAndMalformedFrames(string fault)
    {
        var request = new WindowsScratchCandidateReadiness.StartupFrame("verifyProvisional",
            OpenBladeIpc.ProtocolVersion, Guid.NewGuid(), Guid.NewGuid(), 2, new string('a', 152));
        var response = request with { Frame = "provisionalReady" };
        using var frame = new MemoryStream();
        await IpcMessageCodec.WriteFramedAsync(frame, response, default);
        frame.Position = 0;
        await WindowsScratchCandidateReadiness.ReadResponseAsync(frame, request, default);
        frame.SetLength(0);
        frame.Position = 0;
        object stale = fault switch
        {
            "generic" => new { Frame = "response", response.Version, response.RequestId, Success = true },
            "request" => response with { RequestId = Guid.NewGuid() },
            "deployment-request" => response with { DeploymentRequestId = Guid.NewGuid() },
            "commitment" => response with { Commitment = new string('b', 152) },
            "step" => response with { Step = 1 },
            "duplicate" => JsonDocument.Parse($$"""{"frame":"provisionalReady","version":79,"version":79,"requestId":"{{request.RequestId}}","deploymentRequestId":"{{request.DeploymentRequestId}}","step":2,"commitment":"{{request.Commitment}}"}""").RootElement,
            _ => response,
        };
        await IpcMessageCodec.WriteFramedAsync(frame, stale, default);
        if (fault == "truncated")
        {
            frame.SetLength(frame.Length - 1);
        }
        frame.Position = 0;
        if (fault == "truncated")
        {
            await Assert.ThrowsAsync<EndOfStreamException>(() => WindowsScratchCandidateReadiness.ReadResponseAsync(frame, request, default));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => WindowsScratchCandidateReadiness.ReadResponseAsync(frame, request, default));
        }
    }

    [Theory]
    [InlineData("unbound", "no active launch binding")]
    [InlineData("other-pid", "no active launch binding")]
    [InlineData("old-generation", "launch generations differ")]
    [InlineData("terminal", "no active launch binding")]
    [InlineData("immutable-state", "differs from the issued record")]
    [InlineData("pid-only", "receipt path could not be retained")]
    [InlineData("wrong-secret", "differs from the issued record")]
    [InlineData("receiver-generation", "differs from the issued record")]
    [InlineData("broker-generation", "differs from the issued record")]
    [InlineData("image-digest", "differs from the issued record")]
    [InlineData("wrong-image", "differs from its fixed launch image")]
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
        bool pidOnly = change is "unbound" or "other-pid" or "old-generation" or "pid-only" or "wrong-image";
        await fixture.RunReceiverAsync(request, expectedExit: 3, diagnostic, afterStart: process =>
        {
            if (change == "unbound")
            {
                return;
            }
            if (pidOnly)
            {
                state.BindInstallerProcess(request, change == "other-pid" ? Environment.ProcessId : process.Id,
                    change == "old-generation" ? process.StartTime.ToUniversalTime().AddTicks(-1) : DateTimeOffset.UtcNow);
            }
            switch (change)
            {
                case "broker-generation":
                case "receiver-generation":
                case "image-digest":
                    string launch = Path.Combine(fixture.StageDirectory(request), ScratchCandidateLaunch.EvidenceName);
                    byte[] evidence = File.ReadAllBytes(launch);
                    int offset = change switch { "broker-generation" => 4, "receiver-generation" => 16, _ => 56 };
                    evidence[offset] ^= 1;
                    File.WriteAllBytes(launch, evidence);
                    break;
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
        }, publishBinding: !pidOnly, wrongSecret: change == "wrong-secret", wrongImage: change == "wrong-image");
        Assert.Equal("prior proof", File.ReadAllText(fixture.Committed));
        Assert.Equal(change == "claimed", File.Exists(fixture.Claimed(request)));
        Assert.Equal(change != "pending-missing", File.Exists(fixture.Pending(request)));
    }

    [Fact]
    public async Task ClosingTheRendezvousLeavesTheReceiptPendingAndTheExitedChildCannotBeBound()
    {
        using var fixture = await Fixture.CreateAsync();
        Guid request = Guid.NewGuid();
        await fixture.PrepareAsync(request);
        byte[] before = File.ReadAllBytes(fixture.Paths.InFlightPath);

        await fixture.RunReceiverAsync(request, expectedExit: 3, publishBinding: false, closeWithoutRelease: true);

        Assert.Equal(before, File.ReadAllBytes(fixture.Paths.InFlightPath));
        Assert.True(File.Exists(fixture.Pending(request)));
        Assert.False(File.Exists(fixture.Claimed(request)));
        Assert.False(File.Exists(fixture.Committed));
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
#if OPENBLADE_CANDIDATE_RECEIPT_RUST_PROBE
        byte[] priorCommitment = SHA256.HashData(priorProof);
        await fixture.RunRustReaderAsync(priorCommitment);
#endif

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
        Assert.True(File.Exists(fixture.Startup));
        // An outstanding startup reader prevents retirement and commit.
        using (File.Open(fixture.Startup, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Assert.ThrowsAnyAsync<IOException>(() => update.CommitAsync(default));
            Assert.False(File.Exists(fixture.Committed));
        }
        File.Delete(fixture.Claimed(second));
        await Assert.ThrowsAnyAsync<IOException>(() => update.CommitAsync(default));
        Assert.False(File.Exists(fixture.Committed));
#if OPENBLADE_CANDIDATE_RECEIPT_RUST_PROBE
        await fixture.RunRustReaderAsync(priorCommitment, expectedRefusal: "");
#endif
        await update.RollbackAsync(default);
        Assert.False(File.Exists(fixture.Startup));
        Assert.Equal(priorProof, File.ReadAllBytes(fixture.Committed));
        old.VerifyCommitted(fixture.Tree.Install, default);
#if OPENBLADE_CANDIDATE_RECEIPT_RUST_PROBE
        await fixture.RunRustReaderAsync(priorCommitment);
#endif
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.ReconcileAsync(current, second));
        Assert.True(File.Exists(fixture.Paths.InFlightPath));
    }

    [Fact]
    public async Task LostReadinessAtPublicationRollsBackToPriorReceipt()
    {
        using var fixture = await Fixture.CreateAsync();
        Guid first = Guid.NewGuid();
        var prior = await fixture.PrepareAsync(first);
        await using (var transaction = await fixture.StageAsync(prior, first))
        {
            await transaction.ActivateAsync(default);
            await transaction.CommitAsync(default);
        }
        byte[] committed = File.ReadAllBytes(fixture.Committed);
        await fixture.ReconcileAsync(prior, first);
        fixture.WriteSignedInbox(sequence: 2);
        Guid second = Guid.NewGuid();
        var incoming = await fixture.PrepareAsync(second);
        await using var update = await fixture.StageAsync(incoming, second, verifyReadinessAtPublication: () =>
        {
            Assert.False(File.Exists(fixture.Startup));
            Assert.False(File.Exists(fixture.Committed));
            throw new InvalidDataException("The fixture generation ended before publication.");
        });
        await update.ActivateAsync(default);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => update.CommitAsync(default));
        Assert.Contains("generation ended", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.Committed));
        await update.RollbackAsync(default);
        Assert.Equal(committed, File.ReadAllBytes(fixture.Committed));
        prior.VerifyCommitted(fixture.Tree.Install, default);
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

        using var blocked = await Fixture.CreateAsync();
        Guid blockedRequest = Guid.NewGuid();
        var blockedReceipt = await blocked.PrepareAsync(blockedRequest);
        string? blockedStage = null;
        await using var blockedTransaction = await blocked.StageAsync(blockedReceipt, blockedRequest, beforeMove: () =>
        {
            if (blockedStage is { } directory)
            {
                blockedStage = null;
                File.WriteAllText(Path.Combine(directory, WindowsCandidatePackageReceipt.StartupName), "stale startup");
            }
        });
        blockedStage = blockedTransaction.StagedPayloadDirectory;
        await Assert.ThrowsAnyAsync<IOException>(() => blockedTransaction.ActivateAsync(default));
        Assert.True(blockedTransaction.IsActivated);
        Assert.Equal("stale startup", File.ReadAllText(blocked.Startup));
        Assert.False(File.Exists(blocked.Committed));
        await blockedTransaction.RollbackAsync(default);
        Assert.False(Directory.Exists(blocked.Tree.Install));
    }

    [Fact]
    public async Task ScratchFilesOnlyReopenRejectsCanonicalInFlightMismatchAfterValidLaunchEvidence()
    {
        using var fixture = await Fixture.CreateAsync();
        Guid request = Guid.NewGuid();
        await fixture.PrepareAsync(request);
        byte[] originalPending = File.ReadAllBytes(fixture.Pending(request));
        var issued = BrokerInFlightDeploymentCodec.ParseCanonical(File.ReadAllBytes(fixture.Paths.InFlightPath));
        DateTimeOffset issuedAt = WindowsCandidatePackageReceipt.ReadIssuedAt(originalPending);
        // Simulate an internally consistent active launch over altered immutable
        // state. No child, process lookup, launch admission or IPC is performed.
        var changed = issued with
        {
            PreviousPayloadSha256 = new string('1', 64),
            InstallerProcessId = Environment.ProcessId,
            UpdatedAt = issuedAt.AddSeconds(1),
        };
        byte[] active = BrokerInFlightDeploymentCodec.SerializeCanonical(changed);
        File.WriteAllBytes(fixture.Paths.InFlightPath, active);
        using var current = Process.GetCurrentProcess();
        fixture.CopyReceiverFiles();
        using var closure = ScratchReceiverClosure.Retain(fixture.Storage);
        byte[] secret = new byte[ScratchCandidateLaunch.SecretLength];
        byte[] earlierEvidence = ScratchCandidateLaunch.Encode(active, current, current, closure, secret);
        string evidencePath = Path.Combine(fixture.StageDirectory(request), ScratchCandidateLaunch.EvidenceName);
        fixture.Storage.Publish(evidencePath, earlierEvidence);
        using (var evidence = fixture.Storage.OpenFile(evidencePath))
        {
            WindowsCandidateReceiptStorage.RequireExact(evidence,
                ScratchCandidateLaunch.Encode(active, current, current, closure, secret));
        }

        byte[] manifest = File.ReadAllBytes(Path.Combine(fixture.StageDirectory(request), FixedLocalDevInboxSource.ManifestFileName));
        byte[] enrollment = File.ReadAllBytes(fixture.Paths.EnrollmentPath);
        var enrolled = BrokerEnrollmentCodec.ParseCanonical(enrollment);
        var trust = new OpenBladeReleaseTrustPolicy(true,
            [new(enrolled.DevelopmentSigningKeyId, enrolled.DevelopmentSigningSubjectPublicKeyInfoBase64)]);
        var release = OpenBladeSignedReleaseVerifier.Verify(manifest,
            File.ReadAllBytes(Path.Combine(fixture.StageDirectory(request), FixedLocalDevInboxSource.SignatureFileName)), trust);
        using var package = fixture.Storage.OpenFile(Path.Combine(fixture.StageDirectory(request), FixedLocalDevInboxSource.InstallerFileName));
        var proof = await WindowsNativePackageRoleProof.ReadHeldCandidateAsync(package, release,
            new(PackageHash, new(Revision, System.Version.Parse(Version))), default);
        byte[] reconstructed = BrokerInFlightDeploymentCodec.SerializeCanonical(changed with
        {
            InstallerProcessId = null,
            UpdatedAt = issuedAt,
        });
        var error = Assert.Throws<InvalidDataException>(() => WindowsCandidatePackageReceipt.Reopen(fixture.Storage,
            proof, release, manifest, enrollment, reconstructed, active, request, issuedAt, originalPending));
        Assert.Contains("pending candidate receipt differs", error.Message, StringComparison.Ordinal);
        Assert.Equal(originalPending, File.ReadAllBytes(fixture.Pending(request)));
        Assert.Equal(active, File.ReadAllBytes(fixture.Paths.InFlightPath));
        Assert.Equal(earlierEvidence, File.ReadAllBytes(evidencePath));
        Assert.False(File.Exists(fixture.Claimed(request)));
        Assert.False(File.Exists(fixture.Startup));
        Assert.False(File.Exists(fixture.Committed));
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
        private readonly WindowsCandidateReceiptStorage storage;
        internal WindowsCandidateReceiptStorage Storage => storage;
        private Guid ScratchId => storage.ScratchId;
        private readonly string keyId;

        private Fixture(string bundle)
        {
            package = File.ReadAllBytes(bundle);
            Assert.Equal(PackageHash, Convert.ToHexStringLower(SHA256.HashData(package)));
            storage = WindowsCandidateReceiptStorage.CreateScratch();
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
        internal string Startup => Path.Combine(Tree.Install, WindowsCandidatePackageReceipt.StartupName);
        internal string Committed => Path.Combine(Tree.Install, WindowsCandidatePackageReceipt.CommittedName);
        internal string ReadinessImage => Path.Combine(storage.Root, "receiver", WindowsScratchCandidateReadiness.ImageName);
        internal string StageDirectory(Guid request) => Path.Combine(Paths.StagingDirectory, request.ToString("N"));
        internal string Pending(Guid request) => Path.Combine(StageDirectory(request), WindowsCandidatePackageReceipt.PendingName);
        internal string Claimed(Guid request) => Path.Combine(StageDirectory(request), WindowsCandidatePackageReceipt.ClaimedName);

        internal Task<WindowsCandidatePackageReceipt> PrepareAsync(Guid request) =>
            broker.PrepareAsync(request, BrokerInstalledState.NotInstalled, default);

        internal Task<ISetupPayloadTransaction> StageAsync(WindowsCandidatePackageReceipt receipt, Guid request,
            Guid? installation = null, Action? beforeMove = null, Action? verifyReadinessAtPublication = null) =>
            SetupPayloadTransaction.StageCandidateAsync(Tree.Paths, Tree.Payload, new WindowsSetupPayloadFileSystem(),
                receipt, installation ?? Installation, request, beforeMove ?? (() => { }), default,
                verifyReadinessAtPublication: verifyReadinessAtPublication);

        internal Task ReconcileAsync(WindowsCandidatePackageReceipt receipt, Guid request) =>
            broker.ReconcileCommittedAsync(receipt, Installation, request, default);

        internal void CopyReceiverFiles()
        {
            string source = AppContext.BaseDirectory;
            string directory = Path.GetDirectoryName(ScratchCandidateLaunch.ReceiverPath(storage))!;
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
                string[] names = [ScratchCandidateLaunch.ReceiverName];
                foreach (string name in names.Append(WindowsScratchCandidateReadiness.ImageName))
                {
                    string path = Path.Combine(source, name);
                    if (name != WindowsScratchCandidateReadiness.ImageName || File.Exists(path))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(directory, name))!);
                        File.Copy(path, Path.Combine(directory, name));
                    }
                }
            }

        }

        internal async Task RunReceiverAsync(Guid request, int expectedExit, string? expectedDiagnostic = null,
            Action<Process>? afterStart = null, bool publishBinding = true, bool wrongSecret = false,
            bool wrongImage = false, bool closeWithoutRelease = false,
            Func<byte[], Task>? observeStartup = null)
        {
            string source = AppContext.BaseDirectory;
            string image = ScratchCandidateLaunch.ReceiverPath(storage);
            string directory = Path.GetDirectoryName(image)!;
            CopyReceiverFiles();

            using var root = storage.PinDirectory(directory);
            using var heldClosure = ScratchReceiverClosure.Retain(storage);
            byte[] secret = RandomNumberGenerator.GetBytes(ScratchCandidateLaunch.SecretLength);
            Task Run() => RunScratchChildAsync(wrongImage ? Path.Combine(source, ScratchCandidateLaunch.ReceiverName) : image,
                closeWithoutRelease ? [] : [.. ScratchId.ToByteArray(), .. (wrongSecret ? new byte[secret.Length] : secret)],
                expectedExit, expectedDiagnostic, process =>
                {
                    if (publishBinding)
                    {
                        ScratchCandidateLaunch.Bind(storage, request, process, heldClosure, secret);
                    }
                    afterStart?.Invoke(process);
                }, afterExit: closeWithoutRelease ? child => Assert.Throws<InvalidDataException>(
                    () => ScratchCandidateLaunch.Bind(storage, request, child, heldClosure, secret)) : null,
                observeStartup: observeStartup ?? (expectedExit == 0 ? _ => Task.CompletedTask : null), nativeClosure: heldClosure);
            if (wrongImage)
            {
                var error = await Assert.ThrowsAsync<InvalidDataException>(Run);
                Assert.Contains("differs from its fixed launch image", error.Message, StringComparison.Ordinal);
            }
            else
            {
                await Run();
            }
        }

#if OPENBLADE_CANDIDATE_RECEIPT_RUST_PROBE
        internal Task RunRustReaderAsync(byte[] expected, string? expectedRefusal = null) =>
            RunScratchChildAsync("openblade-scratch-candidate-receipt.exe",
                [.. System.Text.Encoding.ASCII.GetBytes(ScratchId.ToString("D")), .. expected],
                expectedRefusal is null ? 0 : 3, expectedRefusal, timeoutSeconds: 15);

        internal Task VerifyUnrelatedPeerIsRejectedAsync(Guid request, byte[] commitment)
        {
            string endpoint = Guid.NewGuid().ToString("D");
            return RunScratchChildAsync(WindowsScratchCandidateReadiness.ImageName,
                [.. System.Text.Encoding.ASCII.GetBytes(ScratchId.ToString("D") + endpoint), .. new byte[32]],
                3, "peer is not the fixed scratch receiver image", timeoutSeconds: 15,
                exchange: async token =>
                {
                    using var pipe = new NamedPipeClientStream(".", "OpenBlade.Rust.CandidateReceipt.Test." + endpoint,
                        PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
                    await pipe.ConnectAsync(token);
                    var frame = new WindowsScratchCandidateReadiness.StartupFrame("verifyProvisional",
                        OpenBladeIpc.ProtocolVersion, Guid.NewGuid(), request, 1, Convert.ToHexStringLower(commitment));
                    await IpcMessageCodec.WriteFramedAsync(pipe, frame, token);
                    Assert.Equal(0, await pipe.ReadAsync(new byte[1], token));
                });
        }
#endif

        private static async Task RunScratchChildAsync(string name, byte[] input, int expectedExit,
            string? expectedDiagnostic, Action<Process>? bind = null, int timeoutSeconds = 75,
            Action<Process>? afterExit = null, Func<byte[], Task>? observeStartup = null,
            Func<CancellationToken, Task>? exchange = null, ScratchReceiverClosure? nativeClosure = null)
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            if (identity.IsSystem || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            {
                throw new UnauthorizedAccessException("Scratch child processes require a standard Windows token.");
            }
            string testDirectory = Path.GetDirectoryName(typeof(WindowsCandidatePackageReceiptTests).Assembly.Location)!;
            string image = Path.Combine(testDirectory, name);
            if (nativeClosure is not null) { ScratchFixtureBuildPin.Require(image); }
            using var native = nativeClosure is null ? null : ScratchNativeReceiverLaunch.Start(image, nativeClosure);
            using var ordinary = native is null ? Process.Start(new ProcessStartInfo(image)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                RedirectStandardOutput = observeStartup is not null || exchange is not null,
            }) : null;
            var child = native?.Process ?? ordinary ?? throw new InvalidOperationException($"Unable to start scratch child {name}.");
            var childInput = native?.StandardInput ?? child.StandardInput;
            var childError = native?.StandardError ?? child.StandardError;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            try
            {
                bind?.Invoke(child);
                await childInput.BaseStream.WriteAsync(input, deadline.Token);
                var error = childError.ReadToEndAsync(deadline.Token);
                if (observeStartup is not null)
                {
                    byte[] commitment = new byte[76];
                    await (native?.StandardOutput ?? child.StandardOutput).BaseStream.ReadExactlyAsync(commitment, deadline.Token);
                    await observeStartup(commitment);
                }
                childInput.Close();
                if (exchange is not null)
                {
                    byte[] marker = new byte[1];
                    await (native?.StandardOutput ?? child.StandardOutput).BaseStream.ReadExactlyAsync(marker, deadline.Token)
                        .AsTask().WaitAsync(deadline.Token);
                    Assert.Equal(WindowsScratchCandidateReadiness.PipeClaimedMarker, marker[0]);
                    await exchange(deadline.Token);
                }
                await child.WaitForExitAsync(deadline.Token);
                string diagnostics = await error;
                Assert.True(child.ExitCode == expectedExit,
                    $"{name} exit {child.ExitCode}, expected {expectedExit}. {diagnostics}");
                if (expectedDiagnostic is { Length: > 0 })
                {
                    Assert.True(diagnostics.Contains(expectedDiagnostic, StringComparison.Ordinal),
                        $"{name} did not report {expectedDiagnostic}. {diagnostics}");
                }
                afterExit?.Invoke(child);
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
