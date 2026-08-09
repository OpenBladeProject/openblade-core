using System.ComponentModel;
using System.Diagnostics;

namespace OpenBlade.Tray;

public enum ElevatedServiceShutdownOutcome
{
    Completed,
    Canceled,
    Indeterminate,
}

public sealed class ElevatedServiceShutdownLauncher
{
    public const string HelperArgument = "--shutdown-service";

    private const int OperationCanceledError = 1223;
    private readonly Func<ProcessStartInfo, CancellationToken, Task<int>> runAsync;

    public ElevatedServiceShutdownLauncher()
        : this(RunProcessAsync)
    {
    }

    public ElevatedServiceShutdownLauncher(
        Func<ProcessStartInfo, CancellationToken, Task<int>> runAsync)
    {
        this.runAsync = runAsync ?? throw new ArgumentNullException(nameof(runAsync));
    }

    public static bool IsHelperLaunch(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return arguments
            .Skip(1)
            .Contains(HelperArgument, StringComparer.OrdinalIgnoreCase);
    }

    public static ProcessStartInfo CreateStartInfo(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        string workingDirectory = Path.GetDirectoryName(executablePath)
            ?? throw new ArgumentException(
                "The tray executable path must include a directory.",
                nameof(executablePath));
        return new ProcessStartInfo(executablePath)
        {
            Arguments = HelperArgument,
            WorkingDirectory = workingDirectory,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
    }

    public async Task<ElevatedServiceShutdownOutcome> RequestAsync(
        string executablePath,
        CancellationToken cancellationToken)
    {
        try
        {
            int exitCode = await runAsync(
                    CreateStartInfo(executablePath),
                    cancellationToken)
                .ConfigureAwait(false);
            if (exitCode != 0)
            {
                return ElevatedServiceShutdownOutcome.Indeterminate;
            }

            return ElevatedServiceShutdownOutcome.Completed;
        }
        catch (Win32Exception exception)
            when (exception.NativeErrorCode == OperationCanceledError)
        {
            return ElevatedServiceShutdownOutcome.Canceled;
        }
    }

    private static async Task<int> RunProcessAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken)
    {
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Windows did not start the elevated OpenBlade shutdown helper.");
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return process.ExitCode;
    }
}
