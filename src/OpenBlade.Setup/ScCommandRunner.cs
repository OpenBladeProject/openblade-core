using System.ComponentModel;
using System.Diagnostics;

namespace OpenBlade.Setup;

internal sealed record ScProcessResult(int ExitCode, string Output, string Error);

internal interface IScCommandProcess : IDisposable
{
    int ExitCode { get; }
    bool HasExited { get; }
    Task<string> ReadOutputAsync(CancellationToken cancellationToken);
    Task<string> ReadErrorAsync(CancellationToken cancellationToken);
    Task WaitForExitAsync(CancellationToken cancellationToken);
    void Kill();
}

internal sealed class ScCommandRunner
{
    internal static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);
    private readonly Func<ProcessStartInfo, IScCommandProcess> startProcess;
    private readonly TimeSpan commandTimeout;

    public ScCommandRunner()
        : this(static info => new ScCommandProcess(
            Process.Start(info) ?? throw new InvalidOperationException("Could not start Windows Service Control.")),
            CommandTimeout)
    {
    }

    internal ScCommandRunner(
        Func<ProcessStartInfo, IScCommandProcess> startProcess,
        TimeSpan commandTimeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(commandTimeout, TimeSpan.Zero);
        this.startProcess = startProcess;
        this.commandTimeout = commandTimeout;
    }

    internal async Task<ScProcessResult> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ProcessStartInfo startInfo = new(ScSetupServiceControl.ExecutablePath)
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

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(commandTimeout);
        using IScCommandProcess process = startProcess(startInfo);
        Task<string> error = Task.FromResult(string.Empty);
        Task<string> output = Task.FromResult(string.Empty);
        try
        {
            error = process.ReadErrorAsync(deadline.Token);
            output = process.ReadOutputAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            string[] streams = await Task.WhenAll(output, error).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            return new(process.ExitCode, streams[0], streams[1]);
        }
        catch (OperationCanceledException exception) when (deadline.IsCancellationRequested)
        {
            await StopOwnedProcessAsync(process, output, error).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(
                    "Windows Service Control was canceled; its service operation may already have been submitted.",
                    exception,
                    cancellationToken);
            }

            throw new TimeoutException(
                $"Windows Service Control exceeded {commandTimeout}; its service operation may already have been submitted.",
                exception);
        }
        catch
        {
            deadline.Cancel();
            await StopOwnedProcessAsync(process, output, error).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task StopOwnedProcessAsync(
        IScCommandProcess process,
        Task<string> output,
        Task<string> error)
    {
        // Terminate only this sc.exe child, never the service it may have signaled.
        // Killing the client is not evidence that the SCM operation was rolled back.
        using CancellationTokenSource cleanup = new(CleanupTimeout);
        try
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill();
                }
                catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
                {
                    if (!process.HasExited)
                    {
                        throw;
                    }
                }
            }

            await process.WaitForExitAsync(cleanup.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or OperationCanceledException)
        {
            throw new IOException("The timed-out Windows Service Control child could not be confirmed stopped.", exception);
        }
        finally
        {
            try
            {
                await Task.WhenAll(output, error).WaitAsync(cleanup.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // The original timeout/cancellation remains the operation outcome.
            }
        }
    }

    private sealed class ScCommandProcess(Process process) : IScCommandProcess
    {
        public int ExitCode => process.ExitCode;
        public bool HasExited => process.HasExited;
        public Task<string> ReadOutputAsync(CancellationToken cancellationToken) =>
            process.StandardOutput.ReadToEndAsync(cancellationToken);
        public Task<string> ReadErrorAsync(CancellationToken cancellationToken) =>
            process.StandardError.ReadToEndAsync(cancellationToken);
        public Task WaitForExitAsync(CancellationToken cancellationToken) => process.WaitForExitAsync(cancellationToken);
        public void Kill() => process.Kill();
        public void Dispose() => process.Dispose();
    }
}
