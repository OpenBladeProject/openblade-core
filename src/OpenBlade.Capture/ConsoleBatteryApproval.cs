using OpenBlade.Protocol;

namespace OpenBlade.Capture;

public sealed class ConsoleBatteryApproval(TextReader input, TextWriter output) : IInteractiveApproval
{
    private readonly TextReader inputReader = input ?? throw new ArgumentNullException(nameof(input));
    private readonly TextWriter outputWriter = output ?? throw new ArgumentNullException(nameof(output));

    public async Task<bool> ConfirmAsync(
        BatteryValidationSetting prior,
        BatteryValidationSetting candidate,
        CancellationToken cancellationToken)
    {
        await outputWriter.WriteLineAsync($"Prior: {prior}").ConfigureAwait(false);
        await outputWriter.WriteLineAsync($"Candidate: {candidate}").ConfigureAwait(false);
        await outputWriter.WriteLineAsync(
            "Type YES to apply the candidate once, read it back, and restore the prior value:")
            .ConfigureAwait(false);

        string? response = await inputReader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        return string.Equals(response, "YES", StringComparison.Ordinal);
    }
}
