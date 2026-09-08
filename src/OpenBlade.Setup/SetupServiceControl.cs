namespace OpenBlade.Setup;

public enum SetupServiceState
{
    Stopped,
    Running,
}

public interface ISetupServiceControl
{
    Task<bool> ExistsAsync(string serviceName, CancellationToken cancellationToken);
    Task<SetupServiceState?> GetStateAsync(string serviceName, CancellationToken cancellationToken);
    Task RunAsync(
        IReadOnlyList<string> arguments,
        IReadOnlyCollection<int> toleratedExitCodes,
        CancellationToken cancellationToken);
    Task WaitForStateAsync(
        string serviceName,
        SetupServiceState state,
        TimeSpan timeout,
        CancellationToken cancellationToken);
    Task WaitForDeletionAsync(
        string serviceName,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

internal sealed class ScSetupServiceControl : ISetupServiceControl
{
    private const int ServiceDoesNotExist = 1060;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<ScProcessResult>> runProcess;

    public ScSetupServiceControl()
        : this(new ScCommandRunner().RunAsync)
    {
    }

    internal ScSetupServiceControl(
        Func<IReadOnlyList<string>, CancellationToken, Task<ScProcessResult>> runProcess)
    {
        this.runProcess = runProcess;
    }

    internal static string ExecutablePath { get; } = Path.Combine(
        Environment.SystemDirectory,
        "sc.exe");

    public async Task<bool> ExistsAsync(string serviceName, CancellationToken cancellationToken)
    {
        ScProcessResult result = await runProcess(["query", serviceName], cancellationToken);
        return result.ExitCode switch
        {
            0 => true,
            ServiceDoesNotExist => false,
            _ => throw CreateFailure(result),
        };
    }

    public async Task<SetupServiceState?> GetStateAsync(
        string serviceName,
        CancellationToken cancellationToken)
    {
        ScProcessResult result = await runProcess(["query", serviceName], cancellationToken);
        if (result.ExitCode == ServiceDoesNotExist)
        {
            return null;
        }

        if (result.ExitCode != 0)
        {
            throw CreateFailure(result);
        }

        if (result.Output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase))
        {
            return SetupServiceState.Running;
        }

        if (result.Output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase))
        {
            return SetupServiceState.Stopped;
        }

        throw new InvalidOperationException(
            $"Service '{serviceName}' is in a transitional state; retry setup after it settles.");
    }

    public async Task RunAsync(
        IReadOnlyList<string> arguments,
        IReadOnlyCollection<int> toleratedExitCodes,
        CancellationToken cancellationToken)
    {
        ScProcessResult result = await runProcess(arguments, cancellationToken);
        if (result.ExitCode != 0 && !toleratedExitCodes.Contains(result.ExitCode))
        {
            throw CreateFailure(result);
        }
    }

    public async Task WaitForStateAsync(
        string serviceName,
        SetupServiceState state,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        string expected = state == SetupServiceState.Running ? "RUNNING" : "STOPPED";
        await PollAsync(
            serviceName,
            result => result.ExitCode == 0
                ? result.Output.Contains(expected, StringComparison.OrdinalIgnoreCase)
                : throw CreateFailure(result),
            timeout,
            $"Service '{serviceName}' did not reach {state} within {timeout}.",
            cancellationToken);
    }

    public async Task WaitForDeletionAsync(
        string serviceName,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        await PollAsync(
            serviceName,
            result => result.ExitCode switch
            {
                ServiceDoesNotExist => true,
                0 => false,
                _ => throw CreateFailure(result),
            },
            timeout,
            $"Service '{serviceName}' was not deleted within {timeout}.",
            cancellationToken);
    }

    private async Task PollAsync(
        string serviceName,
        Func<ScProcessResult, bool> isComplete,
        TimeSpan timeout,
        string timeoutMessage,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                ScProcessResult result = await runProcess(["query", serviceName], deadline.Token);
                deadline.Token.ThrowIfCancellationRequested();
                if (isComplete(result))
                {
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(200), deadline.Token);
            }
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TimeoutException(timeoutMessage, exception);
        }
    }

    private static InvalidOperationException CreateFailure(ScProcessResult result) =>
        new($"Service Control failed ({result.ExitCode}): {result.Error}{result.Output}".Trim());
}
