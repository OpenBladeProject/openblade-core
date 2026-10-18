using System.ComponentModel;
using System.Diagnostics;

namespace OpenBlade.Tray;

internal enum RazerDriverHelperParseResult
{
    None,
    Valid,
    Invalid,
}

internal sealed class RazerDriverInstallLauncher
{
    internal const string Argument = "--install-razer-keyboard-driver";
    private readonly Func<ProcessStartInfo, CancellationToken, Task<int>> runAsync;

    internal RazerDriverInstallLauncher()
        : this(RunProcessAsync)
    {
    }

    internal RazerDriverInstallLauncher(Func<ProcessStartInfo, CancellationToken, Task<int>> runAsync)
    {
        this.runAsync = runAsync ?? throw new ArgumentNullException(nameof(runAsync));
    }

    internal static RazerDriverHelperParseResult Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count == 2 && string.Equals(arguments[1], Argument, StringComparison.OrdinalIgnoreCase))
        {
            return RazerDriverHelperParseResult.Valid;
        }
        return arguments.Skip(1).Any(value => value.StartsWith(Argument, StringComparison.OrdinalIgnoreCase))
            ? RazerDriverHelperParseResult.Invalid
            : RazerDriverHelperParseResult.None;
    }

    internal static bool IsInstalledExecutable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }
        string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "OpenBlade", "OpenBlade.Tray.exe");
        return string.Equals(Path.GetFullPath(path), expected, StringComparison.OrdinalIgnoreCase);
    }

    internal static ProcessStartInfo CreateStartInfo(string executablePath)
    {
        if (!IsInstalledExecutable(executablePath))
        {
            throw new InvalidOperationException("Driver setup requires the installed OpenBlade application.");
        }
        return new ProcessStartInfo(executablePath)
        {
            Arguments = Argument,
            WorkingDirectory = Path.GetDirectoryName(executablePath)!,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
    }

    internal async Task<RazerDriverInstallOutcome> RequestAsync(string executablePath, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return RazerDriverInstallOutcome.Canceled;
        }
        bool requestStarted = false;
        try
        {
            ProcessStartInfo start = CreateStartInfo(executablePath);
            requestStarted = true;
            int exitCode = await runAsync(start, cancellationToken).ConfigureAwait(false);
            return Enum.IsDefined(typeof(RazerDriverInstallOutcome), exitCode)
                ? (RazerDriverInstallOutcome)exitCode
                : RazerDriverInstallOutcome.RecoveryRequired;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            return RazerDriverInstallOutcome.Canceled;
        }
        catch (OperationCanceledException)
        {
            // Once elevation begins, cancellation does not prove that PnP stopped.
            return RazerDriverInstallOutcome.RecoveryRequired;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException
            or IOException or UnauthorizedAccessException)
        {
            return requestStarted ? RazerDriverInstallOutcome.RecoveryRequired : RazerDriverInstallOutcome.Unavailable;
        }
    }

    private static async Task<int> RunProcessAsync(ProcessStartInfo start, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException("Driver setup did not start.");
        // Closing the Tray must not terminate a driver transaction.
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return process.ExitCode;
    }
}
