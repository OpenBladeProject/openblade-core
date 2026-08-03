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
                "payload-stage", "lifecycle-capture", "shortcut-remove", "agent-shortcut-remove", "start-menu-remove",
                "uninstall-shortcut-remove", "uninstall-registration-remove",
                "service-prepare", "tray-stop", "session-agent-stop",
                "dynamic-lighting-remove", "payload-activate", "dynamic-lighting-install",
                "uninstaller-install", "sid:S-1-5-21-test", "service-install",
                "service-health", "agent-shortcut-install", "start-menu-install",
                "uninstall-shortcut-install", "uninstall-registration-install", "shortcut-install", "payload-commit",
            ],
            platform.Operations);
    }

    [Fact]
    public async Task Install_BundleManaged_LeavesUninstallOwnershipToBurn()
    {
        RecordingSetupPlatform platform = new()
        {
            ServiceExists = true,
            UninstallShortcutExisted = true,
            UninstallRegistrationExisted = true,
            Transaction = new RecordingPayloadTransaction(hadExistingInstall: true),
        };

        int exitCode = await new SetupCommandRunner(platform)
            .RunAsync(["install", "--bundle-managed"], CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("uninstall-shortcut-remove", platform.Operations);
        Assert.Contains("uninstall-registration-remove", platform.Operations);
        Assert.DoesNotContain("uninstaller-install", platform.Operations);
        Assert.DoesNotContain("uninstall-shortcut-install", platform.Operations);
        Assert.DoesNotContain("uninstall-registration-install", platform.Operations);
        Assert.Contains("bundle-ownership-install", platform.Operations);
        Assert.Equal("payload-commit", platform.Operations[^1]);
    }

    [Fact]
    public async Task Install_BundleManagedCancellationAfterOwnershipRegistration_RestoresOldBuildOwnership()
    {
        const string oldBuild = "0.1.0+oldbuild";
        const string newBuild = "0.2.0+newbuild";
        var oldOwnership = new BundleOwnershipIdentity(
            "WiX Burn",
            "0.1.0",
            oldBuild);
        using CancellationTokenSource cancellation = new();
        RecordingSetupPlatform platform = new()
        {
            CurrentBuildIdentity = newBuild,
            InitialBundleOwnership = oldOwnership,
            ServiceExists = true,
            Transaction = new RecordingPayloadTransaction(
                hadExistingInstall: true,
                cancelOnCommit: cancellation),
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new SetupCommandRunner(platform).RunAsync(
                ["install", "--bundle-managed"],
                cancellation.Token));

        Assert.Contains("bundle-ownership-install", platform.Operations);
        Assert.Contains("payload-rollback", platform.Operations);
        Assert.Equal(oldOwnership, platform.CurrentBundleOwnership);
        Assert.Equal(
            "bundle-ownership-restore",
            platform.Operations[^1]);

        platform.CurrentBuildIdentity = oldBuild;
        int uninstallExitCode = await new SetupCommandRunner(platform).RunAsync(
            ["uninstall", "--bundle-managed"],
            CancellationToken.None);

        Assert.Equal(0, uninstallExitCode);
        Assert.Contains("binaries-remove", platform.Operations);
        Assert.Null(platform.CurrentBundleOwnership);
    }

    [Fact]
    public async Task Install_BundleManagedOwnershipWithoutRestorableSnapshot_FailsBeforeMutation()
    {
        RecordingSetupPlatform platform = new()
        {
            BundleOwnershipExisted = true,
            OmitBundleOwnershipSnapshot = true,
            ServiceExists = true,
            Transaction = new RecordingPayloadTransaction(hadExistingInstall: true),
        };

        InvalidOperationException failure =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => new SetupCommandRunner(platform).RunAsync(
                    ["install", "--bundle-managed"],
                    CancellationToken.None));

        Assert.Contains(
            "could not be captured safely",
            failure.Message,
            StringComparison.Ordinal);
        Assert.Equal(
            ["payload-stage", "lifecycle-capture", "payload-rollback"],
            platform.Operations);
    }

    [Fact]
    public async Task Install_StandaloneCommandRejectsBundleManagedInstallationBeforeMutation()
    {
        RecordingSetupPlatform platform = new()
        {
            BundleOwnershipExisted = true,
            ServiceExists = true,
            Transaction = new RecordingPayloadTransaction(hadExistingInstall: true),
        };

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SetupCommandRunner(platform).RunAsync(["install"], CancellationToken.None));

        Assert.Contains("graphical installer", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            ["payload-stage", "lifecycle-capture", "payload-rollback"],
            platform.Operations);
    }

    [Fact]
    public async Task Install_BundleManagedFailure_RestoresPriorStandaloneUninstallOwnership()
    {
        RecordingSetupPlatform platform = new()
        {
            ServiceExists = true,
            UninstallShortcutExisted = true,
            UninstallRegistrationExisted = true,
            FailurePoint = "service-health",
            Transaction = new RecordingPayloadTransaction(hadExistingInstall: true),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SetupCommandRunner(platform)
                .RunAsync(["install", "--bundle-managed"], CancellationToken.None));

        Assert.Contains("payload-rollback", platform.Operations);
        AssertEndsWith(
            platform.Operations,
            [
                "service-restore", "agent-shortcut-install", "shortcut-install", "start-menu-install",
                "uninstall-shortcut-install", "uninstall-registration-install",
                "bundle-ownership-remove",
            ]);
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
        Assert.True(platform.StartMenuShortcutPresent);
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
            StartMenuShortcutExisted = false,
            Transaction = new RecordingPayloadTransaction(hadExistingInstall: false),
        };

        int exitCode = await new SetupCommandRunner(platform).RunAsync(["install"], CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.True(platform.TrayShortcutPresent);
        Assert.True(platform.SessionAgentShortcutPresent);
        Assert.True(platform.StartMenuShortcutPresent);
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
    [InlineData("service-install")]
    [InlineData("service-health")]
    [InlineData("agent-shortcut-install")]
    [InlineData("shortcut-install")]
    [InlineData("start-menu-install")]
    [InlineData("uninstall-shortcut-install")]
    [InlineData("uninstall-registration-install")]
    [InlineData("dynamic-lighting-install")]
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
                "shortcut-remove", "agent-shortcut-remove", "start-menu-remove",
                "uninstall-shortcut-remove", "uninstall-registration-remove",
                "dynamic-lighting-remove", "service-prepare", "tray-stop",
                "session-agent-stop", "payload-rollback", "service-restore",
                "agent-shortcut-install", "shortcut-install", "start-menu-install",
            ]);
        Assert.All(platform.CleanupTokens, token => Assert.False(token.IsCancellationRequested));
    }

    [Theory]
    [InlineData("shortcut-remove")]
    [InlineData("agent-shortcut-remove")]
    [InlineData("start-menu-remove")]
    [InlineData("uninstall-shortcut-remove")]
    [InlineData("uninstall-registration-remove")]
    [InlineData("service-prepare")]
    [InlineData("tray-stop")]
    [InlineData("session-agent-stop")]
    [InlineData("dynamic-lighting-remove")]
    public async Task Install_UpdatePreActivationFailureRestoresCapturedLifecycle(
        string failurePoint)
    {
        RecordingSetupPlatform platform = new()
        {
            ServiceExists = true,
            UninstallShortcutExisted = true,
            UninstallRegistrationExisted = true,
            FailurePoint = failurePoint,
            Transaction = new RecordingPayloadTransaction(hadExistingInstall: true),
        };

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SetupCommandRunner(platform).RunAsync(["install"], CancellationToken.None));

        Assert.Equal($"{failurePoint} failed", failure.Message);
        Assert.DoesNotContain("payload-activate", platform.Operations);
        Assert.Contains("payload-rollback", platform.Operations);
        Assert.Contains("service-restore", platform.Operations);
        Assert.True(platform.ServiceRegistered);
        Assert.True(platform.ServiceRunning);
        Assert.True(platform.TrayShortcutPresent);
        Assert.True(platform.SessionAgentShortcutPresent);
        Assert.True(platform.StartMenuShortcutPresent);
    }

    [Fact]
    public async Task Install_FreshInstallFailure_RemovesNewServiceShortcutAndActivatedTree()
    {
        RecordingSetupPlatform platform = new()
        {
            FailurePoint = "service-health",
            TrayShortcutExisted = false,
            SessionAgentShortcutExisted = false,
            StartMenuShortcutExisted = false,
            Transaction = new RecordingPayloadTransaction(hadExistingInstall: false),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SetupCommandRunner(platform).RunAsync(["install"], CancellationToken.None));

        AssertEndsWith(
            platform.Operations,
            [
                "shortcut-remove", "agent-shortcut-remove", "start-menu-remove",
                "uninstall-shortcut-remove", "uninstall-registration-remove",
                "dynamic-lighting-remove", "service-remove", "tray-stop",
                "session-agent-stop", "payload-rollback",
            ]);
        Assert.DoesNotContain("shortcut-install", platform.Operations);
        Assert.DoesNotContain("data-remove", platform.Operations);
    }

    [Theory]
    [InlineData(false, "copy failed")]
    [InlineData(true, "SHA-256 verification failed")]
    public async Task Install_UninstallerCopyOrVerificationFailure_RollsBackActivatedPayload(
        bool existingInstall,
        string failureMessage)
    {
        RecordingSetupPlatform platform = new()
        {
            ServiceExists = existingInstall,
            UninstallerFailure = failureMessage.Contains("SHA-256", StringComparison.Ordinal)
                ? new InvalidDataException(failureMessage)
                : new IOException(failureMessage),
            Transaction = new RecordingPayloadTransaction(existingInstall),
        };

        Exception failure = await Assert.ThrowsAnyAsync<Exception>(
            () => new SetupCommandRunner(platform).RunAsync(["install"], CancellationToken.None));

        Assert.Equal(failureMessage, failure.Message);
        Assert.Contains("payload-activate", platform.Operations);
        Assert.Contains("uninstaller-install", platform.Operations);
        Assert.Contains("payload-rollback", platform.Operations);
        Assert.DoesNotContain("payload-commit", platform.Operations);
        Assert.Equal(existingInstall, platform.Operations.Contains("service-restore"));
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
        AssertEndsWith(
            platform.Operations,
            ["payload-rollback", "service-restore", "start-menu-install"]);
    }

    [Fact]
    public async Task Install_UpdateFailure_ReregistersPriorDynamicLightingIdentityAfterPayloadRollback()
    {
        RecordingSetupPlatform platform = new()
        {
            ServiceExists = true,
            DynamicLightingIdentityRegistered = true,
            FailurePoint = "service-health",
            Transaction = new RecordingPayloadTransaction(hadExistingInstall: true),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SetupCommandRunner(platform).RunAsync(["install"], CancellationToken.None));

        int rollbackIndex = platform.Operations.IndexOf("payload-rollback");
        int restoreIdentityIndex = platform.Operations.LastIndexOf("dynamic-lighting-install");
        int restoreServiceIndex = platform.Operations.IndexOf("service-restore");
        Assert.True(rollbackIndex >= 0);
        Assert.True(restoreIdentityIndex > rollbackIndex);
        Assert.True(restoreServiceIndex > restoreIdentityIndex);
        Assert.Equal(2, platform.Operations.Count(
            operation => operation == "dynamic-lighting-install"));
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
                "shortcut-remove", "agent-shortcut-remove", "start-menu-remove",
                "uninstall-shortcut-remove", "uninstall-registration-remove",
                "dynamic-lighting-remove", "payload-rollback", "service-restore",
                "agent-shortcut-install", "shortcut-install", "start-menu-install",
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
            ["lifecycle-capture", "shortcut-remove", "agent-shortcut-remove", "start-menu-remove", "service-remove", "tray-stop", "session-agent-stop", "dynamic-lighting-remove", "uninstall-shortcut-remove", "uninstall-registration-remove", "binaries-remove"],
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
            ["lifecycle-capture", "shortcut-remove", "agent-shortcut-remove", "start-menu-remove", "service-remove", "tray-stop", "session-agent-stop", "dynamic-lighting-remove", "uninstall-shortcut-remove", "uninstall-registration-remove", "binaries-remove", "data-remove"],
            platform.Operations);
    }

    [Fact]
    public async Task Uninstall_BinaryRemovalFailure_RestoresExistingRetryEntries()
    {
        RecordingSetupPlatform platform = new()
        {
            UninstallShortcutExisted = true,
            UninstallRegistrationExisted = true,
            FailurePoint = "binaries-remove",
        };

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SetupCommandRunner(platform).RunAsync(["uninstall"], CancellationToken.None));

        Assert.Equal("binaries-remove failed", failure.Message);
        AssertEndsWith(
            platform.Operations,
            ["uninstall-shortcut-remove", "uninstall-registration-remove", "binaries-remove",
             "uninstall-shortcut-install", "uninstall-registration-install"]);
    }

    [Fact]
    public async Task Uninstall_BinaryRemovalFailure_ReregistersPriorDynamicLightingIdentity()
    {
        RecordingSetupPlatform platform = new()
        {
            DynamicLightingIdentityRegistered = true,
            FailurePoint = "binaries-remove",
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SetupCommandRunner(platform).RunAsync(["uninstall"], CancellationToken.None));

        int binaryFailureIndex = platform.Operations.IndexOf("binaries-remove");
        int identityRecoveryIndex = platform.Operations.LastIndexOf("dynamic-lighting-install");
        Assert.True(identityRecoveryIndex > binaryFailureIndex);
    }

    [Fact]
    public async Task Uninstall_BundleManaged_RemovesOwnershipBeforeBinaries()
    {
        RecordingSetupPlatform platform = new() { BundleOwnershipExisted = true };

        int exitCode = await new SetupCommandRunner(platform)
            .RunAsync(["uninstall", "--bundle-managed"], CancellationToken.None);

        Assert.Equal(0, exitCode);
        AssertEndsWith(
            platform.Operations,
            [
                "uninstall-shortcut-remove", "uninstall-registration-remove",
                "bundle-ownership-remove", "binaries-remove",
            ]);
    }

    [Fact]
    public async Task Uninstall_SupersededBundleManagedCommandLeavesNewerPayloadUntouched()
    {
        RecordingSetupPlatform platform = new()
        {
            BundleOwnershipExisted = true,
            BundleOwnershipMatchesCurrentBuild = false,
        };

        int exitCode = await new SetupCommandRunner(platform)
            .RunAsync(["uninstall", "--bundle-managed"], CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(["lifecycle-capture"], platform.Operations);
    }

    [Fact]
    public async Task Uninstall_BundleManagedBinaryFailure_RestoresOwnership()
    {
        RecordingSetupPlatform platform = new()
        {
            BundleOwnershipExisted = true,
            FailurePoint = "binaries-remove",
        };

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SetupCommandRunner(platform)
                .RunAsync(["uninstall", "--bundle-managed"], CancellationToken.None));

        Assert.Equal("binaries-remove failed", failure.Message);
        AssertEndsWith(
            platform.Operations,
            ["bundle-ownership-remove", "binaries-remove", "bundle-ownership-restore"]);
    }

    [Fact]
    public async Task Uninstall_OrphanedBundleRecovery_RemovesOwnershipBeforeBinaries()
    {
        RecordingSetupPlatform platform = new() { BundleOwnershipExisted = true };

        int exitCode = await new SetupCommandRunner(platform)
            .RunAsync(["uninstall", "--recover-orphaned-bundle"], CancellationToken.None);

        Assert.Equal(0, exitCode);
        AssertEndsWith(
            platform.Operations,
            [
                "uninstall-shortcut-remove", "uninstall-registration-remove",
                "bundle-ownership-remove", "binaries-remove",
            ]);
    }

    [Fact]
    public async Task Uninstall_OrphanedBundleRecoveryRequiresOwnershipMarker()
    {
        RecordingSetupPlatform platform = new();

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SetupCommandRunner(platform)
                .RunAsync(["uninstall", "--recover-orphaned-bundle"], CancellationToken.None));

        Assert.Contains("No orphaned", failure.Message, StringComparison.Ordinal);
        Assert.Equal(["lifecycle-capture"], platform.Operations);
    }

    [Fact]
    public async Task Uninstall_OrphanedBundleRecoveryFailure_RestoresOwnership()
    {
        RecordingSetupPlatform platform = new()
        {
            BundleOwnershipExisted = true,
            FailurePoint = "binaries-remove",
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SetupCommandRunner(platform)
                .RunAsync(["uninstall", "--recover-orphaned-bundle"], CancellationToken.None));

        AssertEndsWith(
            platform.Operations,
            ["bundle-ownership-remove", "binaries-remove", "bundle-ownership-restore"]);
    }

    [Fact]
    public async Task Uninstall_StandaloneCommandRejectsBundleManagedInstallationBeforeMutation()
    {
        RecordingSetupPlatform platform = new() { BundleOwnershipExisted = true };

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SetupCommandRunner(platform).RunAsync(["uninstall"], CancellationToken.None));

        Assert.Contains("graphical installer", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["lifecycle-capture"], platform.Operations);
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
            Assert.Contains("--recover-orphaned-bundle", errors.ToString(), StringComparison.Ordinal);
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
        public bool StartMenuShortcutExisted { get; init; } = true;
        public bool UninstallShortcutExisted { get; init; }
        public bool UninstallRegistrationExisted { get; init; }
        public bool BundleOwnershipExisted { get; init; }
        public bool BundleOwnershipMatchesCurrentBuild { get; init; } = true;
        public BundleOwnershipIdentity? InitialBundleOwnership { get; init; }
        public bool OmitBundleOwnershipSnapshot { get; init; }
        public bool DynamicLightingIdentityRegistered { get; init; }
        public string CurrentBuildIdentity { get; set; } = "0.1.0+current";
        public string? FailurePoint { get; set; }
        public CancellationTokenSource? Cancellation { get; init; }
        public Exception? UninstallerFailure { get; init; }
        public RecordingPayloadTransaction Transaction { get; init; } = new(hadExistingInstall: false);
        private bool lifecycleInitialized;
        private bool serviceRegistered;
        private bool serviceRunning;
        private bool trayShortcutPresent;
        private bool sessionAgentShortcutPresent;
        private bool startMenuShortcutPresent;
        private BundleOwnershipIdentity? bundleOwnership;

        public bool ServiceRegistered => serviceRegistered;
        public bool ServiceRunning => serviceRunning;
        public bool TrayShortcutPresent => trayShortcutPresent;
        public bool SessionAgentShortcutPresent => sessionAgentShortcutPresent;
        public bool StartMenuShortcutPresent => startMenuShortcutPresent;
        public BundleOwnershipIdentity? CurrentBundleOwnership => bundleOwnership;

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
                startMenuShortcutPresent = StartMenuShortcutExisted;
                bundleOwnership = InitialBundleOwnership
                    ?? (BundleOwnershipExisted
                        ? new BundleOwnershipIdentity(
                            "WiX Burn",
                            "0.1.0",
                            BundleOwnershipMatchesCurrentBuild
                                ? CurrentBuildIdentity
                                : "0.1.0+different")
                        : null);
                lifecycleInitialized = true;
            }

            BundleOwnershipIdentity? capturedOwnership =
                OmitBundleOwnershipSnapshot ? null : bundleOwnership;
            return Task.FromResult(new SetupInstallLifecycleState(
                new(serviceRegistered, serviceRunning),
                new(
                    trayShortcutPresent,
                    sessionAgentShortcutPresent,
                    startMenuShortcutPresent,
                    UninstallShortcutExisted,
                    UninstallRegistrationExisted),
                bundleOwnership is not null,
                DynamicLightingIdentityRegistered,
                bundleOwnership is null
                    || string.Equals(
                        bundleOwnership.InstalledBuild,
                        CurrentBuildIdentity,
                        StringComparison.Ordinal))
            {
                BundleOwnership = capturedOwnership,
            });
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

        public Task InstallUninstallerAsync(CancellationToken cancellationToken)
        {
            Operations.Add("uninstaller-install");
            return UninstallerFailure is null
                ? Task.CompletedTask
                : Task.FromException(UninstallerFailure);
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
        public Task InstallStartMenuShortcutAsync(CancellationToken cancellationToken)
        {
            startMenuShortcutPresent = true;
            return AddAsync("start-menu-install", cancellationToken);
        }
        public Task RemoveStartMenuShortcutAsync(CancellationToken cancellationToken)
        {
            startMenuShortcutPresent = false;
            return AddAsync("start-menu-remove", cancellationToken);
        }
        public Task InstallUninstallShortcutAsync(CancellationToken cancellationToken) =>
            AddAsync("uninstall-shortcut-install", cancellationToken);
        public Task RemoveUninstallShortcutAsync(CancellationToken cancellationToken) =>
            AddAsync("uninstall-shortcut-remove", cancellationToken);
        public Task InstallUninstallRegistrationAsync(CancellationToken cancellationToken) =>
            AddAsync("uninstall-registration-install", cancellationToken);
        public Task RemoveUninstallRegistrationAsync(CancellationToken cancellationToken) =>
            AddAsync("uninstall-registration-remove", cancellationToken);
        public async Task InstallBundleOwnershipAsync(CancellationToken cancellationToken)
        {
            await AddAsync("bundle-ownership-install", cancellationToken);
            bundleOwnership = new BundleOwnershipIdentity(
                "WiX Burn",
                "0.1.0",
                CurrentBuildIdentity);
        }
        public async Task RestoreBundleOwnershipAsync(
            BundleOwnershipIdentity ownership,
            CancellationToken cancellationToken)
        {
            await AddAsync("bundle-ownership-restore", cancellationToken);
            bundleOwnership = ownership;
        }
        public async Task RemoveBundleOwnershipAsync(CancellationToken cancellationToken)
        {
            await AddAsync("bundle-ownership-remove", cancellationToken);
            bundleOwnership = null;
        }
        public Task InstallDynamicLightingIdentityAsync(CancellationToken cancellationToken) =>
            AddAsync("dynamic-lighting-install", cancellationToken);
        public Task RemoveDynamicLightingIdentityAsync(CancellationToken cancellationToken) =>
            AddAsync("dynamic-lighting-remove", cancellationToken);
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
                && operation is "shortcut-remove" or "agent-shortcut-remove" or "start-menu-remove"
                    or "uninstall-shortcut-remove" or "uninstall-registration-remove"
                    or "dynamic-lighting-remove" or "service-remove" or "tray-stop"
                    or "session-agent-stop")
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
        Exception? disposeCleanupFailure = null,
        CancellationTokenSource? cancelOnCommit = null) : ISetupPayloadTransaction
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
            if (cancelOnCommit is not null)
            {
                cancelOnCommit.Cancel();
                return Task.FromCanceled(cancelOnCommit.Token);
            }

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
