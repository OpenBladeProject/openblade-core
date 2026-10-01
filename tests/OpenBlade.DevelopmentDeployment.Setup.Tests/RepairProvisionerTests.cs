using OpenBlade.DeploymentBroker;
using OpenBlade.DevelopmentDeployment.Setup;

namespace OpenBlade.DevelopmentDeployment.Setup.Tests;

public sealed class RepairProvisionerTests
{
    [Fact]
    public void RepairStopExplicitlyExcludesDependentServices()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        string? sourcePath = null;
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "src", "OpenBlade.DevelopmentDeployment.Setup",
                "DevelopmentDeploymentProvisioner.cs");
            if (File.Exists(candidate))
            {
                sourcePath = candidate;
                break;
            }
            directory = directory.Parent;
        }
        Assert.NotNull(sourcePath);
        string source = File.ReadAllText(sourcePath);
        Assert.Contains("service.Stop(stopDependentServices: false);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("service.Stop();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("service.DependentServices", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ActiveDependentRefusalPreventsReplacementAndDemandRestoration()
    {
        bool disabled = false;
        int copies = 0;
        int restores = 0;
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DevelopmentDeploymentProvisioner.RunRepairTransactionAsync(_ =>
            {
                disabled = true;
                // ERROR_DEPENDENT_SERVICES_RUNNING from non-recursive SCM stop.
                return Task.FromException(new System.ComponentModel.Win32Exception(1051));
            }, _ => { copies++; return Task.CompletedTask; }, () => restores++, default));
        Assert.Equal(1051, Assert.IsType<System.ComponentModel.Win32Exception>(failure.InnerException).NativeErrorCode);
        Assert.True(disabled);
        Assert.Equal(0, copies);
        Assert.Equal(0, restores);
    }

    private static BrokerEnrollment Enrollment => new(1,
        Guid.Parse("11111111-1111-1111-1111-111111111111"), "S-1-5-21-1-2-3-1001",
        "openblade-local-dev-v1", "0.24.0", Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("spki"u8)), "c3BraQ==", DateTimeOffset.UnixEpoch);

    [Fact]
    public void SameVersionIdentityAndReceiptAreAcceptedWithoutReenrollment()
    {
        var enrolled = Enrollment;
        DevelopmentDeploymentProvisioner.RequireRepairIdentity(enrolled,
            new(enrolled.DevelopmentSigningKeyId, enrolled.DevelopmentSigningSubjectPublicKeyInfoBase64),
            enrolled.EnrolledAccountSid, "0.24.0");
        DevelopmentDeploymentProvisioner.RequireRepairReceipt(enrolled,
            DevelopmentDeploymentProvisioner.CreateReceipt(enrolled));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("sid")]
    [InlineData("policy")]
    [InlineData("keyId")]
    [InlineData("publicKey")]
    [InlineData("schema")]
    public void RepairRejectsIdentityChanges(string changed)
    {
        var enrolled = Enrollment;
        var trust = new DevelopmentTrustKey(enrolled.DevelopmentSigningKeyId, enrolled.DevelopmentSigningSubjectPublicKeyInfoBase64);
        Assert.Throws<InvalidDataException>(() => DevelopmentDeploymentProvisioner.RequireRepairIdentity(
            changed switch
            {
                "policy" => enrolled with { DeploymentPolicyId = "another-policy" },
                "schema" => enrolled with { SchemaVersion = 2 },
                _ => enrolled,
            },
            changed switch
            {
                "keyId" => trust with { KeyId = new string('b', 64) },
                "publicKey" => trust with { SubjectPublicKeyInfoBase64 = "bmV3" },
                _ => trust,
            }, changed == "sid" ? "S-1-5-21-1-2-3-1002" : enrolled.EnrolledAccountSid,
            changed == "version" ? "0.25.0" : "0.24.0"));
    }

    [Fact]
    public void ReceiptCannotSubstituteAnotherInstallation()
    {
        byte[] otherReceipt = DevelopmentDeploymentProvisioner.CreateReceipt(Enrollment with { InstallationId = Guid.NewGuid() });
        Assert.Throws<InvalidDataException>(() => DevelopmentDeploymentProvisioner.RequireRepairReceipt(Enrollment, otherReceipt));
    }

    [Theory]
    [InlineData("extra.exe")]
    [InlineData("extra-directory")]
    public void RepairRequiresOnlyTheFourFixedPackageFiles(string extra)
    {
        using var fixture = new Files();
        foreach (string name in new[] { "OpenBlade.DeploymentBroker.exe", "OpenBlade.DeploymentClient.exe",
            "OpenBlade.DevelopmentDeployment.Setup.exe", "OpenBlade.LocalDevDeployment.json" })
        {
            File.WriteAllText(Path.Combine(fixture.Root, name), "package");
        }
        DevelopmentDeploymentProvisioner.RequireExactRepairPackage(fixture.Root);
        if (extra.EndsWith(".exe", StringComparison.Ordinal))
        {
            File.WriteAllText(Path.Combine(fixture.Root, extra), "unexpected");
        }
        else
        {
            Directory.CreateDirectory(Path.Combine(fixture.Root, extra));
        }
        Assert.Throws<InvalidDataException>(() => DevelopmentDeploymentProvisioner.RequireExactRepairPackage(fixture.Root));
    }

    [Fact]
    public async Task StopFailureMakesNoProtectedCopies()
    {
        using var files = new Files();
        files.CreateTargets();
        await Assert.ThrowsAsync<System.TimeoutException>(() => DevelopmentDeploymentProvisioner.ReplaceRepairPayloadsAsync(
            NewPayload(), files.Broker, NewPayload(), files.Client,
            _ => Task.FromException(new System.TimeoutException("stop deadline")), default));
        files.AssertOriginals();
        Assert.Equal(2, Directory.GetFiles(files.Root).Length);
    }

    [Fact]
    public async Task ReplacementWaitsForConfirmedStopAndPreservesUnrelatedState()
    {
        using var files = new Files();
        files.CreateTargets();
        string[] history = ["enrollment.json", "receipt.json", "inbox/request.json", "journal/request.json", "replay/request.json"];
        foreach (string relative in history)
        {
            string path = Path.Combine(files.Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "immutable " + relative);
        }
        var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task repair = DevelopmentDeploymentProvisioner.ReplaceRepairPayloadsAsync(NewPayload(), files.Broker,
            NewPayload(), files.Client, _ => stop.Task, default);
        files.AssertOriginals();
        Assert.Equal(7, Directory.GetFiles(files.Root, "*", SearchOption.AllDirectories).Length);
        stop.SetResult();
        await repair;
        Assert.Equal("new", File.ReadAllText(files.Broker));
        Assert.Equal("new", File.ReadAllText(files.Client));
        foreach (string relative in history)
        {
            Assert.Equal("immutable " + relative, File.ReadAllText(Path.Combine(files.Root, relative)));
        }
        Assert.Equal(7, Directory.GetFiles(files.Root, "*", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public async Task SecondReplacementFailureRestoresFirstAndDoesNotRetry()
    {
        using var files = new Files();
        files.CreateTargets();
        int attempts = 0;
        await Assert.ThrowsAsync<IOException>(() => DevelopmentDeploymentProvisioner.ReplaceRepairPayloadsAsync(
            NewPayload(), files.Broker, NewPayload(), files.Client, _ => Task.CompletedTask, default,
            (source, destination, backup) =>
            {
                attempts++;
                if (attempts == 2)
                {
                    throw new IOException("broker locked");
                }
                File.Replace(source, destination, backup);
            }));
        Assert.Equal(3, attempts); // First replacement, failed second, one rollback.
        files.AssertOriginals();
        Assert.Equal(2, Directory.GetFiles(files.Root).Length);
    }

    [Fact]
    public async Task FailedRollbackRetainsProtectedBackupAndReportsBothFailures()
    {
        using var files = new Files();
        files.CreateTargets();
        int attempts = 0;
        var error = await Assert.ThrowsAsync<AggregateException>(() => DevelopmentDeploymentProvisioner.ReplaceRepairPayloadsAsync(
            NewPayload(), files.Broker, NewPayload(), files.Client, _ => Task.CompletedTask, default,
            (source, destination, backup) =>
            {
                if (++attempts > 1)
                {
                    throw new IOException("replacement unavailable");
                }
                File.Replace(source, destination, backup);
            }));
        Assert.Equal(2, error.InnerExceptions.Count);
        Assert.Equal("old broker", File.ReadAllText(files.Broker));
        Assert.Equal("new", File.ReadAllText(files.Client));
        string backupFile = Assert.Single(Directory.GetFiles(files.Root, "*.old"));
        Assert.Equal("old client", File.ReadAllText(backupFile));
        Assert.Empty(Directory.GetFiles(files.Root, "*.new"));
    }

    [Fact]
    public async Task CancellationAfterStopDoesNotWrite()
    {
        using var files = new Files();
        files.CreateTargets();
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DevelopmentDeploymentProvisioner.ReplaceRepairPayloadsAsync(
            NewPayload(), files.Broker, NewPayload(), files.Client, _ =>
            {
                cancellation.Cancel();
                return Task.CompletedTask;
            }, cancellation.Token));
        files.AssertOriginals();
        Assert.Equal(2, Directory.GetFiles(files.Root).Length);
    }

    [Fact]
    public async Task StopObservationHasFiniteDeadlineAndCancellation()
    {
        await Assert.ThrowsAsync<System.TimeoutException>(() => DevelopmentDeploymentProvisioner.WaitForRepairStopAsync(
            () => false, TimeSpan.Zero, default));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DevelopmentDeploymentProvisioner.WaitForRepairStopAsync(
            () => true, TimeSpan.FromSeconds(30), new CancellationToken(true)));
        await DevelopmentDeploymentProvisioner.WaitForRepairStopAsync(() => true, TimeSpan.Zero, default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepairStartGateRemainsDisabledUntilVerifiedSuccess(bool replacementFails)
    {
        var events = new List<string>();
        bool disabled = false;
        bool stopped = false;
        Task repair = DevelopmentDeploymentProvisioner.RunRepairTransactionAsync(_ =>
        {
            disabled = true;
            events.Add("disable");
            stopped = true;
            events.Add("stopped");
            return Task.CompletedTask;
        }, _ =>
        {
            Assert.True(disabled); // The enrolled user's start is excluded throughout copying.
            Assert.True(stopped);
            events.Add("replace-and-verify");
            return replacementFails ? Task.FromException(new IOException("verification")) : Task.CompletedTask;
        }, () => { disabled = false; events.Add("demand"); }, default);
        if (replacementFails)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => repair);
            Assert.True(disabled);
            Assert.Equal(new[] { "disable", "stopped", "replace-and-verify" }, events);
        }
        else
        {
            await repair;
            Assert.False(disabled);
            Assert.Equal(new[] { "disable", "stopped", "replace-and-verify", "demand" }, events);
        }
        Assert.True(stopped); // Neither path requests Start.
    }

    [Fact]
    public async Task CancelledPendingStopCannotLaterCopyOrRestoreDemand()
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int copies = 0;
        int restores = 0;
        Task repair = DevelopmentDeploymentProvisioner.RunRepairTransactionAsync(_ =>
        {
            entered.SetResult();
            return stop.Task;
        }, _ => { copies++; return Task.CompletedTask; }, () => restores++, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAsync<InvalidOperationException>(() => repair);
        stop.SetResult();
        await stop.Task;
        Assert.Equal(0, copies);
        Assert.Equal(0, restores);
    }

    [Fact]
    public async Task VerificationFailureRollsBackBothFiles()
    {
        using var files = new Files();
        files.CreateTargets();
        await Assert.ThrowsAsync<InvalidDataException>(() => DevelopmentDeploymentProvisioner.ReplaceRepairPayloadsAsync(
            NewPayload(), files.Broker, NewPayload(), files.Client, _ => Task.CompletedTask,
            default, verifyReplacement: () => throw new InvalidDataException("wrong bytes")));
        files.AssertOriginals();
        Assert.Equal(2, Directory.GetFiles(files.Root).Length);
    }

    [Fact]
    public async Task FailureReportedAfterNativeReplacementStillUsesItsBackup()
    {
        using var files = new Files();
        files.CreateTargets();
        int attempts = 0;
        await Assert.ThrowsAsync<IOException>(() => DevelopmentDeploymentProvisioner.ReplaceRepairPayloadsAsync(
            NewPayload(), files.Broker, NewPayload(), files.Client, _ => Task.CompletedTask, default,
            (source, target, backup) =>
            {
                File.Replace(source, target, backup);
                if (++attempts == 2)
                {
                    throw new IOException("replacement completed but failure reported");
                }
            }));
        files.AssertOriginals();
        Assert.Equal(4, attempts);
    }

    [Theory]
    [InlineData(16U, 3U, "\"C:\\fixed\\broker.exe\"", "LocalSystem", true)]
    [InlineData(16U, 4U, "\"C:\\fixed\\broker.exe\"", "LocalSystem", true)]
    [InlineData(16U, 2U, "\"C:\\fixed\\broker.exe\"", "LocalSystem", false)]
    [InlineData(32U, 3U, "\"C:\\fixed\\broker.exe\"", "LocalSystem", false)]
    [InlineData(16U, 3U, "\"C:\\fixed\\broker.exe\" extra", "LocalSystem", false)]
    [InlineData(16U, 3U, "\"C:\\fixed\\broker.exe\"", "OtherUser", false)]
    public void RepairCannotRetargetService(uint type, uint start, string path, string account, bool valid)
    {
        Action check = () => RepairServiceConfiguration.RequireFixedConfiguration(type, start, path, account, @"C:\fixed\broker.exe");
        if (valid)
        {
            check();
        }
        else
        {
            Assert.Throws<InvalidDataException>(check);
        }
    }

    private static MemoryStream NewPayload() => new("new"u8.ToArray());

    private sealed class Files : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "OpenBlade.Repair.Tests", Guid.NewGuid().ToString("N"));
        public string Broker => Path.Combine(Root, "broker.exe");
        public string Client => Path.Combine(Root, "client.exe");
        public Files() => Directory.CreateDirectory(Root);
        public void CreateTargets() { File.WriteAllText(Broker, "old broker"); File.WriteAllText(Client, "old client"); }
        public void AssertOriginals() { Assert.Equal("old broker", File.ReadAllText(Broker)); Assert.Equal("old client", File.ReadAllText(Client)); }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
