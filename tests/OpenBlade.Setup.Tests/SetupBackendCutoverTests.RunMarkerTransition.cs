using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Security.Principal;
using System.Text;
using OpenBlade.Service;
using OpenBlade.Setup;
using OpenBlade.WriterFence.Scratch;
using static OpenBlade.Setup.Tests.ScratchWriterRetirementGate;
using static OpenBlade.Setup.Tests.SetupPayloadTransactionTests;

namespace OpenBlade.Setup.Tests;

public sealed partial class SetupBackendCutoverTests
{
    [LocalWriterRetirementTheory]
    [InlineData(null, ServiceStartupKind.Normal)]
    [InlineData(false, ServiceStartupKind.UnexpectedExitRecovery)]
    [InlineData(true, ServiceStartupKind.Normal)]
    public async Task ScratchRunMarkerRoundTripsBetweenManagedAndRust(
        bool? priorBoot, ServiceStartupKind expected)
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        Assert.False(identity.IsSystem);
        Assert.False(new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator));
        using var tree = new TestTree();
        tree.WriteInstall(ServiceFile, "synthetic Rust service build"u8.ToArray());
        SetupPaths paths = tree.Paths;
        WriteFile(Path.Combine(paths.MachineDataDirectory, "profiles.json"), "saved profiles"u8.ToArray());
        var windows = new OfflineWindows(paths);
        var guardian = new SetupGuardianQuiescenceGuard(paths, windows, File.GetAttributes);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken token = deadline.Token;
        byte[]? managedEvidence = null;

        using var writers = new ScratchWriters(tree.Root, paths, windows, guardian,
            ReadManifest(tree.Install)[ServiceFile], Backend.Candidate, (root, acquire) =>
            {
                if (priorBoot is null)
                {
                    return;
                }

                TimeSpan uptime = ReadUptime();
                DateTimeOffset now = DateTimeOffset.UtcNow;
                string path = Path.Combine(root, "service-running.json");
                // Script an earlier boot without changing the host clock or rebooting.
                var managed = CreateTransitionStore(path,
                    () => priorBoot.Value ? now - uptime - TimeSpan.FromDays(1) : now,
                    () => priorBoot.Value ? uptime + TimeSpan.FromDays(1) : uptime, acquire, null);
                Assert.Equal(ServiceStartupKind.Normal, managed.BeginRunAsync(token).GetAwaiter().GetResult());
                managedEvidence = File.ReadAllBytes(path);
            });

        Assert.Equal($"marker:{expected}\n", writers.RunMarkerFrame?.Replace("\r\n", "\n", StringComparison.Ordinal));
        string marker = Path.Combine(writers.LeaseRoot, "service-running.json");
        byte[] rustEvidence = File.ReadAllBytes(marker);
        if (managedEvidence is not null)
        {
            Assert.False(managedEvidence.AsSpan().SequenceEqual(rustEvidence));
        }
        Assert.False(File.Exists(marker + ".tmp"));

        windows.BeforeStopAsync = () => writers.StopAsync(RetirementFault.None, token);
        windows.BeforeWaitForStop = writers.ObserveStop;
        await windows.RunAsync(["stop", "OpenBlade"], [], token);
        await windows.WaitForStateAsync("OpenBlade", SetupServiceState.Stopped, TimeSpan.FromSeconds(10), token);

        int retirementChecks = 0;
        var handoff = CreateTransitionStore(marker, () => DateTimeOffset.UtcNow, ReadUptime,
            writers.AcquireSuccessorLease, () =>
            {
                writers.EnsureClear();
                retirementChecks++;
            });
        var denied = await Assert.ThrowsAsync<InvalidOperationException>(() => handoff.BeginRunAsync(token));
        Assert.Equal("Uncleared service run-marker evidence must be preserved.", denied.Message);
        Assert.Equal(1, retirementChecks);
        Assert.Equal(rustEvidence, File.ReadAllBytes(marker));
        Assert.False(File.Exists(marker + ".tmp"));

        // An ordinary startup can classify the preserved marker after the test
        // has independently verified that the original child retired.
        var managedReader = CreateTransitionStore(marker,
            () => priorBoot == true ? DateTimeOffset.UtcNow.AddDays(1) : DateTimeOffset.UtcNow,
            () => priorBoot == true ? TimeSpan.Zero : ReadUptime(), writers.AcquireSuccessorLease, null);
        Assert.Equal(priorBoot == true ? ServiceStartupKind.Normal : ServiceStartupKind.UnexpectedExitRecovery,
            await managedReader.BeginRunAsync(token));
        Assert.False(rustEvidence.AsSpan().SequenceEqual(File.ReadAllBytes(marker)));
        await managedReader.CompleteRunAsync(token);
        Assert.False(File.Exists(marker));
        Assert.False(File.Exists(marker + ".tmp"));
        writers.AssertRecoveryPreserved();

        static TimeSpan ReadUptime() => TimeSpan.FromMilliseconds(Environment.TickCount64);
    }

    [LocalWriterRetirementTheory]
    [InlineData(RetirementFault.None, null, null)]
    [InlineData(RetirementFault.None, "", "Uncleared service run-marker evidence must be preserved.")]
    [InlineData(RetirementFault.None, ".tmp", "Uncleared service run-marker evidence must be preserved.")]
    [InlineData(RetirementFault.ReceiptObservationLost, null, "Retirement evidence is missing, incomplete, or stale.")]
    [InlineData(RetirementFault.ObserverLost, null, "Scratch retirement evidence requires the original observer.")]
    [InlineData(RetirementFault.StopTimeout, null, "Writer stop was not confirmed.")]
    [InlineData(RetirementFault.RecoveryFence, null, "Scratch battery configuration still needs reconciliation.")]
    public async Task RustRetirementFencesManagedRunMarker(
        RetirementFault fault, string? existingSuffix, string? expectedFailure)
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        Assert.False(identity.IsSystem);
        Assert.False(new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator));
        using var tree = new TestTree();
        tree.WriteInstall(ServiceFile, "synthetic Rust service build"u8.ToArray());
        SetupPaths paths = tree.Paths;
        WriteFile(Path.Combine(paths.MachineDataDirectory, "profiles.json"), "saved profiles"u8.ToArray());

        var windows = new OfflineWindows(paths);
        var guardian = new SetupGuardianQuiescenceGuard(paths, windows, File.GetAttributes);
        using var writers = new ScratchWriters(tree.Root, paths, windows, guardian,
            ReadManifest(tree.Install)[ServiceFile], Backend.Candidate);
        string marker = Path.Combine(writers.LeaseRoot, "service-running.json");
        byte[]? existingEvidence = null;
        if (existingSuffix is not null)
        {
            string existing = existingSuffix.Length == 0
                ? """{"schemaVersion":2,"startedAtUtc":"2026-08-06T08:00:00Z","uptimeAtStart":"01:00:00"}"""
                : """{"schemaVersion":""";
            existingEvidence = Encoding.UTF8.GetBytes(existing);
            WriteFile(marker + existingSuffix, existingEvidence);
        }

        int retirementChecks = 0;
        var store = CreateTransitionStore(marker, () => DateTimeOffset.UtcNow,
            () => TimeSpan.FromMilliseconds(Environment.TickCount64), writers.AcquireSuccessorLease, () =>
            {
                retirementChecks++;
                var held = Assert.Throws<Win32Exception>(() =>
                {
                    using var unexpected = writers.AcquireSuccessorLease(writers.LeaseRoot, CancellationToken.None);
                });
                Assert.Equal(32, held.NativeErrorCode);
                writers.EnsureClear();
            });

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken token = deadline.Token;
        var outgoingLease = await Assert.ThrowsAsync<Win32Exception>(() => store.BeginRunAsync(token));
        Assert.Equal(32, outgoingLease.NativeErrorCode);
        Assert.Equal(0, retirementChecks);
        AssertMarkerEvidencePreserved();

        windows.BeforeStopAsync = () => writers.StopAsync(fault, token);
        windows.BeforeWaitForStop = writers.ObserveStop;
        await windows.RunAsync(["stop", "OpenBlade"], [], token);
        if (fault == RetirementFault.StopTimeout)
        {
            await Assert.ThrowsAsync<TimeoutException>(() =>
                windows.WaitForStateAsync("OpenBlade", SetupServiceState.Stopped, TimeSpan.FromSeconds(10), token));
        }
        else
        {
            await windows.WaitForStateAsync("OpenBlade", SetupServiceState.Stopped, TimeSpan.FromSeconds(10), token);
        }

        if (expectedFailure is null)
        {
            Assert.Equal(ServiceStartupKind.Normal, await store.BeginRunAsync(token));
            Assert.True(File.Exists(marker));
            Assert.False(File.Exists(marker + ".tmp"));
            await store.CompleteRunAsync(token);
        }
        else
        {
            var denied = await Assert.ThrowsAsync<InvalidOperationException>(() => store.BeginRunAsync(token));
            Assert.Equal(expectedFailure, denied.Message);
        }

        Assert.Equal(existingSuffix == ".tmp" ? 0 : 1, retirementChecks);
        AssertMarkerEvidencePreserved();
        writers.AssertRecoveryPreserved();

        void AssertMarkerEvidencePreserved()
        {
            // Enumerate without opening the lock while the Rust child owns it.
            string[] entries = Directory.GetFileSystemEntries(writers.LeaseRoot)
                .Where(path => Path.GetFileName(path) != WriterLease.Name).ToArray();
            if (existingEvidence is null)
            {
                Assert.Empty(entries);
                return;
            }
            Assert.Equal(marker + existingSuffix, Assert.Single(entries));
            Assert.Equal(existingEvidence, File.ReadAllBytes(entries[0]));
        }
    }

    // Bind the real opt-in constructor from the test assembly. Production keeps
    // its existing public constructor and does not reference the scratch gate.
    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    private static extern ServiceRunMarkerStore CreateTransitionStore(
        string path, Func<DateTimeOffset> now, Func<TimeSpan> uptime,
        Func<string, CancellationToken, IDisposable> acquireVerifiedWriterLease, Action? requireRetiredBackend);
}
