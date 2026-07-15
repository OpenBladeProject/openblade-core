using OpenBlade.Setup;

namespace OpenBlade.Setup.Tests;

public sealed class SetupCommandRunnerTests
{
    [Fact]
    public async Task Install_PayloadStagingFailure_HappensBeforeAnyInstalledStateMutation()
    {
        RecordingSetupPlatform platform = new() { FailPayloadStage = true };
        SetupCommandRunner runner = new(platform);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.RunAsync(["install"], CancellationToken.None));

        Assert.Equal(["payload-stage"], platform.Operations);
        Assert.DoesNotContain("data-remove", platform.Operations);
    }

    [Fact]
    public async Task Install_Update_StagesActivatesVerifiesThenCommitsInExactOrder()
    {
        RecordingSetupPlatform platform = new()
        {
            ServiceExists = true,
            Transaction = new RecordingPayloadTransaction(hadExistingInstall: true),
        };

        int exitCode = await new SetupCommandRunner(platform).RunAsync(["install"], CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(
            [
                "payload-stage", "lifecycle-capture", "shortcut-remove", "service-prepare", "tray-stop",
                "payload-activate", "sid:S-1-5-21-test", "service-install",
                "service-health", "shortcut-install", "payload-commit",
            ],
            platform.Operations);
    }

    [Fact]
    public async Task Install_LifecycleCaptureFailure_RollsBackStagingWithoutMutatingInstalledLifecycle()
    {
        RecordingSetupPlatform platform = new()
        {
            FailurePoint = "lifecycle-capture",
            Transaction = new RecordingPayloadTransaction(hadExistingInstall: true),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SetupCommandRunner(platform).RunAsync(["install"], CancellationToken.None));

        Assert.Equal(["payload-stage", "lifecycle-capture", "payload-rollback"], platform.Operations);
    }

    [Theory]
    [InlineData("service-health")]
    [InlineData("shortcut-install")]
    public async Task Install_UpdatePostActivationFailure_RestoresOldTreeServiceAndShortcut(string failurePoint)
    {
        RecordingSetupPlatform platform = new()
        {
            ServiceExists = true,
            FailurePoint = failurePoint,
            Transaction = new RecordingPayloadTransaction(hadExistingInstall: true),
        };

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SetupCommandRunner(platform).RunAsync(["install"], CancellationToken.None));

        Assert.Equal($"{failurePoint} failed", failure.Message);
        Assert.Equal(1, platform.Operations.Count(operation => operation == "payload-rollback"));
        Assert.DoesNotContain("payload-commit", platform.Operations);
        Assert.DoesNotContain("data-remove", platform.Operations);
        AssertEndsWith(
            platform.Operations,
            [
                "shortcut-remove", "service-prepare", "tray-stop", "payload-rollback",
                "service-restore", "shortcut-install",
            ]);
        Assert.All(platform.CleanupTokens, token => Assert.False(token.IsCancellationRequested));
    }

    [Fact]
    public async Task Install_FreshInstallFailure_RemovesNewServiceShortcutAndActivatedTree()
    {
        RecordingSetupPlatform platform = new()
        {
            FailurePoint = "service-health",
            Transaction = new RecordingPayloadTransaction(hadExistingInstall: false),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SetupCommandRunner(platform).RunAsync(["install"], CancellationToken.None));

        AssertEndsWith(
            platform.Operations,
            ["shortcut-remove", "service-remove", "tray-stop", "payload-rollback"]);
        Assert.DoesNotContain("shortcut-install", platform.Operations);
        Assert.DoesNotContain("data-remove", platform.Operations);
    }

    [Fact]
    public async Task Install_UpdateFailure_PreservesStoppedServiceAndMissingStartupShortcut()
    {
        RecordingSetupPlatform platform = new()
        {
            ServiceExists = true,
            ServiceWasRunning = false,
            ShortcutExisted = false,
            FailurePoint = "service-health",
            Transaction = new RecordingPayloadTransaction(hadExistingInstall: true),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SetupCommandRunner(platform).RunAsync(["install"], CancellationToken.None));

        Assert.Equal(1, platform.Operations.Count(operation => operation == "service-install"));
        Assert.Equal(1, platform.Operations.Count(operation => operation == "service-health"));
        Assert.DoesNotContain("shortcut-install", platform.Operations);
        AssertEndsWith(platform.Operations, ["payload-rollback", "service-restore"]);
    }

    [Fact]
    public async Task Install_ServicePrepareFailureAfterCapture_RestoresPriorRunningServiceAndShortcut()
    {
        RecordingSetupPlatform platform = new()
        {
            ServiceExists = true,
            ShortcutExisted = true,
            FailurePoint = "service-prepare",
            Transaction = new RecordingPayloadTransaction(hadExistingInstall: true),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SetupCommandRunner(platform).RunAsync(["install"], CancellationToken.None));

        AssertEndsWith(
            platform.Operations,
            ["shortcut-remove", "payload-rollback", "service-restore", "shortcut-install"]);
        Assert.DoesNotContain("service-remove", platform.Operations);
    }

    [Fact]
    public async Task Install_NoOldBinaryTreeButExistingService_PreActivationFailurePreservesRegistration()
    {
        RecordingSetupPlatform platform = new()
        {
            ServiceExists = true,
            FailurePoint = "tray-stop",
            Transaction = new RecordingPayloadTransaction(hadExistingInstall: false),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SetupCommandRunner(platform).RunAsync(["install"], CancellationToken.None));

        Assert.DoesNotContain("service-remove", platform.Operations);
        Assert.Contains("payload-rollback", platform.Operations);
    }

    [Fact]
    public async Task Install_RecoveryFailure_PreservesOriginalThenRecoveryFailure()
    {
        RecordingSetupPlatform platform = new()
        {
            ServiceExists = true,
            FailurePoint = "service-health",
            Transaction = new RecordingPayloadTransaction(
                hadExistingInstall: true,
                rollbackFailure: new IOException("rollback failed")),
        };

        AggregateException failure = await Assert.ThrowsAsync<AggregateException>(
            () => new SetupCommandRunner(platform).RunAsync(["install"], CancellationToken.None));

        Assert.Equal("service-health failed", failure.InnerExceptions[0].Message);
        Assert.Contains(failure.InnerExceptions, exception => exception.Message == "rollback failed");
        Assert.DoesNotContain("data-remove", platform.Operations);
    }

    [Fact]
    public async Task Install_CancellationAfterActivation_UsesNonCancelledRecoveryToken()
    {
        using CancellationTokenSource cancellation = new();
        RecordingSetupPlatform platform = new()
        {
            ServiceExists = true,
            FailurePoint = "sid-cancel",
            Cancellation = cancellation,
            Transaction = new RecordingPayloadTransaction(hadExistingInstall: true),
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new SetupCommandRunner(platform).RunAsync(["install"], cancellation.Token));

        Assert.Contains("payload-rollback", platform.Operations);
        Assert.All(platform.CleanupTokens, token => Assert.False(token.IsCancellationRequested));
        Assert.DoesNotContain("data-remove", platform.Operations);
    }

    [Fact]
    public async Task Uninstall_PreservesProfilesUnlessPurgeIsExplicit()
    {
        RecordingSetupPlatform platform = new();

        int exitCode = await new SetupCommandRunner(platform).RunAsync(["uninstall"], CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(["shortcut-remove", "service-remove", "tray-stop", "binaries-remove"], platform.Operations);
        Assert.DoesNotContain("data-remove", platform.Operations);
    }

    [Fact]
    public async Task UninstallPurge_RemovesMachineAndUserData()
    {
        RecordingSetupPlatform platform = new();

        int exitCode = await new SetupCommandRunner(platform).RunAsync(["uninstall", "--purge"], CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(
            ["shortcut-remove", "service-remove", "tray-stop", "binaries-remove", "data-remove"],
            platform.Operations);
    }

    [Fact]
    public async Task InvalidCommand_PrintsShippedExecutableSyntax()
    {
        RecordingSetupPlatform platform = new();
        StringWriter errors = new();
        TextWriter original = Console.Error;
        Console.SetError(errors);
        try
        {
            int exitCode = await new SetupCommandRunner(platform).RunAsync([], CancellationToken.None);

            Assert.Equal(2, exitCode);
            Assert.Contains("OpenBlade.Setup.exe install", errors.ToString(), StringComparison.Ordinal);
            Assert.Contains("OpenBlade.Setup.exe uninstall [--purge]", errors.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(original);
        }
    }

    private static void AssertEndsWith(IReadOnlyList<string> actual, IReadOnlyList<string> expected)
    {
        Assert.True(actual.Count >= expected.Count);
        Assert.Equal(expected, actual.Skip(actual.Count - expected.Count));
    }

    private sealed class RecordingSetupPlatform : ISetupPlatform
    {
        public List<string> Operations { get; } = [];
        public List<CancellationToken> CleanupTokens { get; } = [];
        public bool FailPayloadStage { get; init; }
        public bool ServiceExists { get; init; }
        public bool ServiceWasRunning { get; init; } = true;
        public bool ShortcutExisted { get; init; } = true;
        public string? FailurePoint { get; set; }
        public CancellationTokenSource? Cancellation { get; init; }
        public RecordingPayloadTransaction Transaction { get; init; } = new(hadExistingInstall: false);

        public string CurrentUserSid => "S-1-5-21-test";

        public Task<ISetupPayloadTransaction> StagePayloadAsync(CancellationToken cancellationToken)
        {
            Operations.Add("payload-stage");
            Transaction.Operations = Operations;
            return FailPayloadStage
                ? Task.FromException<ISetupPayloadTransaction>(new InvalidOperationException("payload staging failed"))
                : Task.FromResult<ISetupPayloadTransaction>(Transaction);
        }

        public Task<SetupInstallLifecycleState> CaptureInstallLifecycleStateAsync(
            CancellationToken cancellationToken)
        {
            Operations.Add("lifecycle-capture");
            if (FailurePoint == "lifecycle-capture")
            {
                FailurePoint = null;
                return Task.FromException<SetupInstallLifecycleState>(
                    new InvalidOperationException("lifecycle-capture failed"));
            }

            return Task.FromResult(new SetupInstallLifecycleState(
                new(ServiceExists, ServiceExists && ServiceWasRunning),
                new(ShortcutExisted)));
        }

        public Task PrepareServiceForInstallAsync(CancellationToken cancellationToken)
        {
            if (Operations.Contains("payload-activate"))
            {
                CleanupTokens.Add(cancellationToken);
            }
            return AddAsync("service-prepare", cancellationToken);
        }

        public Task RestoreServiceInstallStateAsync(
            SetupServiceInstallState priorState,
            CancellationToken cancellationToken) => AddAsync("service-restore", cancellationToken);

        public Task WriteAuthorizedUserSidAsync(string sid, CancellationToken cancellationToken)
        {
            Operations.Add($"sid:{sid}");
            if (FailurePoint == "sid-cancel")
            {
                Cancellation!.Cancel();
                return Task.FromCanceled(Cancellation.Token);
            }

            return Task.CompletedTask;
        }

        public Task InstallServiceAsync(CancellationToken cancellationToken) => AddAsync("service-install", cancellationToken);
        public Task VerifyServiceHealthAsync(CancellationToken cancellationToken) => AddAsync("service-health", cancellationToken);
        public Task InstallStartupShortcutAsync(CancellationToken cancellationToken) => AddAsync("shortcut-install", cancellationToken);
        public Task RemoveStartupShortcutAsync(CancellationToken cancellationToken) => AddAsync("shortcut-remove", cancellationToken);
        public Task RemoveServiceAsync(CancellationToken cancellationToken) => AddAsync("service-remove", cancellationToken);
        public Task StopTrayAsync(CancellationToken cancellationToken) => AddAsync("tray-stop", cancellationToken);
        public Task RemoveBinariesAsync(CancellationToken cancellationToken) => AddAsync("binaries-remove", cancellationToken);
        public Task RemoveDataAsync(CancellationToken cancellationToken) => AddAsync("data-remove", cancellationToken);

        private Task AddAsync(string operation, CancellationToken token)
        {
            Operations.Add(operation);
            if (Operations.Contains("payload-activate") && operation is "shortcut-remove" or "service-remove" or "tray-stop")
            {
                CleanupTokens.Add(token);
            }

            if (FailurePoint == operation)
            {
                FailurePoint = null;
                return Task.FromException(new InvalidOperationException($"{operation} failed"));
            }

            return Task.CompletedTask;
        }
    }

    private sealed class RecordingPayloadTransaction(
        bool hadExistingInstall,
        Exception? rollbackFailure = null) : ISetupPayloadTransaction
    {
        public List<string> Operations { private get; set; } = [];
        public bool HadExistingInstall => hadExistingInstall;
        public bool IsActivated { get; private set; }

        public Task ActivateAsync(CancellationToken cancellationToken)
        {
            Operations.Add("payload-activate");
            IsActivated = true;
            return Task.CompletedTask;
        }

        public Task CommitAsync(CancellationToken cancellationToken)
        {
            Operations.Add("payload-commit");
            IsActivated = false;
            return Task.CompletedTask;
        }

        public Task RollbackAsync(CancellationToken cancellationToken)
        {
            Operations.Add("payload-rollback");
            IsActivated = false;
            return rollbackFailure is null ? Task.CompletedTask : Task.FromException(rollbackFailure);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
