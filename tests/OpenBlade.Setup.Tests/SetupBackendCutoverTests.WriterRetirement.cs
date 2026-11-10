using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using OpenBlade.Setup;
using static OpenBlade.Setup.Tests.ScratchWriterRetirementGate;
using static OpenBlade.Setup.Tests.SetupPayloadTransactionTests;

namespace OpenBlade.Setup.Tests;

public sealed partial class SetupBackendCutoverTests
{
    public enum RetirementFault
    {
        None,
        MissingReceipt,
        StaleReceipt,
        TornReceipt,
        TornIntent,
        StaleBoot,
        StaleBootReceipt,
        StaleBuild,
        StaleImage,
        StaleCreation,
        ProcessIndeterminate,
        ProcessFailed,
        ObserverLost,
        StopTimeout,
        ProcessStillRunning,
        GuardianPending,
        OwnershipRecord,
        RecoveryFence,
        PartialConfiguration,
    }

    public static TheoryData<bool, RetirementFault> RetirementCases
    {
        get
        {
            var cases = new TheoryData<bool, RetirementFault>();
            foreach (RetirementFault fault in Enum.GetValues<RetirementFault>())
            {
                cases.Add(false, fault);
                cases.Add(true, fault);
            }
            return cases;
        }
    }

    [LocalWriterRetirementTheory]
    [MemberData(nameof(RetirementCases))]
    public async Task BundleUpgrade_FencesTheIncomingWriterUntilRetirementIsProved(
        bool rollback, RetirementFault fault)
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        Assert.False(new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator));
        using var tree = new TestTree();
        using var synthetic = new TestTree();
        synthetic.CreateCompletePayload();
        var pinned = ReadManifest(synthetic.Payload);
        var fixture = CreateSignedFixture(SignedFixtureFault.None, synthetic.Payload);
        var candidate = await StageSignedFixtureAsync(fixture, tree.Payload, pinned[ServiceFile], pinned[AgentFile]);
        Assert.Equal(pinned, candidate);
        foreach (string file in candidate.Keys)
        {
            tree.WriteInstall(file, Encoding.UTF8.GetBytes($"prior managed {file}"));
        }
        tree.WriteInstall("OpenBlade.Setup.exe", "prior uninstaller"u8.ToArray());
        var prior = ReadManifest(tree.Install);

        SetupPaths paths = tree.Paths;
        Directory.CreateDirectory(paths.CommonStartupRoot);
        WriteFile(Path.Combine(paths.MachineDataDirectory, "profiles.json"), "saved profiles"u8.ToArray());
        var windows = new OfflineWindows(paths);
        var guardian = new SetupGuardianQuiescenceGuard(paths, windows, File.GetAttributes);
        using var writers = new ScratchWriters(tree.Root, paths, windows, guardian, prior[ServiceFile]);
        AssertRestartBlocked();
        using var progress = new RecordingSetupProgressReporter();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        const string rejectedCandidate = "Candidate readiness failed.";
        string? expectedBlock = fault switch
        {
            RetirementFault.None => null,
            RetirementFault.StopTimeout => "Writer stop was not confirmed.",
            RetirementFault.ProcessStillRunning or RetirementFault.ProcessIndeterminate or RetirementFault.ProcessFailed =>
                "Outgoing writer process exit was not confirmed.",
            RetirementFault.ObserverLost => "Scratch retirement evidence requires the original observer.",
            RetirementFault.StaleBoot => "Scratch retirement boot identity changed.",
            RetirementFault.GuardianPending => "Fan-stop guardian ownership is pending.",
            RetirementFault.OwnershipRecord => "Scratch writer ownership still needs reconciliation.",
            RetirementFault.RecoveryFence or RetirementFault.PartialConfiguration =>
                "Scratch battery configuration still needs reconciliation.",
            _ => "Retirement evidence is missing, incomplete, or stale.",
        };

        var acquisitions = new List<Backend> { Backend.Managed };
        windows.BeforeStopAsync = () => writers.StopAsync(
            (writers.Current == Backend.Candidate) == rollback ? fault : RetirementFault.None, deadline.Token);
        windows.BeforeWaitForStop = writers.ObserveStop;
        windows.BeforeStart = () =>
        {
            Backend incoming = acquisitions.Count == 1 ? Backend.Candidate : Backend.Managed;
            Assert.Equal(incoming == Backend.Candidate ? candidate : prior, ReadManifest(tree.Install));
            writers.Acquire(incoming, (incoming == Backend.Candidate ? candidate : prior)[ServiceFile]);
            acquisitions.Add(incoming);
        };
        var health = new RehearsalHealthProbe((_, _) =>
        {
            if (rollback && writers.Current == Backend.Candidate)
            {
                throw new InvalidDataException(rejectedCandidate);
            }
            return Task.CompletedTask;
        });

        WindowsSetupPlatform platform = WindowsSetupPlatformTestFactory.Create(
            serviceControl: windows, shortcutWriter: windows, healthProbe: health, processOperations: windows,
            paths: paths, payloadDirectory: tree.Payload, uninstallRegistration: windows,
            bundleOwnershipRegistration: windows, installedUninstallerWriter: windows,
            dynamicLightingIdentityRegistration: windows, progressReporter: progress,
            currentUserSid: "S-1-5-21-1-2-3-1001", pawnIoPrerequisite: windows,
            guardianQuiescenceGuard: writers);
        var runner = new SetupCommandRunner(platform, progressReporter: progress);
        string[] command = ["install", "--bundle-managed", "--bundle-owner-id", "01234567-89ab-cdef-0123-456789abcdef"];

        if (expectedBlock is not null)
        {
            var failure = await Assert.ThrowsAsync<AggregateException>(() => runner.RunAsync(command, deadline.Token));
            Assert.Contains(failure.Flatten().InnerExceptions,
                error => error.Message.StartsWith(expectedBlock, StringComparison.Ordinal));
            if (fault == RetirementFault.StopTimeout)
            {
                Assert.Contains(failure.Flatten().InnerExceptions, error => error is TimeoutException);
            }
            if (rollback)
            {
                Assert.Contains(failure.Flatten().InnerExceptions, error => error.Message == rejectedCandidate);
                string backup = Assert.Single(Directory.GetDirectories(tree.ProgramFiles, "OpenBlade.rollback.*"));
                Assert.Equal(prior, ReadManifest(backup));
                Assert.Equal(candidate, ReadManifest(tree.Install));
                Assert.Empty(Directory.GetDirectories(tree.ProgramFiles, "OpenBlade.failed.*"));
                Assert.Equal(new[] { Backend.Managed, Backend.Candidate }, acquisitions);
            }
            else
            {
                Assert.Equal(prior, ReadManifest(tree.Install));
                Assert.Empty(Directory.GetDirectories(tree.ProgramFiles, "OpenBlade.rollback.*"));
                Assert.Equal(candidate, ReadManifest(
                    Assert.Single(Directory.GetDirectories(tree.ProgramFiles, "OpenBlade.installing.*"))));
                Assert.Equal(new[] { Backend.Managed }, acquisitions);
            }
        }
        else if (rollback)
        {
            var failure = await Assert.ThrowsAsync<InvalidDataException>(() => runner.RunAsync(command, deadline.Token));
            Assert.Equal(rejectedCandidate, failure.Message);
            Assert.Equal(prior, ReadManifest(tree.Install));
            Assert.Equal(new[] { Backend.Managed, Backend.Candidate, Backend.Managed }, acquisitions);
            Assert.Equal(tree.Install, Assert.Single(Directory.GetDirectories(tree.ProgramFiles)));
        }
        else
        {
            Assert.Equal(0, await runner.RunAsync(command, deadline.Token));
            Assert.Equal(candidate, ReadManifest(tree.Install));
            Assert.Equal(new[] { Backend.Managed, Backend.Candidate }, acquisitions);
            Assert.Equal(tree.Install, Assert.Single(Directory.GetDirectories(tree.ProgramFiles)));
        }

        bool committed = !rollback && fault == RetirementFault.None;
        Assert.Equal(rollback ? 2 : 1, writers.StopRequests);
        Assert.Equal(committed ? OfflineWindows.CandidateOwner : OfflineWindows.PriorOwner, windows.Ownership);
        Assert.Equal(committed, progress.Events.Any(entry => entry.Code == SetupProgressCode.CommitFiles));
        Assert.Equal(candidate, ReadManifest(tree.Payload));
        AssertRestartBlocked();
        writers.AssertRecoveryPreserved();

        void AssertRestartBlocked()
        {
            var failure = Assert.Throws<InvalidOperationException>(() =>
            {
                using var restarted = new ScratchWriters(tree.Root, paths, windows, guardian, prior[ServiceFile]);
            });
            Assert.Equal("Scratch retirement evidence requires the original observer.", failure.Message);
        }
    }

    private sealed class ScratchWriters : ISetupGuardianQuiescenceGuard, IDisposable
    {
        private const string BatteryPreferences = "battery-preferences.json";
        private const string BatteryCommit = "battery-configuration.commit.json";
        private const string BatteryFence = BatteryCommit + ".recovery-required";
        private readonly string root;
        private readonly SetupPaths paths;
        private readonly OfflineWindows scm;
        private readonly ISetupGuardianQuiescenceGuard guardian;
        private readonly ScratchWriterRetirementGate gate;
        private readonly string pending;
        private readonly string ownership;
        private readonly string imagePath = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        private readonly FileStream image;
        private readonly string imageSha256;
        private Process process;
        private Generation generation;
        private Observer? observer;
        private bool retirementStarted;
        private RetirementFault fault;
        private bool stopConfirmed;
        private SortedDictionary<string, string>? recoveryAfterStop;

        public ScratchWriters(string root, SetupPaths paths, OfflineWindows scm,
            ISetupGuardianQuiescenceGuard guardian, string priorBuildSha256)
        {
            this.root = root;
            this.paths = paths;
            this.scm = scm;
            this.guardian = guardian;
            pending = Path.Combine(paths.MachineDataDirectory, "FanStopGuardian", "pending.json");
            ownership = Path.Combine(paths.MachineDataDirectory, "hyperboost-ownership.json");
            gate = new(paths.MachineDataDirectory);
            gate.ClaimFreshObserver();
            // Seal the trusted inert image before launch. The installed service
            // artifact is separate test data and is never executed.
            image = File.Open(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                imageSha256 = Convert.ToHexString(SHA256.HashData(image));
                (process, Observer initialObserver) = Launch(Backend.Managed, priorBuildSha256);
                observer = initialObserver;
                generation = initialObserver.Generation;
            }
            catch
            {
                image.Dispose();
                throw;
            }
        }

        public Backend Current => generation.Backend;
        public int StopRequests { get; private set; }

        public async Task StopAsync(RetirementFault fault, CancellationToken token)
        {
            StopRequests++;
            Assert.False(retirementStarted);
            retirementStarted = true;
            stopConfirmed = false;
            this.fault = fault;
            RequireInstalledBuild(generation.BuildSha256);
            // Persist the intent before signaling the retained child. The
            // scripted receipt and fake SCM cannot prove kernel process exit.
            gate.WriteRecord(generation, "intent", generation);
            if (fault == RetirementFault.TornIntent)
            {
                gate.WriteRecord(generation, "intent", generation, torn: true);
            }
            if (fault != RetirementFault.MissingReceipt)
            {
                Generation reported = fault switch
                {
                    RetirementFault.StaleReceipt => generation with { Instance = Guid.NewGuid() },
                    RetirementFault.StaleBootReceipt => generation with { Boot = generation.Boot - 1 },
                    RetirementFault.StaleBuild => generation with { BuildSha256 = new string('0', 64) },
                    RetirementFault.StaleImage => generation with { ImageSha256 = new string('0', 64) },
                    RetirementFault.StaleCreation => generation with { Creation = generation.Creation - 1 },
                    _ => generation,
                };
                gate.WriteRecord(generation, "receipt", reported, fault == RetirementFault.TornReceipt);
                if (fault == RetirementFault.TornReceipt)
                {
                    string receipt = gate.RecordPath(generation, "receipt");
                    File.Move(receipt + ".new", receipt);
                }
            }

            if (fault == RetirementFault.GuardianPending)
            {
                WriteFile(pending, """{"schemaVersion":"""u8.ToArray());
            }
            if (fault == RetirementFault.OwnershipRecord)
            {
                WriteFile(ownership, """{"generation":"""u8.ToArray());
            }
            if (fault == RetirementFault.RecoveryFence)
            {
                WriteFile(Path.Combine(paths.MachineDataDirectory, BatteryFence), []);
            }
            if (fault == RetirementFault.PartialConfiguration)
            {
                WriteFile(Path.Combine(paths.MachineDataDirectory, BatteryPreferences), """{"schemaVersion":"""u8.ToArray());
            }

            recoveryAfterStop = ReadManifest(paths.MachineDataDirectory);
            if (fault != RetirementFault.ProcessStillRunning)
            {
                string exit = fault == RetirementFault.ProcessFailed ? "exit 19" : "exit 0";
                await process.StandardInput.WriteLineAsync(exit.AsMemory(), token);
                await process.StandardInput.FlushAsync(token);
                await process.WaitForExitAsync(token);
            }
            if (fault == RetirementFault.ProcessIndeterminate)
            {
                observer!.OriginalHandle.Dispose();
            }
            if (fault == RetirementFault.ObserverLost)
            {
                // The harness keeps its child reference solely for cleanup.
                observer = null;
            }
        }

        public void ObserveStop()
        {
            if (fault == RetirementFault.StopTimeout)
            {
                // A failed wait stays failed even with a late Stopped state.
                throw new TimeoutException("The fake SCM stop timed out.");
            }
            stopConfirmed = scm.ServiceState == SetupServiceState.Stopped;
        }

        public void EnsureClear()
        {
            if (retirementStarted)
            {
                gate.RequireRetired(generation, stopConfirmed && scm.ServiceState == SetupServiceState.Stopped,
                    observer, fault == RetirementFault.StaleBoot ? () => ReadWindowsBoot() - 1 : ReadWindowsBoot,
                    RequireRecoveryClear);
            }
            else
            {
                guardian.EnsureClear();
            }
        }

        public void Acquire(Backend incoming, string buildSha256)
        {
            Assert.True(retirementStarted);
            Assert.NotEqual(generation.Backend, incoming);
            EnsureClear();
            AssertRecoveryPreserved();
            (Process next, Observer nextObserver) = Launch(incoming, buildSha256);
            process.Dispose();
            process = next;
            observer = nextObserver;
            generation = nextObserver.Generation;
            retirementStarted = false;
        }

        private void RequireRecoveryClear()
        {
            guardian.EnsureClear();
            RequireAbsent(ownership, "Scratch writer ownership still needs reconciliation.");
            // The only admitted configuration is the fixture's opaque profiles
            // baseline. No cross-backend interpretation of the aggregate is implied.
            foreach (string name in new[] { BatteryFence, BatteryPreferences, BatteryCommit })
            {
                RequireAbsent(Path.Combine(paths.MachineDataDirectory, name),
                    "Scratch battery configuration still needs reconciliation.");
            }
            if (File.ReadAllText(Path.Combine(paths.MachineDataDirectory, "profiles.json")) != "saved profiles")
            {
                throw new InvalidOperationException("The scratch profiles baseline changed.");
            }
        }

        private static void RequireAbsent(string path, string failure)
        {
            try
            {
                _ = File.GetAttributes(path);
            }
            catch (FileNotFoundException)
            {
                return;
            }

            throw new InvalidOperationException(failure);
        }

        public void AssertRecoveryPreserved()
        {
            Assert.NotNull(recoveryAfterStop);
            Assert.Equal(recoveryAfterStop, ReadManifest(paths.MachineDataDirectory));
            Assert.Equal("saved profiles", File.ReadAllText(Path.Combine(paths.MachineDataDirectory, "profiles.json")));
        }

        private (Process, Observer) Launch(Backend backend, string buildSha256)
        {
            RequireInstalledBuild(buildSha256);
            long boot = ReadWindowsBoot();
            var start = new ProcessStartInfo(imagePath, "/d /q")
            {
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            Process child = Process.Start(start) ?? throw new InvalidOperationException("Scratch process did not start.");
            try
            {
                return (child, ObserveLaunch(child, backend, boot, imagePath, imageSha256, buildSha256));
            }
            catch
            {
                DisposeChild(child);
                throw;
            }
        }

        private void RequireInstalledBuild(string expected)
        {
            using FileStream service = File.OpenRead(Path.Combine(paths.InstallDirectory, ServiceFile));
            if (Convert.ToHexString(SHA256.HashData(service)) != expected)
            {
                throw new InvalidOperationException("Scratch installed writer build changed.");
            }
        }

        public void Dispose()
        {
            using (image)
            {
                // This fault closes the retained handle after the child has
                // already exited. Cleanup must not query the closed handle.
                if (fault == RetirementFault.ProcessIndeterminate && observer?.OriginalHandle.IsClosed == true)
                {
                    process.Dispose();
                    return;
                }
                DisposeChild(process);
            }
        }

        private static void DisposeChild(Process child)
        {
            using (child)
            {
                if (!child.HasExited)
                {
                    child.Kill();
                    Assert.True(child.WaitForExit(5_000));
                }
            }
        }
    }
}

internal sealed class LocalWriterRetirementTheoryAttribute : TheoryAttribute
{
    public LocalWriterRetirementTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows()
            || Environment.GetEnvironmentVariable("OPENBLADE_PHASE_8G_RETIREMENT") != "1")
        {
            Skip = "Set OPENBLADE_PHASE_8G_RETIREMENT=1 for the non-elevated Windows scratch rehearsal.";
        }
    }
}
