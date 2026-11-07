using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using OpenBlade.Capture;
using OpenBlade.Guardian.TestHost;

namespace OpenBlade.Capture.Tests;

public sealed class So6120GuardianLaunchTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper output;

    public So6120GuardianLaunchTests(Xunit.Abstractions.ITestOutputHelper output) => this.output = output;

    private static string Executable => Path.Combine(AppContext.BaseDirectory, "OpenBlade.Guardian.TestHost.exe");

    [Fact]
    public async Task FakeOwnerInSeparateConsoleSurvivesCallerJobClosureAndRestoresAuto()
    {
        string[] payload = ["", "has spaces", "trailing\\", "slash\\\"quote", "two\\\\\"quotes\"", "München"];
        await using var run = new LaunchRun("disconnect", payload);
        LaunchReadyReport ready = await run.Report<LaunchReadyReport>("ready.json");
        Assert.Equal(await run.Report<int>("launched.json"), ready.ProcessId);
        Assert.NotEqual(run.Caller.Id, ready.ProcessId);
        Assert.Equal(run.Caller.Id, ready.CallerProcessId);
        output.WriteLine($"Owner retained an ambient host job: {ready.InAmbientJob}.");
        Assert.False(ready.InheritedUnrelated);
        Assert.Equal(payload, ready.Payload);

        Process owner = run.ObserveOwner(ready.ProcessId);
        Process member = run.ObserveMember(await run.Report<int>("member.json"));
        PendingReport pending = await run.Report<PendingReport>("pending.json", owner);
        Assert.Equal(ready.ProcessId, pending.OwnerPid);
        Assert.Equal("zero-checkpoint", pending.Gate);
        string[] before = run.Commands();
        Assert.Equal(pending.Commands, before.Length);

        run.Caller.Kill(entireProcessTree: false);
        await run.Caller.WaitForExitAsync(run.Token);
        // The fake member has no job handle and stays alive until either the
        // private job closes or the explicit release file appears.
        await member.WaitForExitAsync(run.Token);
        Assert.False(run.Exists("release"));
        DisconnectReport disconnect = await run.Report<DisconnectReport>("disconnected.json", owner);
        Assert.Equal(ready.ProcessId, disconnect.OwnerPid);
        Assert.Equal(nameof(So6120GuardianStopReason.CallerDisconnected), disconnect.StopReason);
        Assert.True(disconnect.OwnerRunning);
        Assert.False(owner.HasExited);
        Assert.Equal(before, run.Commands());
        Assert.False(run.Exists("receipt.json"));

        run.ReleaseOwner();
        TerminalReport receipt = await run.Report<TerminalReport>("receipt.json", owner);
        Assert.Equal(ready.ProcessId, receipt.OwnerPid);
        Assert.True(receipt.Resolved, JsonSerializer.Serialize(receipt));
        Assert.True(receipt.ZeroPwmObserved);
        await owner.WaitForExitAsync(run.Token);
        Assert.Equal(23, owner.ExitCode);
    }

    [Fact]
    public async Task RestrictiveCallerJobRefusesBreakawayWithoutFallback()
    {
        await using var run = new LaunchRun("restricted", []);
        const int errorAccessDenied = 5;
        Assert.Equal(errorAccessDenied, await run.Report<int>("rejected.json"));
        await run.Caller.WaitForExitAsync(run.Token);
        Assert.Equal(0, run.Caller.ExitCode);
        Assert.False(run.Exists("launched.json"));
        Assert.False(run.Exists("ready.json"));
    }

    [Fact]
    public async Task CanceledWaitAndDisposedObservationLeaveFakeOwnerRunning()
    {
        await using var run = new LaunchRun("observe", []);
        LaunchReadyReport ready = await run.Report<LaunchReadyReport>("ready.json");
        Assert.Equal(run.Caller.Id, ready.CallerProcessId);
        output.WriteLine($"Owner retained an ambient host job: {ready.InAmbientJob}.");
        Process owner = run.ObserveOwner(ready.ProcessId);
        Assert.Equal(2, await run.Report<int>("control.json"));
        Assert.Equal(0, await run.Report<int>("shared-console.json", owner));
        Assert.False(owner.HasExited);
        run.ReleaseOwner();
        Assert.Equal(23, await run.Report<int>("exit.json"));
        await run.Caller.WaitForExitAsync(run.Token);
        Assert.Equal(0, run.Caller.ExitCode);
    }

    [Fact]
    public void InvalidAndNoninheritableControlHandlesAreRefusedBeforeLaunch()
    {
        using var invalid = new SafePipeHandle(0, ownsHandle: false);
        using var control = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
        using var fakeFile = File.OpenHandle(Executable, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var nonPipe = new SafePipeHandle(fakeFile.DangerousGetHandle(), ownsHandle: false);
        using var writeEnd = new SafePipeHandle(control.SafePipeHandle.DangerousGetHandle(), ownsHandle: false);

        // Invalid control handles must fail before accessing a caller lifetime.
        Assert.Throws<ArgumentException>(() => So6120GuardianProcess.Start(Executable, [], invalid, showConsole: false, null!));
        Assert.Throws<ArgumentException>(() => So6120GuardianProcess.Start(Executable, [], nonPipe, showConsole: false, null!));
        try
        {
            Assert.Throws<ArgumentException>(() =>
                So6120GuardianProcess.Start(Executable, [], control.ClientSafePipeHandle, showConsole: false, null!));
        }
        finally { control.DisposeLocalCopyOfClientHandle(); }
        Assert.Throws<Win32Exception>(() => So6120GuardianProcess.Start(Executable, [], writeEnd, showConsole: false, null!));
    }

    [Fact]
    public async Task UnrelatedValidJobCannotEstablishCallerIsolation()
    {
        await using var run = new LaunchRun("wrong-job", []);
        string refusal = await run.Report<string>("owner-error.json");
        Assert.Contains("live original caller in its private job", refusal);
        Assert.False(run.Exists("ready.json"));
        Assert.False(run.Exists("pending.json"));
    }

    [Fact]
    public void InvalidVerificationHandlesAreRefused()
    {
        using var job = new SafeFileHandle(0, ownsHandle: false);
        using var caller = new SafeProcessHandle(0, ownsHandle: false);
        Assert.Throws<Win32Exception>(() => So6120GuardianProcess.VerifyOwnerIsolation(job, caller));
    }

    [Fact]
    public async Task OriginalCallerExitCode259CannotMasqueradeAsAlive()
    {
        await using var run = new LaunchRun("exit-259", []);
        int ownerId = await run.Report<int>("launched.json");
        Process owner = run.ObserveOwner(ownerId);
        run.ReleaseOwner();
        string refusal = await run.Report<string>("owner-error.json", owner);
        await run.Caller.WaitForExitAsync(run.Token);
        Assert.Equal(259, run.Caller.ExitCode);
        Assert.Contains("live original caller in its private job", refusal);
        Assert.False(run.Exists("ready.json"));
        Assert.False(run.Exists("pending.json"));
    }

    private sealed class LaunchRun : IAsyncDisposable
    {
        private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("ob guardian ");
        private readonly CancellationTokenSource timeout = new(TimeSpan.FromSeconds(25));
        private readonly Task<string> callerError;
        private Process? owner;
        private Process? member;

        internal LaunchRun(string mode, string[] payload)
        {
            var start = new ProcessStartInfo(Executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };
            foreach (string argument in new[] { "launch-caller", directory.FullName, mode }.Concat(payload))
            {
                start.ArgumentList.Add(argument);
            }

            Caller = Process.Start(start) ?? throw new InvalidOperationException("Could not start the fake caller.");
            callerError = Caller.StandardError.ReadToEndAsync();
        }

        internal Process Caller { get; }
        internal CancellationToken Token => timeout.Token;
        internal bool Exists(string name) => File.Exists(Path.Combine(directory.FullName, name));
        internal void ReleaseOwner() => File.WriteAllText(Path.Combine(directory.FullName, "release"), "");
        internal string[] Commands() => File.ReadAllLines(Path.Combine(directory.FullName, "commands.log"));

        internal Process ObserveOwner(int id)
        {
            owner = Process.GetProcessById(id);
            _ = owner.SafeHandle;
            return owner;
        }

        internal Process ObserveMember(int id)
        {
            member = Process.GetProcessById(id);
            _ = member.SafeHandle;
            return member;
        }

        internal async Task<T> Report<T>(string name, Process? writer = null)
        {
            writer ??= Caller;
            while (!Exists(name))
            {
                if (name != "owner-error.json" && Exists("owner-error.json"))
                {
                    Assert.Fail(ReadReport<string>("owner-error.json"));
                }
                if (writer.HasExited)
                {
                    string detail = writer == Caller ? await callerError.WaitAsync(Token) : "";
                    Assert.True(Exists(name), $"Fake process exited with {writer.ExitCode} before {name}. {detail}");
                    break;
                }

                await Task.Delay(20, Token);
            }

            return ReadReport<T>(name);
        }

        private T ReadReport<T>(string name)
        {
            using var stream = new FileStream(Path.Combine(directory.FullName, name), FileMode.Open,
                FileAccess.Read, FileShare.Read | FileShare.Delete);
            return JsonSerializer.Deserialize<T>(stream)!;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                ReleaseOwner();
                if (!Caller.HasExited)
                {
                    try { Caller.Kill(entireProcessTree: false); }
                    catch (InvalidOperationException) when (Caller.HasExited) { }
                }

                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await Caller.WaitForExitAsync(cleanup.Token);
                if (owner is null && Exists("launched.json"))
                {
                    try { ObserveOwner(ReadReport<int>("launched.json")); }
                    catch (ArgumentException) { } // The fake owner already exited.
                }
                if (owner is not null) { await owner.WaitForExitAsync(cleanup.Token); }
                if (member is null && Exists("member.json"))
                {
                    try { ObserveMember(ReadReport<int>("member.json")); }
                    catch (ArgumentException) { }
                }
                if (member is not null) { await member.WaitForExitAsync(cleanup.Token); }
            }
            finally
            {
                owner?.Dispose();
                member?.Dispose();
                Caller.Dispose();
                timeout.Dispose();
                directory.Delete(recursive: true);
            }
        }
    }
}
