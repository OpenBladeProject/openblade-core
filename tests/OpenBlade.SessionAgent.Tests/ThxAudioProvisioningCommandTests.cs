using OpenBlade.Tray;

namespace OpenBlade.SessionAgent.Tests;

public sealed class ThxAudioProvisioningCommandTests
{
    [Fact]
    public void ParserAcceptsOnlyOneExactPrivateArgument()
    {
        Assert.True(ThxAudioProvisioningCommand.IsRequested([ThxAudioProvisioningCommand.Argument]));
        Assert.False(ThxAudioProvisioningCommand.IsRequested([]));
        Assert.False(ThxAudioProvisioningCommand.IsRequested([ThxAudioProvisioningCommand.Argument, "untrusted"]));
        Assert.True(ThxAudioProvisioningCommand.HasCommandSwitch([ThxAudioProvisioningCommand.Argument + "=path"]));
    }

    [Theory]
    [InlineData(true, false, ThxAudioProvisioningResult.AlreadyInstalled)]
    [InlineData(false, false, ThxAudioProvisioningResult.SourceUnavailable)]
    public async Task InitialInspectionStopsBeforeAcquisition(
        bool installed,
        bool ready,
        ThxAudioProvisioningResult expected)
    {
        var platform = new FakePlatform
        {
            Snapshot = new(ready, installed, ThxAudioProvisioningResult.SourceUnavailable),
        };
        Assert.Equal(expected, await new ThxAudioProvisioningCommand(platform).RunAsync(default));
        Assert.False(platform.Acquired);
        // Inspect the durable recovery marker before deciding that a fresh setup
        // is unnecessary; refusal must still prevent acquisition and mutation.
        Assert.True(platform.OpenedJournal);
        Assert.Empty(platform.Journal.Intents);
        Assert.Empty(platform.Executed);
    }

    [Fact]
    public async Task ExecutesFixedOrderAndRequiresFinalReadback()
    {
        var platform = new FakePlatform();
        Assert.Equal(ThxAudioProvisioningResult.Installed,
            await new ThxAudioProvisioningCommand(platform).RunAsync(default));
        Assert.Equal(Enum.GetValues<ThxAudioProvisioningStep>(), platform.Executed);
        Assert.Equal(
            new ThxAudioProvisioningStep?[] { null, null, null, ThxAudioProvisioningStep.InstallDriverStack,
                ThxAudioProvisioningStep.InstallDriverStack, ThxAudioProvisioningStep.InstallProperties,
                ThxAudioProvisioningStep.InstallProperties, ThxAudioProvisioningStep.InstallSurroundConfiguration,
            },
            platform.Captures);
        Assert.Equal(3, platform.Journal.Intents.Count);
        Assert.True(platform.Journal.Completed);
    }

    [Fact]
    public async Task ZeroExitWithoutFinalAdmissionRequiresRecovery()
    {
        var platform = new FakePlatform { FailFinalReadback = true };
        Assert.Equal(ThxAudioProvisioningResult.RecoveryRequired,
            await new ThxAudioProvisioningCommand(platform).RunAsync(default));
        Assert.False(platform.Journal.Completed);
    }

    [Theory]
    [InlineData(3010, ThxAudioProvisioningResult.RestartRequired)]
    [InlineData(5, ThxAudioProvisioningResult.RecoveryRequired)]
    public async Task NonzeroExitStopsWithoutRetry(int exitCode, ThxAudioProvisioningResult expected)
    {
        var platform = new FakePlatform { ExitCode = exitCode };
        Assert.Equal(expected, await new ThxAudioProvisioningCommand(platform).RunAsync(default));
        Assert.Single(platform.Executed);
        Assert.False(platform.Journal.Completed);
    }

    [Fact]
    public async Task UnfinishedTransactionNeverReplays()
    {
        var platform = new FakePlatform { Pending = true };
        Assert.Equal(ThxAudioProvisioningResult.RecoveryRequired,
            await new ThxAudioProvisioningCommand(platform).RunAsync(default));
        Assert.False(platform.Acquired);
        Assert.Empty(platform.Executed);
    }

    [Fact]
    public async Task KnownRestartOnSameBootStillRequiresRestart()
    {
        var platform = new FakePlatform
        {
            Pending = true,
            BootIdentity = "boot-a",
            Recovery = Recovery(ThxAudioProvisioningStep.InstallDriverStack, "boot-a"),
        };
        Assert.Equal(ThxAudioProvisioningResult.RestartRequired,
            await new ThxAudioProvisioningCommand(platform).RunAsync(default));
        Assert.Empty(platform.Executed);
    }

    [Fact]
    public async Task KnownRestartAfterNewBootContinuesWithoutReplayingSubmittedStep()
    {
        var platform = new FakePlatform
        {
            Pending = true,
            BootIdentity = "boot-b",
            Recovery = Recovery(ThxAudioProvisioningStep.InstallDriverStack, "boot-a"),
        };
        Assert.Equal(ThxAudioProvisioningResult.Installed,
            await new ThxAudioProvisioningCommand(platform).RunAsync(default));
        Assert.Equal(new[]
        {
            ThxAudioProvisioningStep.InstallProperties,
            ThxAudioProvisioningStep.InstallSurroundConfiguration,
        }, platform.Executed);
    }

    [Fact]
    public async Task FinalStepRestartCompletesFromReadOnlyAdmissionAfterNewBoot()
    {
        var platform = new FakePlatform
        {
            Pending = true,
            BootIdentity = "boot-b",
            Recovery = Recovery(ThxAudioProvisioningStep.InstallSurroundConfiguration, "boot-a",
                ThxAudioProvisioningStep.InstallProperties),
        };
        Assert.Equal(ThxAudioProvisioningResult.Installed,
            await new ThxAudioProvisioningCommand(platform).RunAsync(default));
        Assert.Empty(platform.Executed);
        Assert.True(platform.Journal.Completed);
    }

    [Fact]
    public async Task IndependentlyCompletedStackStopsBeforeNextVendorProcess()
    {
        var platform = new FakePlatform { CompleteBeforeSecondStep = true };
        Assert.Equal(ThxAudioProvisioningResult.Installed,
            await new ThxAudioProvisioningCommand(platform).RunAsync(default));
        Assert.Equal(new[] { ThxAudioProvisioningStep.InstallDriverStack }, platform.Executed);
        Assert.True(platform.Journal.Completed);
    }

    [Fact]
    public async Task CancellationAfterSubmissionIsRecoveryRequired()
    {
        var platform = new FakePlatform { CancelDuringExecute = true };
        Assert.Equal(ThxAudioProvisioningResult.RecoveryRequired,
            await new ThxAudioProvisioningCommand(platform).RunAsync(default));
        Assert.Single(platform.Executed);
    }

    private sealed class FakePlatform : IThxAudioProvisioningPlatform
    {
        internal ThxAudioProvisioningSnapshot Snapshot = new(true, false);
        internal readonly FakeJournal Journal = new();
        internal readonly List<ThxAudioProvisioningStep> Executed = [];
        internal readonly List<ThxAudioProvisioningStep?> Captures = [];
        internal bool Pending, Acquired, OpenedJournal, FailFinalReadback, CancelDuringExecute,
            CompleteBeforeSecondStep;
        internal int ExitCode;
        internal string BootIdentity = "boot-a";
        internal ThxAudioProvisioningRecovery? Recovery;
        private int baseCaptures;

        public ThxAudioProvisioningSnapshot Capture(ThxAudioProvisioningStep? completedStep = null)
        {
            Captures.Add(completedStep);
            if (CompleteBeforeSecondStep && completedStep == ThxAudioProvisioningStep.InstallDriverStack
                && ++baseCaptures == 2)
            {
                return InstalledSnapshot();
            }
            if (FailFinalReadback && completedStep == ThxAudioProvisioningStep.InstallSurroundConfiguration)
            {
                return new(true, false);
            }
            return completedStep == ThxAudioProvisioningStep.InstallSurroundConfiguration
                ? InstalledSnapshot() : Snapshot with
                {
                    Profile = "RZ09-0528/rznb11",
                    ProvisioningProcessesAbsent = true,
                };
        }
        public string ReadBootIdentity() => BootIdentity;
        public IThxAudioProvisioningJournal OpenJournal()
        {
            OpenedJournal = true;
            Journal.RequiresRecovery = Pending;
            Journal.Recovery = Recovery;
            return Journal;
        }
        public Task<string> AcquireVerifiedAsync(string destination, CancellationToken cancellationToken)
        {
            Acquired = true;
            return Task.FromResult(destination);
        }
        public Task<ThxAudioProvisioningProcessResult> ExecuteAsync(
            string root, ThxAudioProvisioningStep step, CancellationToken cancellationToken)
        {
            Executed.Add(step);
            if (CancelDuringExecute)
            {
                throw new OperationCanceledException();
            }
            return Task.FromResult(new ThxAudioProvisioningProcessResult(ExitCode, string.Empty));
        }

        private static ThxAudioProvisioningSnapshot InstalledSnapshot() => new(true, true)
        {
            Profile = "RZ09-0528/rznb11",
            ProvisioningProcessesAbsent = true,
        };
    }

    private sealed class FakeJournal : IThxAudioProvisioningJournal
    {
        internal readonly List<ThxAudioProvisioningStep> Intents = [];
        internal bool Completed;
        public bool RequiresRecovery { get; set; }
        public ThxAudioProvisioningRecovery? Recovery { get; set; }
        public string PackageDestination => "reviewed";
        public void Begin(ThxAudioProvisioningSnapshot baseline, string bootIdentity) { }
        public void Intent(ThxAudioProvisioningStep step) { Intents.Add(step); RequiresRecovery = true; }
        public void RecordResult(ThxAudioProvisioningProcessResult result, string bootIdentity) { }
        public void Observe(ThxAudioProvisioningStep step, ThxAudioProvisioningSnapshot snapshot) { }
        public void Complete() { Completed = true; RequiresRecovery = false; }
        public void Dispose() { }
    }

    private static ThxAudioProvisioningRecovery Recovery(ThxAudioProvisioningStep step,
        string resultBoot, ThxAudioProvisioningStep? completed = null) =>
        new("reviewed", "RZ09-0528/rznb11", resultBoot, step, completed);
}
