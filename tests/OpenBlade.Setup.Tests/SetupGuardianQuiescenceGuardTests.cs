using System.ComponentModel;
using OpenBlade.Setup;

namespace OpenBlade.Setup.Tests;

public sealed class SetupGuardianQuiescenceGuardTests
{
    private static SetupPaths Paths(string root) => SetupPaths.Create(
        Path.Combine(root, "Programs"), Path.Combine(root, "Data"), Path.Combine(root, "User"), Path.Combine(root, "Startup"));

    [Fact]
    public void MissingMarkerAndNoHelperPermitInitialInstallation()
    {
        var paths = Paths(Path.Combine(Path.GetTempPath(), "ob-guard-absent"));
        var processes = new Processes([]);
        new SetupGuardianQuiescenceGuard(paths, processes, _ => throw new FileNotFoundException()).EnsureClear();
        Assert.Equal(1, processes.Enumerations);
        Assert.Equal(0, processes.Signals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PendingMarkerOrUnreadableEvidenceRefuses(bool inaccessible)
    {
        var paths = Paths(Path.Combine(Path.GetTempPath(), "ob-guard-pending"));
        FileAttributes Read(string path)
        {
            if (path.EndsWith("pending.json", StringComparison.Ordinal))
            {
                if (inaccessible) { throw new UnauthorizedAccessException("synthetic evidence access failure"); }
                return FileAttributes.Normal;
            }
            return FileAttributes.Directory;
        }
        var processes = new Processes([]);
        Assert.Throws<InvalidOperationException>(() =>
            new SetupGuardianQuiescenceGuard(paths, processes, Read).EnsureClear());
        Assert.Equal(0, processes.Signals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InstalledOrRollbackHelperBlocksWithoutSignalling(bool rollback)
    {
        var paths = Paths(Path.Combine(Path.GetTempPath(), "ob-guard-running"));
        string directory = rollback
            ? Path.Combine(paths.ProgramFilesRoot, "OpenBlade.rollback." + new string('a', 32))
            : paths.InstallDirectory;
        var process = new ObservedProcess(() => Path.Combine(directory, "OpenBlade.FanStop.Guardian.exe"));
        var processes = new Processes([process]);
        Assert.Throws<InvalidOperationException>(() =>
            new SetupGuardianQuiescenceGuard(paths, processes, _ => throw new FileNotFoundException()).EnsureClear());
        Assert.True(process.Disposed);
        Assert.Equal(0, process.Waits);
        Assert.Equal(0, processes.Signals);
    }

    [Fact]
    public void UnreadableProcessIdentityBlocksAndDisposesEveryObservation()
    {
        var paths = Paths(Path.Combine(Path.GetTempPath(), "ob-guard-identity"));
        var unreadable = new ObservedProcess(() => throw new Win32Exception(5));
        var other = new ObservedProcess(() => Path.Combine(Path.GetTempPath(), "Other", "OpenBlade.FanStop.Guardian.exe"));
        Assert.Throws<InvalidOperationException>(() =>
            new SetupGuardianQuiescenceGuard(paths, new Processes([unreadable, other]),
                _ => throw new FileNotFoundException()).EnsureClear());
        Assert.True(unreadable.Disposed);
        Assert.True(other.Disposed);
    }

    [Fact]
    public void CoherentUnrelatedExecutableDoesNotBlockThisInstallation()
    {
        var paths = Paths(Path.Combine(Path.GetTempPath(), "ob-guard-other"));
        var other = new ObservedProcess(() => Path.Combine(Path.GetTempPath(), "Other", "OpenBlade.FanStop.Guardian.exe"));
        new SetupGuardianQuiescenceGuard(paths, new Processes([other]),
            _ => throw new FileNotFoundException()).EnsureClear();
        Assert.True(other.Disposed);
    }

    [Fact]
    public async Task BinaryRemovalAndPurgePreservePendingEvidence()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("ob-setup-guardian-");
        try
        {
            var paths = Paths(directory.FullName);
            Directory.CreateDirectory(paths.InstallDirectory);
            Directory.CreateDirectory(Path.Combine(paths.MachineDataDirectory, "FanStopGuardian"));
            string payload = Path.Combine(paths.InstallDirectory, "OpenBlade.FanStop.Guardian.exe");
            string pending = Path.Combine(paths.MachineDataDirectory, "FanStopGuardian", "pending.json");
            File.WriteAllText(payload, "fake payload");
            File.WriteAllText(pending, "unresolved");
            var platform = WindowsSetupPlatformTestFactory.Create(paths: paths,
                guardianQuiescenceGuard: new SetupGuardianQuiescenceGuard(paths, new Processes([]), File.GetAttributes));

            await Assert.ThrowsAsync<InvalidOperationException>(() => platform.RemoveBinariesAsync(CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() => platform.RemoveDataAsync(CancellationToken.None));

            Assert.Equal("fake payload", File.ReadAllText(payload));
            Assert.Equal("unresolved", File.ReadAllText(pending));
        }
        finally
        {
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), directory.FullName, StringComparison.OrdinalIgnoreCase);
            directory.Delete(recursive: true);
        }
    }

    private sealed class Processes(IReadOnlyList<ISetupProcess> observations) : ISetupProcessOperations
    {
        internal int Enumerations { get; private set; }
        internal int Signals { get; private set; }
        public IReadOnlyList<ISetupProcess> GetProcessesByName(string processName)
        {
            Assert.Equal(SetupGuardianQuiescenceGuard.ProcessName, processName);
            Enumerations++;
            return observations;
        }
        public bool TrySignalEvent(string eventName) { Signals++; return false; }
    }

    private sealed class ObservedProcess(Func<string?> executable) : ISetupProcess
    {
        internal bool Disposed { get; private set; }
        internal int Waits { get; private set; }
        public string? ExecutablePath => executable();
        public Task WaitForExitAsync(CancellationToken cancellationToken) { Waits++; return Task.CompletedTask; }
        public void Dispose() => Disposed = true;
    }
}
