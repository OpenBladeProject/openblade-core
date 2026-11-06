using System.Diagnostics;
using System.Text.Json;
using OpenBlade.Guardian.TestHost;

namespace OpenBlade.Capture.Tests;

// Sibling fake processes demonstrate caller/owner independence in this test
// environment. These tests do not exercise Windows deployment or job lifetime.
public sealed class So6120GuardianProcessTests
{
    [Theory]
    [InlineData("fe-pending")]
    [InlineData("zero-checkpoint")]
    public async Task AbruptCallerExitLeavesSameOwnerToRestoreAuto(string gate)
    {
        using var run = new TestRun();
        await using var owner = Child.Start("owner", run.Root, run.Pipe, gate);
        await using var caller = Child.Start("caller", run.Pipe);
        var pending = await run.ReadReport<PendingReport>("pending.json", owner);
        Assert.Equal(gate, pending.Gate);
        Assert.Equal(owner.Id, pending.OwnerPid);
        Assert.NotEqual(caller.Id, owner.Id);
        string[] before = run.Commands();
        Assert.Equal(pending.Commands, before.Length);
        bool atZero = gate == "zero-checkpoint";
        string[] entryWrites = ["A1 B8 5B", "A1 B9 5B", "A1 F0 51", "A1 1A FE"];
        string[] expectedBefore = atZero ? [.. entryWrites, "A1 B8 00", "A1 B9 00"] : entryWrites;
        Assert.Equal(expectedBefore, before.Where(command => command.StartsWith("A1 ", StringComparison.Ordinal)));
        if (!atZero) { Assert.Equal("A1 1A FE", before[^1]); }

        await caller.Kill();
        var disconnect = await run.ReadReport<DisconnectReport>("disconnected.json", owner);
        Assert.Equal(pending.OwnerPid, disconnect.OwnerPid);
        Assert.Equal(nameof(So6120GuardianStopReason.CallerDisconnected), disconnect.StopReason);
        Assert.True(disconnect.OwnerRunning);
        Assert.False(owner.Exited);
        Assert.False(run.Exists("disposed.json"));
        Assert.False(run.Exists("receipt.json"));
        Assert.Equal(before, run.Commands());

        run.Release();
        await owner.ExpectSuccess(run.Token);

        var receipt = await run.ReadReport<TerminalReport>("receipt.json", owner);
        Assert.Equal(pending.OwnerPid, receipt.OwnerPid);
        Assert.Null(receipt.Failure);
        Assert.True(receipt.AutoConfirmed);
        Assert.True(receipt.CleanupConfirmed);
        Assert.True(receipt.JournalCompleted);
        Assert.True(receipt.ReleaseCompleted);
        Assert.True(receipt.Resolved, JsonSerializer.Serialize(receipt));
        Assert.Equal(nameof(So6120SilentTrialStop.Cancelled), receipt.TrialStop);
        Assert.Equal(atZero, receipt.ZeroPwmObserved);
        Assert.Equal(owner.Id, await run.ReadReport<int>("disposed.json", owner));
        string[] commands = run.Commands();
        if (!atZero) { Assert.Equal("A0 1A", commands[before.Length]); }
        Assert.Equal(["A1 1A 00", "A1 F0 11", "A1 B8 0A", "A1 B9 14"],
            commands.Skip(before.Length).Where(command => command.StartsWith("A1 ", StringComparison.Ordinal)));
        var journal = So6120DirectDutyJournal.Inspect(run.Root);
        Assert.True(journal.Complete);
        Assert.True(journal.PermissionRestoredConfirmed);
        Assert.False(journal.UnresolvedWork);
        Assert.False(journal.Corrupt);
    }

    [Fact]
    public async Task AbruptOwnerExitLeavesIncompleteJournalAndSameRootRefusesReuseWithoutCommands()
    {
        using var run = new TestRun();
        await using var owner = Child.Start("owner", run.Root, run.Pipe, "fe-pending");
        await using var caller = Child.Start("caller", run.Pipe);
        var pending = await run.ReadReport<PendingReport>("pending.json", owner);
        Assert.Equal(owner.Id, pending.OwnerPid);
        string[] before = run.Commands();
        Assert.Equal(pending.Commands, before.Length);
        Assert.Equal("A1 1A FE", before[^1]);

        await owner.Kill();
        Assert.False(run.Exists("receipt.json"));
        Assert.False(run.Exists("disposed.json"));
        Assert.Equal(before, run.Commands());

        string journalPath = Path.Combine(run.Root, So6120DirectDutyJournal.FileName);
        byte[] interrupted = File.ReadAllBytes(journalPath);
        var journal = So6120DirectDutyJournal.Inspect(run.Root);
        Assert.False(journal.Complete);
        Assert.False(journal.Corrupt);
        Assert.True(journal.UnresolvedWork);
        Assert.True(journal.PermissionPublicationPossible);
        Assert.False(journal.PermissionRestoredConfirmed);
        Assert.False(journal.CanResume);

        await using var probe = Child.Start("probe-reuse", run.Root);
        await probe.ExpectSuccess(run.Token);
        var reuse = await run.ReadReport<ReuseReport>("reuse.json", probe);
        Assert.Equal(probe.Id, reuse.ProcessId);
        Assert.True(reuse.Refused);
        Assert.Equal(0, reuse.PortCalls);
        Assert.Equal(interrupted, File.ReadAllBytes(journalPath));
        Assert.Equal(before, run.Commands());
        Assert.False(run.Exists("receipt.json"));
    }

    private sealed class TestRun : IDisposable
    {
        private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("ob-guardian-");
        private readonly CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        public string Root => directory.FullName;
        public string Pipe { get; } = "ob-guardian-" + Guid.NewGuid().ToString("N");
        public CancellationToken Token => timeout.Token;

        public bool Exists(string name) => File.Exists(Path.Combine(Root, name));
        public void Release() => File.WriteAllText(Path.Combine(Root, "release"), "");
        public string[] Commands() => File.ReadAllLines(Path.Combine(Root, "commands.log"));

        public async Task<T> ReadReport<T>(string name, Child writer)
        {
            string path = Path.Combine(Root, name);
            while (!File.Exists(path))
            {
                if (writer.Exited)
                {
                    await writer.ExpectSuccess(Token);
                    string receipt = Path.Combine(Root, "receipt.json");
                    string diagnostic = File.Exists(receipt) ? ReadPublishedReport(receipt) : "No terminal receipt.";
                    Assert.True(File.Exists(path), $"Fake process {writer.Id} exited without writing {name}. {diagnostic}");
                    break;
                }

                await Task.Delay(20, Token);
            }

            return JsonSerializer.Deserialize<T>(ReadPublishedReport(path))!;
        }

        private static string ReadPublishedReport(string path)
        {
            // File.Move may still hold its DELETE access handle after the name
            // becomes visible. Share it instead of retrying a rename-window race.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        public void Dispose()
        {
            timeout.Dispose();
            directory.Delete(recursive: true);
        }
    }

    private sealed class Child : IAsyncDisposable
    {
        private static readonly TimeSpan CleanupLimit = TimeSpan.FromSeconds(5);
        private readonly Process process;
        private readonly Task<string> stdout;
        private readonly Task<string> stderr;

        private Child(Process process)
        {
            this.process = process;
            stdout = process.StandardOutput.ReadToEndAsync();
            stderr = process.StandardError.ReadToEndAsync();
        }

        public int Id => process.Id;
        public bool Exited => process.HasExited;

        public static Child Start(params string[] arguments)
        {
            string executable = Path.Combine(AppContext.BaseDirectory, "OpenBlade.Guardian.TestHost.exe");
            var info = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string argument in arguments) { info.ArgumentList.Add(argument); }
            return new(Process.Start(info) ?? throw new InvalidOperationException("Could not start the fake test host."));
        }

        public async Task ExpectSuccess(CancellationToken cancellationToken)
        {
            await process.WaitForExitAsync(cancellationToken);
            string output = await stdout.WaitAsync(cancellationToken);
            string errors = await stderr.WaitAsync(cancellationToken);
            Assert.True(process.ExitCode == 0, $"Fake process {Id} exited with {process.ExitCode}.\n{output}\n{errors}");
        }

        public async Task Kill()
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: false); }
                catch (InvalidOperationException) when (process.HasExited) { }
            }

            using var timeout = new CancellationTokenSource(CleanupLimit);
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(stdout, stderr).WaitAsync(timeout.Token);
        }

        public async ValueTask DisposeAsync()
        {
            try { await Kill(); }
            finally { process.Dispose(); }
        }
    }
}
