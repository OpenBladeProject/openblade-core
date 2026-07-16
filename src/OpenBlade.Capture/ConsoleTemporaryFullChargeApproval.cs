using OpenBlade.Protocol;

namespace OpenBlade.Capture;

public sealed class ConsoleTemporaryFullChargeApproval(
    TextReader input,
    TextWriter output) : ITemporaryFullChargeApproval
{
    private readonly TextReader input = input ?? throw new ArgumentNullException(nameof(input));
    private readonly TextWriter output = output ?? throw new ArgumentNullException(nameof(output));

    public async Task<bool> ConfirmAsync(
        BatteryValidationSetting baselineProtection,
        bool baselineTemporaryFullCharge,
        CancellationToken cancellationToken)
    {
        await output.WriteLineAsync($"Baseline protection: {baselineProtection}")
            .ConfigureAwait(false);
        await output.WriteLineAsync(
            $"Baseline temporary full charge: {baselineTemporaryFullCharge}")
            .ConfigureAwait(false);
        await output.WriteLineAsync(
            "Type YES to enable temporary full charge once, read it back, cancel it, and verify restoration:")
            .ConfigureAwait(false);

        string? response = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        return string.Equals(response, "YES", StringComparison.Ordinal);
    }
}
