using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBlade.Session;

namespace OpenBlade.SessionAgent;

internal sealed class ThxAudioHelperCommand(
    Func<IThxAudioNativeAdapter> createNative,
    IThxAudioAdmissionSource admission,
    TextReader input,
    TextWriter output)
{
    private const string PresetGroupId = "profile_presets";
    private const string SpatialPropertyId = "spatial_enabled";
    private const string EqualizerPropertyId = "eq_enabled";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    internal async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        ThxAudioHelperResponse response;
        try
        {
            string? line = await ThxAudioHelperProtocol.ReadBoundedLineAsync(
                input,
                cancellationToken).ConfigureAwait(false);
            if (line is null || line.Length == 0)
            {
                throw new InvalidDataException("The THX helper request is invalid.");
            }
            ThxAudioHelperRequest request = JsonSerializer.Deserialize<ThxAudioHelperRequest>(
                line,
                JsonOptions) ?? throw new InvalidDataException("The THX helper request is missing.");
            Validate(request);
            response = Execute(request);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            response = new ThxAudioHelperResponse(
                ThxAudioHelperProtocol.Version,
                ThxAudioOperationStatus.Failed,
                ThxAudioAvailability.ComponentsUnavailable,
                $"THX helper failed: {exception.GetType().Name}.",
                null);
        }

        string json = JsonSerializer.Serialize(response, JsonOptions);
        await output.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        // Any fully encoded typed response is a successful helper protocol
        // exchange. Nonzero exit codes are reserved for process-level failure.
        return 0;
    }

    private ThxAudioHelperResponse Execute(ThxAudioHelperRequest request)
    {
        ThxAudioAdmission current = admission.Check();
        if (!current.IsAvailable)
        {
            return Unavailable(
                current.Availability,
                current.Error ?? "THX audio is unavailable.");
        }

        using IThxAudioNativeAdapter native = createNative();
        string device = JsonSerializer.Serialize(
            new
            {
                productId = WindowsThxAudioAdmissionSource.ProductId,
                endpointId = request.Endpoint == ThxAudioEndpoint.Speakers
                    ? "speakers"
                    : "headphones",
            });
        bool added = false;
        try
        {
            if (!native.AddDevice(device))
            {
                return Failed("The THX device could not be registered.");
            }
            added = true;
            if (!native.CheckStatus(device))
            {
                return Failed("The THX service is not ready.");
            }

            bool mutationAccepted = request switch
            {
                ThxHelperReadRequest => true,
                ThxHelperSetPropertyRequest value => native.SetProperty(
                    device,
                    value.PropertyId,
                    value.Value ? "true" : "false",
                    null),
                ThxHelperSelectPresetRequest value => SelectPreset(
                    native,
                    device,
                    value.PresetId),
                _ => false,
            };
            if (!mutationAccepted)
            {
                return request is ThxHelperReadRequest
                    ? Failed("The THX request is unsupported.")
                    : Indeterminate("The THX mutation was not acknowledged.");
            }

            ThxAudioState state = ReadState(native, device, request.Endpoint);
            if (!MatchesRequestedMutation(request, state))
            {
                return Indeterminate("The THX mutation could not be confirmed by readback.");
            }
            return new ThxAudioHelperResponse(
                ThxAudioHelperProtocol.Version,
                ThxAudioOperationStatus.Succeeded,
                ThxAudioAvailability.Available,
                null,
                state);
        }
        catch (UnknownPresetException)
        {
            return Failed("The preset ID was not returned by the current THX device.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return request is ThxHelperReadRequest
                ? Failed($"THX state could not be read: {exception.GetType().Name}.")
                : Indeterminate(
                    $"THX mutation readback failed: {exception.GetType().Name}.");
        }
        finally
        {
            if (added)
            {
                try
                {
                    _ = native.RemoveDevice(device);
                }
                catch
                {
                    // The helper is one-shot and exits immediately. Cleanup
                    // failure must not trigger a second mutation or obscure
                    // an already-indeterminate write/readback result.
                }
            }
        }
    }

    private static ThxAudioState ReadState(
        IThxAudioNativeAdapter native,
        string device,
        ThxAudioEndpoint endpoint)
    {
        bool spatial = ThxVendorJson.ParseBoolean(
            native.GetProperty(device, SpatialPropertyId));
        bool equalizer = ThxVendorJson.ParseBoolean(
            native.GetProperty(device, EqualizerPropertyId));
        IReadOnlyList<ThxAudioPreset> presets = ThxVendorJson.ParsePresetList(
            native.GetPresetList(device, PresetGroupId));
        string? selected = ThxVendorJson.ParseSelectedPreset(
            native.GetSelectedPreset(device, PresetGroupId));
        if (selected is not null
            && !presets.Any(preset => string.Equals(
                preset.Id,
                selected,
                StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                "The selected THX preset is not present in the vendor preset list.");
        }
        return new ThxAudioState(endpoint, spatial, equalizer, selected, presets);
    }

    private static bool SelectPreset(
        IThxAudioNativeAdapter native,
        string device,
        string presetId)
    {
        IReadOnlyList<ThxAudioPreset> presets = ThxVendorJson.ParsePresetList(
            native.GetPresetList(device, PresetGroupId));
        if (!presets.Any(preset => string.Equals(
                preset.Id,
                presetId,
                StringComparison.Ordinal)))
        {
            throw new UnknownPresetException();
        }
        return native.SelectPreset(device, PresetGroupId, presetId);
    }

    private static bool MatchesRequestedMutation(
        ThxAudioHelperRequest request,
        ThxAudioState state) => request switch
        {
            ThxHelperReadRequest => true,
            ThxHelperSetPropertyRequest value
                when value.PropertyId == SpatialPropertyId =>
                    state.SpatialEnabled == value.Value,
            ThxHelperSetPropertyRequest value
                when value.PropertyId == EqualizerPropertyId =>
                    state.EqualizerEnabled == value.Value,
            ThxHelperSelectPresetRequest value => string.Equals(
                state.SelectedPresetId,
                value.PresetId,
                StringComparison.Ordinal),
            _ => false,
        };

    private static void Validate(ThxAudioHelperRequest request)
    {
        if (request.Version != ThxAudioHelperProtocol.Version
            || !Enum.IsDefined(request.Endpoint)
            || request is ThxHelperSetPropertyRequest property
                && property.PropertyId is not (SpatialPropertyId or EqualizerPropertyId)
            || request is ThxHelperSelectPresetRequest preset
                && (string.IsNullOrWhiteSpace(preset.PresetId)
                    || preset.PresetId.Length > 256
                    || !string.Equals(
                        preset.PresetId,
                        preset.PresetId.Trim(),
                        StringComparison.Ordinal)
                    || preset.PresetId.Any(char.IsControl)))
        {
            throw new InvalidDataException("The THX helper request is invalid.");
        }
    }

    private static ThxAudioHelperResponse Failed(string error) => new(
        ThxAudioHelperProtocol.Version,
        ThxAudioOperationStatus.Failed,
        ThxAudioAvailability.Available,
        error,
        null);

    private static ThxAudioHelperResponse Unavailable(
        ThxAudioAvailability availability,
        string error) => new(
        ThxAudioHelperProtocol.Version,
        ThxAudioOperationStatus.Unavailable,
        availability,
        error,
        null);

    private static ThxAudioHelperResponse Indeterminate(string error) => new(
        ThxAudioHelperProtocol.Version,
        ThxAudioOperationStatus.Indeterminate,
        ThxAudioAvailability.Available,
        error,
        null);

    private sealed class UnknownPresetException : Exception
    {
    }
}
