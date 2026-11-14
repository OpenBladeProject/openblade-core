using OpenBlade.Contracts;

namespace OpenBlade.Setup.Tests;

public sealed class SetupServiceQuiescenceTests
{
    private const string Install = @"C:\Fake\OpenBlade";

    [Theory]
    [InlineData(SetupServiceState.Running)]
    [InlineData(SetupServiceState.Stopped)]
    [InlineData(null)]
    public async Task Stop_RetainsIdentityBeforeScmStop_AndWaitsForActualExit(SetupServiceState? state)
    {
        ServiceExitProcess process = new(Path.Combine(Install, "OpenBlade.Service.exe"));
        ServiceExitControl control = new(state)
        {
            OnStop = () => Assert.True(process.Retained),
        };
        ServiceExitProcesses processes = new(process);

        Task stop = SetupServiceQuiescence.StopAsync(control, processes, Install,
            TimeSpan.FromSeconds(5), CancellationToken.None);
        await process.WaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(stop.IsCompleted);
        Assert.True(process.Retained);
        Assert.False(process.Disposed);
        Assert.Equal(state == SetupServiceState.Running ? 1 : 0, control.Stops);
        Assert.Equal(state == SetupServiceState.Running ? 1 : 0, control.StoppedWaits);
        process.Exit.SetResult();
        await stop;
        Assert.True(process.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stop_DeadlineOrCancellation_DoesNotReturnSuccess_AndDisposesCandidates(bool cancel)
    {
        ServiceExitProcess installed = new(Path.Combine(Install, "OpenBlade.Service.exe"));
        ServiceExitProcess foreign = new(@"C:\Foreign\OpenBlade.Service.exe");
        using CancellationTokenSource cancellation = new();
        Task stop = SetupServiceQuiescence.StopAsync(new ServiceExitControl(SetupServiceState.Running),
            new ServiceExitProcesses(installed, foreign), Install,
            cancel ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(50), cancellation.Token);
        await installed.WaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (cancel)
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stop);
        }
        else
        {
            await Assert.ThrowsAsync<TimeoutException>(() => stop);
        }
        Assert.True(installed.Disposed);
        Assert.True(foreign.Disposed);
        Assert.False(foreign.WaitStarted.Task.IsCompleted);
        installed.Exit.TrySetResult();
    }

    [Theory]
    [InlineData("retain")]
    [InlineData("identity")]
    [InlineData("wait")]
    public async Task Stop_InspectionOrWaitError_FailsClosedAndDisposesAll(string stage)
    {
        InvalidOperationException failure = new("test failure");
        ServiceExitProcess installed = new(Path.Combine(Install, "OpenBlade.Service.exe"))
        {
            RetainFailure = stage == "retain" ? failure : null,
            IdentityFailure = stage == "identity" ? failure : null,
            WaitFailure = stage == "wait" ? failure : null,
        };
        ServiceExitProcess other = new(@"C:\Foreign\OpenBlade.Service.exe");
        ServiceExitControl control = new(SetupServiceState.Running);
        Exception observed = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SetupServiceQuiescence.StopAsync(control, new ServiceExitProcesses(installed, other),
                Install, TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.Same(failure, observed);
        Assert.Equal(stage == "wait" ? 1 : 0, control.Stops);
        Assert.True(installed.Disposed);
        Assert.True(other.Disposed);
    }

    [Fact]
    public async Task Stop_AlreadyExitedAndForeignProcesses_DoNotReceiveExitWaitOrSignal()
    {
        ServiceExitProcess exited = new(Path.Combine(Install, "OpenBlade.Service.exe")) { AlreadyExited = true };
        ServiceExitProcess foreign = new(@"C:\Foreign\OpenBlade.Service.exe");
        await SetupServiceQuiescence.StopAsync(new ServiceExitControl(null),
            new ServiceExitProcesses(exited, foreign), Install, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.False(exited.WaitStarted.Task.IsCompleted);
        Assert.False(foreign.WaitStarted.Task.IsCompleted);
        Assert.True(exited.Disposed);
        Assert.True(foreign.Disposed);
    }

    [Fact]
    public async Task Stop_ExitDuringIdentityInspection_IsConfirmedFromRetainedLifetime()
    {
        ServiceExitProcess process = new(Path.Combine(Install, "OpenBlade.Service.exe"))
        {
            ExitDuringIdentityInspection = true,
        };
        await SetupServiceQuiescence.StopAsync(new ServiceExitControl(null),
            new ServiceExitProcesses(process), Install, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.True(process.Disposed);
        Assert.False(process.WaitStarted.Task.IsCompleted);
    }

    [Fact]
    public async Task Stop_IdentityAccessDeniedOnLiveProcess_DoesNotStopService()
    {
        ServiceExitProcess process = new(Path.Combine(Install, "OpenBlade.Service.exe"))
        {
            IdentityFailure = new System.ComponentModel.Win32Exception(5),
        };
        ServiceExitControl control = new(SetupServiceState.Running);
        await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() =>
            SetupServiceQuiescence.StopAsync(control, new ServiceExitProcesses(process),
                Install, TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.Equal(0, control.Stops);
        Assert.True(process.Disposed);
    }
}

internal sealed class ServiceExitProcess(string executablePath) : ISetupProcess
{
    internal TaskCompletionSource Exit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource WaitStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal bool Retained { get; private set; }
    internal bool Disposed { get; private set; }
    internal bool AlreadyExited { get; init; }
    internal bool ExitDuringIdentityInspection { get; init; }
    private bool exitedDuringInspection;
    internal Exception? RetainFailure { get; init; }
    internal Exception? IdentityFailure { get; init; }
    internal Exception? WaitFailure { get; init; }

    public bool TryRetainLifetime()
    {
        if (RetainFailure is not null) { throw RetainFailure; }
        Retained = !AlreadyExited && !exitedDuringInspection;
        return Retained;
    }
    public string? ExecutablePath
    {
        get
        {
            Assert.True(Retained);
            if (ExitDuringIdentityInspection)
            {
                exitedDuringInspection = true;
                throw new InvalidOperationException("Process exited during module inspection.");
            }
            if (IdentityFailure is not null) { throw IdentityFailure; }
            return executablePath;
        }
    }
    public Task WaitForExitAsync(CancellationToken cancellationToken)
    {
        Assert.True(Retained);
        WaitStarted.TrySetResult();
        // Deliberately ignore cancellation to verify the caller's bounded wait.
        return WaitFailure is null ? Exit.Task : Task.FromException(WaitFailure);
    }
    public void Dispose() => Disposed = true;
}

internal sealed class ServiceExitProcesses(params ISetupProcess[] processes) : ISetupProcessOperations
{
    public IReadOnlyList<ISetupProcess> GetProcessesByName(string processName)
    {
        Assert.Equal("OpenBlade.Service", processName);
        return processes;
    }
    public bool TrySignalEvent(string eventName) => throw new InvalidOperationException("No process may be signaled.");
}

internal sealed class ServiceExitControl(SetupServiceState? state) : ISetupServiceControl
{
    internal Action? OnStop { get; init; }
    internal int Stops { get; private set; }
    internal int StoppedWaits { get; private set; }
    internal bool Deleted { get; private set; }
    public Task<bool> ExistsAsync(string serviceName, CancellationToken cancellationToken) => Task.FromResult(state is not null);
    public Task<SetupServiceState?> GetStateAsync(string serviceName, CancellationToken cancellationToken) => Task.FromResult(state);
    public Task RunAsync(IReadOnlyList<string> arguments, IReadOnlyCollection<int> toleratedExitCodes, CancellationToken cancellationToken)
    {
        Assert.Equal(OpenBladeWindowsService.Name, arguments[1]);
        if (arguments[0] == "stop") { OnStop?.Invoke(); Stops++; }
        else if (arguments[0] == "delete") { Deleted = true; }
        else { throw new InvalidOperationException("Unexpected service operation."); }
        return Task.CompletedTask;
    }
    public Task WaitForStateAsync(string serviceName, SetupServiceState target, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Assert.Equal(SetupServiceState.Stopped, target);
        StoppedWaits++;
        return Task.CompletedTask;
    }
    public Task WaitForDeletionAsync(string serviceName, TimeSpan timeout, CancellationToken cancellationToken) => Task.CompletedTask;
}
