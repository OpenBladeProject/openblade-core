using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBlade.Session;

namespace OpenBlade.SessionAgent;

internal sealed class ThxAudioHelperCommand(
    Func<IThxAudioNativeAdapter> createNative,
    IThxAudioAdmissionSource admission,
    TextReader input,
    TextWriter output,
    Func<string, ThxAudioEndpoint, bool?>? readSystemEffectsDisabled = null,
    Func<ILegacyThxAudioNativeAdapter>? createLegacyNative = null,
    Func<ThxAudioCompatibilityProfile, ThxAudioEndpoint, string?>? readLegacyOutput = null)
{
    private const string PresetGroupId = "profile_presets";
    private const string SpatialPropertyId = "spatial_enabled";
    private const string EqualizerPropertyId = "eq_enabled";
    private const string NormalizationEnabledPropertyId = "volume_leveling_enabled";
    private const string NormalizationLevelPropertyId = "volume_leveling_level";
    private const string VoiceClarityEnabledPropertyId = "voice_clarity_enabled";
    private const string VoiceClarityLevelPropertyId = "voice_clarity_level";
    private const string FloatPropertyType = "Float";

    private static readonly int[] NativeEqualizerFrequenciesHz =
        [31, 63, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];

    private static readonly ThxAudioEndpoint[] RequiredEndpointProbeOrder =
        [ThxAudioEndpoint.Speakers, ThxAudioEndpoint.Headphones];

    private static readonly IReadOnlyDictionary<string, string> LogicalToNativePreset =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["game"] = "game",
            ["movie"] = "movie",
            ["music"] = "music",
            ["voice"] = "podcast",
            ["custom"] = "custom",
            ["default"] = "default",
        };

    private static readonly IReadOnlyDictionary<string, string> PresetDisplayNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["game"] = "Game",
            ["movie"] = "Movie",
            ["music"] = "Music",
            ["voice"] = "Voice",
            ["custom"] = "Custom",
            ["default"] = "Default",
        };

    // Product 736 module 26061 DEFAULTPROFILE values. Its getDefaultProfile
    // factory is used by the vendor middleware for new and reset profiles.
    // Original cache SHA-256 91B8536142593F30F4AD9A53B7EEC701613C7B2E35D689479734976B65DFD9D0;
    // extracted source SHA-256 33E76F7E3FDEDD58740B2BB1CE1712E69D6E5366E4267C299F08A19DA7B20201,
    // factory profile at offsets 155900-157300 and factory clone at 161511-161900.
    private static readonly IReadOnlyDictionary<string, double[]> SpeakerFactoryCurves =
        new Dictionary<string, double[]>(StringComparer.Ordinal)
        {
            ["game"] = [1, 2, 4, 5, 1, -1, -4],
            ["movie"] = [3, -3, -4, 4, 5, 4, 2],
            ["music"] = [2, 0, 0, 3, 4, 3, 1],
            ["voice"] = [-4, 0, 3, 6, 5, 1, -6],
            ["custom"] = [0, 0, 0, 0, 0, 0, 0],
        };

    private static readonly IReadOnlyDictionary<string, double[]> HeadphoneFactoryCurves =
        new Dictionary<string, double[]>(StringComparer.Ordinal)
        {
            ["game"] = [-5, -5, -4, -3, -1, 2, 4, 4, 3, 2],
            ["movie"] = [4, 4, 3, -4, -5, -1, 3, 5, 3, 2],
            ["music"] = [1, 1, 1, 0, 0, 2, 3, 3, 2, 1],
            ["voice"] = [-12, -6, 4, 4, 4, 3, 0, 2, 4, -12],
            ["custom"] = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
        };

    // Exact product710 (0528) and709 (0530) DEFAULTPROFILE speaker seeds.
    // Main SHA256s: 201541BA7E521BB4B161CFCD077BC95EA2577BFFC37B3452D1CCBFB57A57F626
    // and CC8E0EFD2B37F1ED5AF6C8CC8C3AB1327B9502927DC05F20EE7465486AB006F1.
    // Their headphone seeds are byte-identical to product736; speaker seeds differ.
    private static readonly IReadOnlyDictionary<string, double[]> Rz090528SpeakerFactoryCurves =
        new Dictionary<string, double[]>(StringComparer.Ordinal)
        {
            ["game"] = [-1, 1, 3, 4, 0, -3, -6],
            ["movie"] = [2, -4, -5, 3, 4, 3, -1],
            ["music"] = [1, -1, -1, 2, 3, 2, 0],
            ["voice"] = [-5, -1, 2, 5, 4, 0, -7],
            ["custom"] = [0, 0, 0, 0, 0, 0, 0],
        };

    private static readonly IReadOnlyDictionary<string, double[]> Rz090530SpeakerFactoryCurves =
        new Dictionary<string, double[]>(StringComparer.Ordinal)
        {
            ["game"] = [-1, 2, 3, 4, 0, -2, -4],
            ["movie"] = [2, -4, -4, 3, 4, 3, 0],
            ["music"] = [2, -3, -1, 2, 3, 2, 1],
            ["voice"] = [-4, -1, 2, 5, 4, -1, -6],
            ["custom"] = [0, 0, 0, 0, 0, 0, 0],
        };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private IThxAudioNativeAdapter? native;
    private LegacyThxAudioCommand? legacy;
    private (string? ProductId, string? ModelNumber, bool RequiresEndpointProbe, string DriverStackIdentity)?
        registeredIdentity;
    private readonly Dictionary<ThxAudioEndpoint, string> registeredDevices = [];
    private string? probedStackIdentity;

    internal async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                ThxAudioHelperResponse response;
                try
                {
                    string? line = await ThxAudioHelperProtocol.ReadBoundedLineAsync(
                        input,
                        cancellationToken).ConfigureAwait(false);
                    if (line is null)
                    {
                        return 0;
                    }
                    if (line.Length == 0)
                    {
                        throw new InvalidDataException("The THX helper request is invalid.");
                    }
                    ThxAudioHelperRequest request = JsonSerializer.Deserialize<ThxAudioHelperRequest>(
                        line,
                        JsonOptions) ?? throw new InvalidDataException("The THX helper request is missing.");
                    Validate(request);
                    cancellationToken.ThrowIfCancellationRequested();
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
                if (response.Status != ThxAudioOperationStatus.Succeeded)
                {
                    // Do not reuse a connection after failed admission or an
                    // uncertain native result. The parent confirms our exit.
                    return 0;
                }
            }
        }
        finally
        {
            if (native is not null)
            {
                foreach (string device in registeredDevices.Values)
                {
                    try
                    {
                        _ = native.RemoveDevice(device);
                    }
                    catch
                    {
                        // Cleanup changes no setting and never retries a write.
                        // The parent bounds shutdown; the DLL stays process-pinned.
                    }
                }
                native.Dispose();
            }
        }
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

        ThxAudioCompatibilityProfile profile = ThxAudioCompatibility.Profiles.Single(value =>
            value.ProductId == current.ProductId && value.ModelNumber == current.ModelNumber);
        if (profile.Backend == ThxAudioNativeBackend.Legacy265)
        {
            // The legacy COM service has a different endpoint and setting contract.
            // It must never load/register a device through the V4 JSON client.
            legacy ??= new LegacyThxAudioCommand(
                createLegacyNative ?? (static () => new LegacyThxAudioNativeAdapter()),
                admission, readLegacyOutput ?? WindowsThxActiveOutput.ReadExactDefaultOutputId);
            // A pre-buffered first pipe read can complete synchronously on
            // Program.Main's STA thread. ConfigureAwait(false) alone does not
            // guarantee MTA; construct/use/release COM together on a pool thread.
            return Task.Run(() => legacy.Execute(request, current, profile)).GetAwaiter().GetResult();
        }

        if ((current.ProductId, current.ModelNumber) is not
            (("rznb12", "RZ09-0581") or ("rznb11", "RZ09-0528") or ("rznb11", "RZ09-0530")))
        {
            return Unavailable(ThxAudioAvailability.ComponentsUnavailable, "The THX product identity is unverified.");
        }
        if (request is not ThxHelperReadRequest && !current.MutationAllowed)
        {
            return Unavailable(ThxAudioAvailability.AvailableReadOnly, "This THX stack requires model-specific write validation.");
        }
        if (string.IsNullOrEmpty(current.DriverStackIdentity))
        {
            return Unavailable(
                ThxAudioAvailability.ComponentsUnavailable,
                "The THX driver stack identity is unavailable.");
        }
        if (request is ThxHelperSelectPresetRequest preset
            && preset.GainsDb.Count != (preset.Endpoint == ThxAudioEndpoint.Speakers
                && current.ModelNumber == "RZ09-0581" ? 7 : 10))
        {
            return Failed("The EQ vector does not match this model and endpoint.");
        }

        if (registeredIdentity is { } identity
            && identity != (
                current.ProductId,
                current.ModelNumber,
                current.RequiresEndpointProbe,
                current.DriverStackIdentity))
        {
            probedStackIdentity = null;
            return Unavailable(
                ThxAudioAvailability.ComponentsUnavailable,
                "The THX device or driver stack identity changed. Connect again before controlling audio.");
        }
        if (request is not ThxHelperReadRequest
            && current.RequiresEndpointProbe
            && !string.Equals(probedStackIdentity, current.DriverStackIdentity, StringComparison.Ordinal))
        {
            return Unavailable(
                ThxAudioAvailability.AvailableReadOnly,
                "Read both THX endpoints before controlling this driver stack.");
        }

        native ??= createNative();
        bool mayHaveSubmitted = false;
        try
        {
            if (request is ThxHelperReadRequest
                && current.RequiresEndpointProbe
                && !string.Equals(probedStackIdentity, current.DriverStackIdentity, StringComparison.Ordinal))
            {
                ThxAudioState? requestedState = null;
                foreach (ThxAudioEndpoint endpoint in RequiredEndpointProbeOrder)
                {
                    string? probeDevice = GetOrRegisterDevice(current, endpoint);
                    if (probeDevice is null)
                    {
                        return Failed("The THX device could not be registered.");
                    }
                    if (!native.CheckStatus(probeDevice))
                    {
                        return Failed("The THX service is not ready.");
                    }

                    ThxAudioState probeState = ReadState(native, probeDevice, endpoint,
                        current.ModelNumber, current.MutationAllowed);
                    if (endpoint == request.Endpoint)
                    {
                        requestedState = probeState;
                    }
                }

                probedStackIdentity = current.DriverStackIdentity;
                requestedState = (requestedState
                    ?? throw new InvalidDataException("The requested THX endpoint was not probed.")) with
                {
                    SystemEffectsDisabled = ReadSystemEffectsDisabled(
                        current.ProductId!, request.Endpoint),
                };
                return new ThxAudioHelperResponse(
                    ThxAudioHelperProtocol.Version,
                    ThxAudioOperationStatus.Succeeded,
                    current.MutationAllowed
                        ? ThxAudioAvailability.Available
                        : ThxAudioAvailability.AvailableReadOnly,
                    null,
                    requestedState);
            }

            string? device = GetOrRegisterDevice(current, request.Endpoint);
            if (device is null)
            {
                return Failed("The THX device could not be registered.");
            }
            if (!native.CheckStatus(device))
            {
                return Failed("The THX service is not ready.");
            }

            if (request is not ThxHelperReadRequest)
            {
                // Registration and service status are not write admission. Recheck the
                // exact host, controller state, bound package identity, and selected
                // typed endpoint immediately before the first possible mutation.
                ThxAudioAdmission fresh = admission.Check();
                if (!fresh.IsAvailable || !fresh.MutationAllowed
                    || fresh.ProductId != current.ProductId
                    || fresh.ModelNumber != current.ModelNumber
                    || !string.Equals(fresh.DriverStackIdentity, current.DriverStackIdentity,
                        StringComparison.Ordinal))
                {
                    return Unavailable(
                        fresh.Availability,
                        fresh.Error ?? "THX write admission changed.");
                }

                _ = ReadState(native, device, request.Endpoint, current.ModelNumber, current.MutationAllowed);
                mayHaveSubmitted = true;
            }

            bool mutationAccepted = request switch
            {
                ThxHelperReadRequest => true,
                ThxHelperSetPropertyRequest value => native.SetProperty(
                    device,
                    value.PropertyId,
                    value.Value ? "true" : "false",
                    null),
                ThxHelperSelectPresetRequest value => ApplyEqualizerPreset(
                    native,
                    device,
                    value),
                ThxHelperSetEnhancementRequest value => SetEnhancement(
                    native,
                    device,
                    value),
                ThxHelperApplyApplicationProfileRequest value => ApplyApplicationProfile(
                    native,
                    device,
                    value),
                _ => false,
            };
            if (!mutationAccepted)
            {
                return request is ThxHelperReadRequest
                    ? Failed("The THX request is unsupported.")
                    : Indeterminate("The THX mutation was not acknowledged.");
            }

            ThxAudioState state = ReadState(native, device, request.Endpoint, current.ModelNumber, current.MutationAllowed) with
            {
                // Advisory calls never run after a possible native write.
                SystemEffectsDisabled = request is ThxHelperReadRequest
                    ? ReadSystemEffectsDisabled(current.ProductId!, request.Endpoint) : null,
            };
            if (!MatchesRequestedMutation(request, state))
            {
                return Indeterminate("The THX mutation could not be confirmed by readback.");
            }
            return new ThxAudioHelperResponse(
                ThxAudioHelperProtocol.Version,
                ThxAudioOperationStatus.Succeeded,
                current.MutationAllowed ? ThxAudioAvailability.Available : ThxAudioAvailability.AvailableReadOnly,
                null,
                state);
        }
        catch (UnknownPresetException)
        {
            return Failed("The preset ID was not returned by the current THX device.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return request is ThxHelperReadRequest || !mayHaveSubmitted
                ? Failed($"THX state could not be read: {exception.GetType().Name}.")
                : Indeterminate(
                    $"THX mutation readback failed: {exception.GetType().Name}.");
        }
    }

    private string? GetOrRegisterDevice(
        ThxAudioAdmission current,
        ThxAudioEndpoint endpoint)
    {
        if (registeredDevices.TryGetValue(endpoint, out string? device))
        {
            return device;
        }

        device = JsonSerializer.Serialize(
            new
            {
                productId = current.ProductId,
                endpointId = endpoint == ThxAudioEndpoint.Speakers
                    ? "speakers"
                    : "headphones",
            });
        if (!native!.AddDevice(device))
        {
            return null;
        }

        // The pinned vendor client retains an IPC object per endpoint.
        // Register each of the two typed endpoints once, not per setting.
        registeredDevices.Add(endpoint, device);
        registeredIdentity = (
            current.ProductId,
            current.ModelNumber,
            current.RequiresEndpointProbe,
            current.DriverStackIdentity);
        return device;
    }

    private bool? ReadSystemEffectsDisabled(string productId, ThxAudioEndpoint endpoint)
    {
        try
        {
            return readSystemEffectsDisabled?.Invoke(productId, endpoint);
        }
        catch
        {
            // A diagnostic failure cannot change an independently confirmed mutation.
            return null;
        }
    }

    private static ThxAudioState ReadState(
        IThxAudioNativeAdapter native,
        string device,
        ThxAudioEndpoint endpoint,
        string modelNumber,
        bool mutationAllowed)
    {
        bool spatial = ThxVendorJson.ParseBoolean(
            native.GetProperty(device, SpatialPropertyId));
        bool equalizer = ThxVendorJson.ParseBoolean(
            native.GetProperty(device, EqualizerPropertyId));
        IReadOnlyList<string> vendorPresets = ThxVendorJson.ParsePresetIds(
            native.GetPresetList(device, PresetGroupId));
        string? selectedNative = ThxVendorJson.ParseSelectedPreset(
            native.GetSelectedPreset(device, PresetGroupId));
        if (selectedNative is not null
            && !vendorPresets.Contains(selectedNative, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                "The selected THX preset is not present in the vendor preset list.");
        }

        string? selected = selectedNative is null
            ? null
            : LogicalToNativePreset.FirstOrDefault(pair => string.Equals(
                pair.Value,
                selectedNative,
                StringComparison.Ordinal)).Key;
        IReadOnlyList<ThxAudioPreset> presets = LogicalToNativePreset
            .Where(pair => vendorPresets.Contains(pair.Value, StringComparer.Ordinal))
            .Select(pair => new ThxAudioPreset(pair.Key, PresetDisplayNames[pair.Key]))
            .ToArray();

        double[] nativeGains = ReadNativeGains(native, device);
        int firstVisibleBand = endpoint == ThxAudioEndpoint.Speakers && modelNumber == "RZ09-0581" ? 3 : 0;
        IReadOnlyList<ThxAudioEqualizerBand> visibleBands = nativeGains
            .Skip(firstVisibleBand)
            .Select((gain, index) => new ThxAudioEqualizerBand(
                NativeEqualizerFrequenciesHz[index + firstVisibleBand],
                gain))
            .ToArray();

        ThxAudioEnhancement? normalization = endpoint == ThxAudioEndpoint.Speakers
            ? ReadEnhancement(
                native,
                device,
                NormalizationEnabledPropertyId,
                NormalizationLevelPropertyId)
            : null;
        ThxAudioEnhancement voiceClarity = ReadEnhancement(
            native,
            device,
            VoiceClarityEnabledPropertyId,
            VoiceClarityLevelPropertyId);
        IReadOnlyList<ThxAudioApplicationProfile> applicationProfiles =
            ThxAudioApplicationProfileCatalog.AvailableProfiles(vendorPresets);

        return new ThxAudioState(endpoint, spatial, equalizer, selected, presets)
        {
            SelectedNativeProfileId = selectedNative,
            EqualizerBands = visibleBands,
            FactoryPresetCurves = mutationAllowed ? FactoryCurves(endpoint, modelNumber) : [],
            SoundNormalization = normalization,
            VoiceClarity = voiceClarity,
            ApplicationProfiles = !mutationAllowed ? ThxAudioFeatureAccess.ObservedOnly : applicationProfiles.Count == 0
                ? ThxAudioFeatureAccess.Unavailable
                : ThxAudioFeatureAccess.Available,
            ApplicationProfileCatalog = applicationProfiles,
        };
    }

    private static bool ApplyEqualizerPreset(
        IThxAudioNativeAdapter native,
        string device,
        ThxHelperSelectPresetRequest request)
    {
        if (!LogicalToNativePreset.TryGetValue(request.PresetId, out string? nativePresetId))
        {
            throw new UnknownPresetException();
        }
        IReadOnlyList<string> presets = ThxVendorJson.ParsePresetIds(
            native.GetPresetList(device, PresetGroupId));
        if (!presets.Contains(nativePresetId, StringComparer.Ordinal))
        {
            throw new UnknownPresetException();
        }

        string? selected = ThxVendorJson.ParseSelectedPreset(
            native.GetSelectedPreset(device, PresetGroupId));
        double[] requestedNativeGains = ToNativeGains(request.Endpoint, request.GainsDb);
        double[] currentNativeGains = ReadNativeGains(native, device);
        bool gainsMatch = currentNativeGains.Select((gain, index) =>
            Math.Abs(gain - requestedNativeGains[index]) <= 0.0001).All(match => match);
        if (string.Equals(selected, nativePresetId, StringComparison.Ordinal) && gainsMatch)
        {
            return true;
        }
        if (!string.Equals(selected, nativePresetId, StringComparison.Ordinal)
            && !native.SelectPreset(device, PresetGroupId, nativePresetId))
        {
            return false;
        }

        int index = 0;
        foreach (double gain in requestedNativeGains)
        {
            index++;
            if (!native.SetProperty(
                    device,
                    $"eq_band_{index}_gain_db",
                    JsonSerializer.Serialize(gain),
                    FloatPropertyType))
            {
                return false;
            }
        }
        return index == NativeEqualizerFrequenciesHz.Length;
    }

    private static bool ApplyApplicationProfile(
        IThxAudioNativeAdapter native,
        string device,
        ThxHelperApplyApplicationProfileRequest request)
    {
        if (!ThxAudioApplicationProfileCatalog.TryResolve(
                request.ExecutableName,
                request.Mode,
                out string nativePresetId))
        {
            throw new UnknownPresetException();
        }
        IReadOnlyList<string> presets = ThxVendorJson.ParsePresetIds(
            native.GetPresetList(device, PresetGroupId));
        if (!presets.Contains(nativePresetId, StringComparer.Ordinal))
        {
            throw new UnknownPresetException();
        }

        bool spatial = ThxVendorJson.ParseBoolean(
            native.GetProperty(device, SpatialPropertyId));
        if (!spatial
            && !native.SetProperty(device, SpatialPropertyId, "true", null))
        {
            return false;
        }

        string? selected = ThxVendorJson.ParseSelectedPreset(
            native.GetSelectedPreset(device, PresetGroupId));
        return string.Equals(selected, nativePresetId, StringComparison.Ordinal)
            || native.SelectPreset(device, PresetGroupId, nativePresetId);
    }

    private static double[] ReadNativeGains(
        IThxAudioNativeAdapter native,
        string device) => Enumerable.Range(1, NativeEqualizerFrequenciesHz.Length)
        .Select(index => ThxVendorJson.ParseFloat(
            native.GetProperty(device, $"eq_band_{index}_gain_db"),
            minimum: -12,
            maximum: 12))
        .ToArray();

    private static double[] ToNativeGains(
        ThxAudioEndpoint endpoint,
        IReadOnlyList<double> visibleGains) => endpoint == ThxAudioEndpoint.Speakers && visibleGains.Count == 7
        ? [0, 0, 0, .. visibleGains]
        : [.. visibleGains];

    private static IReadOnlyList<ThxAudioPresetCurve> FactoryCurves(
        ThxAudioEndpoint endpoint, string modelNumber)
    {
        IReadOnlyDictionary<string, double[]> source =
            endpoint == ThxAudioEndpoint.Speakers
                ? modelNumber switch
                {
                    "RZ09-0581" => SpeakerFactoryCurves,
                    "RZ09-0528" => Rz090528SpeakerFactoryCurves,
                    "RZ09-0530" => Rz090530SpeakerFactoryCurves,
                    _ => throw new InvalidDataException("The THX model is unsupported."),
                }
                : HeadphoneFactoryCurves;
        return [.. source.Select(pair => new ThxAudioPresetCurve(
            pair.Key,
            endpoint == ThxAudioEndpoint.Speakers && modelNumber != "RZ09-0581"
                ? [0, 0, 0, .. pair.Value] : pair.Value.ToArray()))];
    }

    private static bool SetEnhancement(
        IThxAudioNativeAdapter native,
        string device,
        ThxHelperSetEnhancementRequest request)
    {
        (string enabledProperty, string levelProperty) = request.Kind switch
        {
            ThxAudioEnhancementKind.SoundNormalization =>
                (NormalizationEnabledPropertyId, NormalizationLevelPropertyId),
            ThxAudioEnhancementKind.VoiceClarity =>
                (VoiceClarityEnabledPropertyId, VoiceClarityLevelPropertyId),
            _ => throw new InvalidDataException("The THX enhancement is unsupported."),
        };

        bool currentEnabled = ThxVendorJson.ParseBoolean(
            native.GetProperty(device, enabledProperty));
        double currentLevel = ThxVendorJson.ParseFloat(
            native.GetProperty(device, levelProperty),
            minimum: 0,
            maximum: 1);
        if (currentEnabled != request.Enabled
            && !native.SetProperty(
                device,
                enabledProperty,
                request.Enabled ? "true" : "false",
                null))
        {
            return false;
        }
        if (request.Enabled && request.LevelPercent is int requestedPercent)
        {
            double requestedLevel = requestedPercent / 100d;
            if (Math.Abs(currentLevel - requestedLevel) > 0.000001
                && !native.SetProperty(
                    device,
                    levelProperty,
                    JsonSerializer.Serialize(requestedLevel),
                    FloatPropertyType))
            {
                return false;
            }
        }
        return true;
    }

    private static ThxAudioEnhancement ReadEnhancement(
        IThxAudioNativeAdapter native,
        string device,
        string enabledProperty,
        string levelProperty)
    {
        bool enabled = ThxVendorJson.ParseBoolean(
            native.GetProperty(device, enabledProperty));
        double normalizedLevel = ThxVendorJson.ParseFloat(
            native.GetProperty(device, levelProperty),
            minimum: 0,
            maximum: 1);
        int displayLevelPercent = checked((int)Math.Round(
            normalizedLevel * 100,
            MidpointRounding.AwayFromZero));
        return new ThxAudioEnhancement(enabled, displayLevelPercent);
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
                StringComparison.Ordinal)
                && GainsMatch(state.EqualizerBands, value.GainsDb),
            ThxHelperSetEnhancementRequest value => EnhancementMatches(value, state),
            ThxHelperApplyApplicationProfileRequest value =>
                state.SpatialEnabled
                && ThxAudioApplicationProfileCatalog.TryResolve(
                    value.ExecutableName,
                    value.Mode,
                    out string nativePresetId)
                && string.Equals(
                    state.SelectedNativeProfileId,
                    nativePresetId,
                    StringComparison.Ordinal),
            _ => false,
        };

    private static bool GainsMatch(
        IReadOnlyList<ThxAudioEqualizerBand> bands,
        IReadOnlyList<double> requested) =>
        bands.Count == requested.Count
        && bands.Select((band, index) => Math.Abs(band.GainDb - requested[index]))
            .All(difference => difference <= 0.0001);

    private static bool EnhancementMatches(
        ThxHelperSetEnhancementRequest request,
        ThxAudioState state)
    {
        ThxAudioEnhancement? enhancement = request.Kind switch
        {
            ThxAudioEnhancementKind.SoundNormalization => state.SoundNormalization,
            ThxAudioEnhancementKind.VoiceClarity => state.VoiceClarity,
            _ => null,
        };
        return enhancement is not null
            && enhancement.Enabled == request.Enabled
            && (!request.Enabled
                || request.LevelPercent is null
                || enhancement.LevelPercent == request.LevelPercent);
    }

    private static void Validate(ThxAudioHelperRequest request)
    {
        if (request.Version != ThxAudioHelperProtocol.Version
            || !Enum.IsDefined(request.Endpoint)
            || request is ThxHelperSetPropertyRequest property
                && property.PropertyId is not (SpatialPropertyId or EqualizerPropertyId)
            || request is ThxHelperSelectPresetRequest preset
                && (!LogicalToNativePreset.ContainsKey(preset.PresetId)
                    || !ValidGains(preset.Endpoint, preset.GainsDb))
            || request is ThxHelperSetEnhancementRequest enhancement
                && (!Enum.IsDefined(enhancement.Kind)
                    || enhancement.LevelPercent is not null and (< 0 or > 100)
                    || !enhancement.Enabled && enhancement.LevelPercent is not null
                    || enhancement.Kind == ThxAudioEnhancementKind.SoundNormalization
                        && enhancement.Endpoint != ThxAudioEndpoint.Speakers)
            || request is ThxHelperApplyApplicationProfileRequest application
                && (!Enum.IsDefined(application.Mode)
                    || !ValidExecutableName(application.ExecutableName)))
        {
            throw new InvalidDataException("The THX helper request is invalid.");
        }
    }

    private static bool ValidGains(
        ThxAudioEndpoint endpoint,
        IReadOnlyList<double>? gains) =>
        gains is not null
        && (gains.Count == 10 || endpoint == ThxAudioEndpoint.Speakers && gains.Count == 7)
        && gains.All(value => double.IsFinite(value) && value is >= -12 and <= 12);

    private static bool ValidExecutableName(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 260
        && value.EndsWith(".exe", StringComparison.Ordinal)
        && string.Equals(value, value.Trim(), StringComparison.Ordinal)
        && string.Equals(value, value.ToLowerInvariant(), StringComparison.Ordinal)
        && value.IndexOfAny(['/', '\\']) < 0
        && !value.Any(char.IsControl);

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
