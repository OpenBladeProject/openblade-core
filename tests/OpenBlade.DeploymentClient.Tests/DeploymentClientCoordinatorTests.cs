using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;

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
        RecordingProcessLauncher processes = new(sessionRemainsAlive: true);
        DeploymentClientCoordinator coordinator = CreateCoordinator(
            BrokerProtocolTests.Success(),
            elevated: false,
            Paths,
            processes);

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
            diagnostics.Add);

        Assert.False(await coordinator.ApplyFixedInboxAsync(CancellationToken.None));
        Assert.Empty(processes.Plans);
        Assert.Equal(
            [$"broker-response:{(DeploymentBrokerResponseStatus)status}:{code}"],
            diagnostics);
    }

    [Fact]
    public async Task ElevatedCallerOrUnverifiedInstalledPaths_LaunchNothing()
    {
        RecordingProcessLauncher elevatedProcesses = new(sessionRemainsAlive: true);
        FixedBrokerChannel elevatedBroker = new(BrokerProtocolTests.Success());
        FixedIntegritySource elevatedIntegrity = new(elevated: true);
        DeploymentClientCoordinator elevated = new(
            elevatedBroker,
            elevatedIntegrity,
            new FixedSessionSource(active: true),
            new FixedInstalledPayloadVerifier(Paths),
            new SequenceProcessInventory(),
            elevatedProcesses,
            new FixedRequestIdSource());
        Assert.False(await elevated.ApplyFixedInboxAsync(CancellationToken.None));
        Assert.Equal(0, elevatedBroker.Calls);
        Assert.Equal(1, elevatedIntegrity.Calls);
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

    [Fact]
    public async Task IntegrityDriftAfterBrokerSuccess_BlocksAllRelaunches()
    {
        RecordingProcessLauncher processes = new(sessionRemainsAlive: true);
        SequenceIntegritySource integrity = new(false, true);
        DeploymentClientCoordinator coordinator = new(
            new FixedBrokerChannel(BrokerProtocolTests.Success()),
            integrity,
            new FixedSessionSource(active: true),
            new FixedInstalledPayloadVerifier(Paths),
            new SequenceProcessInventory(),
            processes,
            new FixedRequestIdSource());

        Assert.False(await coordinator.ApplyFixedInboxAsync(CancellationToken.None));
        Assert.Equal(2, integrity.Calls);
        Assert.Empty(processes.Plans);
    }

    [Fact]
    public async Task ActiveConsoleSessionDriftAfterBrokerSuccess_BlocksAllRelaunches()
    {
        RecordingProcessLauncher processes = new(sessionRemainsAlive: true);
        SequenceSessionSource session = new(true, false);
        DeploymentClientCoordinator coordinator = new(
            new FixedBrokerChannel(BrokerProtocolTests.Success()),
            new FixedIntegritySource(elevated: false),
            session,
            new FixedInstalledPayloadVerifier(Paths),
            new SequenceProcessInventory(),
            processes,
            new FixedRequestIdSource());

        Assert.False(await coordinator.ApplyFixedInboxAsync(CancellationToken.None));
        Assert.Equal(2, session.Calls);
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

    [Theory]
    [InlineData(false, 0, 0)]
    [InlineData(true, 1, 0)]
    [InlineData(true, 0, 1)]
    [InlineData(true, 1, 1)]
    public async Task NonemptyOrAmbiguousPrelaunchInventory_LaunchesNothing(
        bool unambiguous,
        int sessionAgents,
        int trays)
    {
        RecordingProcessLauncher processes = new(sessionRemainsAlive: true);
        DeploymentClientCoordinator coordinator = new(
            new FixedBrokerChannel(BrokerProtocolTests.Success()),
            new FixedIntegritySource(elevated: false),
            new FixedSessionSource(active: true),
            new FixedInstalledPayloadVerifier(Paths),
            new SequenceProcessInventory(
                new InstalledProcessInventory(unambiguous, sessionAgents, trays)),
            processes,
            new FixedRequestIdSource());

        Assert.False(await coordinator.ApplyFixedInboxAsync(CancellationToken.None));
        Assert.Empty(processes.Plans);
    }

    [Fact]
    public async Task DuplicateOrElevatedFinalInventory_FailsRelaunchResult()
    {
        RecordingProcessLauncher processes = new(sessionRemainsAlive: true);
        DeploymentClientCoordinator coordinator = new(
            new FixedBrokerChannel(BrokerProtocolTests.Success()),
            new FixedIntegritySource(elevated: false),
            new FixedSessionSource(active: true),
            new FixedInstalledPayloadVerifier(Paths),
            new SequenceProcessInventory(
                new InstalledProcessInventory(true, 0, 0),
                new InstalledProcessInventory(true, 1, 0),
                new InstalledProcessInventory(false, 1, 2)),
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
        Action<string>? diagnostic = null) => new(
            new FixedBrokerChannel(response),
            new FixedIntegritySource(elevated),
            new FixedSessionSource(active: true),
            new FixedInstalledPayloadVerifier(paths),
            new SequenceProcessInventory(),
            processes,
            new FixedRequestIdSource(),
            diagnostic);

    private sealed class FixedBrokerChannel(DeploymentBrokerResponse response) : IBrokerChannel
    {
        internal int Calls { get; private set; }

        public Task<DeploymentBrokerResponse> ApplyFixedInboxAsync(
            Guid requestId,
            CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal(RequestId, requestId);
            return Task.FromResult(response);
        }
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
                new InstalledProcessInventory(true, 1, 0),
                new InstalledProcessInventory(true, 1, 1),
            ]
            : values;
        private int next;

        public InstalledProcessInventory Inspect(InstalledPayloadPaths paths)
        {
            Assert.Same(Paths, paths);
            if (next >= sequence.Length)
            {
                throw new InvalidOperationException("No synthetic process inventory remains.");
            }

            return sequence[next++];
        }
    }

    private sealed class RecordingProcessLauncher(bool sessionRemainsAlive)
        : IDeploymentProcessLauncher
    {
        internal List<InstalledProcessLaunchPlan> Plans { get; } = [];

        internal int SessionAliveChecks { get; private set; }

        internal int TrayAliveChecks { get; private set; }

        public ILaunchedDeploymentProcess Launch(InstalledProcessLaunchPlan plan)
        {
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
