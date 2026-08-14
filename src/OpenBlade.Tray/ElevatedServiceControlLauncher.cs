using System.ComponentModel;
using System.Diagnostics;

namespace OpenBlade.Tray;

internal enum ElevatedServiceControlAction
{
    Start,
    Shutdown,
    StopRazerServices,
}

internal enum ElevatedServiceControlOutcome
{
    Completed,
    Canceled,
    Indeterminate,
}

internal enum ElevatedServiceHelperParseResult
{
    None,
    Valid,
    Invalid,
}

internal sealed class ElevatedServiceControlLauncher
{
    public const string StartHelperArgument = "--start-service";
    public const string ShutdownHelperArgument = "--shutdown-service";
    public const string StopRazerServicesHelperArgument = "--stop-razer-services";

    private const int OperationCanceledError = 1223;
    private readonly Func<ProcessStartInfo, CancellationToken, Task<int>> runAsync;

    public ElevatedServiceControlLauncher()
        : this(RunProcessAsync)
    {
    }

    public ElevatedServiceControlLauncher(
        Func<ProcessStartInfo, CancellationToken, Task<int>> runAsync)
    {
        this.runAsync = runAsync ?? throw new ArgumentNullException(nameof(runAsync));
    }

    public static ElevatedServiceHelperParseResult ParseHelperAction(
        IReadOnlyList<string> arguments,
        out ElevatedServiceControlAction action)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        action = default;
        if (arguments.Count == 2)
        {
            if (string.Equals(
                    arguments[1],
                    StartHelperArgument,
                    StringComparison.OrdinalIgnoreCase))
            {
                action = ElevatedServiceControlAction.Start;
                return ElevatedServiceHelperParseResult.Valid;
            }

            if (string.Equals(
                    arguments[1],
                    ShutdownHelperArgument,
                    StringComparison.OrdinalIgnoreCase))
            {
                action = ElevatedServiceControlAction.Shutdown;
                return ElevatedServiceHelperParseResult.Valid;
            }

            if (string.Equals(
                    arguments[1],
                    StopRazerServicesHelperArgument,
                    StringComparison.OrdinalIgnoreCase))
            {
                action = ElevatedServiceControlAction.StopRazerServices;
                return ElevatedServiceHelperParseResult.Valid;
            }
        }

        return arguments.Skip(1).Any(IsReservedHelperArgument)
            ? ElevatedServiceHelperParseResult.Invalid
            : ElevatedServiceHelperParseResult.None;
    }

    public static ProcessStartInfo CreateStartInfo(
        string executablePath,
        ElevatedServiceControlAction action)
    {
        // Production service control assumes the installer-owned tray under
        // Program Files. Portable or user-writable deployments are unsupported.
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        string workingDirectory = Path.GetDirectoryName(executablePath)
            ?? throw new ArgumentException(
                "The tray executable path must include a directory.",
                nameof(executablePath));
        return new ProcessStartInfo(executablePath)
        {
            Arguments = ArgumentFor(action),
            WorkingDirectory = workingDirectory,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
    }

    public async Task<ElevatedServiceControlOutcome> RequestAsync(
        string executablePath,
        ElevatedServiceControlAction action,
        CancellationToken cancellationToken)
    {
        try
        {
            int exitCode = await runAsync(
                    CreateStartInfo(executablePath, action),
                    cancellationToken)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return exitCode == 0
                ? ElevatedServiceControlOutcome.Completed
                : ElevatedServiceControlOutcome.Indeterminate;
        }
        catch (Win32Exception exception)
            when (exception.NativeErrorCode == OperationCanceledError)
        {
            return ElevatedServiceControlOutcome.Canceled;
        }
    }

    private static string ArgumentFor(ElevatedServiceControlAction action) => action switch
    {
        ElevatedServiceControlAction.Start => StartHelperArgument,
        ElevatedServiceControlAction.Shutdown => ShutdownHelperArgument,
        ElevatedServiceControlAction.StopRazerServices => StopRazerServicesHelperArgument,
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    private static bool IsReservedHelperArgument(string argument) =>
        argument.StartsWith(StartHelperArgument, StringComparison.OrdinalIgnoreCase)
        || argument.StartsWith(ShutdownHelperArgument, StringComparison.OrdinalIgnoreCase)
        || argument.StartsWith(
            StopRazerServicesHelperArgument,
            StringComparison.OrdinalIgnoreCase);

    private static async Task<int> RunProcessAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken)
    {
        using Process process = await Task.Run(
                () => Process.Start(startInfo)
                    ?? throw new InvalidOperationException(
                        "Windows did not start the elevated OpenBlade service helper."),
                cancellationToken)
            .ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return process.ExitCode;
    }
}
