using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using OpenBlade.Contracts;

namespace OpenBlade.DeploymentClient.Tests;

public sealed class DeploymentClientCoordinatorTests
{
    private static readonly Guid RequestId = Guid.Parse(
        "22222222-2222-2222-2222-222222222222");
    private static readonly InstalledPayloadPaths Paths = new(
        @"C:\Program Files\OpenBlade",
        @"C:\Program Files\OpenBlade\OpenBlade.SessionAgent.exe",
        @"C:\Program Files\OpenBlade\OpenBlade.Tray.exe");

    [Fact]
    public async Task ExactSuccess_NormalIntegrityAndVerifiedPaths_LaunchInRequiredOrder()
    {
        List<string> events = [];
        RecordingLegacyStartupCleanup cleanup = new(() => events.Add("cleanup"));
        RecordingProcessLauncher processes = new(
            sessionRemainsAlive: true,
            onLaunch: _ => events.Add("launch"));
        DeploymentClientCoordinator coordinator = CreateCoordinator(
            BrokerProtocolTests.Success(),
            elevated: false,
            Paths,
            processes,
            legacyStartup: cleanup);

        bool result = await coordinator.ApplyFixedInboxAsync(CancellationToken.None);

        Assert.True(result);
        Assert.Collection(
            processes.Plans,
            session =>
            {
                Assert.Equal(Paths.SessionAgentExecutable, session.FileName);
                Assert.Equal(Paths.InstallDirectory, session.WorkingDirectory);
                Assert.True(session.CreateNoWindow);
                Assert.Equal(ProcessWindowStyle.Hidden, session.WindowStyle);
            },
            tray =>
            {
                Assert.Equal(Paths.TrayExecutable, tray.FileName);
                Assert.Equal(Paths.InstallDirectory, tray.WorkingDirectory);
                Assert.False(tray.CreateNoWindow);
                Assert.Equal(ProcessWindowStyle.Normal, tray.WindowStyle);
            });
        Assert.Equal(1, processes.SessionAliveChecks);
        Assert.Equal(1, processes.TrayAliveChecks);
        Assert.Equal(1, cleanup.Calls);
        Assert.Equal(["cleanup", "launch", "launch"], events);
    }

    [Theory]
    [InlineData(0, 0, (int)InstalledProcessShape.None)]
    [InlineData(1, 0, (int)InstalledProcessShape.SessionAgentOnly)]
    [InlineData(1, 1, (int)InstalledProcessShape.SessionAgentAndTray)]
    public async Task AllowedInitialProcessShapes_ShutdownBeforeBrokerAndInstallSucceeds(
        int sessionAgents,
        int trays,
        int expectedShape)
    {
        List<string> events = [];
        FixedBrokerChannel broker = new(
            BrokerProtocolTests.Success(),
            () => events.Add("broker"));
        RecordingProcessShutdown shutdown = new(
            succeeds: true,
            onShutdown: _ => events.Add("shutdown"));
        RecordingProcessLauncher processes = new(
            sessionRemainsAlive: true,
            onLaunch: plan => events.Add(
                string.Equals(
                    plan.FileName,
                    Paths.SessionAgentExecutable,
                    StringComparison.OrdinalIgnoreCase)
                    ? "agent"
                    : "tray"));
        SequenceProcessInventory inventory = new(
            new InstalledProcessInventory(true, sessionAgents, trays),
            new InstalledProcessInventory(true, 0, 0),
            new InstalledProcessInventory(true, 0, 0),
            new InstalledProcessInventory(true, 1, 0),
            new InstalledProcessInventory(true, 1, 1));
        DeploymentClientCoordinator coordinator = new(
            broker,
            new FixedIntegritySource(elevated: false),
            new FixedSessionSource(active: true),
            new RecordingLegacyStartupCleanup(() => events.Add("cleanup")),
            new FixedInstalledPayloadVerifier(Paths),
            inventory,
            shutdown,
            processes,
            new FixedRequestIdSource());

        Assert.True(await coordinator.ApplyFixedInboxAsync(CancellationToken.None));
        Assert.Equal(1, broker.Calls);
        Assert.Equal(5, inventory.Calls);
        Assert.Equal(
            sessionAgents == 0
                ? []
                : [(InstalledProcessShape)expectedShape],
            shutdown.Shapes);
        Assert.Equal(
            sessionAgents == 0
                ? ["broker", "cleanup", "agent", "tray"]
                : ["shutdown", "broker", "cleanup", "agent", "tray"],
            events);
    }

    [Fact]
    public async Task ShutdownFailure_EmitsStableDiagnosticAndNeverCallsBroker()
    {
        FixedBrokerChannel broker = new(BrokerProtocolTests.Success());
        RecordingProcessShutdown shutdown = new(succeeds: false);
        List<string> diagnostics = [];
        DeploymentClientCoordinator coordinator = new(
            broker,
            new FixedIntegritySource(elevated: false),
            new FixedSessionSource(active: true),
            new RecordingLegacyStartupCleanup(),
            new FixedInstalledPayloadVerifier(Paths),
            new SequenceProcessInventory(
                new InstalledProcessInventory(true, 1, 1)),
            shutdown,
            new RecordingProcessLauncher(sessionRemainsAlive: true),
            new FixedRequestIdSource(),
            diagnostics.Add);

        Assert.False(await coordinator.ApplyFixedInboxAsync(CancellationToken.None));
        Assert.Equal(0, broker.Calls);
        Assert.Equal([InstalledProcessShape.SessionAgentAndTray], shutdown.Shapes);
        Assert.Equal(["installed-process-shutdown-failed"], diagnostics);
    }

    [Fact]
    public async Task ShutdownException_EmitsStableDiagnosticAndNeverCallsBroker()
    {
        FixedBrokerChannel broker = new(BrokerProtocolTests.Success());
        List<string> diagnostics = [];
        DeploymentClientCoordinator coordinator = new(
            broker,
            new FixedIntegritySource(elevated: false),
            new FixedSessionSource(active: true),
            new RecordingLegacyStartupCleanup(),
            new FixedInstalledPayloadVerifier(Paths),
            new SequenceProcessInventory(
                new InstalledProcessInventory(true, 1, 0)),
            new ThrowingProcessShutdown(new UnauthorizedAccessException()),
            new RecordingProcessLauncher(sessionRemainsAlive: true),
            new FixedRequestIdSource(),
            diagnostics.Add);

        Assert.False(await coordinator.ApplyFixedInboxAsync(CancellationToken.None));
        Assert.Equal(0, broker.Calls);
        Assert.Equal(["installed-process-shutdown-failed"], diagnostics);
    }

    [Fact]
    public async Task PresentProcessWithUnverifiedPayload_NeverShutsDownOrCallsBroker()
    {
        FixedBrokerChannel broker = new(BrokerProtocolTests.Success());
        RecordingProcessShutdown shutdown = new(succeeds: true);
        DeploymentClientCoordinator coordinator = new(
            broker,
            new FixedIntegritySource(elevated: false),
            new FixedSessionSource(active: true),
            new RecordingLegacyStartupCleanup(),
            new FixedInstalledPayloadVerifier(paths: null),
            new SequenceProcessInventory(
                new InstalledProcessInventory(true, 1, 0)),
            shutdown,
            new RecordingProcessLauncher(sessionRemainsAlive: true),
            new FixedRequestIdSource());

        Assert.False(await coordinator.ApplyFixedInboxAsync(CancellationToken.None));
        Assert.Empty(shutdown.Shapes);
        Assert.Equal(0, broker.Calls);
    }

    [Fact]
    public async Task NonemptyInventoryAfterShutdown_NeverCallsBroker()
    {
        FixedBrokerChannel broker = new(BrokerProtocolTests.Success());
        DeploymentClientCoordinator coordinator = new(
            broker,
            new FixedIntegritySource(elevated: false),
            new FixedSessionSource(active: true),
            new RecordingLegacyStartupCleanup(),
            new FixedInstalledPayloadVerifier(Paths),
            new SequenceProcessInventory(
                new InstalledProcessInventory(true, 1, 0),
                new InstalledProcessInventory(true, 1, 0)),
            new RecordingProcessShutdown(succeeds: true),
            new RecordingProcessLauncher(sessionRemainsAlive: true),
            new FixedRequestIdSource());

        Assert.False(await coordinator.ApplyFixedInboxAsync(CancellationToken.None));
        Assert.Equal(0, broker.Calls);
    }

    [Fact]
    public async Task ContextDriftBeforeBroker_NeverCallsBroker()
    {
        FixedBrokerChannel broker = new(BrokerProtocolTests.Success());
        SequenceIntegritySource integrity = new(false, true);
        DeploymentClientCoordinator coordinator = new(
            broker,
            integrity,
            new FixedSessionSource(active: true),
            new RecordingLegacyStartupCleanup(),
            new FixedInstalledPayloadVerifier(Paths),
            new SequenceProcessInventory(
                new InstalledProcessInventory(true, 0, 0)),
            new RecordingProcessShutdown(succeeds: true),
            new RecordingProcessLauncher(sessionRemainsAlive: true),
            new FixedRequestIdSource());

        Assert.False(await coordinator.ApplyFixedInboxAsync(CancellationToken.None));
        Assert.Equal(0, broker.Calls);
        Assert.Equal(2, integrity.Calls);
    }

    [Theory]
    [InlineData((int)DeploymentBrokerResponseStatus.Rejected, "rejected")]
    [InlineData((int)DeploymentBrokerResponseStatus.RolledBack, "rollback-complete")]
    [InlineData((int)DeploymentBrokerResponseStatus.Indeterminate, "installer-indeterminate")]
    [InlineData((int)DeploymentBrokerResponseStatus.Succeeded, "wrong-success-code")]
    public async Task AnyNonExactBrokerSuccess_LaunchesNothing(
        int status,
        string code)
    {
        RecordingProcessLauncher processes = new(sessionRemainsAlive: true);
        RecordingLegacyStartupCleanup cleanup = new();
        List<string> diagnostics = [];
        DeploymentBrokerResponse response = BrokerProtocolTests.Success() with
        {
            Status = (DeploymentBrokerResponseStatus)status,
            Code = code,
        };
        DeploymentClientCoordinator coordinator = CreateCoordinator(
            response,
            elevated: false,
            Paths,
            processes,
            diagnostics.Add,
            cleanup);

        Assert.False(await coordinator.ApplyFixedInboxAsync(CancellationToken.None));
        Assert.Empty(processes.Plans);
        Assert.Equal(
            [$"broker-response:{(DeploymentBrokerResponseStatus)status}:{code}"],
            diagnostics);
        Assert.Equal(0, cleanup.Calls);
    }

    [Fact]
    public async Task ElevatedCallerOrUnverifiedInstalledPaths_LaunchNothing()
    {
        RecordingProcessLauncher elevatedProcesses = new(sessionRemainsAlive: true);
        FixedBrokerChannel elevatedBroker = new(BrokerProtocolTests.Success());
        FixedIntegritySource elevatedIntegrity = new(elevated: true);
        RecordingLegacyStartupCleanup elevatedCleanup = new();
        DeploymentClientCoordinator elevated = new(
            elevatedBroker,
            elevatedIntegrity,
            new FixedSessionSource(active: true),
            elevatedCleanup,
            new FixedInstalledPayloadVerifier(Paths),
            new SequenceProcessInventory(),
            new RecordingProcessShutdown(succeeds: true),
            elevatedProcesses,
            new FixedRequestIdSource());
        Assert.False(await elevated.ApplyFixedInboxAsync(CancellationToken.None));
        Assert.Equal(0, elevatedBroker.Calls);
        Assert.Equal(1, elevatedIntegrity.Calls);
        Assert.Equal(0, elevatedCleanup.Calls);
        Assert.Empty(elevatedProcesses.Plans);

        RecordingProcessLauncher missingProcesses = new(sessionRemainsAlive: true);
        DeploymentClientCoordinator missing = CreateCoordinator(
            BrokerProtocolTests.Success(),
            elevated: false,
            paths: null,
            missingProcesses);
        Assert.False(await missing.ApplyFixedInboxAsync(CancellationToken.None));
        Assert.Empty(missingProcesses.Plans);
    }

    [Fact]
    public async Task SessionAgentThatDoesNotRemainAlive_BlocksTrayLaunch()
    {
        RecordingProcessLauncher processes = new(sessionRemainsAlive: false);
        DeploymentClientCoordinator coordinator = CreateCoordinator(
            BrokerProtocolTests.Success(),
            elevated: false,
            Paths,
            processes);

        Assert.False(await coordinator.ApplyFixedInboxAsync(CancellationToken.None));
        InstalledProcessLaunchPlan session = Assert.Single(processes.Plans);
        Assert.Equal(Paths.SessionAgentExecutable, session.FileName);
        Assert.Equal(1, processes.SessionAliveChecks);
    }

    [Fact]
    public async Task MismatchedResponseRequestId_LaunchesNothing()
    {
        RecordingProcessLauncher processes = new(sessionRemainsAlive: true);
        DeploymentClientCoordinator coordinator = CreateCoordinator(
            BrokerProtocolTests.Success() with { RequestId = Guid.NewGuid() },
            elevated: false,
            Paths,
            processes);

        Assert.False(await coordinator.ApplyFixedInboxAsync(CancellationToken.None));
        Assert.Empty(processes.Plans);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 0, 1)]
    [InlineData(1, 1, 2)]
    public async Task ExactRollback_RestoresOnlyOriginalShapeWithoutCleanup(
        int sessionAgents,
        int trays,
        int expectedLaunches)
    {
        DeploymentBrokerResponse rollback = BrokerProtocolTests.Success() with
        {
            Status = DeploymentBrokerResponseStatus.RolledBack,
            Code = "install-rolled-back",
        };
        List<InstalledProcessInventory> states =
        [
            new InstalledProcessInventory(true, sessionAgents, trays),
            new InstalledProcessInventory(true, 0, 0),
            new InstalledProcessInventory(true, 0, 0),
        ];
        if (expectedLaunches >= 1)
        {
            states.Add(new InstalledProcessInventory(true, 1, 0));
        }

        if (expectedLaunches == 2)
        {
            states.Add(new InstalledProcessInventory(true, 1, 1));
        }

        RecordingLegacyStartupCleanup cleanup = new();
        RecordingProcessLauncher processes = new(sessionRemainsAlive: true);
        List<string> diagnostics = [];
        DeploymentClientCoordinator coordinator = new(
            new FixedBrokerChannel(rollback),
            new FixedIntegritySource(elevated: false),
            new FixedSessionSource(active: true),
            cleanup,
            new FixedInstalledPayloadVerifier(Paths),
            new SequenceProcessInventory(states.ToArray()),
            new RecordingProcessShutdown(succeeds: true),
            processes,
            new FixedRequestIdSource(),
            diagnostics.Add);

        Assert.False(await coordinator.ApplyFixedInboxAsync(CancellationToken.None));
        Assert.Equal(expectedLaunches, processes.Plans.Count);
        Assert.Equal(0, cleanup.Calls);
        Assert.Equal(["broker-response:RolledBack:install-rolled-back"], diagnostics);
    }

    [Theory]
    [InlineData((int)DeploymentBrokerResponseStatus.Rejected, "deployment-in-progress")]
    [InlineData((int)DeploymentBrokerResponseStatus.Indeterminate, "installer-indeterminate")]
    public async Task RejectedOrIndeterminateAfterShutdown_DoesNotRestore(
        int status,
        string code)
    {
        RecordingProcessLauncher processes = new(sessionRemainsAlive: true);
        DeploymentClientCoordinator coordinator = new(
            new FixedBrokerChannel(BrokerProtocolTests.Success() with
            {
                Status = (DeploymentBrokerResponseStatus)status,
                Code = code,
            }),
            new FixedIntegritySource(elevated: false),
            new FixedSessionSource(active: true),
            new RecordingLegacyStartupCleanup(),
            new FixedInstalledPayloadVerifier(Paths),
            new SequenceProcessInventory(
                new InstalledProcessInventory(true, 1, 1),
                new InstalledProcessInventory(true, 0, 0)),
            new RecordingProcessShutdown(succeeds: true),
            processes,
            new FixedRequestIdSource());

        Assert.False(await coordinator.ApplyFixedInboxAsync(CancellationToken.None));
        Assert.Empty(processes.Plans);
    }

    [Fact]
    public async Task MismatchedRollbackAfterShutdown_DoesNotRestore()
    {
        RecordingProcessLauncher processes = new(sessionRemainsAlive: true);
        DeploymentClientCoordinator coordinator = new(
            new FixedBrokerChannel(BrokerProtocolTests.Success() with
            {
                RequestId = Guid.NewGuid(),
                Status = DeploymentBrokerResponseStatus.RolledBack,
                Code = "install-rolled-back",
            }),
            new FixedIntegritySource(elevated: false),
            new FixedSessionSource(active: true),
            new RecordingLegacyStartupCleanup(),
            new FixedInstalledPayloadVerifier(Paths),
            new SequenceProcessInventory(
                new InstalledProcessInventory(true, 1, 1),
                new InstalledProcessInventory(true, 0, 0)),
            new RecordingProcessShutdown(succeeds: true),
            processes,
            new FixedRequestIdSource());

        Assert.False(await coordinator.ApplyFixedInboxAsync(CancellationToken.None));
        Assert.Empty(processes.Plans);
    }

    [Fact]
    public async Task ContextDriftAfterExactRollback_DoesNotRestore()
    {
        RecordingProcessLauncher processes = new(sessionRemainsAlive: true);
        SequenceIntegritySource integrity = new(false, false, true);
        DeploymentClientCoordinator coordinator = new(
            new FixedBrokerChannel(BrokerProtocolTests.Success() with
            {
                Status = DeploymentBrokerResponseStatus.RolledBack,
                Code = "install-rolled-back",
            }),
            integrity,
            new FixedSessionSource(active: true),
            new RecordingLegacyStartupCleanup(),
            new FixedInstalledPayloadVerifier(Paths),
            new SequenceProcessInventory(
                new InstalledProcessInventory(true, 1, 1),
                new InstalledProcessInventory(true, 0, 0)),
            new RecordingProcessShutdown(succeeds: true),
            processes,
            new FixedRequestIdSource());

        Assert.False(await coordinator.ApplyFixedInboxAsync(CancellationToken.None));
        Assert.Equal(3, integrity.Calls);
        Assert.Empty(processes.Plans);
    }

    [Fact]
    public async Task IntegrityDriftAfterBrokerSuccess_BlocksAllRelaunches()
    {
        RecordingProcessLauncher processes = new(sessionRemainsAlive: true);
        SequenceIntegritySource integrity = new(false, false, true);
        RecordingLegacyStartupCleanup cleanup = new();
        DeploymentClientCoordinator coordinator = new(
            new FixedBrokerChannel(BrokerProtocolTests.Success()),
            integrity,
            new FixedSessionSource(active: true),
            cleanup,
            new FixedInstalledPayloadVerifier(Paths),
            new SequenceProcessInventory(),
            new RecordingProcessShutdown(succeeds: true),
            processes,
            new FixedRequestIdSource());

        Assert.False(await coordinator.ApplyFixedInboxAsync(CancellationToken.None));
        Assert.Equal(3, integrity.Calls);
        Assert.Equal(0, cleanup.Calls);
        Assert.Empty(processes.Plans);
    }

    [Fact]
    public async Task ActiveConsoleSessionDriftAfterBrokerSuccess_BlocksAllRelaunches()
    {
        RecordingProcessLauncher processes = new(sessionRemainsAlive: true);
        SequenceSessionSource session = new(true, true, false);
        RecordingLegacyStartupCleanup cleanup = new();
        DeploymentClientCoordinator coordinator = new(
            new FixedBrokerChannel(BrokerProtocolTests.Success()),
            new FixedIntegritySource(elevated: false),
            session,
            cleanup,
            new FixedInstalledPayloadVerifier(Paths),
            new SequenceProcessInventory(),
            new RecordingProcessShutdown(succeeds: true),
            processes,
            new FixedRequestIdSource());

        Assert.False(await coordinator.ApplyFixedInboxAsync(CancellationToken.None));
        Assert.Equal(3, session.Calls);
        Assert.Equal(0, cleanup.Calls);
        Assert.Empty(processes.Plans);
    }

    [Fact]
    public async Task InitialInactiveConsoleSession_DoesNotCallBrokerOrCleanup()
    {
        FixedBrokerChannel broker = new(BrokerProtocolTests.Success());
        RecordingLegacyStartupCleanup cleanup = new();
        RecordingProcessLauncher processes = new(sessionRemainsAlive: true);
        DeploymentClientCoordinator coordinator = new(
            broker,
            new FixedIntegritySource(elevated: false),
            new FixedSessionSource(active: false),
            cleanup,
            new FixedInstalledPayloadVerifier(Paths),
            new SequenceProcessInventory(),
            new RecordingProcessShutdown(succeeds: true),
            processes,
            new FixedRequestIdSource());

        Assert.False(await coordinator.ApplyFixedInboxAsync(CancellationToken.None));
        Assert.Equal(0, broker.Calls);
        Assert.Equal(0, cleanup.Calls);
        Assert.Empty(processes.Plans);
    }

    [Fact]
    public async Task LegacyStartupCleanupFailure_EmitsStableDiagnosticAndLaunchesNothing()
    {
        RecordingProcessLauncher processes = new(sessionRemainsAlive: true);
        List<string> diagnostics = [];
        DeploymentClientCoordinator coordinator = new(
            new FixedBrokerChannel(BrokerProtocolTests.Success()),
            new FixedIntegritySource(elevated: false),
            new FixedSessionSource(active: true),
            new FailingLegacyStartupCleanup(),
            new FixedInstalledPayloadVerifier(Paths),
            new SequenceProcessInventory(),
            new RecordingProcessShutdown(succeeds: true),
            processes,
            new FixedRequestIdSource(),
            diagnostics.Add);

        Assert.False(await coordinator.ApplyFixedInboxAsync(CancellationToken.None));
        Assert.Equal(["legacy-startup-cleanup-failed"], diagnostics);
        Assert.Empty(processes.Plans);
    }

    [Fact]
    public void ConcreteLaunchPolicy_AllowsOnlyTwoFixedInstalledPlans()
    {
        Assert.True(InstalledProcessLaunchPolicy.IsAllowed(
            InstalledProcessLaunchPlan.SessionAgent(Paths),
            Paths));
        Assert.True(InstalledProcessLaunchPolicy.IsAllowed(
            InstalledProcessLaunchPlan.Tray(Paths),
            Paths));
        Assert.False(InstalledProcessLaunchPolicy.IsAllowed(
            InstalledProcessLaunchPlan.Tray(Paths) with
            {
                FileName = @"C:\Users\Public\OpenBlade.Tray.exe",
            },
            Paths));
        Assert.False(InstalledProcessLaunchPolicy.IsAllowed(
            InstalledProcessLaunchPlan.SessionAgent(Paths) with
            {
                WorkingDirectory = @"C:\Users\Public",
            },
            Paths));
        Assert.False(InstalledProcessLaunchPolicy.IsAllowed(
            InstalledProcessLaunchPlan.SessionAgent(Paths) with
            {
                WindowStyle = ProcessWindowStyle.Normal,
            },
            Paths));
    }

    [Fact]
    public void InstalledPayloadPolicy_RejectsAnyCallerWritableOrReparsePath()
    {
        InstalledPayloadSecurityEvidence secure = new(
            InstallDirectoryExists: true,
            SessionAgentExists: true,
            TrayExists: true,
            InstallDirectoryIsReparsePoint: false,
            SessionAgentIsReparsePoint: false,
            TrayIsReparsePoint: false,
            InstallDirectoryCallerWritable: false,
            SessionAgentCallerWritable: false,
            TrayCallerWritable: false);

        Assert.True(InstalledPayloadVerificationPolicy.IsTrusted(Paths, Paths, secure));
        Assert.False(InstalledPayloadVerificationPolicy.IsTrusted(
            Paths,
            Paths,
            secure with { InstallDirectoryCallerWritable = true }));
        Assert.False(InstalledPayloadVerificationPolicy.IsTrusted(
            Paths,
            Paths,
            secure with { SessionAgentCallerWritable = true }));
        Assert.False(InstalledPayloadVerificationPolicy.IsTrusted(
            Paths,
            Paths,
            secure with { TrayCallerWritable = true }));
        Assert.False(InstalledPayloadVerificationPolicy.IsTrusted(
            Paths,
            Paths,
            secure with { SessionAgentIsReparsePoint = true }));
        Assert.False(InstalledPayloadVerificationPolicy.IsTrusted(
            Paths with { TrayExecutable = @"C:\Users\Public\OpenBlade.Tray.exe" },
            Paths,
            secure));
    }

    [Fact]
    public void EffectiveCallerIdentities_QueryFilteredTokenWithoutAccessDenied()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier user = Assert.IsType<SecurityIdentifier>(identity.User);

        HashSet<SecurityIdentifier> effective =
            WindowsInstalledPayloadVerifier.GetEffectiveCallerIdentities();

        Assert.Contains(user, effective);
    }

    [Fact]
    public void InstalledPayloadAcl_ReadExecuteIsNotTreatedAsWriteAccess()
    {
        FileSystemRights readOnly = FileSystemRights.ReadAndExecute
            | FileSystemRights.Synchronize;

        Assert.False(WindowsInstalledPayloadVerifier.HasDangerousWriteAccess(
            readOnly,
            denied: 0));
        Assert.True(WindowsInstalledPayloadVerifier.HasDangerousWriteAccess(
            FileSystemRights.WriteData,
            denied: 0));
        Assert.True(WindowsInstalledPayloadVerifier.HasDangerousWriteAccess(
            FileSystemRights.Delete,
            denied: 0));
        Assert.False(WindowsInstalledPayloadVerifier.HasDangerousWriteAccess(
            FileSystemRights.WriteData,
            FileSystemRights.WriteData));
    }

    [Fact]
    public void ExactInstalledProcessInAnotherSession_IsAmbiguous()
    {
        Assert.False(WindowsInstalledProcessInventory.ShouldInspectIntegrity(
            Paths.TrayExecutable,
            Paths.TrayExecutable,
            currentSession: 1,
            processSession: 2,
            out bool ambiguous));
        Assert.True(ambiguous);

        Assert.False(WindowsInstalledProcessInventory.ShouldInspectIntegrity(
            Paths.TrayExecutable,
            @"C:\Users\Public\OpenBlade.Tray.exe",
            currentSession: 1,
            processSession: 2,
            out bool unrelatedAmbiguous));
        Assert.False(unrelatedAmbiguous);
    }

    [Theory]
    [InlineData(false, 0, 0)]
    [InlineData(true, 0, 1)]
    [InlineData(true, 2, 0)]
    [InlineData(true, 1, 2)]
    [InlineData(true, 2, 1)]
    public async Task ForbiddenInitialInventory_NeverCallsBrokerOrShutdown(
        bool unambiguous,
        int sessionAgents,
        int trays)
    {
        FixedBrokerChannel broker = new(BrokerProtocolTests.Success());
        RecordingProcessShutdown shutdown = new(succeeds: true);
        RecordingProcessLauncher processes = new(sessionRemainsAlive: true);
        DeploymentClientCoordinator coordinator = new(
            broker,
            new FixedIntegritySource(elevated: false),
            new FixedSessionSource(active: true),
            new RecordingLegacyStartupCleanup(),
            new FixedInstalledPayloadVerifier(Paths),
            new SequenceProcessInventory(
                new InstalledProcessInventory(unambiguous, sessionAgents, trays)),
            shutdown,
            processes,
            new FixedRequestIdSource());

        Assert.False(await coordinator.ApplyFixedInboxAsync(CancellationToken.None));
        Assert.Equal(0, broker.Calls);
        Assert.Empty(shutdown.Shapes);
        Assert.Empty(processes.Plans);
    }

    [Fact]
    public async Task ConcreteShutdown_OpensAllFixedHandlesBeforeTrayThenAgentSignals()
    {
        InstalledPayloadPaths expected = Assert.IsType<InstalledPayloadPaths>(
            InstalledPayloadPathPolicy.GetExpectedPaths());
        List<string> events = [];
        RecordingShutdownEventSource eventSource = new(events);
        WindowsInstalledProcessShutdown shutdown = new(
            eventSource,
            new SequenceProcessInventory(
                new InstalledProcessInventory(true, 0, 0)));

        Assert.True(await shutdown.ShutdownAsync(
            expected,
            InstalledProcessShape.SessionAgentAndTray,
            CancellationToken.None));

        string trayEndpoint = TrayShutdownEndpoint.ForExecutablePath(expected.TrayExecutable);
        string agentEndpoint = SessionAgentShutdownEndpoint.ForExecutablePath(
            expected.SessionAgentExecutable);
        Assert.Equal(
            [
                $"open:{trayEndpoint}",
                $"open:{agentEndpoint}",
                $"signal:{trayEndpoint}",
                $"signal:{agentEndpoint}",
                $"dispose:{trayEndpoint}",
                $"dispose:{agentEndpoint}",
            ],
            events);
    }

    [Fact]
    public async Task ConcreteShutdown_MissingSecondHandleSignalsNeitherProcess()
    {
        InstalledPayloadPaths expected = Assert.IsType<InstalledPayloadPaths>(
            InstalledPayloadPathPolicy.GetExpectedPaths());
        string agentEndpoint = SessionAgentShutdownEndpoint.ForExecutablePath(
            expected.SessionAgentExecutable);
        List<string> events = [];
        WindowsInstalledProcessShutdown shutdown = new(
            new RecordingShutdownEventSource(events, missingEndpoint: agentEndpoint),
            new SequenceProcessInventory());

        Assert.False(await shutdown.ShutdownAsync(
            expected,
            InstalledProcessShape.SessionAgentAndTray,
            CancellationToken.None));
        Assert.DoesNotContain(events, item => item.StartsWith("signal:", StringComparison.Ordinal));
    }

    [Fact]
    public void ShutdownEndpointPolicy_UsesOnlySharedFixedInstalledEndpoints()
    {
        InstalledPayloadPaths expected = Assert.IsType<InstalledPayloadPaths>(
            InstalledPayloadPathPolicy.GetExpectedPaths());
        InstalledShutdownEndpoints both = Assert.IsType<InstalledShutdownEndpoints>(
            InstalledShutdownEndpointPolicy.Create(
                expected,
                InstalledProcessShape.SessionAgentAndTray));

        Assert.Equal(
            SessionAgentShutdownEndpoint.ForExecutablePath(expected.SessionAgentExecutable),
            both.SessionAgent);
        Assert.Equal(
            TrayShutdownEndpoint.ForExecutablePath(expected.TrayExecutable),
            both.Tray);
        Assert.Null(InstalledShutdownEndpointPolicy.Create(
            expected with
            {
                TrayExecutable = @"C:\Users\Public\OpenBlade.Tray.exe",
            },
            InstalledProcessShape.SessionAgentAndTray));
    }

    [Fact]
    public async Task BrokerChannelExceptionAfterShutdown_DoesNotRestoreOrCleanup()
    {
        RecordingProcessLauncher processes = new(sessionRemainsAlive: true);
        RecordingLegacyStartupCleanup cleanup = new();
        List<string> diagnostics = [];
        DeploymentClientCoordinator coordinator = new(
            new ThrowingBrokerChannel(new TimeoutException("Synthetic broker timeout.")),
            new FixedIntegritySource(elevated: false),
            new FixedSessionSource(active: true),
            cleanup,
            new FixedInstalledPayloadVerifier(Paths),
            new SequenceProcessInventory(
                new InstalledProcessInventory(true, 1, 1),
                new InstalledProcessInventory(true, 0, 0)),
            new RecordingProcessShutdown(succeeds: true),
            processes,
            new FixedRequestIdSource(),
            diagnostics.Add);

        Assert.False(await coordinator.ApplyFixedInboxAsync(CancellationToken.None));
        Assert.Empty(processes.Plans);
        Assert.Equal(0, cleanup.Calls);
        Assert.Equal(["client-exception:TimeoutException"], diagnostics);
    }

    [Fact]
    public async Task DuplicateOrElevatedFinalInventory_FailsRelaunchResult()
    {
        RecordingProcessLauncher processes = new(sessionRemainsAlive: true);
        DeploymentClientCoordinator coordinator = new(
            new FixedBrokerChannel(BrokerProtocolTests.Success()),
            new FixedIntegritySource(elevated: false),
            new FixedSessionSource(active: true),
            new RecordingLegacyStartupCleanup(),
            new FixedInstalledPayloadVerifier(Paths),
            new SequenceProcessInventory(
                new InstalledProcessInventory(true, 0, 0),
                new InstalledProcessInventory(true, 0, 0),
                new InstalledProcessInventory(true, 0, 0),
                new InstalledProcessInventory(true, 1, 0),
                new InstalledProcessInventory(false, 1, 2)),
            new RecordingProcessShutdown(succeeds: true),
            processes,
            new FixedRequestIdSource());

        Assert.False(await coordinator.ApplyFixedInboxAsync(CancellationToken.None));
        Assert.Equal(2, processes.Plans.Count);
        Assert.Equal(1, processes.TrayAliveChecks);
    }

    private static DeploymentClientCoordinator CreateCoordinator(
        DeploymentBrokerResponse response,
        bool elevated,
        InstalledPayloadPaths? paths,
        RecordingProcessLauncher processes,
        Action<string>? diagnostic = null,
        ILegacyStartupCleanup? legacyStartup = null) => new(
            new FixedBrokerChannel(response),
            new FixedIntegritySource(elevated),
            new FixedSessionSource(active: true),
            legacyStartup ?? new RecordingLegacyStartupCleanup(),
            new FixedInstalledPayloadVerifier(paths),
            new SequenceProcessInventory(),
            new RecordingProcessShutdown(succeeds: true),
            processes,
            new FixedRequestIdSource(),
            diagnostic);

    private sealed class FixedBrokerChannel(
        DeploymentBrokerResponse response,
        Action? onCall = null) : IBrokerChannel
    {
        internal int Calls { get; private set; }

        public Task<DeploymentBrokerResponse> ApplyFixedInboxAsync(
            Guid requestId,
            CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal(RequestId, requestId);
            onCall?.Invoke();
            return Task.FromResult(response);
        }
    }

    private sealed class ThrowingBrokerChannel(Exception exception) : IBrokerChannel
    {
        public Task<DeploymentBrokerResponse> ApplyFixedInboxAsync(
            Guid requestId,
            CancellationToken cancellationToken) => Task.FromException<DeploymentBrokerResponse>(
                exception);
    }

    private sealed class FixedIntegritySource(bool elevated) : IClientIntegritySource
    {
        internal int Calls { get; private set; }

        public bool IsElevated()
        {
            Calls++;
            return elevated;
        }
    }

    private sealed class SequenceIntegritySource(params bool[] values) : IClientIntegritySource
    {
        private int next;

        internal int Calls => next;

        public bool IsElevated()
        {
            if (next >= values.Length)
            {
                throw new InvalidOperationException("No synthetic integrity result remains.");
            }

            return values[next++];
        }
    }

    private sealed class FixedSessionSource(bool active) : IClientSessionSource
    {
        public bool IsActiveLocalConsoleSession() => active;
    }

    private sealed class SequenceSessionSource(params bool[] values) : IClientSessionSource
    {
        private int next;

        internal int Calls => next;

        public bool IsActiveLocalConsoleSession()
        {
            if (next >= values.Length)
            {
                throw new InvalidOperationException("No synthetic session result remains.");
            }

            return values[next++];
        }
    }

    private sealed class FixedInstalledPayloadVerifier(InstalledPayloadPaths? paths)
        : IInstalledPayloadVerifier
    {
        public InstalledPayloadPaths? GetExpectedPaths() => Paths;

        public InstalledPayloadPaths? GetVerifiedPaths() => paths;
    }

    private sealed class FixedRequestIdSource : IRequestIdSource
    {
        public Guid Create() => RequestId;
    }

    private sealed class SequenceProcessInventory(params InstalledProcessInventory[] values)
        : IInstalledProcessInventory
    {
        private readonly InstalledProcessInventory[] sequence = values.Length == 0
            ?
            [
                new InstalledProcessInventory(true, 0, 0),
                new InstalledProcessInventory(true, 0, 0),
                new InstalledProcessInventory(true, 0, 0),
                new InstalledProcessInventory(true, 1, 0),
                new InstalledProcessInventory(true, 1, 1),
            ]
            : values;
        private int next;

        internal int Calls => next;

        public InstalledProcessInventory Inspect(InstalledPayloadPaths paths)
        {
            Assert.Equal(Paths, paths);
            if (next >= sequence.Length)
            {
                throw new InvalidOperationException("No synthetic process inventory remains.");
            }

            return sequence[next++];
        }
    }

    private sealed class RecordingProcessShutdown(
        bool succeeds,
        Action<InstalledProcessShape>? onShutdown = null) : IInstalledProcessShutdown
    {
        internal List<InstalledProcessShape> Shapes { get; } = [];

        public Task<bool> ShutdownAsync(
            InstalledPayloadPaths paths,
            InstalledProcessShape shape,
            CancellationToken cancellationToken)
        {
            Assert.Equal(Paths, paths);
            Shapes.Add(shape);
            onShutdown?.Invoke(shape);
            return Task.FromResult(succeeds);
        }
    }

    private sealed class ThrowingProcessShutdown(Exception exception)
        : IInstalledProcessShutdown
    {
        public Task<bool> ShutdownAsync(
            InstalledPayloadPaths paths,
            InstalledProcessShape shape,
            CancellationToken cancellationToken) => Task.FromException<bool>(exception);
    }

    private sealed class RecordingShutdownEventSource(
        List<string> events,
        string? missingEndpoint = null) : IInstalledShutdownEventSource
    {
        public IInstalledShutdownEvent? TryOpen(string eventName)
        {
            events.Add($"open:{eventName}");
            return string.Equals(eventName, missingEndpoint, StringComparison.Ordinal)
                ? null
                : new RecordingShutdownEvent(eventName, events);
        }
    }

    private sealed class RecordingShutdownEvent(string eventName, List<string> events)
        : IInstalledShutdownEvent
    {
        public bool Signal()
        {
            events.Add($"signal:{eventName}");
            return true;
        }

        public void Dispose() => events.Add($"dispose:{eventName}");
    }

    private sealed class RecordingLegacyStartupCleanup(Action? onCleanup = null)
        : ILegacyStartupCleanup
    {
        internal int Calls { get; private set; }

        public void RemoveCurrentUserEntries()
        {
            Calls++;
            onCleanup?.Invoke();
        }
    }

    private sealed class FailingLegacyStartupCleanup : ILegacyStartupCleanup
    {
        public void RemoveCurrentUserEntries() =>
            throw new IOException("Synthetic cleanup failure.");
    }

    private sealed class RecordingProcessLauncher(
        bool sessionRemainsAlive,
        Action<InstalledProcessLaunchPlan>? onLaunch = null)
        : IDeploymentProcessLauncher
    {
        internal List<InstalledProcessLaunchPlan> Plans { get; } = [];

        internal int SessionAliveChecks { get; private set; }

        internal int TrayAliveChecks { get; private set; }

        public ILaunchedDeploymentProcess Launch(InstalledProcessLaunchPlan plan)
        {
            onLaunch?.Invoke(plan);
            Plans.Add(plan);
            bool isSession = string.Equals(
                plan.FileName,
                Paths.SessionAgentExecutable,
                StringComparison.OrdinalIgnoreCase);
            return new RecordingProcess(
                isSession ? sessionRemainsAlive : true,
                isSession ? () => SessionAliveChecks++ : () => TrayAliveChecks++);
        }
    }

    private sealed class RecordingProcess(bool remainsAlive, Action? onCheck)
        : ILaunchedDeploymentProcess
    {
        public Task<bool> RemainsAliveAsync(TimeSpan duration)
        {
            Assert.Equal(TimeSpan.FromSeconds(1), duration);
            onCheck?.Invoke();
            return Task.FromResult(remainsAlive);
        }

        public void Dispose()
        {
        }
    }
}
