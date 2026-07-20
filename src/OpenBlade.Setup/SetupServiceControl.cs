using System.Diagnostics;

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
    internal static string ExecutablePath { get; } = Path.Combine(
        Environment.SystemDirectory,
        "sc.exe");

    public async Task<bool> ExistsAsync(string serviceName, CancellationToken cancellationToken)
    {
        ProcessResult result = await RunProcessAsync(["query", serviceName], cancellationToken);
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
        ProcessResult result = await RunProcessAsync(["query", serviceName], cancellationToken);
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
        ProcessResult result = await RunProcessAsync(arguments, cancellationToken);
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
        long started = Stopwatch.GetTimestamp();
        string expected = state == SetupServiceState.Running ? "RUNNING" : "STOPPED";
        while (Stopwatch.GetElapsedTime(started) < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProcessResult result = await RunProcessAsync(["query", serviceName], cancellationToken);
            if (result.ExitCode != 0)
            {
                throw CreateFailure(result);
            }

            if (result.Output.Contains(expected, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
        }

        throw new TimeoutException($"Service '{serviceName}' did not reach {state} within {timeout}.");
    }

    public async Task WaitForDeletionAsync(
        string serviceName,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        long started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started) < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProcessResult result = await RunProcessAsync(["query", serviceName], cancellationToken);
            if (result.ExitCode == ServiceDoesNotExist)
            {
                return;
            }

            if (result.ExitCode != 0)
            {
                throw CreateFailure(result);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
        }

        throw new TimeoutException($"Service '{serviceName}' was not deleted within {timeout}.");
    }

    private static async Task<ProcessResult> RunProcessAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new(ExecutablePath)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start Windows Service Control.");
        Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new(process.ExitCode, await output, await error);
    }

    private static InvalidOperationException CreateFailure(ProcessResult result) =>
        new($"Service Control failed ({result.ExitCode}): {result.Error}{result.Output}".Trim());

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}
