using OpenBlade.Protocol;

namespace OpenBlade.Capture;

public sealed class BatteryProbeCommand
{
    public const int SuccessExitCode = 0;
    public const int UsageExitCode = 1;
    public const int ProbeFailureExitCode = 2;

    private readonly Func<BatteryValidationProbeResult> probe;
    private readonly TextWriter output;

    public BatteryProbeCommand(
        Func<BatteryValidationProbeResult> probe,
        TextWriter output)
    {
        this.probe = probe ?? throw new ArgumentNullException(nameof(probe));
        this.output = output ?? throw new ArgumentNullException(nameof(output));
    }

    public async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count != 0)
        {
            await output.WriteLineAsync("Usage: OpenSynapse.Capture probe-battery")
                .ConfigureAwait(false);
            return UsageExitCode;
        }

        cancellationToken.ThrowIfCancellationRequested();
        BatteryValidationProbeResult result = probe();
        cancellationToken.ThrowIfCancellationRequested();
        await RenderAsync(result).ConfigureAwait(false);
        return result.Classification is
            BatteryValidationProbeClassification.Ready or
            BatteryValidationProbeClassification.ReadyWithAccessFallback
            ? SuccessExitCode
            : ProbeFailureExitCode;
    }

    private async Task RenderAsync(BatteryValidationProbeResult result)
    {
        await output.WriteLineAsync($"Stage={result.Stage}").ConfigureAwait(false);
        await output.WriteLineAsync($"Classification={result.Classification}").ConfigureAwait(false);
        await output.WriteLineAsync($"HostIdentityMatched={result.HostIdentityMatched}").ConfigureAwait(false);
        await output.WriteLineAsync($"ConflictCheckPassed={result.ConflictCheckPassed}").ConfigureAwait(false);
        await output.WriteLineAsync($"EnumeratedCandidateCount={result.EnumeratedCandidateCount}").ConfigureAwait(false);
        await output.WriteLineAsync($"CandidateOpenAttemptCount={result.CandidateOpenAttemptCount}").ConfigureAwait(false);
        await output.WriteLineAsync($"CandidateOpenSuccessCount={result.CandidateOpenSuccessCount}").ConfigureAwait(false);
        await output.WriteLineAsync(
                $"CandidateOpenWin32Error={result.CandidateOpenWin32Error?.ToString() ?? "None"}")
            .ConfigureAwait(false);
        await output.WriteLineAsync($"CandidateAccessFallbackCount={result.CandidateAccessFallbackCount}").ConfigureAwait(false);
        await output.WriteLineAsync($"IdentityReadCount={result.IdentityReadCount}").ConfigureAwait(false);
        await output.WriteLineAsync($"VendorMatchCount={result.VendorMatchCount}").ConfigureAwait(false);
        await output.WriteLineAsync($"ProductMatchCount={result.ProductMatchCount}").ConfigureAwait(false);
        await output.WriteLineAsync($"UsagePageMatchCount={result.UsagePageMatchCount}").ConfigureAwait(false);
        await output.WriteLineAsync($"UsageMatchCount={result.UsageMatchCount}").ConfigureAwait(false);
        await output.WriteLineAsync($"FeatureLengthMatchCount={result.FeatureLengthMatchCount}").ConfigureAwait(false);
        await output.WriteLineAsync($"ExactMatchCount={result.ExactMatchCount}").ConfigureAwait(false);
    }
}
