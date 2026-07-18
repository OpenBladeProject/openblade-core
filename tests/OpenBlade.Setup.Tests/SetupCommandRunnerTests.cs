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
                "payload-stage", "lifecycle-capture", "shortcut-remove", "agent-shortcut-remove",
                "service-prepare", "tray-stop", "session-agent-stop",
                "payload-activate", "sid:S-1-5-21-test", "service-install",
                "service-health", "agent-shortcut-install", "shortcut-install", "payload-commit",
            ],
            platform.Operations);
    }

    [Fact]
    public async Task Install_UpdateWithDisabledTrayStartup_PreservesMissingTrayShortcutAndRepairsAgentShortcut()
    {
        RecordingSetupPlatform platform = new()
        {
            ServiceExists = true,
            TrayShortcutExisted = false,
            SessionAgentShortcutExisted = false,
            Transaction = new RecordingPayloadTransaction(hadExistingInstall: true),
        };

        int exitCode = await new SetupCommandRunner(platform).RunAsync(["install"], CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.False(platform.TrayShortcutPresent);
        Assert.True(platform.SessionAgentShortcutPresent);
        Assert.DoesNotContain("shortcut-install", platform.Operations);
        Assert.Contains("agent-shortcut-install", platform.Operations);
        Assert.Equal("payload-commit", platform.Operations[^1]);
    }

    [Fact]
    public async Task Install_FreshInstallWithNoPriorShortcut_DefaultsStartupOn()
    {
        RecordingSetupPlatform platform = new()
        {
            TrayShortcutExisted = false,
            SessionAgentShortcutExisted = false,
            Transaction = new RecordingPayloadTransaction(hadExistingInstall: false),
        };

        int exitCode = await new SetupCommandRunner(platform).RunAsync(["install"], CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.True(platform.TrayShortcutPresent);
        Assert.True(platform.SessionAgentShortcutPresent);
        Assert.Contains("shortcut-install", platform.Operations);
        Assert.Equal("payload-commit", platform.Operations[^1]);
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
    [InlineData("sid")]
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
                "shortcut-remove", "agent-shortcut-remove", "service-prepare", "tray-stop",
                "session-agent-stop", "payload-rollback", "service-restore",
                "agent-shortcut-install", "shortcut-install",
            ]);
        Assert.All(platform.CleanupTokens, token => Assert.False(token.IsCancellationRequested));
    }

    [Fact]
    public async Task Install_FreshInstallFailure_RemovesNewServiceShortcutAndActivatedTree()
    {
        RecordingSetupPlatform platform = new()
        {
            FailurePoint = "service-health",
            TrayShortcutExisted = false,
            SessionAgentShortcutExisted = false,
            Transaction = new RecordingPayloadTransaction(hadExistingInstall: false),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SetupCommandRunner(platform).RunAsync(["install"], CancellationToken.None));

        AssertEndsWith(
            platform.Operations,
            [
                "shortcut-remove", "agent-shortcut-remove", "service-remove", "tray-stop",
                "session-agent-stop", "payload-rollback",
            ]);
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
            TrayShortcutExisted = false,
            SessionAgentShortcutExisted = false,
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
            TrayShortcutExisted = true,
            FailurePoint = "service-prepare",
            Transaction = new RecordingPayloadTransaction(hadExistingInstall: true),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SetupCommandRunner(platform).RunAsync(["install"], CancellationToken.None));

        AssertEndsWith(
            platform.Operations,
            [
                "shortcut-remove", "agent-shortcut-remove", "payload-rollback", "service-restore",
                "agent-shortcut-install", "shortcut-install",
            ]);
        Assert.DoesNotContain("service-remove", platform.Operations);
    }

    [Fact]
    public async Task Install_NoOldBinaryTreeButRunningService_FailsClosedBeforeAnyLifecycleMutation()
    {
        RecordingSetupPlatform platform = new()
        {
            ServiceExists = true,
            Transaction = new RecordingPayloadTransaction(hadExistingInstall: false),
        };

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SetupCommandRunner(platform).RunAsync(["install"], CancellationToken.None));

        Assert.Contains("manual repair or uninstall", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["payload-stage", "lifecycle-capture", "payload-rollback"], platform.Operations);
        Assert.True(platform.ServiceRegistered);
        Assert.True(platform.ServiceRunning);
        Assert.True(platform.TrayShortcutPresent);
    }

    [Fact]
    public async Task Install_NoOldBinaryTreeButStoppedService_FailsClosedBeforeAnyLifecycleMutation()
    {
        RecordingSetupPlatform platform = new()
        {
            ServiceExists = true,
            ServiceWasRunning = false,
            TrayShortcutExisted = true,
            Transaction = new RecordingPayloadTransaction(hadExistingInstall: false),
        };

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SetupCommandRunner(platform).RunAsync(["install"], CancellationToken.None));

        Assert.Contains("manual repair or uninstall", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["payload-stage", "lifecycle-capture", "payload-rollback"], platform.Operations);
        Assert.True(platform.ServiceRegistered);
        Assert.False(platform.ServiceRunning);
        Assert.True(platform.TrayShortcutPresent);
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
    public async Task Install_RollbackAndDisposeCleanupFailures_DoNotMaskOriginalFailure()
    {
        RecordingPayloadTransaction transaction = new(
            hadExistingInstall: true,
            rollbackFailure: new IOException("rollback cleanup failed"),
            disposeCleanupFailure: new IOException("dispose cleanup failed"));
        RecordingSetupPlatform platform = new()
        {
            FailurePoint = "lifecycle-capture",
            Transaction = transaction,
        };

        AggregateException failure = await Assert.ThrowsAsync<AggregateException>(
            () => new SetupCommandRunner(platform).RunAsync(["install"], CancellationToken.None));

        Assert.Equal("lifecycle-capture failed", failure.InnerExceptions[0].Message);
        Assert.Contains(failure.InnerExceptions, exception => exception.Message == "rollback cleanup failed");
        Assert.True(transaction.DisposeCleanupFailureObserved);
        Assert.DoesNotContain(failure.InnerExceptions, exception => exception.Message == "dispose cleanup failed");
    }

    [Fact]
    public async Task Install_CommittedCleanupWarning_IsOperatorVisibleWithoutRollback()
    {
        const string warning = "OpenBlade updated; guarded rollback directory retained.";
        RecordingSetupPlatform platform = new()
        {
            Transaction = new RecordingPayloadTransaction(false, commitWarning: warning),
        };
        StringWriter errors = new();
        TextWriter original = Console.Error;
        Console.SetError(errors);
        try
        {
            int exitCode = await new SetupCommandRunner(platform).RunAsync(["install"], CancellationToken.None);

            Assert.Equal(0, exitCode);
            Assert.Contains(warning, errors.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("payload-rollback", platform.Operations);
        }
        finally
        {
            Console.SetError(original);
        }
    }

    [Fact]
    public async Task Install_CommittedCleanupWarningWriterFailure_CannotEnterRecoveryOrFailInstall()
    {
        RecordingSetupPlatform platform = new()
        {
            TrayShortcutExisted = false,
            Transaction = new RecordingPayloadTransaction(
                false,
                commitWarning: "guarded rollback directory retained"),
        };
        TextWriter original = Console.Error;
        Console.SetError(new ThrowingTextWriter());
        int exitCode;
        try
        {
            exitCode = await new SetupCommandRunner(platform).RunAsync(["install"], CancellationToken.None);
        }
        finally
        {
            Console.SetError(original);
        }

        Assert.Equal(0, exitCode);
        int commitIndex = platform.Operations.IndexOf("payload-commit");
        Assert.True(commitIndex >= 0);
        Assert.DoesNotContain("payload-rollback", platform.Operations.Skip(commitIndex + 1));
        Assert.DoesNotContain("shortcut-remove", platform.Operations.Skip(commitIndex + 1));
        Assert.DoesNotContain("service-remove", platform.Operations.Skip(commitIndex + 1));
        Assert.DoesNotContain("service-prepare", platform.Operations.Skip(commitIndex + 1));
        Assert.DoesNotContain("tray-stop", platform.Operations.Skip(commitIndex + 1));
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
        Assert.Equal(
            ["shortcut-remove", "agent-shortcut-remove", "service-remove", "tray-stop", "session-agent-stop", "binaries-remove"],
            platform.Operations);
        Assert.DoesNotContain("data-remove", platform.Operations);
    }

    [Fact]
    public async Task UninstallPurge_RemovesMachineAndUserData()
    {
        RecordingSetupPlatform platform = new();

        int exitCode = await new SetupCommandRunner(platform).RunAsync(["uninstall", "--purge"], CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(
            ["shortcut-remove", "agent-shortcut-remove", "service-remove", "tray-stop", "session-agent-stop", "binaries-remove", "data-remove"],
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
        public bool TrayShortcutExisted { get; init; } = true;
        public bool SessionAgentShortcutExisted { get; init; } = true;
        public string? FailurePoint { get; set; }
        public CancellationTokenSource? Cancellation { get; init; }
        public RecordingPayloadTransaction Transaction { get; init; } = new(hadExistingInstall: false);
        private bool lifecycleInitialized;
        private bool serviceRegistered;
        private bool serviceRunning;
        private bool trayShortcutPresent;
        private bool sessionAgentShortcutPresent;

        public bool ServiceRegistered => serviceRegistered;
        public bool ServiceRunning => serviceRunning;
        public bool TrayShortcutPresent => trayShortcutPresent;
        public bool SessionAgentShortcutPresent => sessionAgentShortcutPresent;

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

            if (!lifecycleInitialized)
            {
                serviceRegistered = ServiceExists;
                serviceRunning = ServiceExists && ServiceWasRunning;
                trayShortcutPresent = TrayShortcutExisted;
                sessionAgentShortcutPresent = SessionAgentShortcutExisted;
                lifecycleInitialized = true;
            }

            return Task.FromResult(new SetupInstallLifecycleState(
                new(serviceRegistered, serviceRunning),
                new(trayShortcutPresent, sessionAgentShortcutPresent)));
        }

        public Task PrepareServiceForInstallAsync(CancellationToken cancellationToken)
        {
            serviceRunning = false;
            if (Operations.Contains("payload-activate"))
            {
                CleanupTokens.Add(cancellationToken);
            }
            return AddAsync("service-prepare", cancellationToken);
        }

        public Task RestoreServiceInstallStateAsync(
            SetupServiceInstallState priorState,
            CancellationToken cancellationToken)
        {
            serviceRegistered = priorState.Existed;
            serviceRunning = priorState.Existed && priorState.WasRunning;
            return AddAsync("service-restore", cancellationToken);
        }

        public Task WriteAuthorizedUserSidAsync(string sid, CancellationToken cancellationToken)
        {
            Operations.Add($"sid:{sid}");
            if (FailurePoint == "sid")
            {
                FailurePoint = null;
                return Task.FromException(new InvalidOperationException("sid failed"));
            }

            if (FailurePoint == "sid-cancel")
            {
                Cancellation!.Cancel();
                return Task.FromCanceled(Cancellation.Token);
            }

            return Task.CompletedTask;
        }

        public Task InstallServiceAsync(CancellationToken cancellationToken)
        {
            serviceRegistered = true;
            serviceRunning = true;
            return AddAsync("service-install", cancellationToken);
        }
        public Task VerifyServiceHealthAsync(CancellationToken cancellationToken) => AddAsync("service-health", cancellationToken);
        public Task InstallStartupShortcutAsync(CancellationToken cancellationToken)
        {
            trayShortcutPresent = true;
            return AddAsync("shortcut-install", cancellationToken);
        }
        public Task RemoveStartupShortcutAsync(CancellationToken cancellationToken)
        {
            trayShortcutPresent = false;
            return AddAsync("shortcut-remove", cancellationToken);
        }
        public Task InstallSessionAgentStartupShortcutAsync(CancellationToken cancellationToken)
        {
            sessionAgentShortcutPresent = true;
            return AddAsync("agent-shortcut-install", cancellationToken);
        }
        public Task RemoveSessionAgentStartupShortcutAsync(CancellationToken cancellationToken)
        {
            sessionAgentShortcutPresent = false;
            return AddAsync("agent-shortcut-remove", cancellationToken);
        }
        public Task RemoveServiceAsync(CancellationToken cancellationToken)
        {
            serviceRegistered = false;
            serviceRunning = false;
            return AddAsync("service-remove", cancellationToken);
        }
        public Task StopTrayAsync(CancellationToken cancellationToken) => AddAsync("tray-stop", cancellationToken);
        public Task StopSessionAgentAsync(CancellationToken cancellationToken) =>
            AddAsync("session-agent-stop", cancellationToken);
        public Task RemoveBinariesAsync(CancellationToken cancellationToken) => AddAsync("binaries-remove", cancellationToken);
        public Task RemoveDataAsync(CancellationToken cancellationToken) => AddAsync("data-remove", cancellationToken);

        private Task AddAsync(string operation, CancellationToken token)
        {
            Operations.Add(operation);
            if (Operations.Contains("payload-activate")
                && operation is "shortcut-remove" or "agent-shortcut-remove" or "service-remove" or "tray-stop" or "session-agent-stop")
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
        Exception? rollbackFailure = null,
        string? commitWarning = null,
        Exception? disposeCleanupFailure = null) : ISetupPayloadTransaction
    {
        public List<string> Operations { private get; set; } = [];
        public bool HadExistingInstall => hadExistingInstall;
        public bool IsActivated { get; private set; }
        public string? CommitWarning => commitWarning;
        public bool DisposeCleanupFailureObserved { get; private set; }

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

        public ValueTask DisposeAsync()
        {
            if (disposeCleanupFailure is not null)
            {
                DisposeCleanupFailureObserved = true;
                System.Diagnostics.Trace.TraceWarning(
                    "Best-effort fake cleanup retained staging: {0}",
                    disposeCleanupFailure.Message);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingTextWriter : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public override void WriteLine(string? value) =>
            throw new IOException("stderr is unavailable");
    }
}
