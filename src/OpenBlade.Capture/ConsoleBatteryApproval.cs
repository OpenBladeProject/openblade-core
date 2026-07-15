using OpenBlade.Protocol;

namespace OpenBlade.Capture;

public sealed class ConsoleBatteryApproval(TextReader input, TextWriter output) : IInteractiveApproval
{
    private readonly TextReader _input = input ?? throw new ArgumentNullException(nameof(input));
    private readonly TextWriter _output = output ?? throw new ArgumentNullException(nameof(output));

    public async Task<bool> ConfirmAsync(
        BatteryValidationSetting prior,
        BatteryValidationSetting candidate,
        CancellationToken cancellationToken)
    {
        await _output.WriteLineAsync($"Prior: {prior}").ConfigureAwait(false);
        await _output.WriteLineAsync($"Candidate: {candidate}").ConfigureAwait(false);
        await _output.WriteLineAsync(
            "Type YES to apply the candidate once, read it back, and restore the prior value:")
            .ConfigureAwait(false);

        string? response = await _input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        return string.Equals(response, "YES", StringComparison.Ordinal);
    }
}
