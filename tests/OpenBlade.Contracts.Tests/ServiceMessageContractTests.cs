using System.Reflection;
using System.Text.Json.Serialization;
using OpenBlade.Contracts;

namespace OpenBlade.Contracts.Tests;

public sealed class ServiceMessageContractTests
{
    [Fact]
    public void ProtocolConstants_AreCurrentAndRetainExactSizeLimit()
    {
        Assert.Equal(61, OpenBladeIpc.ProtocolVersion);
        Assert.Equal("OpenBlade.Service.v2", OpenBladeIpc.PipeName);
        Assert.Equal(64 * 1024, OpenBladeIpc.MaximumMessageBytes);
        Assert.Equal(32 * 1024, OpenBladeIpc.MaximumDiagnosticArchiveBytes);
    }

    [Fact]
    public void RequestAndServerFrameBases_DeclareOnlyTheClosedDerivedTypes()
    {
        AssertDerivedTypes(
            typeof(ServiceRequest),
            (typeof(GetStatusRequest), "getStatus"),
            (typeof(ObserveStateRequest), "observeState"),
            (typeof(SubscribeStatusRequest), "subscribeStatus"),
            (typeof(ExportDiagnosticsRequest), "exportDiagnostics"),
            (typeof(UpdateProfilesRequest), "updateProfiles"),
            (typeof(SetPerformanceOverrideRequest), "setPerformanceOverride"),
            (typeof(SetGpuClockTuningRequest), "setGpuClockTuning"),
            (typeof(SetHyperBoostRequest), "setHyperBoost"),
            (typeof(ValidatePadlessHyperBoostLifecycleRequest), "validatePadlessHyperBoostLifecycle"),
            (typeof(ValidateThermalExternalDriftRequest), "validateThermalExternalDrift"),
            (typeof(CyclePerformanceShortcutRequest), "cyclePerformanceShortcut"),
            (typeof(SetPerformanceShortcutPreviewRequest), "setPerformanceShortcutPreview"),
            (typeof(SetFanOverrideRequest), "setFanOverride"),
            (typeof(SetLightingOverrideRequest), "setLightingOverride"),
            (typeof(StepKeyboardBrightnessRequest), "stepKeyboardBrightness"),
            (typeof(SetDisplayBrightnessHidUsageRequest), "setDisplayBrightnessHidUsage"),
            (typeof(SetKeyboardLightingControlModeRequest), "setKeyboardLightingControlMode"),
            (typeof(SetExternalLightingOverridePolicyRequest), "setExternalLightingOverridePolicy"),
            (typeof(SetLightingRefreshRateRequest), "setLightingRefreshRate"),
            (typeof(SetFirmwareCompatibilityOverrideRequest), "setFirmwareCompatibilityOverride"),
            (typeof(ReapplyChangedSettingsRequest), "reapplyChangedSettings"),
            (typeof(RecoverHyperBoostRestoreRequest), "recoverHyperBoostRestore"),
            (typeof(SetThermalControlPolicyRequest), "setThermalControlPolicy"),
            (typeof(AcknowledgeThermalPolicyUpgradeDisclosureRequest), "acknowledgeThermalPolicyUpgradeDisclosure"),
            (typeof(SetLightingSuppressionRequest), "setLightingSuppression"),
            (typeof(PulseReactiveKeyRequest), "pulseReactiveKey"),
            (typeof(PushAmbientLightingFrameRequest), "pushAmbientLightingFrame"),
            (typeof(SetPlaybackMuteIndicatorRequest), "setPlaybackMuteIndicator"),
            (typeof(SetBatteryPolicyRequest), "setBatteryPolicy"),
            (typeof(SetBatteryProtectionRequest), "setBatteryProtection"),
            (typeof(SetFullChargeOverrideRequest), "setFullChargeOverride"),
            (typeof(ApplyOneShotLaptopControlRequest), "applyOneShotLaptopControl"),
            (typeof(ReportSessionPowerTransitionRequest), "reportSessionPowerTransition"),
            (typeof(ShutdownOpenBladeRequest), "shutdownOpenBlade"));
        AssertDerivedTypes(
            typeof(ServiceServerFrame),
            (typeof(ServiceResponse), "response"),
            (typeof(ServiceStatusEvent), "statusEvent"));
    }

    [Fact]
    public void ServiceStatusSnapshot_ContainsFullControlPlaneState()
    {
        string[] expected =
        [
            "Revision", "DeviceVerified", "WritesBlocked", "PotentialControllers",
            "ControllerDetectionWarning", "ExternalChanges", "UnconfirmedWrites", "Message", "ObservationReason",
            "Capabilities", "ConfirmedState", "Profiles", "DesiredState",
            "ActivePowerSource", "LiveReadings",
            "PadlessHyperBoostEligibility",
            "HyperBoostRestorePending", "UnsupportedLaptop", "HyperBoostBlockingControllers",
            "ThermalConvergence",
            "PerformanceOverride", "FanOverride", "FanControlOverride", "LightingOverride",
            "BatteryPolicyOverride", "BatteryProtectionOverride", "FullChargeOverride",
            "Firmware", "ServiceVersion", "LightingRendererOwnership", "LightingSuppressions",
            "StartupAnimationEnabled", "FunctionKeyPrimary", "GamingModeEnabled",
            "AlwaysOverrideExternalLightingChanges", "KeyboardDriverModeActive",
            "LightingRefreshRate",
            "PerformanceShortcutPreview", "BatteryHealth", "KeyboardLightingControlMode",
            "ControlAccess", "AllowUntestedFirmware", "FirmwareCompatibility",
            "DisplayBrightnessHidAvailability",
        ];

        Assert.Equal(
            expected.Order(),
            typeof(ServiceStatusSnapshot).GetProperties().Select(property => property.Name).Order());
        Assert.Equal(typeof(DeviceCapabilities), typeof(ServiceStatusSnapshot).GetProperty("Capabilities")!.PropertyType);
        Assert.Equal(typeof(ConfirmedDeviceState), typeof(ServiceStatusSnapshot).GetProperty("ConfirmedState")!.PropertyType);
        Assert.Equal(typeof(FirmwareVersions), typeof(ServiceStatusSnapshot).GetProperty("Firmware")!.PropertyType);
        Assert.Equal(typeof(string), typeof(ServiceStatusSnapshot).GetProperty("ServiceVersion")!.PropertyType);
        Assert.Equal(typeof(LightingRendererOwnershipState), typeof(ServiceStatusSnapshot).GetProperty("LightingRendererOwnership")!.PropertyType);
        Assert.Equal(typeof(bool?), typeof(ServiceStatusSnapshot).GetProperty("StartupAnimationEnabled")!.PropertyType);
        Assert.Equal(typeof(FunctionKeyPrimaryMode?), typeof(ServiceStatusSnapshot).GetProperty("FunctionKeyPrimary")!.PropertyType);
        Assert.Equal(typeof(bool?), typeof(ServiceStatusSnapshot).GetProperty("GamingModeEnabled")!.PropertyType);
        Assert.Equal(typeof(bool), typeof(ServiceStatusSnapshot).GetProperty("AlwaysOverrideExternalLightingChanges")!.PropertyType);
        Assert.Equal(typeof(LightingRefreshRate), typeof(ServiceStatusSnapshot).GetProperty("LightingRefreshRate")!.PropertyType);
        Assert.Equal(typeof(bool?), typeof(ServiceStatusSnapshot).GetProperty("KeyboardDriverModeActive")!.PropertyType);
        Assert.Equal(typeof(KeyboardLightingControlMode), typeof(ServiceStatusSnapshot).GetProperty("KeyboardLightingControlMode")!.PropertyType);
        Assert.Equal(typeof(PerformanceShortcutPreview), typeof(ServiceStatusSnapshot).GetProperty("PerformanceShortcutPreview")!.PropertyType);
        Assert.Equal(typeof(BatteryHealthStatus), typeof(ServiceStatusSnapshot).GetProperty("BatteryHealth")!.PropertyType);
        Assert.Equal(typeof(SessionControlAccess), typeof(ServiceStatusSnapshot).GetProperty("ControlAccess")!.PropertyType);
        Assert.Equal(typeof(bool), typeof(ServiceStatusSnapshot).GetProperty("AllowUntestedFirmware")!.PropertyType);
        Assert.Equal(typeof(FirmwareCompatibilityStatus), typeof(ServiceStatusSnapshot).GetProperty("FirmwareCompatibility")!.PropertyType);
        Assert.Equal(typeof(DisplayBrightnessHidAvailability), typeof(ServiceStatusSnapshot).GetProperty("DisplayBrightnessHidAvailability")!.PropertyType);
        Assert.Equal(typeof(BatteryPolicy), typeof(ServiceStatusSnapshot).GetProperty("BatteryPolicyOverride")!.PropertyType);
        Assert.Equal(typeof(bool?), typeof(ServiceStatusSnapshot).GetProperty("BatteryProtectionOverride")!.PropertyType);
        Assert.Equal(typeof(ThermalConvergenceStatus), typeof(ServiceStatusSnapshot).GetProperty("ThermalConvergence")!.PropertyType);
    }

    [Fact]
    public void ThermalControlContracts_DeclareClosedPolicyAndStatusValues()
    {
        Assert.Equal(
            ["MaintainDesiredState", "Strict"],
            Enum.GetNames<ThermalControlPolicy>());
        Assert.Equal(
            ["NotRequired", "UpgradeDisclosurePending", "Acknowledged"],
            Enum.GetNames<ThermalPolicyUpgradeDisclosureState>());
        Assert.Equal(
            [
                "Matched", "AwaitingObservation", "Converging", "Paused",
                "Suppressed", "External", "RestorePending",
                "ReconciliationUnavailable",
            ],
            Enum.GetNames<ThermalConvergenceState>());
        Assert.Equal(
            ["Performance", "Fans"],
            Enum.GetNames<ThermalComponent>());
    }

    [Fact]
    public void DeviceSubsystem_DeclaresOnlyTheClosedControlPlaneSubsystems()
    {
        Assert.Equal(
            ["Performance", "Fans", "Battery", "Lighting"],
            Enum.GetNames<DeviceSubsystem>());
    }

    [Fact]
    public void ServiceResponse_ExposesExactlyThreeTypedBodiesAndStatusEventHasNoRequestId()
    {
        Assert.Equal(
            new[]
            {
                "Archive", "Error", "ErrorCode", "RequestId", "Result", "Status", "Success", "Version",
            },
            typeof(ServiceResponse).GetProperties().Select(property => property.Name).Order());
        Assert.DoesNotContain(
            typeof(ServiceStatusEvent).GetProperties(),
            property => property.Name == "RequestId");
        Assert.Equal(
            typeof(ControlOperationResult),
            typeof(ServiceResponse).GetProperty("Result")!.PropertyType);
        Assert.Equal(
            typeof(DiagnosticArchive),
            typeof(ServiceResponse).GetProperty("Archive")!.PropertyType);
        Assert.Equal(
            typeof(ServiceResponseErrorCode?),
            typeof(ServiceResponse).GetProperty("ErrorCode")!.PropertyType);
    }

    [Fact]
    public void UnavailableCapabilities_ContainNoHardwareClaims()
    {
        DeviceCapabilities capabilities = DeviceCapabilities.Unavailable();

        Assert.Empty(capabilities.PerformancePresets);
        Assert.Empty(capabilities.FanModes);
        Assert.Empty(capabilities.LightingEffects);
        Assert.Empty(capabilities.ChargeLimits);
        Assert.False(capabilities.SupportsBatteryProtectionOff);
        Assert.False(capabilities.SupportsTemporaryFullCharge);
        Assert.Null(capabilities.KeyboardLightingLayoutId);
    }

    [Fact]
    public void ExportedMessageSurface_ContainsNoRawOrFirmwareCommandFields()
    {
        Type[] roots = [typeof(ServiceRequest), typeof(ServiceServerFrame)];
        HashSet<Type> visited = [];

        foreach (Type root in roots)
        {
            AssertSafeSurface(root, visited);
        }
    }

    private static void AssertDerivedTypes(
        Type baseType,
        params (Type Type, string Discriminator)[] expected)
    {
        JsonPolymorphicAttribute polymorphic = Assert.Single(
            baseType.GetCustomAttributes<JsonPolymorphicAttribute>());
        Assert.NotNull(polymorphic.TypeDiscriminatorPropertyName);
        JsonDerivedTypeAttribute[] actual =
            baseType.GetCustomAttributes<JsonDerivedTypeAttribute>().ToArray();
        Assert.Equal(expected.Length, actual.Length);
        foreach ((Type type, string discriminator) in expected)
        {
            Assert.Contains(actual, attribute =>
                attribute.DerivedType == type
                && string.Equals(attribute.TypeDiscriminator?.ToString(), discriminator, StringComparison.Ordinal));
        }
    }

    private static void AssertSafeSurface(
        Type type,
        HashSet<Type> visited,
        bool allowBoundedTypedBytes = false)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type.IsArray)
        {
            if (type.GetElementType() == typeof(byte))
            {
                Assert.True(allowBoundedTypedBytes);
                return;
            }
            AssertSafeSurface(type.GetElementType()!, visited);
            return;
        }

        if (type.IsGenericType)
        {
            Type definition = type.GetGenericTypeDefinition();
            Assert.NotEqual(typeof(Memory<>), definition);
            Assert.NotEqual(typeof(ReadOnlyMemory<>), definition);
            foreach (Type argument in type.GetGenericArguments())
            {
                AssertSafeSurface(argument, visited);
            }
        }

        if (!visited.Add(type) || type.Namespace != typeof(ServiceRequest).Namespace)
        {
            return;
        }

        Assert.NotEqual(typeof(IntPtr), type);
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            Assert.DoesNotContain(
                property.Name,
                ["transactionId", "commandClass", "commandId"],
                StringComparer.OrdinalIgnoreCase);
            AssertSafeSurface(
                property.PropertyType,
                visited,
                allowBoundedTypedBytes:
                    (type == typeof(DiagnosticArchive)
                        && property.Name == nameof(DiagnosticArchive.ZipBytes))
                    || (type == typeof(PushAmbientLightingFrameRequest)
                        && property.Name == nameof(PushAmbientLightingFrameRequest.RgbBytes)));
        }

        foreach (JsonDerivedTypeAttribute derived in type.GetCustomAttributes<JsonDerivedTypeAttribute>())
        {
            AssertSafeSurface(derived.DerivedType, visited);
        }
    }
}
