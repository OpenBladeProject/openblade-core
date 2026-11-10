using System.Diagnostics;
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
        StopTimeout,
        ProcessStillRunning,
        GuardianPending,
        OwnershipRecord,
        RecoveryFence,
        PartialConfiguration,
    }

    [Theory]
    [InlineData(false, RetirementFault.None)]
    [InlineData(false, RetirementFault.MissingReceipt)]
    [InlineData(false, RetirementFault.StaleReceipt)]
    [InlineData(false, RetirementFault.TornReceipt)]
    [InlineData(false, RetirementFault.StopTimeout)]
    [InlineData(false, RetirementFault.ProcessStillRunning)]
    [InlineData(false, RetirementFault.GuardianPending)]
    [InlineData(false, RetirementFault.OwnershipRecord)]
    [InlineData(false, RetirementFault.RecoveryFence)]
    [InlineData(false, RetirementFault.PartialConfiguration)]
    [InlineData(true, RetirementFault.None)]
    [InlineData(true, RetirementFault.MissingReceipt)]
    [InlineData(true, RetirementFault.StaleReceipt)]
    [InlineData(true, RetirementFault.TornReceipt)]
    [InlineData(true, RetirementFault.StopTimeout)]
    [InlineData(true, RetirementFault.ProcessStillRunning)]
    [InlineData(true, RetirementFault.GuardianPending)]
    [InlineData(true, RetirementFault.OwnershipRecord)]
    [InlineData(true, RetirementFault.RecoveryFence)]
    [InlineData(true, RetirementFault.PartialConfiguration)]
    public async Task BundleUpgrade_FencesTheIncomingWriterUntilRetirementIsProved(
        bool rollback, RetirementFault fault)
    {
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
        using var writers = new ScratchWriters(tree.Root, paths, windows, guardian);
        using var progress = new RecordingSetupProgressReporter();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        const string rejectedCandidate = "Candidate readiness failed.";
        string? expectedBlock = fault switch
        {
            RetirementFault.None => null,
            RetirementFault.StopTimeout => "Writer stop was not confirmed.",
            RetirementFault.ProcessStillRunning => "Outgoing writer process exit was not confirmed.",
            RetirementFault.GuardianPending => "Fan-stop guardian ownership is pending.",
            RetirementFault.OwnershipRecord => "Scratch writer ownership still needs reconciliation.",
            RetirementFault.RecoveryFence or RetirementFault.PartialConfiguration =>
                "Scratch battery configuration still needs reconciliation.",
            _ => "Retirement receipt is missing, incomplete, or stale.",
        };

        var acquisitions = new List<Backend> { Backend.Managed };
        windows.BeforeStopAsync = () => writers.StopAsync(
            (writers.Current == Backend.Candidate) == rollback ? fault : RetirementFault.None, deadline.Token);
        windows.BeforeWaitForStop = writers.ObserveStop;
        windows.BeforeStart = () =>
        {
            Backend incoming = acquisitions.Count == 1 ? Backend.Candidate : Backend.Managed;
            writers.EnsureClear();
            Assert.Equal(incoming == Backend.Candidate ? candidate : prior, ReadManifest(tree.Install));
            writers.Acquire(incoming);
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
        writers.AssertRecoveryPreserved();
    }

    private sealed class ScratchWriters(string root, SetupPaths paths, OfflineWindows scm,
        ISetupGuardianQuiescenceGuard guardian) : ISetupGuardianQuiescenceGuard, IDisposable
    {
        private const string BatteryPreferences = "battery-preferences.json";
        private const string BatteryCommit = "battery-configuration.commit.json";
        private const string BatteryFence = BatteryCommit + ".recovery-required";
        private readonly string pending = Path.Combine(paths.MachineDataDirectory, "FanStopGuardian", "pending.json");
        private readonly string ownership = Path.Combine(paths.MachineDataDirectory, "hyperboost-ownership.json");
        private Process process = StartProcess(root);
        private Generation generation = new(Backend.Managed, Guid.NewGuid());
        private bool retirementStarted;
        private bool injectStopTimeout;
        private bool stopTimedOut;
        private SortedDictionary<string, string>? recoveryAfterStop;

        public Backend Current => generation.Backend;
        public int StopRequests { get; private set; }
        private string Receipt => Path.Combine(paths.MachineDataDirectory, $"scratch-retirement-{generation.Instance:N}.bin");

        public async Task StopAsync(RetirementFault fault, CancellationToken token)
        {
            StopRequests++;
            Assert.False(retirementStarted);
            retirementStarted = true;
            injectStopTimeout = fault == RetirementFault.StopTimeout;
            if (fault != RetirementFault.MissingReceipt)
            {
                Generation reported = fault == RetirementFault.StaleReceipt
                    ? generation with { Instance = Guid.NewGuid() }
                    : generation;
                byte[] bytes = [(byte)reported.Backend, .. reported.Instance.ToByteArray()];
                using (var journal = new FileStream(Receipt + ".new", FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    journal.Write(fault == RetirementFault.TornReceipt ? bytes.AsSpan(0, 8) : bytes);
                    journal.Flush(flushToDisk: true);
                }
                File.Move(Receipt + ".new", Receipt);
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
                await process.StandardInput.WriteLineAsync("exit 0".AsMemory(), token);
                await process.StandardInput.FlushAsync(token);
                await process.WaitForExitAsync(token);
            }
        }

        public void ObserveStop()
        {
            if (injectStopTimeout)
            {
                // Model a failed wait with a late Stopped observation, even
                // though the child and its receipt otherwise look retired.
                stopTimedOut = true;
                throw new TimeoutException("The fake SCM stop timed out.");
            }
        }

        public void EnsureClear()
        {
            if (retirementStarted)
            {
                RequireRetired(generation, !stopTimedOut && scm.ServiceState == SetupServiceState.Stopped,
                    process.HasExited && process.ExitCode == 0, ReadReceipt, RequireRecoveryClear);
            }
            else
            {
                guardian.EnsureClear();
            }
        }

        public void Acquire(Backend incoming)
        {
            Assert.True(retirementStarted);
            Assert.NotEqual(generation.Backend, incoming);
            EnsureClear();
            Process next = StartProcess(root);
            process.Dispose();
            process = next;
            generation = new(incoming, Guid.NewGuid());
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

        private Generation? ReadReceipt()
        {
            try
            {
                using var file = File.OpenRead(Receipt);
                Span<byte> bytes = stackalloc byte[17];
                file.ReadExactly(bytes);
                if (file.ReadByte() == -1 && (Backend)bytes[0] is Backend.Managed or Backend.Candidate)
                {
                    return new Generation((Backend)bytes[0], new Guid(bytes[1..]));
                }
                return null;
            }
            catch (Exception error) when (error is FileNotFoundException or EndOfStreamException)
            {
                return null;
            }
        }

        public void AssertRecoveryPreserved()
        {
            Assert.NotNull(recoveryAfterStop);
            Assert.Equal(recoveryAfterStop, ReadManifest(paths.MachineDataDirectory));
            Assert.Equal("saved profiles", File.ReadAllText(Path.Combine(paths.MachineDataDirectory, "profiles.json")));
        }

        private static Process StartProcess(string root)
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d /q")
            {
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            Process child = Process.Start(start) ?? throw new InvalidOperationException("Scratch process did not start.");
            _ = child.SafeHandle;
            return child;
        }

        public void Dispose()
        {
            using (process)
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    Assert.True(process.WaitForExit(5_000));
                }
            }
        }
    }
}
