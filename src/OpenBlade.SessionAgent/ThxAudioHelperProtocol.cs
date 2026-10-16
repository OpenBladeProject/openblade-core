using System.Text;
using System.Text.Json.Serialization;
using OpenBlade.Session;

namespace OpenBlade.SessionAgent;

internal static class ThxAudioHelperProtocol
{
    internal const int Version = 1;
    internal const int MaximumLineCharacters = 64 * 1024;
    internal const string CommandSwitch = "--thx-audio-helper";
    private const string PipeNamePrefix = "OpenBlade.Thx.Helper.v1";

    internal static string CreatePipeName(int parentProcessId, Guid nonce) =>
        $"{PipeNamePrefix}.{parentProcessId}.{nonce:N}";

    internal static bool IsExpectedPipeName(string value, int parentProcessId)
    {
        string prefix = $"{PipeNamePrefix}.{parentProcessId}.";
        return value.StartsWith(prefix, StringComparison.Ordinal)
            && value.Length == prefix.Length + 32
            && Guid.TryParseExact(value[prefix.Length..], "N", out _);
    }

    internal static async Task<string?> ReadBoundedLineAsync(
        TextReader reader,
        CancellationToken cancellationToken)
    {
        var value = new StringBuilder();
        char[] single = new char[1];
        while (true)
        {
            int read = await reader.ReadAsync(single.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                return value.Length == 0 ? null : value.ToString();
            }
            if (single[0] == '\n')
            {
                return value.ToString().TrimEnd('\r');
            }
            if (value.Length == MaximumLineCharacters)
            {
                throw new InvalidDataException("The THX helper message exceeds 64 KiB.");
            }
            value.Append(single[0]);
        }
    }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "operation")]
[JsonDerivedType(typeof(ThxHelperReadRequest), "read")]
[JsonDerivedType(typeof(ThxHelperSetPropertyRequest), "setProperty")]
[JsonDerivedType(typeof(ThxHelperSelectPresetRequest), "selectPreset")]
internal abstract record ThxAudioHelperRequest(int Version, ThxAudioEndpoint Endpoint);

internal sealed record ThxHelperReadRequest(int Version, ThxAudioEndpoint Endpoint)
    : ThxAudioHelperRequest(Version, Endpoint);

internal sealed record ThxHelperSetPropertyRequest(
    int Version,
    ThxAudioEndpoint Endpoint,
    string PropertyId,
    bool Value)
    : ThxAudioHelperRequest(Version, Endpoint);

internal sealed record ThxHelperSelectPresetRequest(
    int Version,
    ThxAudioEndpoint Endpoint,
    string PresetId)
    : ThxAudioHelperRequest(Version, Endpoint);

internal sealed record ThxAudioHelperResponse(
    int Version,
    ThxAudioOperationStatus Status,
    ThxAudioAvailability Availability,
    string? Error,
    ThxAudioState? State);

internal sealed record ThxAudioWorkerResult(
    ThxAudioOperationStatus Status,
    ThxAudioAvailability Availability,
    string? Error,
    ThxAudioState? State);

internal interface IThxAudioWorker
{
    Task<ThxAudioWorkerResult> ExecuteAsync(
        ThxAudioHelperRequest request,
        CancellationToken cancellationToken);
}
