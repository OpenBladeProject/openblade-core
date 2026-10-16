using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace OpenBlade.Contracts;

public static class OpenBladeIpc
{
    public const int ProtocolVersion = 67;
    public const int ReadOnlyCompatibilityProtocolVersion = 51;
    public const int MaximumMessageBytes = 64 * 1024;
    public const int MaximumDiagnosticArchiveBytes = 32 * 1024;
    public const string PipeName = "OpenBlade.Service.v2";
}

public sealed class IpcProtocolVersionMismatchException : IOException
{
    public IpcProtocolVersionMismatchException(int expectedVersion, int actualVersion)
        : base("IPC protocol version is unsupported.")
    {
        ExpectedVersion = expectedVersion;
        ActualVersion = actualVersion;
    }

    public int ExpectedVersion { get; }

    public int ActualVersion { get; }
}

public static class IpcMessageCodec
{
    private static readonly JsonSerializerOptions SerializerOptions =
        CreateSerializerOptions(protocol51Projection: false);
    private static readonly JsonSerializerOptions Protocol51SerializerOptions =
        CreateSerializerOptions(protocol51Projection: true);

    public static Task WriteRequestAsync(
        Stream stream,
        ServiceRequest request,
        CancellationToken token)
    {
        ValidateRequest(request);
        return WriteFramedAsync<ServiceRequest>(stream, request, token);
    }

    public static async Task<ServiceRequest> ReadRequestAsync(
        Stream stream,
        CancellationToken token)
    {
        ServiceRequest request = await ReadFramedAsync<ServiceRequest>(stream, token)
            .ConfigureAwait(false);
        ValidateRequest(request);
        return request;
    }

    public static async Task<ServiceRequest> ReadRequestWithProtocol51CompatibilityAsync(
        Stream stream,
        CancellationToken token)
    {
        ServiceRequest request = await ReadFramedAsync<ServiceRequest>(
                stream,
                token,
                allowProtocol51: true)
            .ConfigureAwait(false);
        if (request.Version == OpenBladeIpc.ProtocolVersion)
        {
            ValidateRequest(request);
            return request;
        }

        if (request.Version != OpenBladeIpc.ReadOnlyCompatibilityProtocolVersion
            || request.RequestId == Guid.Empty)
        {
            throw new InvalidDataException("IPC request body is invalid.");
        }

        return request;
    }

    public static Task WriteServerFrameAsync(
        Stream stream,
        ServiceServerFrame frame,
        CancellationToken token)
    {
        ValidateServerFrame(frame);
        return WriteFramedAsync<ServiceServerFrame>(stream, frame, token);
    }

    public static async Task<ServiceServerFrame> ReadServerFrameAsync(
        Stream stream,
        CancellationToken token)
    {
        ServiceServerFrame frame = await ReadFramedAsync<ServiceServerFrame>(stream, token)
            .ConfigureAwait(false);
        ValidateServerFrame(frame);
        return frame;
    }

    public static Task WriteProtocol51ReadOnlyServerFrameAsync(
        Stream stream,
        ServiceServerFrame frame,
        CancellationToken token)
    {
        ValidateServerFrame(frame);
        ServiceServerFrame projected = frame switch
        {
            ServiceResponse response => response with
            {
                Version = OpenBladeIpc.ReadOnlyCompatibilityProtocolVersion,
                Status = response.Status is null
                    ? null
                    : ProjectProtocol51ReadOnlyStatus(response.Status),
            },
            ServiceStatusEvent statusEvent => statusEvent with
            {
                Version = OpenBladeIpc.ReadOnlyCompatibilityProtocolVersion,
                Status = ProjectProtocol51ReadOnlyStatus(statusEvent.Status),
            },
            _ => throw new InvalidDataException("IPC server frame body is invalid."),
        };
        return WriteFramedAsync(
            stream,
            projected,
            token,
            Protocol51SerializerOptions);
    }

    private static async Task WriteFramedAsync<T>(
        Stream stream,
        T message,
        CancellationToken cancellationToken,
        JsonSerializerOptions? serializerOptions = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(message);
        byte[] payload;
        try
        {
            payload = JsonSerializer.SerializeToUtf8Bytes(
                message,
                serializerOptions ?? SerializerOptions);
        }
        catch (JsonException exception)
        {
            throw InvalidBody(exception);
        }
        catch (NotSupportedException exception)
        {
            throw new InvalidDataException("IPC message body is invalid.", exception);
        }

        if (payload.Length > OpenBladeIpc.MaximumMessageBytes)
        {
            throw new InvalidDataException("IPC message exceeds the 64 KiB limit.");
        }

        byte[] prefix = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T> ReadFramedAsync<T>(
        Stream stream,
        CancellationToken cancellationToken,
        bool allowProtocol51 = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] prefix = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is <= 0 or > OpenBladeIpc.MaximumMessageBytes)
        {
            throw new InvalidDataException("IPC message length is invalid.");
        }

        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateNoDuplicateObjectMembers(payload);
            ValidatePayloadVersion(payload, allowProtocol51);
            return JsonSerializer.Deserialize<T>(payload, SerializerOptions)
                ?? throw new InvalidDataException("IPC message body is invalid.");
        }
        catch (JsonException exception)
        {
            throw InvalidBody(exception);
        }
        catch (NotSupportedException exception)
        {
            throw new InvalidDataException("IPC message body is invalid.", exception);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("IPC message body is invalid.", exception);
        }
    }

    private static void ValidateRequest(ServiceRequest? request)
    {
        if (request is null)
        {
            throw new InvalidDataException("IPC request is missing.");
        }

        ValidateVersion(request.Version);
        if (request.RequestId == Guid.Empty)
        {
            throw new InvalidDataException("IPC request ID is missing.");
        }

        bool validBody = request switch
        {
            GetStatusRequest => true,
            ObserveStateRequest observe =>
                (observe.RequestedLiveReadings & ~LiveReadingSensors.All) == 0,
            SubscribeStatusRequest => true,
            ExportDiagnosticsRequest => true,
            UpdateProfilesRequest update => update.Profiles is not null
                && ValidWindowsPowerModes(update.Profiles.WindowsPowerModes)
                && LegacyWindowsPowerModesAreClear(update.Profiles),
            SetPerformanceOverrideRequest set =>
                set.Value is null
                || set.Value.IsWellFormed(),
            SetHyperBoostRequest => true,
            SetGpuClockTuningRequest set =>
                GpuClockTuningSettings.IsWellFormed(set.Value)
                && Enum.IsDefined(set.Lifetime),
            ValidatePadlessHyperBoostLifecycleRequest validate =>
                Enum.IsDefined(validate.Scenario)
                && validate.ValidationRunId != Guid.Empty
                && IsHex(validate.ExpectedSourceRevision, 40)
                && IsHex(validate.ExpectedServiceAssemblySha256, 64)
                && IsHex(validate.ExpectedAdmissionManifestSha256, 64),
            ValidateThermalExternalDriftRequest validate =>
                validate.ValidationRunId != Guid.Empty
                && IsHex(validate.ExpectedSourceRevision, 40)
                && IsHex(validate.ExpectedServiceAssemblySha256, 64)
                && IsHex(validate.ExpectedProtocolAssemblySha256, 64)
                && IsHex(validate.ExpectedProductionFixtureSha256, 64),
            CyclePerformanceShortcutRequest cycle => Enum.IsDefined(cycle.Source),
            SetPerformanceShortcutPreviewRequest preview =>
                preview.SessionId != Guid.Empty
                && preview.Generation > 0
                && (preview.Preset is null || Enum.IsDefined(preview.Preset.Value)),
            SetFanOverrideRequest set =>
                set.Value is null
                    ? set.Settings is null
                    : Enum.IsDefined(set.Value.Value)
                        && Enum.IsDefined(set.Value.Lifetime)
                        && (set.Settings is null
                            || set.Settings.Mode == set.Value.Value),
            SetLightingOverrideRequest set =>
                set.Value is null
                || set.Value.Value is not null
                    && Enum.IsDefined(set.Value.Lifetime)
                    && LightingSettingsValidation.IsWellFormed(set.Value.Value),
            StepKeyboardBrightnessRequest step =>
                Enum.IsDefined(step.Direction)
                && Enum.IsDefined(step.Lifetime),
            SetDisplayBrightnessHidUsageRequest set =>
                Enum.IsDefined(set.Usage),
            SetKeyboardLightingControlModeRequest set => Enum.IsDefined(set.Mode),
            SetExternalLightingOverridePolicyRequest => true,
            SetLightingRefreshRateRequest set => Enum.IsDefined(set.RefreshRate),
            SetFirmwareCompatibilityOverrideRequest => true,
            SetExperimentalFeaturesRequest => true,
            ResumeExperimentalWritesRequest => true,
            ReapplyChangedSettingsRequest => true,
            RecoverHyperBoostRestoreRequest => true,
            SetThermalControlPolicyRequest set => Enum.IsDefined(set.Policy),
            AcknowledgeThermalPolicyUpgradeDisclosureRequest => true,
            SetLightingSuppressionRequest set => Enum.IsDefined(set.Reason),
            PulseReactiveKeyRequest pulse =>
                pulse.Pulse.MatrixSlot <= ReactiveKeyPulse.MaximumMatrixSlot,
            PushAmbientLightingFrameRequest ambient => ambient.RgbBytes is not null
                && AmbientLightingFrame.IsWellFormedRgbBytes(ambient.RgbBytes),
            PushUniformLightingFrameRequest uniform => uniform.Frame is not null,
            SetPlaybackMuteIndicatorRequest => true,
            SetBatteryPolicyRequest set => set.Value is not null,
            SetBatteryProtectionRequest => true,
            SetFullChargeOverrideRequest => true,
            ApplyOneShotLaptopControlRequest apply =>
                apply.Value is not null && apply.Value.IsWellFormed(),
            ReportSessionPowerTransitionRequest report =>
                report.InstanceId != Guid.Empty
                && report.InstanceStartedQpc > 0
                && report.Sequence > 0
                && report.TimestampQpc >= report.InstanceStartedQpc,
            ShutdownOpenBladeRequest => true,
            _ => false,
        };
        if (!validBody)
        {
            throw new InvalidDataException("IPC request body is invalid.");
        }
    }

    private static bool IsHex(string? value, int length) =>
        value is not null
        && value.Length == length
        && value.All(Uri.IsHexDigit);

    private static void ValidateServerFrame(ServiceServerFrame? frame)
    {
        if (frame is null)
        {
            throw new InvalidDataException("IPC server frame is missing.");
        }

        ValidateVersion(frame.Version);
        bool valid = frame switch
        {
            ServiceStatusEvent statusEvent => ValidateStatus(statusEvent.Status),
            ServiceResponse response => ValidateResponse(response),
            _ => false,
        };
        if (!valid)
        {
            throw new InvalidDataException("IPC server frame body is invalid.");
        }
    }

    private static bool ValidateResponse(ServiceResponse response)
    {
        if (response.RequestId == Guid.Empty)
        {
            return false;
        }

        int bodyCount = (response.Status is null ? 0 : 1)
            + (response.Result is null ? 0 : 1)
            + (response.Archive is null ? 0 : 1);
        bool valid = response.Success
            ? response.Error is null
                && response.ErrorCode is null
                && bodyCount == 1
            : !string.IsNullOrWhiteSpace(response.Error)
                && bodyCount == 0
                && (response.ErrorCode is null
                    || Enum.IsDefined(response.ErrorCode.Value));
        return valid
            && (response.Status is null || ValidateStatus(response.Status))
            && (response.Archive is null || ValidateArchive(response.Archive));
    }

    private static bool ValidateArchive(DiagnosticArchive archive)
    {
        string fileName = archive.SuggestedFileName;
        byte[] bytes = archive.ZipBytes;
        return !string.IsNullOrWhiteSpace(fileName)
            && string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal)
            && !fileName.Contains('/')
            && !fileName.Contains('\\')
            && fileName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
            && string.Equals(Path.GetExtension(fileName), ".zip", StringComparison.OrdinalIgnoreCase)
            && bytes is { Length: >= 4 and <= OpenBladeIpc.MaximumDiagnosticArchiveBytes }
            && bytes[0] == 0x50
            && bytes[1] == 0x4b
            && bytes[2] == 0x03
            && bytes[3] == 0x04;
    }

    private static bool ValidateStatus(ServiceStatusSnapshot? status)
    {
        if (status is null)
        {
            throw InvalidStatus("snapshot is missing");
        }

        if (!Enum.IsDefined(status.ObservationReason))
        {
            throw InvalidStatus("observation reason is invalid");
        }

        if (status.Revision < 0)
        {
            throw InvalidStatus("revision must be nonnegative");
        }

        if (status.PotentialControllers is null)
        {
            throw InvalidStatus("potential-controller collection is missing");
        }

        if (status.PotentialControllers.Count > 16)
        {
            throw InvalidStatus("potential-controller collection exceeds 16 entries");
        }

        SortedCollectionStatus potentialControllerStatus = ValidateSortedUnique(
            status.PotentialControllers,
            StringComparer.Ordinal);
        if (potentialControllerStatus == SortedCollectionStatus.Duplicate)
        {
            throw InvalidStatus("potential-controller collection contains duplicates");
        }

        if (potentialControllerStatus == SortedCollectionStatus.Unsorted)
        {
            throw InvalidStatus("potential-controller collection is not ordinally sorted");
        }

        if (status.HyperBoostBlockingControllers is { } thermalBlockers)
        {
            if (thermalBlockers.Count > 16)
            {
                throw InvalidStatus("HyperBoost blocker collection exceeds 16 entries");
            }

            SortedCollectionStatus blockerStatus = ValidateSortedUnique(
                thermalBlockers,
                StringComparer.Ordinal);
            if (blockerStatus != SortedCollectionStatus.Valid
                || thermalBlockers.Any(controller =>
                    !status.PotentialControllers.Contains(controller, StringComparer.OrdinalIgnoreCase)))
            {
                throw InvalidStatus("HyperBoost blocker collection is invalid");
            }
        }

        if (status.ExternalChanges is null)
        {
            throw InvalidStatus("external-change collection is missing");
        }

        if (status.ExternalChanges.Count > 4)
        {
            throw InvalidStatus("external-change collection exceeds four entries");
        }

        if (!status.ExternalChanges.All(Enum.IsDefined))
        {
            throw InvalidStatus("external-change collection contains an unknown subsystem");
        }

        SortedCollectionStatus externalChangeStatus = ValidateSortedUnique(
            status.ExternalChanges,
            Comparer<DeviceSubsystem>.Default);
        if (externalChangeStatus == SortedCollectionStatus.Duplicate)
        {
            throw InvalidStatus("external-change collection contains duplicates");
        }

        if (externalChangeStatus == SortedCollectionStatus.Unsorted)
        {
            throw InvalidStatus("external-change collection is not sorted");
        }

        if (status.UnconfirmedWrites is null)
        {
            throw InvalidStatus("unconfirmed-write collection is missing");
        }

        if (status.UnconfirmedWrites.Count > 4)
        {
            throw InvalidStatus("unconfirmed-write collection exceeds four entries");
        }

        if (!status.UnconfirmedWrites.All(Enum.IsDefined))
        {
            throw InvalidStatus("unconfirmed-write collection contains an unknown subsystem");
        }

        SortedCollectionStatus unconfirmedWriteStatus = ValidateSortedUnique(
            status.UnconfirmedWrites,
            Comparer<DeviceSubsystem>.Default);
        if (unconfirmedWriteStatus == SortedCollectionStatus.Duplicate)
        {
            throw InvalidStatus("unconfirmed-write collection contains duplicates");
        }

        if (unconfirmedWriteStatus == SortedCollectionStatus.Unsorted)
        {
            throw InvalidStatus("unconfirmed-write collection is not sorted");
        }

        if (status.Message is null)
        {
            throw InvalidStatus("message is missing");
        }

        if (status.Profiles is null)
        {
            throw InvalidStatus("profile set is missing");
        }

        if (!ValidWindowsPowerModes(status.Profiles.WindowsPowerModes))
        {
            throw InvalidStatus("Windows power mode associations are malformed");
        }

        if (!LegacyWindowsPowerModesAreClear(status.Profiles))
        {
            throw InvalidStatus("legacy Windows power mode fields are not allowed");
        }

        if (status.DesiredState is null)
        {
            throw InvalidStatus("desired state is missing");
        }

        if (!ValidProfileLighting(status.Profiles)
            || status.DesiredState.Lighting is null
            || !Enum.IsDefined(status.DesiredState.Lighting.Source)
            || !LightingSettingsValidation.IsWellFormed(
                status.DesiredState.Lighting.Value)
            || status.ConfirmedState?.Lighting is { } confirmedLighting
                && !LightingSettingsValidation.IsWellFormed(confirmedLighting)
            || status.LightingOverride is { } lightingOverride
                && (!Enum.IsDefined(lightingOverride.Lifetime)
                    || !LightingSettingsValidation.IsWellFormed(
                        lightingOverride.Value)))
        {
            throw InvalidStatus("lighting state is malformed");
        }

        if (!ValidPerformanceState(
                status.DesiredState.Performance.Value,
                status.DesiredState.CustomPerformance))
        {
            throw InvalidStatus("desired performance state is inconsistent");
        }

        if (status.DesiredState.GpuClockTuning is { } gpuClockTuning
            && !GpuClockTuningSettings.IsWellFormed(gpuClockTuning))
        {
            throw InvalidStatus("desired GPU clock tuning state is inconsistent");
        }

        if (status.ConfirmedState is { } confirmed
            && !ValidPerformanceState(
                confirmed.Performance,
                confirmed.CustomPerformance))
        {
            throw InvalidStatus("confirmed performance state is inconsistent");
        }

        if (!Enum.IsDefined(status.PadlessHyperBoostEligibility))
        {
            throw InvalidStatus("pad-less HyperBoost eligibility is unknown");
        }

        if (status.ConfirmedState is { HyperBoostActive: true } hyperBoost
            && (status.Capabilities?.HyperBoostAccess is not (
                    DeviceFeatureAccess.ObservedOnly
                    or DeviceFeatureAccess.ReadOnly
                    or DeviceFeatureAccess.ReadWrite)
                || hyperBoost.Performance is not null
                || hyperBoost.Fans != FanMode.Automatic))
        {
            throw InvalidStatus("confirmed HyperBoost state is inconsistent");
        }

        if (status.ConfirmedState?.GpuClockOffsets is { } gpuClockOffsets
            && !gpuClockOffsets.IsWellFormed)
        {
            throw InvalidStatus("confirmed GPU clock-offset state is inconsistent");
        }

        if (status.PerformanceOverride is { } performanceOverride
            && !performanceOverride.IsWellFormed())
        {
            throw InvalidStatus("performance override is malformed");
        }

        if (status.PerformanceShortcutPreview is { } preview
            && (preview.SessionId == Guid.Empty
                || preview.Generation <= 0
                || !Enum.IsDefined(preview.Preset)))
        {
            throw InvalidStatus("performance-shortcut preview is malformed");
        }

        if (!Enum.IsDefined(status.ActivePowerSource))
        {
            throw InvalidStatus("active power source is unknown");
        }

        if (!Enum.IsDefined(status.LightingRendererOwnership))
        {
            throw InvalidStatus("lighting-renderer ownership is unknown");
        }

        if (!Enum.IsDefined(status.KeyboardLightingControlMode))
        {
            throw InvalidStatus("keyboard-lighting control mode is unknown");
        }

        if (!Enum.IsDefined(status.LightingRefreshRate))
        {
            throw InvalidStatus("lighting refresh rate is unknown");
        }

        if (!Enum.IsDefined(status.ControlAccess))
        {
            throw InvalidStatus("session control access is unknown");
        }

        if (!Enum.IsDefined(status.DisplayBrightnessHidAvailability))
        {
            throw InvalidStatus("display-brightness HID availability is unknown");
        }

        if (status.FunctionKeyPrimary is { } functionKeyPrimary
            && !Enum.IsDefined(functionKeyPrimary))
        {
            throw InvalidStatus("function-key primary mode is unknown");
        }

        if (status.BatteryHealth is { } batteryHealth
            && (batteryHealth.DesignCapacityMilliwattHours is <= 0 or > 1_000_000
                || batteryHealth.FullChargeCapacityMilliwattHours
                    is <= 0 or > 1_000_000
                || batteryHealth.FullChargeCapacityMilliwattHours
                    > batteryHealth.DesignCapacityMilliwattHours * 2L))
        {
            throw InvalidStatus("battery-health capacities are outside the valid range");
        }

        if (status.LightingSuppressions is { } suppressions
            && (suppressions.Count > 2
                || suppressions.Any(reason => !Enum.IsDefined(reason))
                || ValidateSortedUnique(
                    suppressions,
                    Comparer<LightingSuppressionReason>.Default)
                    != SortedCollectionStatus.Valid))
        {
            throw InvalidStatus("lighting-suppression collection is malformed");
        }

        if (status.ThermalConvergence is { } thermalConvergence)
        {
            if (!Enum.IsDefined(thermalConvergence.Policy))
            {
                throw InvalidStatus("thermal-control policy is unknown");
            }

            if (!Enum.IsDefined(thermalConvergence.Disclosure))
            {
                throw InvalidStatus("thermal-policy disclosure state is unknown");
            }

            if (!Enum.IsDefined(thermalConvergence.State))
            {
                throw InvalidStatus("thermal-convergence state is unknown");
            }

            if (!Enum.IsDefined(thermalConvergence.Reason))
            {
                throw InvalidStatus("thermal-convergence reason is unknown");
            }

            if (thermalConvergence.AffectedComponents is null)
            {
                throw InvalidStatus("thermal-convergence affected-component collection is missing");
            }

            if (thermalConvergence.AffectedComponents.Count > 2)
            {
                throw InvalidStatus("thermal-convergence affected-component collection exceeds two entries");
            }

            if (!thermalConvergence.AffectedComponents.All(Enum.IsDefined))
            {
                throw InvalidStatus("thermal-convergence affected-component collection contains an unknown component");
            }

            SortedCollectionStatus affectedComponentStatus = ValidateSortedUnique(
                thermalConvergence.AffectedComponents,
                Comparer<ThermalComponent>.Default);
            if (affectedComponentStatus == SortedCollectionStatus.Duplicate)
            {
                throw InvalidStatus("thermal-convergence affected-component collection contains duplicates");
            }

            if (affectedComponentStatus == SortedCollectionStatus.Unsorted)
            {
                throw InvalidStatus("thermal-convergence affected-component collection is not sorted");
            }
        }

        if (status.Capabilities is { IsWellFormed: false })
        {
            throw InvalidStatus("capability collections are malformed");
        }

        if (string.IsNullOrWhiteSpace(status.ServiceVersion))
        {
            throw InvalidStatus("service version is missing");
        }

        if (status.ServiceVersion.Length > 64)
        {
            throw InvalidStatus("service version exceeds 64 characters");
        }

        if (!string.Equals(
                status.ServiceVersion,
                status.ServiceVersion.Trim(),
                StringComparison.Ordinal))
        {
            throw InvalidStatus("service version contains surrounding whitespace");
        }

        if (status.LiveReadings is not null && !ValidLiveReadings(status.LiveReadings))
        {
            throw InvalidStatus("live readings are outside the valid range");
        }

        return true;
    }

    private static InvalidDataException InvalidStatus(string reason) =>
        new($"IPC server status is invalid: {reason}.");

    private static bool ValidPerformanceState(
        PerformancePreset? performance,
        CustomPerformanceSettings? customPerformance) =>
        performance is null
            ? customPerformance is null
            : Enum.IsDefined(performance.Value)
                && (performance == PerformancePreset.Custom
                    ? CustomPerformanceSettings.IsWellFormed(customPerformance)
                    : customPerformance is null);

    private static bool ValidProfileLighting(ProfileSet profiles) =>
        profiles.PluggedIn is not null
        && LightingSettingsValidation.IsWellFormed(profiles.PluggedIn.Lighting)
        && profiles.Battery is not null
        && LightingSettingsValidation.IsWellFormed(profiles.Battery.Lighting)
        && profiles.UsbC is not null
        && LightingSettingsValidation.IsWellFormed(profiles.UsbC.Lighting);

    private static bool ValidWindowsPowerModes(
        WindowsPowerModeAssociations? associations) =>
        associations is not null
        && Enum.GetValues<PerformancePreset>().All(performance =>
            associations.ForPerformance(performance) is not { } mode
            || Enum.IsDefined(mode));

    private static bool LegacyWindowsPowerModesAreClear(ProfileSet profiles) =>
        profiles.PluggedIn?.WindowsPowerMode is null
        && profiles.Battery?.WindowsPowerMode is null
        && profiles.UsbC?.WindowsPowerMode is null;

    private static void ValidatePayloadVersion(
        ReadOnlySpan<byte> payload,
        bool allowProtocol51)
    {
        var reader = new Utf8JsonReader(payload);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return;
        }

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return;
            }
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                return;
            }

            string? propertyName = reader.GetString();
            if (!reader.Read())
            {
                return;
            }
            if (string.Equals(propertyName, "version", StringComparison.OrdinalIgnoreCase)
                && reader.TokenType == JsonTokenType.Number
                && reader.TryGetInt32(out int version))
            {
                if (allowProtocol51
                    && version == OpenBladeIpc.ReadOnlyCompatibilityProtocolVersion)
                {
                    return;
                }

                ValidateVersion(version);
                return;
            }

            reader.Skip();
        }
    }

    private static void ValidateNoDuplicateObjectMembers(ReadOnlyMemory<byte> payload)
    {
        using JsonDocument document = JsonDocument.Parse(payload);
        ValidateNoDuplicateObjectMembers(document.RootElement);
    }

    private static void ValidateNoDuplicateObjectMembers(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new JsonException("IPC message contains a duplicate member.");
                }

                ValidateNoDuplicateObjectMembers(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                ValidateNoDuplicateObjectMembers(item);
            }
        }
    }

    private static bool ValidLiveReadings(LiveReadings readings) =>
        readings.BatteryPowerFlowMilliwatts is null
            or >= -1_000_000 and <= 1_000_000
        && readings.Fan1Rpm is null or >= 0 and <= 20_000
        && readings.Fan2Rpm is null or >= 0 and <= 20_000
        && readings.CpuTemperatureCelsius is null or >= 0 and <= 115
        && readings.GpuTemperatureCelsius is null or >= 0 and <= 115;

    private static void ValidateVersion(int version)
    {
        if (version != OpenBladeIpc.ProtocolVersion)
        {
            throw new IpcProtocolVersionMismatchException(
                OpenBladeIpc.ProtocolVersion,
                version);
        }
    }

    private static InvalidDataException InvalidBody(JsonException exception) =>
        new("IPC message body is invalid.", exception);

    private static ServiceStatusSnapshot ProjectProtocol51ReadOnlyStatus(
        ServiceStatusSnapshot status)
    {
        BatteryPolicy projectedPolicy = IsProtocol51BatteryLimit(
            status.Profiles.BatteryPolicy.ChargeLimitPercent)
                ? status.Profiles.BatteryPolicy
                : BatteryPolicy.Create(80);
        DeviceCapabilities? projectedCapabilities = status.Capabilities is null
            ? null
            : status.Capabilities with
            {
                ChargeLimits = status.Capabilities.ChargeLimits
                    .Where(IsProtocol51BatteryLimit)
                    .ToHashSet(),
                CustomPerformance = ProjectProtocol51CustomCapabilities(
                    status.Capabilities.CustomPerformance),
            };
        ConfirmedDeviceState? projectedConfirmedState = status.ConfirmedState is null
            ? null
            : status.ConfirmedState with
            {
                Battery = status.ConfirmedState.Battery is
                { ChargeLimitPercent: int confirmedLimit }
                    && !IsProtocol51BatteryLimit(confirmedLimit)
                        ? null
                        : status.ConfirmedState.Battery,
                Performance = IsProtocol51CustomState(status.ConfirmedState.CustomPerformance)
                    ? status.ConfirmedState.Performance : null,
                CustomPerformance = IsProtocol51CustomState(status.ConfirmedState.CustomPerformance)
                    ? status.ConfirmedState.CustomPerformance : null,
                Fans = status.ConfirmedState.CustomPerformance?.MaximumFans == true
                    ? null : status.ConfirmedState.Fans,
            };

        return status with
        {
            WritesBlocked = true,
            Message = "OpenBlade was updated. Update or repair the app to restore hardware control.",
            Capabilities = projectedCapabilities,
            ConfirmedState = projectedConfirmedState,
            // Legacy desired/profile values are display placeholders only. Never
            // project an unrepresentable hardware observation as confirmed.
            Profiles = status.Profiles with
            {
                BatteryPolicy = projectedPolicy,
                PluggedIn = ProjectProtocol51Profile(status.Profiles.PluggedIn),
                Battery = ProjectProtocol51Profile(status.Profiles.Battery),
                UsbC = ProjectProtocol51Profile(status.Profiles.UsbC),
            },
            DesiredState = status.DesiredState with
            {
                Battery = new DesiredBatteryState(
                    IsProtocol51BatteryLimit(status.DesiredState.Battery.ConfiguredPolicy.ChargeLimitPercent)
                        ? status.DesiredState.Battery.ConfiguredPolicy
                        : projectedPolicy,
                    status.DesiredState.Battery.ProtectionEnabled,
                    status.DesiredState.Battery.TemporaryFullCharge),
                CustomPerformance = ProjectProtocol51CustomSettings(
                    status.DesiredState.CustomPerformance),
            },
            PerformanceOverride = status.PerformanceOverride is { } performanceOverride
                ? performanceOverride with
                {
                    CustomPerformance = ProjectProtocol51CustomSettings(
                        performanceOverride.CustomPerformance),
                } : null,
            BatteryPolicyOverride = status.BatteryPolicyOverride is { } batteryOverride
                && IsProtocol51BatteryLimit(batteryOverride.ChargeLimitPercent)
                    ? batteryOverride
                    : null,
            ThermalConvergence = null,
            HyperBoostBlockingControllers = null,
            ObservationReason = DeviceObservationReason.None,
            UnsupportedLaptop = false,
        };

        static bool IsProtocol51BatteryLimit(int value) =>
            value is >= 50 and <= 80 && value % 5 == 0;
    }

    private static bool IsProtocol51CustomState(CustomPerformanceSettings? settings) =>
        settings is null || !settings.MaximumFans
            && settings.Cpu != CustomPerformanceLevel.Boost
            && !(settings.Cpu == CustomPerformanceLevel.High
                && settings.Gpu == CustomPerformanceLevel.High);

    private static CustomPerformanceSettings? ProjectProtocol51CustomSettings(
        CustomPerformanceSettings? settings) => settings is null ? null
        : IsProtocol51CustomState(settings) ? settings
        : CustomPerformanceSettings.Default;

    private static PowerProfile ProjectProtocol51Profile(PowerProfile profile) =>
        profile with { CustomPerformance = ProjectProtocol51CustomSettings(profile.CustomPerformance) };

    private static CustomPerformanceCapabilities? ProjectProtocol51CustomCapabilities(
        CustomPerformanceCapabilities? capabilities)
    {
        if (capabilities is null)
        {
            return null;
        }
        HashSet<CustomPerformanceCombination> allowed = capabilities.AllowedCombinations
            .Where(combination => combination.Cpu != CustomPerformanceLevel.Boost
                && !(combination.Cpu == CustomPerformanceLevel.High
                    && combination.Gpu == CustomPerformanceLevel.High))
            .ToHashSet();
        return allowed.Count == 0 ? null : new(allowed);
    }

    private static JsonSerializerOptions CreateSerializerOptions(
        bool protocol51Projection)
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(typeInfo =>
        {
            if (typeInfo.Type == typeof(ProfileSet))
            {
                JsonPropertyInfo windowsPowerModes = typeInfo.Properties.Single(property =>
                    string.Equals(
                        property.Name,
                        "windowsPowerModes",
                        StringComparison.OrdinalIgnoreCase));
                windowsPowerModes.IsRequired = true;
            }

            if (protocol51Projection
                && (typeInfo.Type == typeof(CustomPerformanceSettings)
                    || typeInfo.Type == typeof(CustomPerformanceCapabilities)))
            {
                string propertyName = typeInfo.Type == typeof(CustomPerformanceSettings)
                    ? "maximumFans" : "supportsMaximumFans";
                typeInfo.Properties.Single(property => string.Equals(
                    property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                    .ShouldSerialize = static (_, _) => false;
            }

            if (protocol51Projection
                && typeInfo.Type == typeof(ConfirmedDeviceState))
            {
                // Protocol 51 clients only understand confirmed on or off. Their
                // projection is read-only, so unknown HyperBoost is shown as
                // inactive there and can never authorize a write.
                JsonPropertyInfo hyperBoostActive = typeInfo.Properties.Single(property =>
                    string.Equals(
                        property.Name,
                        "hyperBoostActive",
                        StringComparison.OrdinalIgnoreCase));
                hyperBoostActive.Get = static state =>
                    ((ConfirmedDeviceState)state).HyperBoostActive ?? false;
            }

            if (protocol51Projection
                && typeInfo.Type == typeof(ServiceStatusSnapshot))
            {
                JsonPropertyInfo thermalConvergence = typeInfo.Properties.Single(property =>
                    string.Equals(
                        property.Name,
                        "thermalConvergence",
                        StringComparison.OrdinalIgnoreCase));
                thermalConvergence.ShouldSerialize = static (_, _) => false;
                JsonPropertyInfo hyperBoostBlockers = typeInfo.Properties.Single(property =>
                    string.Equals(
                        property.Name,
                        "hyperBoostBlockingControllers",
                        StringComparison.OrdinalIgnoreCase));
                hyperBoostBlockers.ShouldSerialize = static (_, _) => false;
                JsonPropertyInfo observationReason = typeInfo.Properties.Single(property =>
                    string.Equals(
                        property.Name,
                        "observationReason",
                        StringComparison.OrdinalIgnoreCase));
                observationReason.ShouldSerialize = static (_, _) => false;
                JsonPropertyInfo allowExperimentalFeatures = typeInfo.Properties.Single(
                    property => string.Equals(
                        property.Name,
                        "allowExperimentalFeatures",
                        StringComparison.OrdinalIgnoreCase));
                allowExperimentalFeatures.ShouldSerialize = static (_, _) => false;
                JsonPropertyInfo experimentalWritesPaused = typeInfo.Properties.Single(property =>
                    string.Equals(property.Name, "experimentalWritesPaused", StringComparison.OrdinalIgnoreCase));
                experimentalWritesPaused.ShouldSerialize = static (_, _) => false;
                JsonPropertyInfo isExperimentalDevice = typeInfo.Properties.Single(property =>
                    string.Equals(
                        property.Name,
                        "isExperimentalDevice",
                        StringComparison.OrdinalIgnoreCase));
                isExperimentalDevice.ShouldSerialize = static (_, _) => false;
                JsonPropertyInfo experimentalSupportSource = typeInfo.Properties.Single(
                    property => string.Equals(
                        property.Name,
                        "experimentalSupportSource",
                        StringComparison.OrdinalIgnoreCase));
                experimentalSupportSource.ShouldSerialize = static (_, _) => false;
                JsonPropertyInfo unsupportedLaptop = typeInfo.Properties.Single(property =>
                    string.Equals(
                        property.Name,
                        "unsupportedLaptop",
                        StringComparison.OrdinalIgnoreCase));
                unsupportedLaptop.ShouldSerialize = static (_, _) => false;
            }

            if (protocol51Projection
                && typeInfo.Type == typeof(DeviceCapabilities))
            {
                JsonPropertyInfo directManualFanTransitions =
                    typeInfo.Properties.Single(property =>
                        string.Equals(
                            property.Name,
                            "supportsDirectManualFanTransitions",
                            StringComparison.OrdinalIgnoreCase));
                directManualFanTransitions.ShouldSerialize = static (_, _) => false;
                typeInfo.Properties.Single(property => string.Equals(
                    property.Name, "supportsManualFansOnLimitedPower", StringComparison.OrdinalIgnoreCase))
                    .ShouldSerialize = static (_, _) => false;
                typeInfo.Properties.Single(property => string.Equals(
                    property.Name, "reportsPerformanceWithManualFans", StringComparison.OrdinalIgnoreCase))
                    .ShouldSerialize = static (_, _) => false;
                JsonPropertyInfo breathingColorModes =
                    typeInfo.Properties.Single(property =>
                        string.Equals(
                            property.Name,
                            "supportedBreathingColorModes",
                            StringComparison.OrdinalIgnoreCase));
                breathingColorModes.ShouldSerialize = static (_, _) => false;
                JsonPropertyInfo uniformLightingFrames =
                    typeInfo.Properties.Single(property =>
                        string.Equals(
                            property.Name,
                            "supportsUniformLightingFrames",
                            StringComparison.OrdinalIgnoreCase));
                uniformLightingFrames.ShouldSerialize = static (_, _) => false;
            }
        });
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            RespectRequiredConstructorParameters = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            TypeInfoResolver = resolver,
        };
        options.Converters.Add(new BatteryPolicyJsonConverter());
        options.Converters.Add(new ReadOnlySetJsonConverterFactory());
        return options;
    }

    private sealed class BatteryPolicyJsonConverter : JsonConverter<BatteryPolicy>
    {
        public override BatteryPolicy Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            using JsonDocument document = JsonDocument.ParseValue(ref reader);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("Battery policy must be an object.");
            }

            int? chargeLimitPercent = null;
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (!string.Equals(
                    property.Name,
                    "chargeLimitPercent",
                    StringComparison.Ordinal))
                {
                    throw new JsonException("Battery policy contains an unknown member.");
                }

                if (chargeLimitPercent.HasValue
                    || property.Value.ValueKind != JsonValueKind.Number
                    || !property.Value.TryGetInt32(out int value))
                {
                    throw new JsonException("Battery policy charge limit is invalid.");
                }

                chargeLimitPercent = value;
            }

            if (!chargeLimitPercent.HasValue)
            {
                throw new JsonException("Battery policy charge limit is missing.");
            }

            try
            {
                return BatteryPolicy.Create(chargeLimitPercent.Value);
            }
            catch (ArgumentOutOfRangeException exception)
            {
                throw new JsonException("Battery policy charge limit is invalid.", exception);
            }
        }

        public override void Write(
            Utf8JsonWriter writer,
            BatteryPolicy value,
            JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteNumber("chargeLimitPercent", value.ChargeLimitPercent);
            writer.WriteEndObject();
        }
    }

    private sealed class ReadOnlySetJsonConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) =>
            typeToConvert.IsGenericType
            && typeToConvert.GetGenericTypeDefinition() == typeof(IReadOnlySet<>);

        public override JsonConverter CreateConverter(
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            Type elementType = typeToConvert.GetGenericArguments()[0];
            if (elementType == typeof(int))
            {
                return new ChargeLimitSetJsonConverter();
            }

            return (JsonConverter)Activator.CreateInstance(
                typeof(ReadOnlySetJsonConverter<>).MakeGenericType(elementType))!;
        }
    }

    private sealed class ChargeLimitSetJsonConverter : JsonConverter<IReadOnlySet<int>>
    {
        public override IReadOnlySet<int> Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray)
            {
                throw new JsonException("Charge limits must be an array.");
            }

            List<int> values = [];
            int? previous = null;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType != JsonTokenType.Number
                    || !reader.TryGetInt32(out int value)
                    || value is < 50 or > 100
                    || previous.HasValue && value <= previous.Value)
                {
                    throw new JsonException("Charge limits are invalid.");
                }

                values.Add(value);
                if (values.Count > 51)
                {
                    throw new JsonException("Too many charge limits were supplied.");
                }

                previous = value;
            }

            if (reader.TokenType != JsonTokenType.EndArray)
            {
                throw new JsonException("Charge limits array is incomplete.");
            }

            return values.ToFrozenSet();
        }

        public override void Write(
            Utf8JsonWriter writer,
            IReadOnlySet<int> value,
            JsonSerializerOptions options)
        {
            if (!DeviceCapabilities.AreChargeLimitsWellFormed(value))
            {
                throw new JsonException("Charge limits are invalid.");
            }

            Span<int> sortedLimits = stackalloc int[value.Count];
            int index = 0;
            foreach (int limit in value)
            {
                sortedLimits[index++] = limit;
            }
            sortedLimits.Sort();

            writer.WriteStartArray();
            foreach (int limit in sortedLimits)
            {
                writer.WriteNumberValue(limit);
            }
            writer.WriteEndArray();
        }
    }

    private sealed class ReadOnlySetJsonConverter<T> : JsonConverter<IReadOnlySet<T>>
        where T : notnull
    {
        public override IReadOnlySet<T> Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) =>
            JsonSerializer.Deserialize<HashSet<T>>(ref reader, options)
            ?? throw new JsonException("Set value is missing.");

        public override void Write(
            Utf8JsonWriter writer,
            IReadOnlySet<T> value,
            JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value.ToArray(), options);
    }

    private static SortedCollectionStatus ValidateSortedUnique<T>(
        IReadOnlyList<T> values,
        IComparer<T> comparer)
    {
        for (int index = 1; index < values.Count; index++)
        {
            int comparison = comparer.Compare(values[index - 1], values[index]);
            if (comparison == 0)
            {
                return SortedCollectionStatus.Duplicate;
            }
            if (comparison > 0)
            {
                return SortedCollectionStatus.Unsorted;
            }
        }
        return SortedCollectionStatus.Valid;
    }

    private enum SortedCollectionStatus
    {
        Valid,
        Duplicate,
        Unsorted,
    }
}
