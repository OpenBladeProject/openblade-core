using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenBlade.Capture;

internal sealed class HyperBoostRecoveryCommand
{
    internal const string RestoreConfirmation =
        "RZ09-0581:1532:02E0:4.00:HYPERBOOST-ACTIVE:RESTORE-BALANCED";
    internal const string InteractiveApproval =
        "RZ09-0581:CONFIRMED-HYPERBOOST:RESTORE-BALANCED-ONCE";
    internal const int UsageExitCode = 1;
    internal const int FailureExitCode = 2;
    internal const int ApprovalRejectedExitCode = 3;
    internal const int CanceledExitCode = 4;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly Func<CancellationToken, Task<HyperBoostRecoveryResult>> query;
    private readonly Func<CancellationToken, Task<HyperBoostRecoveryResult>> restore;
    private readonly TextReader input;
    private readonly TextWriter output;
    private readonly Func<Action, IDisposable> registerCancellation;

    internal HyperBoostRecoveryCommand(
        Func<CancellationToken, Task<HyperBoostRecoveryResult>> query,
        Func<CancellationToken, Task<HyperBoostRecoveryResult>> restore,
        TextReader input,
        TextWriter output,
        Func<Action, IDisposable>? registerCancellation = null)
    {
        this.query = query ?? throw new ArgumentNullException(nameof(query));
        this.restore = restore ?? throw new ArgumentNullException(nameof(restore));
        this.input = input ?? throw new ArgumentNullException(nameof(input));
        this.output = output ?? throw new ArgumentNullException(nameof(output));
        this.registerCancellation = registerCancellation ?? ConsoleCancellation.Register;
    }

    internal async Task<int> RunQueryAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count != 0)
        {
            await output.WriteLineAsync(
                "Usage: OpenBlade.Capture " +
                "query-rz09-0581-02e0-bios-4.00-hyperboost-recovery")
                .ConfigureAwait(false);
            return UsageExitCode;
        }

        return await RunCoreAsync(query, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<int> RunRestoreAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count != 2
            || !arguments[0].Equals("--confirm-target", StringComparison.Ordinal)
            || !arguments[1].Equals(RestoreConfirmation, StringComparison.Ordinal))
        {
            await output.WriteLineAsync(
                "Usage: OpenBlade.Capture " +
                "restore-rz09-0581-02e0-bios-4.00-hyperboost-balanced " +
                "--confirm-target " + RestoreConfirmation).ConfigureAwait(false);
            return UsageExitCode;
        }

        using CancellationTokenSource approvalCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using IDisposable registration = registerCancellation(
            () => ConsoleCancellation.Cancel(approvalCancellation));
        try
        {
            await output.WriteLineAsync(
                "This command writes only when a fresh paired query confirms HyperBoost. " +
                "It submits the captured Balanced restoration once and never retries.")
                .ConfigureAwait(false);
            await output.WriteLineAsync("Type this exact phrase:").ConfigureAwait(false);
            await output.WriteLineAsync(InteractiveApproval).ConfigureAwait(false);
            string? approval = await input.ReadLineAsync(approvalCancellation.Token)
                .ConfigureAwait(false);
            if (!string.Equals(approval, InteractiveApproval, StringComparison.Ordinal))
            {
                await output.WriteLineAsync(
                    "{\"interactiveApprovalConfirmed\":false}").ConfigureAwait(false);
                return ApprovalRejectedExitCode;
            }

            return await RunCoreAsync(restore, approvalCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (approvalCancellation.IsCancellationRequested)
        {
            await output.WriteLineAsync("{\"canceled\":true}").ConfigureAwait(false);
            return CanceledExitCode;
        }
    }

    private async Task<int> RunCoreAsync(
        Func<CancellationToken, Task<HyperBoostRecoveryResult>> operation,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource commandCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using IDisposable registration = registerCancellation(
            () => ConsoleCancellation.Cancel(commandCancellation));
        try
        {
            HyperBoostRecoveryResult result =
                await operation(commandCancellation.Token).ConfigureAwait(false);
            await output.WriteLineAsync(JsonSerializer.Serialize(
                new CaptureDocument(
                    SchemaVersion: 1,
                    ProductionAdmitted: false,
                    result),
                JsonOptions)).ConfigureAwait(false);
            return result.Success
                ? 0
                : result.Failure == HyperBoostRecoveryFailure.Canceled
                    ? CanceledExitCode
                    : FailureExitCode;
        }
        catch (OperationCanceledException) when (commandCancellation.IsCancellationRequested)
        {
            await output.WriteLineAsync("{\"canceled\":true}").ConfigureAwait(false);
            return CanceledExitCode;
        }
    }

    private sealed record CaptureDocument(
        int SchemaVersion,
        bool ProductionAdmitted,
        HyperBoostRecoveryResult Result);
}
