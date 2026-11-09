using OpenBlade.Contracts;
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
        Assert.Equal(ThxAudioProvisioningSequence.Steps, platform.Executed);
        Assert.Equal(
            new ThxAudioProvisioningStep?[] { null, null, null,
                ThxAudioProvisioningStep.InstallBaseAudio, ThxAudioProvisioningStep.InstallBaseAudio,
                ThxAudioProvisioningStep.InstallDriverStack,
                ThxAudioProvisioningStep.InstallDriverStack, ThxAudioProvisioningStep.InstallProperties,
                ThxAudioProvisioningStep.InstallProperties, ThxAudioProvisioningStep.InstallSurroundConfiguration,
            },
            platform.Captures);
        Assert.Equal(4, platform.Journal.Intents.Count);
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
        Assert.Equal(new[] { ThxAudioProvisioningStep.InstallBaseAudio,
            ThxAudioProvisioningStep.InstallDriverStack }, platform.Executed);
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

    [Fact]
    public async Task MissingBaseReadbackStopsBeforeThxInstaller()
    {
        var platform = new FakePlatform { FailBaseReadback = true };
        Assert.Equal(ThxAudioProvisioningResult.RecoveryRequired,
            await new ThxAudioProvisioningCommand(platform).RunAsync(default));
        Assert.Equal([ThxAudioProvisioningStep.InstallBaseAudio], platform.Executed);
        Assert.False(platform.Journal.Completed);
    }

    [Fact]
    public async Task ChangedBaseTargetAfterSubmissionStopsBeforeThxInstaller()
    {
        var platform = new FakePlatform { ChangeBaseTarget = true };
        Assert.Equal(ThxAudioProvisioningResult.RecoveryRequired,
            await new ThxAudioProvisioningCommand(platform).RunAsync(default));
        Assert.Equal([ThxAudioProvisioningStep.InstallBaseAudio], platform.Executed);
        Assert.False(platform.Journal.Completed);
    }

    [Fact]
    public async Task VerifiedBaseRestartContinuesWithThxWithoutReplayingBase()
    {
        var platform = new FakePlatform
        {
            Pending = true,
            BootIdentity = "boot-b",
            Recovery = Recovery(ThxAudioProvisioningStep.InstallBaseAudio, "boot-a"),
        };
        Assert.Equal(ThxAudioProvisioningResult.Installed,
            await new ThxAudioProvisioningCommand(platform).RunAsync(default));
        Assert.Equal(new[]
        {
            ThxAudioProvisioningStep.InstallDriverStack,
            ThxAudioProvisioningStep.InstallProperties,
            ThxAudioProvisioningStep.InstallSurroundConfiguration,
        }, platform.Executed);
    }

    [Fact]
    public async Task DifferentRecipeOrBaseTargetCannotResumeAnOldTransaction()
    {
        foreach (ThxAudioProvisioningRecovery recovery in new[]
        {
            Recovery(ThxAudioProvisioningStep.InstallBaseAudio, "boot-a") with { Profile = "old-recipe" },
            Recovery(ThxAudioProvisioningStep.InstallBaseAudio, "boot-a") with { BaseAudioTarget = "different-root" },
        })
        {
            var platform = new FakePlatform { Pending = true, BootIdentity = "boot-b", Recovery = recovery };
            Assert.Equal(ThxAudioProvisioningResult.RecoveryRequired,
                await new ThxAudioProvisioningCommand(platform).RunAsync(default));
            Assert.Empty(platform.Executed);
            Assert.False(platform.Acquired);
        }
    }

    [Theory]
    [InlineData(false, ThxAudioProvisioningResult.Failed)]
    [InlineData(true, ThxAudioProvisioningResult.RecoveryRequired)]
    public async Task InvalidPackageStopsWithResultAppropriateToSubmission(bool afterIntent, ThxAudioProvisioningResult expected)
    {
        var platform = new FakePlatform { InvalidPackageAfterIntent = afterIntent, InvalidPackageBeforeIntent = !afterIntent };
        Assert.Equal(expected, await new ThxAudioProvisioningCommand(platform).RunAsync(default));
        Assert.Equal(afterIntent ? 1 : 0, platform.Executed.Count);
        Assert.Equal(afterIntent ? 1 : 0, platform.Journal.Intents.Count);
        Assert.False(platform.Journal.Completed);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCanceledAcquisitionRemovesOnlyTheCurrentAttempt(bool canceled)
    {
        using var caller = new CancellationTokenSource();
        string parent = Path.Combine(Path.GetTempPath(), "OpenBlade-ThxCleanup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        try
        {
            using var stream = new IntentFailureStream(Path.Combine(parent, "installation.jsonl"));
            using var journal = IsolatedJournal(parent, stream);
            var platform = new FakePlatform
            {
                JournalOverride = journal,
                InvalidPackageBeforeIntent = !canceled,
                CancelDuringAcquisition = canceled,
                AcquisitionStarted = () => { if (canceled) { caller.Cancel(); } },
            };
            Assert.Equal(canceled ? ThxAudioProvisioningResult.Canceled : ThxAudioProvisioningResult.Failed,
                await new ThxAudioProvisioningCommand(platform).RunAsync(caller.Token));
            Assert.False(Directory.Exists(journal.PackageDestination));
            Assert.True(File.Exists(Path.Combine(parent, "installation.jsonl")));
            Assert.Empty(platform.Executed);
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [Fact]
    public async Task AcquiredPackageIsRemovedAfterFreshAdmissionRefusesBeforeIntent()
    {
        string parent = Path.Combine(Path.GetTempPath(), "OpenBlade-ThxCleanup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        try
        {
            using var stream = new IntentFailureStream(Path.Combine(parent, "installation.jsonl"));
            using var journal = IsolatedJournal(parent, stream);
            var platform = new FakePlatform { JournalOverride = journal, RefuseAfterAcquisition = true };
            Assert.Equal(ThxAudioProvisioningResult.AcPowerRequired,
                await new ThxAudioProvisioningCommand(platform).RunAsync(default));
            Assert.False(Directory.Exists(journal.PackageDestination));
            Assert.Empty(platform.Executed);
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [Fact]
    public void IntentFlushFailureRetainsPackageBeforeRecoveryFlagCanBeUpdated()
    {
        string parent = Path.Combine(Path.GetTempPath(), "OpenBlade-ThxCleanup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        try
        {
            string destination;
            using (var stream = new IntentFailureStream(Path.Combine(parent, "installation.jsonl")))
            using (var journal = IsolatedJournal(parent, stream))
            {
                journal.BeginPackageAcquisition();
                destination = journal.PackageDestination;
                File.WriteAllText(Path.Combine(destination, "recovery.bin"), "keep");
                journal.Begin(new(true, false) { Profile = "RZ09-0528/rznb11", BaseAudioTarget = "root-a" }, "boot-a");
                stream.FailFlush = true;
                Assert.Throws<IOException>(() => journal.Intent(ThxAudioProvisioningStep.InstallBaseAudio));
                Assert.False(journal.RequiresRecovery);
            }
            Assert.Equal("keep", File.ReadAllText(Path.Combine(destination, "recovery.bin")));
            Assert.Contains("\"kind\":\"intent\"", File.ReadAllText(Path.Combine(parent, "installation.jsonl")));
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [Fact]
    public async Task RestartRecoveryRetainsTheOriginalPackageWithoutAcquisition()
    {
        string parent = Path.Combine(Path.GetTempPath(), "OpenBlade-ThxCleanup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        try
        {
            string path = Path.Combine(parent, "installation.jsonl");
            string destination;
            using (var stream = new IntentFailureStream(path))
            using (var journal = IsolatedJournal(parent, stream))
            {
                journal.BeginPackageAcquisition();
                destination = journal.PackageDestination;
                File.WriteAllText(Path.Combine(destination, "recovery.bin"), "keep");
                journal.Begin(new(true, false) { Profile = "RZ09-0528/rznb11", BaseAudioTarget = "root-a" }, "boot-a");
                journal.Intent(ThxAudioProvisioningStep.InstallBaseAudio);
                journal.RecordResult(new((int)ThxAudioProvisioningResult.RestartRequired, "restart"), "boot-a");
            }
            using var recoveryStream = new IntentFailureStream(path);
            using var recovery = IsolatedJournal(parent, recoveryStream);
            Assert.True(recovery.RequiresRecovery);
            Assert.Equal(destination, recovery.PackageDestination);
            Assert.Throws<InvalidOperationException>(recovery.BeginPackageAcquisition);
            var platform = new FakePlatform { JournalOverride = recovery };
            Assert.Equal(ThxAudioProvisioningResult.RestartRequired,
                await new ThxAudioProvisioningCommand(platform).RunAsync(default));
            Assert.False(platform.Acquired);
            Assert.Empty(platform.Executed);
            Assert.Equal("keep", File.ReadAllText(Path.Combine(destination, "recovery.bin")));
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    private static ThxAudioProvisioningJournal IsolatedJournal(string parent, FileStream stream) =>
        new(ThxAudioProductIds.Rznb11, "RZ09-0528", parent, stream,
            path => Directory.CreateDirectory(path), path =>
            {
                if (!Directory.Exists(path)) { throw new IOException("Missing isolated directory."); }
            });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrUntrustedRecoveryPackageStillRequiresRecovery(bool refuseProtection)
    {
        string parent = Path.Combine(Path.GetTempPath(), "OpenBlade-ThxRecovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        try
        {
            string path = Path.Combine(parent, "installation.jsonl");
            string destination;
            using (var stream = new IntentFailureStream(path))
            using (var journal = IsolatedJournal(parent, stream))
            {
                journal.BeginPackageAcquisition();
                destination = journal.PackageDestination;
                journal.Begin(new(true, false) { Profile = "RZ09-0528/rznb11", BaseAudioTarget = "root-a" }, "boot-a");
                journal.Intent(ThxAudioProvisioningStep.InstallBaseAudio);
                journal.RecordResult(new((int)ThxAudioProvisioningResult.RestartRequired, "restart"), "boot-a");
            }
            byte[] durableJournal = File.ReadAllBytes(path);
            if (!refuseProtection) { Directory.Delete(destination, recursive: true); }
            var platform = new FakePlatform
            {
                OpenJournalOverride = () => new ThxAudioProvisioningJournal(
                    ThxAudioProductIds.Rznb11, "RZ09-0528", parent, new IntentFailureStream(path),
                    directory => Directory.CreateDirectory(directory), directory =>
                    {
                        if (refuseProtection) { throw new UnauthorizedAccessException("Protection changed."); }
                        if (!Directory.Exists(directory)) { throw new DirectoryNotFoundException(); }
                    }),
            };

            Assert.Equal(ThxAudioProvisioningResult.RecoveryRequired,
                await new ThxAudioProvisioningCommand(platform).RunAsync(default));

            Assert.Equal(durableJournal, File.ReadAllBytes(path));
            Assert.False(platform.Acquired);
            Assert.Empty(platform.Executed);
            Assert.Equal(refuseProtection, Directory.Exists(destination));
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    private sealed class IntentFailureStream(string path)
        : FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
    {
        internal bool FailFlush { get; set; }

        public override void Flush(bool flushToDisk)
        {
            if (FailFlush) { throw new IOException("Simulated durable intent flush failure."); }
            base.Flush(flushToDisk);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task UnconfirmedExtractionExitRetainsPackageWithoutClaimingHardwareSubmission(
        bool canceled,
        bool stopTimesOut)
    {
        using var caller = new CancellationTokenSource();
        string parent = Path.Combine(Path.GetTempPath(), "OpenBlade-ThxExtraction-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        try
        {
            string path = Path.Combine(parent, "installation.jsonl");
            using var stream = new IntentFailureStream(path);
            using var journal = IsolatedJournal(parent, stream);
            var platform = new FakePlatform
            {
                JournalOverride = journal,
                ExtractionFailure = canceled ? new OperationCanceledException("cancel") : new IOException("extract"),
                StopTimesOut = stopTimesOut,
                AcquisitionStarted = () => { if (canceled) { caller.Cancel(); } },
            };

            Assert.Equal(canceled ? ThxAudioProvisioningResult.Canceled : ThxAudioProvisioningResult.Failed,
                await new ThxAudioProvisioningCommand(platform).RunAsync(caller.Token));

            Assert.True(File.Exists(Path.Combine(journal.PackageDestination, "partial.bin")));
            Assert.False(journal.RequiresRecovery);
            Assert.Equal(0, new FileInfo(path).Length);
            Assert.Empty(platform.Executed);
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [Fact]
    public async Task RestartUsesTheJournalAdmittedDestination()
    {
        var platform = new FakePlatform
        {
            Pending = true,
            BootIdentity = "boot-b",
            Recovery = Recovery(ThxAudioProvisioningStep.InstallDriverStack, "boot-a") with
            {
                PackageDestination = "raw/../reviewed",
            },
        };
        platform.Journal.AdmittedDestination = Path.GetFullPath("reviewed");

        Assert.Equal(ThxAudioProvisioningResult.Installed,
            await new ThxAudioProvisioningCommand(platform).RunAsync(default));

        Assert.NotEmpty(platform.ExecutionRoots);
        Assert.All(platform.ExecutionRoots, root => Assert.Equal(platform.Journal.PackageDestination, root));
        Assert.False(platform.Acquired);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AcquisitionCancellationRequiresCallerCancellation(bool callerCanceled, bool extractorUnconfirmed)
    {
        using var caller = new CancellationTokenSource();
        var platform = new FakePlatform
        {
            AcquisitionStarted = () => { if (callerCanceled) { caller.Cancel(); } },
            CancelDuringAcquisition = !extractorUnconfirmed,
            ExtractionFailure = extractorUnconfirmed ? new OperationCanceledException("acquisition timeout") : null,
        };

        Assert.Equal(callerCanceled ? ThxAudioProvisioningResult.Canceled : ThxAudioProvisioningResult.Failed,
            await new ThxAudioProvisioningCommand(platform).RunAsync(caller.Token));

        Assert.Empty(platform.Executed);
        Assert.False(platform.Journal.RequiresRecovery);
    }

    private sealed class FakePlatform : IThxAudioProvisioningPlatform
    {
        internal ThxAudioProvisioningSnapshot Snapshot = new(true, false);
        internal readonly FakeJournal Journal = new();
        internal readonly List<ThxAudioProvisioningStep> Executed = [];
        internal readonly List<ThxAudioProvisioningStep?> Captures = [];
        internal bool Pending, Acquired, OpenedJournal, FailFinalReadback, CancelDuringExecute,
            CompleteBeforeSecondStep, FailBaseReadback, ChangeBaseTarget, InvalidPackageBeforeIntent, InvalidPackageAfterIntent;
        internal int ExitCode;
        internal string BootIdentity = "boot-a";
        internal ThxAudioProvisioningRecovery? Recovery;
        internal IThxAudioProvisioningJournal? JournalOverride;
        internal Func<IThxAudioProvisioningJournal>? OpenJournalOverride;
        internal Exception? ExtractionFailure;
        internal bool StopTimesOut;
        internal Action? AcquisitionStarted;
        internal readonly List<string> ExecutionRoots = [];
        internal bool CancelDuringAcquisition, RefuseAfterAcquisition;
        private int baseCaptures;

        public ThxAudioProvisioningSnapshot Capture(ThxAudioProvisioningStep? completedStep = null)
        {
            Captures.Add(completedStep);
            if (RefuseAfterAcquisition && Acquired)
            {
                return new(false, false, ThxAudioProvisioningResult.AcPowerRequired);
            }
            if (completedStep == ThxAudioProvisioningStep.InstallBaseAudio && FailBaseReadback)
            {
                return new(false, false, ThxAudioProvisioningResult.BaseAudioUnverified)
                {
                    BaseAudioTarget = "root-a",
                };
            }
            if (completedStep == ThxAudioProvisioningStep.InstallBaseAudio && ChangeBaseTarget)
            {
                return new(true, false) { BaseAudioTarget = "root-b" };
            }
            if (CompleteBeforeSecondStep && completedStep == ThxAudioProvisioningStep.InstallDriverStack
                && ++baseCaptures == 2)
            {
                return InstalledSnapshot();
            }
            if (FailFinalReadback && completedStep == ThxAudioProvisioningStep.InstallSurroundConfiguration)
            {
                return new(true, false) { BaseAudioTarget = "root-a" };
            }
            return completedStep == ThxAudioProvisioningStep.InstallSurroundConfiguration
                ? InstalledSnapshot() : Snapshot with
                {
                    Profile = "RZ09-0528/rznb11",
                    ProvisioningProcessesAbsent = true,
                    BaseAudioTarget = "root-a",
                };
        }
        public string ReadBootIdentity() => BootIdentity;
        public IThxAudioProvisioningJournal OpenJournal()
        {
            OpenedJournal = true;
            if (OpenJournalOverride is not null) { return OpenJournalOverride(); }
            if (JournalOverride is not null) { return JournalOverride; }
            Journal.RequiresRecovery = Pending;
            Journal.Recovery = Recovery;
            return Journal;
        }
        public Task<string> AcquireVerifiedAsync(string destination, CancellationToken cancellationToken)
        {
            AcquisitionStarted?.Invoke();
            if (JournalOverride is not null)
            {
                Assert.True(Directory.Exists(destination));
                File.WriteAllText(Path.Combine(destination, "partial.bin"), "partial package");
            }
            if (CancelDuringAcquisition) { throw new OperationCanceledException(); }
            if (ExtractionFailure is not null) { return UnconfirmedExtractionAsync(destination); }
            if (InvalidPackageBeforeIntent)
            {
                throw new InvalidDataException("Changed downloaded package.");
            }
            Acquired = true;
            return Task.FromResult(destination);
        }

        private async Task<string> UnconfirmedExtractionAsync(string destination)
        {
            await ThxBaseAudioPackage.StopExtractionAsync(() => false,
                () =>
                {
                    if (!StopTimesOut) { throw new System.ComponentModel.Win32Exception(5, "stop failed"); }
                }, token => Task.FromException(new OperationCanceledException("exit wait expired")), ExtractionFailure!);
            return destination;
        }
        public Task<ThxAudioProvisioningProcessResult> ExecuteAsync(
            string root, ThxAudioProvisioningStep step, ThxAudioProvisioningSnapshot expected,
            CancellationToken cancellationToken)
        {
            ExecutionRoots.Add(root);
            Executed.Add(step);
            if (InvalidPackageAfterIntent)
            {
                throw new InvalidDataException("Changed execution package.");
            }
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
            BaseAudioTarget = "root-a",
        };
    }

    private sealed class FakeJournal : IThxAudioProvisioningJournal
    {
        internal readonly List<ThxAudioProvisioningStep> Intents = [];
        internal bool Completed;
        public bool RequiresRecovery { get; set; }
        public ThxAudioProvisioningRecovery? Recovery { get; set; }
        internal string AdmittedDestination = "reviewed";
        public string PackageDestination => AdmittedDestination;
        public void BeginPackageAcquisition() { }
        public void RetainPackage() { }
        public void Begin(ThxAudioProvisioningSnapshot baseline, string bootIdentity) { }
        public void Intent(ThxAudioProvisioningStep step) { Intents.Add(step); RequiresRecovery = true; }
        public void RecordResult(ThxAudioProvisioningProcessResult result, string bootIdentity) { }
        public void Observe(ThxAudioProvisioningStep step, ThxAudioProvisioningSnapshot snapshot) { }
        public void Complete() { Completed = true; RequiresRecovery = false; }
        public void Dispose() { }
    }

    private static ThxAudioProvisioningRecovery Recovery(ThxAudioProvisioningStep step,
        string resultBoot, ThxAudioProvisioningStep? completed = null) =>
        new("reviewed", "RZ09-0528/rznb11", resultBoot, step, completed) { BaseAudioTarget = "root-a" };
}
