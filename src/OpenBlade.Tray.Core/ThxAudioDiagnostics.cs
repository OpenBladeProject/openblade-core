using System.Text.Json.Serialization;
using OpenBlade.Contracts;
using OpenBlade.Session;

namespace OpenBlade.Tray;

public enum ThxAudioDiagnosticCaptureStatus
{
    Succeeded,
    Failed,
}

public sealed record ThxAudioDiagnosticReport(
    ThxAudioDiagnosticCaptureStatus CaptureStatus,
    ThxAudioOperationStatus? OperationStatus,
    ThxAudioAvailability? Availability,
    string? Error)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ThxBaseAudioDiagnosticProof? BaseAudio { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DiagnosticFailure? Failure { get; init; }
}

public interface IThxAudioDiagnosticProvider
{
    Task<ThxAudioDiagnosticReport> CaptureAsync(CancellationToken cancellationToken);
}

public sealed class ThxAudioDiagnosticProvider(IThxAudioAdapter adapter)
    : IThxAudioDiagnosticProvider
{
    private const int MaximumErrorCharacters = 1024;
    private readonly IThxAudioAdapter adapter = adapter
        ?? throw new ArgumentNullException(nameof(adapter));

    public async Task<ThxAudioDiagnosticReport> CaptureAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            SessionAudioResponse response = await adapter
                .DiscoverAsync(cancellationToken)
                .ConfigureAwait(false);
            return new ThxAudioDiagnosticReport(
                ThxAudioDiagnosticCaptureStatus.Succeeded,
                response.Status,
                response.Availability,
                SanitizeError(response.Error))
            {
                BaseAudio = response.BaseAudio is { } proof
                    ? proof with { Detail = SanitizeError(proof.Detail) ?? "No base-audio explanation was returned." }
                    : null,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            return new ThxAudioDiagnosticReport(
                ThxAudioDiagnosticCaptureStatus.Failed,
                null,
                null,
                "The Session Agent THX admission query timed out.")
            {
                Failure = DiagnosticFailure.FromException("THX", "Diagnostics", "Discovery", "TimedOut", exception),
            };
        }
        catch (Exception exception)
        {
            return Failure(exception);
        }
    }

    internal static ThxAudioDiagnosticReport Failure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return new ThxAudioDiagnosticReport(
            ThxAudioDiagnosticCaptureStatus.Failed,
            null,
            null,
            $"The Session Agent THX admission query failed: {exception.GetType().Name}.")
        {
            Failure = DiagnosticFailure.FromException("THX", "Diagnostics", "Discovery", "CaptureFailed", exception),
        };
    }

    private static string? SanitizeError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return null;
        }

        string singleLine = UserModeDiagnosticTextRedactor.RedactDefault(error)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        return singleLine.Length <= MaximumErrorCharacters
            ? singleLine
            : singleLine[..MaximumErrorCharacters];
    }
}
