using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using OpenBlade.Setup;
using OpenBlade.WriterFence.Scratch;
using static OpenBlade.Setup.Tests.ScratchWriterRetirementGate;
using static OpenBlade.Setup.Tests.SetupPayloadTransactionTests;
using FenceNative = OpenBlade.WriterFence.Scratch.Native;

namespace OpenBlade.Setup.Tests;

public sealed partial class SetupBackendCutoverTests
{
    public enum RetirementFault
    {
        None,
        MissingReceipt,
        ReceiptObservationLost,
        StaleTransport,
        TornTransport,
        AbruptExit,
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
        Assert.False(identity.IsSystem);
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
            RetirementFault.ProcessStillRunning or RetirementFault.ProcessIndeterminate or RetirementFault.ProcessFailed
                or RetirementFault.AbruptExit =>
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
        private readonly string leaseRoot;
        private readonly string rootId;
        private readonly string lockId;
        private SealedFixture image;
        private Process process;
        private Generation generation;
        private Observer? observer;
        private string? observedReceipt;
        private bool retirementStarted;
        private RetirementFault fault;
        private bool stopConfirmed;
        private SortedDictionary<string, string>? recoveryAfterStop;

        public ScratchWriters(string root, SetupPaths paths, OfflineWindows scm,
            ISetupGuardianQuiescenceGuard guardian, string priorBuildSha256, Backend initial = Backend.Managed,
            Action<string, Func<string, CancellationToken, IDisposable>>? prepareMarker = null)
        {
            this.root = root;
            this.paths = paths;
            this.scm = scm;
            this.guardian = guardian;
            pending = Path.Combine(paths.MachineDataDirectory, "FanStopGuardian", "pending.json");
            ownership = Path.Combine(paths.MachineDataDirectory, "hyperboost-ownership.json");
            gate = new(paths.MachineDataDirectory);
            gate.ClaimFreshObserver();
            string name = $"openblade-writer-fence-{Guid.NewGuid():N}";
            leaseRoot = Path.Combine(root, name);
            using var parent = PinnedRoot.OpenPath(root);
            using var directory = FenceNative.OpenRelative(parent.Handle, name, directory: true, create: true);
            rootId = FenceNative.Inspect(directory, directory: true).Identity;
            using (var file = FenceNative.OpenRelative(directory, WriterLease.Name, directory: false, create: true))
            {
                lockId = FenceNative.Inspect(file, directory: false).Identity;
            }

            if (prepareMarker is not null)
            {
                Assert.Equal(Backend.Candidate, initial);
                prepareMarker(leaseRoot, AcquireSuccessorLease);
            }
            (process, Observer initialObserver, image) = Launch(initial, priorBuildSha256,
                beginRunMarker: prepareMarker is not null);
            observer = initialObserver;
            generation = initialObserver.Generation;
        }

        public Backend Current => generation.Backend;
        public int StopRequests { get; private set; }
        public string LeaseRoot => leaseRoot;
        public string? RunMarkerFrame { get; private set; }

        public IDisposable AcquireSuccessorLease(string path, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert.Equal(leaseRoot, path);
            return WriterLease.AcquireExactRoot(path, rootId, lockId);
        }

        public async Task StopAsync(RetirementFault fault, CancellationToken token)
        {
            StopRequests++;
            Assert.False(retirementStarted);
            retirementStarted = true;
            stopConfirmed = false;
            this.fault = fault;
            RequireInstalledBuild(generation.BuildSha256);
            // Persist intent before touching the original child. A private
            // stdout frame and the fake SCM each prove a different condition.
            gate.WriteRecord(generation, "intent", generation);
            if (fault == RetirementFault.TornIntent)
            {
                gate.WriteRecord(generation, "intent", generation, torn: true);
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

            var recoveryBeforeStop = ReadRecoveryState();
            if (fault == RetirementFault.AbruptExit)
            {
                process.Kill();
                await process.WaitForExitAsync(token);
                Assert.NotEqual(0, process.ExitCode);
                Assert.Null(await ReadFrameAsync(process.StandardOutput, token));
                Assert.False(File.Exists(gate.RecordPath(generation, "receipt")));
            }
            else
            {
                await SendAsync(process, "retire", token);
                observedReceipt = await ReadFrameAsync(process.StandardOutput, token);
                if (IsReceiptFor(generation, observedReceipt))
                {
                    // Independently verify that the same enrolled file can be
                    // opened exclusively while the retiring child is still live.
                    using (WriterLease.Acquire(leaseRoot, rootId, lockId))
                    {
                        Assert.False(process.HasExited);
                    }
                    PersistReceipt(fault, observedReceipt!);
                }

                if (fault != RetirementFault.ProcessStillRunning)
                {
                    await SendAsync(process, fault == RetirementFault.ProcessFailed ? "fail" : "exit", token);
                    await process.WaitForExitAsync(token);
                    if (await ReadFrameAsync(process.StandardOutput, token) is not null)
                    {
                        observedReceipt = null;
                    }
                }
            }

            // Faults on the observer side exercise rejection even when the
            // complete record from the real child is available on disk.
            observedReceipt = fault switch
            {
                RetirementFault.ReceiptObservationLost => null,
                RetirementFault.StaleTransport => observedReceipt!.Replace(generation.Instance.ToString("N"),
                    Guid.NewGuid().ToString("N"), StringComparison.Ordinal),
                RetirementFault.TornTransport => observedReceipt![..^1],
                _ => observedReceipt,
            };
            Assert.Equal(recoveryBeforeStop, ReadRecoveryState());
            recoveryAfterStop = ReadManifest(paths.MachineDataDirectory);
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

        private void PersistReceipt(RetirementFault fault, string frame)
        {
            if (fault == RetirementFault.MissingReceipt)
            {
                return;
            }
            Generation reported = fault switch
            {
                RetirementFault.StaleReceipt => generation with { Instance = Guid.NewGuid() },
                RetirementFault.StaleBootReceipt => generation with { Boot = generation.Boot - 1 },
                RetirementFault.StaleBuild => generation with { BuildSha256 = new string('0', 64) },
                RetirementFault.StaleImage => generation with { FixtureSha256 = new string('0', 64) },
                RetirementFault.StaleCreation => generation with { Creation = generation.Creation - 1 },
                _ => generation,
            };
            bool torn = fault == RetirementFault.TornReceipt;
            gate.WriteRecord(generation, "receipt", reported, torn, frame);
            if (torn)
            {
                string receipt = gate.RecordPath(generation, "receipt");
                File.Move(receipt + ".new", receipt);
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
                    observer, observedReceipt,
                    fault == RetirementFault.StaleBoot ? () => ReadWindowsBoot() - 1 : ReadWindowsBoot,
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
            (Process next, Observer nextObserver, SealedFixture nextImage) = Launch(incoming, buildSha256);
            process.Dispose();
            image.Dispose();
            process = next;
            image = nextImage;
            observedReceipt = null;
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

        private KeyValuePair<string, string>[] ReadRecoveryState() => ReadManifest(paths.MachineDataDirectory)
            .Where(file => !file.Key.StartsWith("scratch-retirement-", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        public void AssertRecoveryPreserved()
        {
            Assert.NotNull(recoveryAfterStop);
            Assert.Equal(recoveryAfterStop, ReadManifest(paths.MachineDataDirectory));
            Assert.Equal("saved profiles", File.ReadAllText(Path.Combine(paths.MachineDataDirectory, "profiles.json")));
        }

        private (Process, Observer, SealedFixture) Launch(Backend backend, string buildSha256,
            bool beginRunMarker = false)
        {
            RequireInstalledBuild(buildSha256);
            long boot = ReadWindowsBoot();
            Guid instance = Guid.NewGuid();
            string imagePath = FixtureImage(backend);
            // The signed synthetic payload is data only. Execute the separately
            // built inert child and retain its image and original Windows handle.
            var sealedImage = new SealedFixture(imagePath, backend);
            Process? child = null;
            try
            {
                var start = new ProcessStartInfo(imagePath)
                {
                    WorkingDirectory = root,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                if (backend == Backend.Managed)
                {
                    start.ArgumentList.Add("hold");
                }
                if (beginRunMarker)
                {
                    start.ArgumentList.Add("--phase-8o-run-marker");
                }
                start.ArgumentList.Add(leaseRoot);
                start.ArgumentList.Add(rootId);
                start.ArgumentList.Add(lockId);
                start.ArgumentList.Add(instance.ToString("N"));
                child = Process.Start(start) ?? throw new InvalidOperationException("Scratch child did not start.");
                Observer launched = ObserveLaunch(child, backend, boot, imagePath, sealedImage.Sha256, buildSha256, instance);
                using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                string? ready = ReadFrameAsync(child.StandardOutput, startup.Token).GetAwaiter().GetResult();
                if (ready is not ("held\n" or "held\r\n"))
                {
                    throw new InvalidOperationException("Scratch child did not acquire the writer lease.");
                }
                if (beginRunMarker)
                {
                    RunMarkerFrame = ReadFrameAsync(child.StandardOutput, startup.Token)
                        .GetAwaiter().GetResult();
                }
                var held = Assert.Throws<Win32Exception>(() =>
                {
                    using var unexpected = WriterLease.Acquire(leaseRoot, rootId, lockId);
                });
                Assert.Equal(32, held.NativeErrorCode); // ERROR_SHARING_VIOLATION
                return (child, launched, sealedImage);
            }
            catch
            {
                using (sealedImage)
                {
                    if (child is not null)
                    {
                        DisposeChild(child);
                    }
                }
                throw;
            }
        }

        private static string FixtureImage(Backend backend)
        {
            (string variable, string name) = backend == Backend.Managed
                ? ("OPENBLADE_PHASE_8K_MANAGED_WRITER", "OpenBlade.WriterFence.Scratch.exe")
                : ("OPENBLADE_PHASE_8K_RUST_WRITER", "openblade-scratch-rust-writer.exe");
            string? path = Environment.GetEnvironmentVariable(variable);
            if (path is null || !Path.IsPathFullyQualified(path) || Path.GetFileName(path) != name)
            {
                throw new InvalidOperationException($"Set {variable} to the absolute path of the built {name} fixture.");
            }
            path = Path.GetFullPath(path);
            return path;
        }

        private sealed class SealedFixture : IDisposable
        {
            private readonly List<FileStream> files = [];

            public SealedFixture(string imagePath, Backend backend)
            {
                string stem = Path.ChangeExtension(imagePath, null);
                string[] members = backend == Backend.Managed
                    ? [imagePath, stem + ".dll", stem + ".deps.json", stem + ".runtimeconfig.json"]
                    : [imagePath];
                try
                {
                    using var combined = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    foreach (string member in members)
                    {
                        RequireUnlinkedPath(member);
                        var file = File.Open(member, FileMode.Open, FileAccess.Read, FileShare.Read);
                        files.Add(file);
                        combined.AppendData(SHA256.HashData(file));
                    }
                    Sha256 = Convert.ToHexString(combined.GetHashAndReset());
                }
                catch
                {
                    Dispose();
                    throw;
                }
            }

            public string Sha256 { get; }

            public void Dispose()
            {
                foreach (FileStream file in files)
                {
                    file.Dispose();
                }
            }
        }

        private static async Task SendAsync(Process child, string command, CancellationToken token)
        {
            await child.StandardInput.WriteLineAsync(command.AsMemory(), token);
            await child.StandardInput.FlushAsync(token);
        }

        private static async Task<string?> ReadFrameAsync(StreamReader reader, CancellationToken token)
        {
            // Retain the line terminator to distinguish a complete frame from
            // an EOF after a partial write. Both existing children fit in 64 chars.
            var frame = new StringBuilder(64);
            char[] next = new char[1];
            while (frame.Length < frame.Capacity && await reader.ReadAsync(next.AsMemory(), token) != 0)
            {
                frame.Append(next[0]);
                if (next[0] == '\n')
                {
                    break;
                }
            }
            return frame.Length == 0 ? null : frame.ToString();
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
            || Environment.GetEnvironmentVariable("OPENBLADE_PHASE_8K_RETIREMENT") != "1")
        {
            Skip = "Set OPENBLADE_PHASE_8K_RETIREMENT=1 for the non-elevated Windows scratch rehearsal.";
        }
    }
}
