using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

/// <summary>
/// Evidence captured from Synapse while it acted as the protocol oracle. These
/// captures establish wire formats only; they are not evidence that OpenBlade
/// applied or restored any setting.
/// </summary>
internal sealed record ProductionLightingOracleEvidence(
    int SchemaVersion,
    string EvidenceKind,
    ProductionLightingDeviceEvidence Device,
    ProductionLightingOracleCaptureEvidence CoreCapture,
    ProductionLightingOracleCaptureEvidence ReactiveCapture,
    ProductionLightingOracleCaptureEvidence VariantCapture,
    ProductionLightingOracleCaptureEvidence ReactiveTimingCapture,
    ProductionLightingOracleCaptureEvidence DualBreathingCapture,
    ProductionLightingOracleCaptureEvidence ReactiveMainKeyMapCapture,
    ProductionLightingOracleCaptureEvidence ReactiveFunctionKeyMapCapture,
    ProductionLightingOracleCaptureEvidence RippleCapture,
    ProductionLightingProtocolEvidence Protocol);

internal sealed record ProductionLightingDeviceEvidence(
    string Model,
    ushort VendorId,
    ushort ProductId,
    string BiosVersion);

internal sealed record ProductionLightingOracleCaptureEvidence(
    string Sha256,
    bool Captured,
    bool Decoded);

/// <summary>
/// A separately captured, machine-local OpenBlade hardware-validation result.
/// It is never embedded: production admission binds this external artifact to
/// the exact installed protocol assembly after every gate has passed.
/// </summary>
internal sealed record ProductionLightingOpenBladeValidationEvidence(
    int SchemaVersion,
    string EvidenceKind,
    ProductionLightingDeviceEvidence Device,
    string InformationalVersion,
    string CommitHash,
    string ProtocolAssemblySha256,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset ValidatedAtUtc,
    ProductionLightingKnownBaselineEvidence KnownBaseline,
    ProductionLightingValidationActionEvidence[] Actions,
    bool AllSupportedEffectsApplied,
    bool BrightnessIndependentReadback,
    bool TransitionAcknowledged,
    bool MatrixWriteVisuallyConfirmed,
    bool MatrixReadbackAvailable,
    bool BaselineRestoreAttempted,
    int? BaselineRestoreObservedBrightnessRaw,
    bool BaselineRestoreBrightnessMatched,
    bool BaselineRestoreVisuallyConfirmed,
    bool BaselineRestored,
    string RestoredStateKind,
    bool UnknownPreTestStateRestored,
    string? Failure);

internal sealed record ProductionLightingKnownBaselineEvidence(
    LightingSettings Settings,
    int ExpectedBrightnessRaw,
    int? ObservedBrightnessRaw,
    bool BrightnessMatched,
    bool VisuallyConfirmed);

internal sealed record ProductionLightingValidationActionEvidence(
    string Name,
    LightingSettings Settings,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    bool ApplySucceeded,
    int ExpectedBrightnessRaw,
    int? ObservedBrightnessRaw,
    bool BrightnessMatched,
    bool ReactivePulseRequired,
    bool ReactivePulseApplied,
    bool VisuallyConfirmed,
    string? Failure);

internal sealed record ProductionLightingBuildIdentity(
    string InformationalVersion,
    string CommitHash,
    string ProtocolAssemblySha256);

internal sealed record ProductionLightingProtocolEvidence(
    string BrightnessQuery,
    string BrightnessSetPrefix,
    string[] TransitionRequests,
    string[] TransitionResponses,
    string MatrixCommand,
    int MatrixEnvelopeLength,
    int MatrixFeatureReportLength,
    int MatrixSlotCount);

internal static class ProductionLightingEvidenceSource
{
    private const int MaximumValidationArtifactBytes = 64 * 1024;
    internal const string OracleResourceName =
        "OpenBlade.Protocol.Fixtures.Rz09_0581_02e0_3_01_LightingProduction.json";

    internal const string OpenBladeValidationFileName = "lighting-validation.json";

    private const string ApprovedOracleFixtureSha256 =
        "5177FA9F6F91D96C9C34C5506CDA8EDC0A86EE5B5EEFA5F4322F4E4633C23521";

    private const string LightingImplementationMetadataKey =
        "OpenBladeLightingImplementationFingerprint";

    private const string ApprovedLightingImplementationFingerprintSha256 =
        // Re-reviewed after moving Wave, Wheel, and Tidal to the already
        // admitted 30 FPS matrix cadence. Exact captured timestamps preserve
        // their approved oracle frames; intermediate timestamps interpolate
        // per-key RGB values without changing the HID command or report shape.
        "DD2323A85F53CE4F7022550434EE0A9A93F9AA6248BB4A576866D456535151C9";

    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        };

    internal static ProductionLightingOracleEvidence ReadValidatedOracle()
    {
        byte[] bytes = ReadRequiredEmbeddedResource(OracleResourceName);
        RequireApprovedHash(bytes, ApprovedOracleFixtureSha256, "Lighting oracle fixture");

        ProductionLightingOracleEvidence? evidence =
            JsonSerializer.Deserialize<ProductionLightingOracleEvidence>(bytes, SerializerOptions);
        ValidateOracle(evidence);
        return evidence!;
    }

    internal static bool IsApprovedLightingImplementation()
    {
        string? fingerprint = typeof(ProductionLightingEvidenceSource).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(attribute =>
                string.Equals(
                    attribute.Key,
                    LightingImplementationMetadataKey,
                    StringComparison.Ordinal))
            ?.Value;
        return IsApprovedLightingImplementationFingerprint(fingerprint);
    }

    internal static bool IsApprovedLightingImplementationFingerprint(
        string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            return false;
        }

        string actual = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint)));
        return string.Equals(
            actual,
            ApprovedLightingImplementationFingerprintSha256,
            StringComparison.Ordinal);
    }

    internal static bool IsCurrentBuildValidated()
    {
        try
        {
            Assembly assembly = typeof(ProductionLightingEvidenceSource).Assembly;
            return IsCurrentBuildValidated(
                GetDefaultOpenBladeValidationPath(),
                ReadBuildIdentity(assembly));
        }
        catch (Exception exception) when (
            exception is UnvalidatedCommandException
            or JsonException
            or IOException
            or UnauthorizedAccessException)
        {
            // A missing, damaged, stale, or unapproved validation artifact can
            // never turn into permission to write. Keep the factory read-only.
            return false;
        }
    }

    internal static bool IsCurrentBuildValidated(
        string artifactPath,
        ProductionLightingBuildIdentity buildIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactPath);
        ArgumentNullException.ThrowIfNull(buildIdentity);

        try
        {
            var file = new FileInfo(artifactPath);
            if (!file.Exists || file.Length is <= 0 or > MaximumValidationArtifactBytes)
            {
                return false;
            }

            byte[] bytes = File.ReadAllBytes(artifactPath);
            ProductionLightingOpenBladeValidationEvidence? evidence =
                JsonSerializer.Deserialize<ProductionLightingOpenBladeValidationEvidence>(
                    bytes,
                    SerializerOptions);
            return IsValidatedForBuild(evidence, buildIdentity);
        }
        catch (Exception exception) when (
            exception is JsonException
            or IOException
            or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static bool IsValidatedForBuild(
        ProductionLightingOpenBladeValidationEvidence? evidence,
        ProductionLightingBuildIdentity buildIdentity)
    {
        ArgumentNullException.ThrowIfNull(buildIdentity);
        string informationalVersion = buildIdentity.InformationalVersion;
        if (evidence is null
            || evidence.SchemaVersion != 2
            || evidence.EvidenceKind != "openBladeExactBuildHardwareValidation"
            || evidence.Device != TargetDeviceEvidence
            || string.IsNullOrWhiteSpace(informationalVersion)
            || !string.Equals(
                evidence.InformationalVersion,
                informationalVersion,
                StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(evidence.CommitHash)
            || evidence.CommitHash.Length != 40
            || !evidence.CommitHash.All(Uri.IsHexDigit)
            || !informationalVersion.EndsWith(
                $"+{evidence.CommitHash}",
                StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(evidence.ProtocolAssemblySha256)
            || !ValidSha256(evidence.ProtocolAssemblySha256)
            || !string.Equals(
                evidence.ProtocolAssemblySha256,
                buildIdentity.ProtocolAssemblySha256,
                StringComparison.OrdinalIgnoreCase)
            || evidence.StartedAtUtc == default
            || evidence.ValidatedAtUtc == default
            || evidence.ValidatedAtUtc < evidence.StartedAtUtc
            || !ValidKnownBaseline(evidence.KnownBaseline)
            || !ValidActions(
                evidence.Actions,
                evidence.KnownBaseline.Settings.Brightness)
            || evidence.Actions.Any(action =>
                action.StartedAtUtc < evidence.StartedAtUtc
                || action.CompletedAtUtc > evidence.ValidatedAtUtc)
            || !evidence.AllSupportedEffectsApplied
            || !evidence.BrightnessIndependentReadback
            || !evidence.TransitionAcknowledged
            || !evidence.MatrixWriteVisuallyConfirmed
            || evidence.MatrixReadbackAvailable
            || !evidence.BaselineRestoreAttempted
            || evidence.BaselineRestoreObservedBrightnessRaw
                != evidence.KnownBaseline.ExpectedBrightnessRaw
            || !evidence.BaselineRestoreBrightnessMatched
            || !evidence.BaselineRestoreVisuallyConfirmed
            || !evidence.BaselineRestored
            || evidence.RestoredStateKind != "knownStaticGreenBaseline"
            || evidence.UnknownPreTestStateRestored
            || evidence.Failure is not null)
        {
            return false;
        }

        return true;
    }

    internal static string GetDefaultOpenBladeValidationPath()
    {
        string root = Environment.GetFolderPath(
            Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(root, "OpenBlade", OpenBladeValidationFileName);
    }

    internal static ProductionLightingBuildIdentity ReadCurrentBuildIdentity() =>
        ReadBuildIdentity(typeof(ProductionLightingEvidenceSource).Assembly);

    private static bool ValidKnownBaseline(
        ProductionLightingKnownBaselineEvidence? baseline) =>
        baseline is not null
        && baseline.Settings == LightingSettingsFactory.Static(
            new RgbColor(0, byte.MaxValue, 0),
            baseline.Settings.Brightness) with
            {
                LogoMode = LogoLightingMode.Static,
            }
        && baseline.Settings.Brightness is > 0 and <= 100
        && baseline.ExpectedBrightnessRaw
            == LightingBrightnessProtocol.PercentToRaw(baseline.Settings.Brightness)
        && baseline.ObservedBrightnessRaw == baseline.ExpectedBrightnessRaw
        && baseline.BrightnessMatched
        && baseline.VisuallyConfirmed;

    private static bool ValidActions(
        ProductionLightingValidationActionEvidence[]? actions,
        byte brightness)
    {
        string[] expectedNames =
        [
            "off",
            "static",
            "spectrum",
            "breathingSingle",
            "breathingDual",
            "breathingRandom",
            "reactiveShort",
            "reactiveMedium",
            "reactiveLong",
            "ripple",
            "tidalRightFixed",
            "tidalLeftFixed",
            "tidalRandom",
            "starlightSingleShort",
            "starlightSingleMedium",
            "starlightSingleLong",
            "starlightDual",
            "starlightRandom",
            "waveRightward",
            "waveLeftward",
            "wheelClockwise",
            "wheelCounterclockwise",
            "logoBreathing",
            "logoOff",
            "logoStaticRestore",
        ];
        if (actions is null
            || actions.Length != expectedNames.Length
            || !actions.Select(action => action.Name).SequenceEqual(expectedNames))
        {
            return false;
        }

        foreach (ProductionLightingValidationActionEvidence action in actions)
        {
            bool keyTriggered = action.Settings.Effect is
                LightingEffect.Reactive or LightingEffect.Ripple;
            if (!LightingSettingsValidation.IsWellFormed(action.Settings)
                || !MatchesExpectedTypedAction(action, brightness)
                || action.StartedAtUtc == default
                || action.CompletedAtUtc < action.StartedAtUtc
                || !action.ApplySucceeded
                || action.ExpectedBrightnessRaw
                    != LightingBrightnessProtocol.PercentToRaw(action.Settings.Brightness)
                || action.ObservedBrightnessRaw != action.ExpectedBrightnessRaw
                || !action.BrightnessMatched
                || action.ReactivePulseRequired != keyTriggered
                || action.ReactivePulseApplied != keyTriggered
                || !action.VisuallyConfirmed
                || action.Failure is not null)
            {
                return false;
            }
        }

        return true;
    }

    private static bool MatchesExpectedTypedAction(
        ProductionLightingValidationActionEvidence action,
        byte brightness) =>
        action.Name switch
        {
            "off" => action.Settings == LightingSettingsFactory.Off(),
            "static" => action.Settings == LightingSettingsFactory.Static(
                new RgbColor(0, 128, byte.MaxValue), brightness),
            "spectrum" =>
                action.Settings == LightingSettingsFactory.SpectrumCycling(brightness),
            "breathingSingle" =>
                action.Settings == LightingSettingsFactory.BreathingSingle(
                    new RgbColor(0, byte.MaxValue, 0), brightness),
            "breathingDual" =>
                action.Settings == LightingSettingsFactory.BreathingDual(
                    new RgbColor(byte.MaxValue, 0, 0),
                    new RgbColor(0, 0, byte.MaxValue),
                    brightness),
            "breathingRandom" =>
                action.Settings == LightingSettingsFactory.BreathingRandom(brightness),
            "reactiveShort" => action.Settings == LightingSettingsFactory.Reactive(
                new RgbColor(0, byte.MaxValue, 0),
                brightness,
                ReactiveLightingDuration.Short),
            "reactiveMedium" => action.Settings == LightingSettingsFactory.Reactive(
                new RgbColor(0, byte.MaxValue, 0),
                brightness,
                ReactiveLightingDuration.Medium),
            "reactiveLong" => action.Settings == LightingSettingsFactory.Reactive(
                new RgbColor(0, byte.MaxValue, 0),
                brightness,
                ReactiveLightingDuration.Long),
            "ripple" => action.Settings == LightingSettingsFactory.Ripple(
                new RgbColor(0, byte.MaxValue, 0),
                brightness),
            "tidalRightFixed" => action.Settings == LightingSettingsFactory.Tidal(
                new RgbColor(0, byte.MaxValue, 0),
                new RgbColor(0, 0, byte.MaxValue),
                randomColors: false,
                LightingDirection.Rightward,
                brightness),
            "tidalLeftFixed" => action.Settings == LightingSettingsFactory.Tidal(
                new RgbColor(0, byte.MaxValue, 0),
                new RgbColor(0, 0, byte.MaxValue),
                randomColors: false,
                LightingDirection.Leftward,
                brightness),
            "tidalRandom" => action.Settings == LightingSettingsFactory.Tidal(
                new RgbColor(0, byte.MaxValue, 0),
                new RgbColor(0, 0, byte.MaxValue),
                randomColors: true,
                LightingDirection.Rightward,
                brightness),
            "starlightSingleShort" => action.Settings ==
                LightingSettingsFactory.StarlightSingle(
                    new RgbColor(0, byte.MaxValue, 0),
                    brightness,
                    StarlightLightingDuration.Short),
            "starlightSingleMedium" => action.Settings ==
                LightingSettingsFactory.StarlightSingle(
                    new RgbColor(0, byte.MaxValue, 0),
                    brightness,
                    StarlightLightingDuration.Medium),
            "starlightSingleLong" => action.Settings ==
                LightingSettingsFactory.StarlightSingle(
                    new RgbColor(0, byte.MaxValue, 0),
                    brightness,
                    StarlightLightingDuration.Long),
            "starlightDual" => action.Settings == LightingSettingsFactory.StarlightDual(
                new RgbColor(byte.MaxValue, 0, 0),
                new RgbColor(0, 0, byte.MaxValue),
                brightness,
                StarlightLightingDuration.Medium),
            "starlightRandom" => action.Settings == LightingSettingsFactory.StarlightRandom(
                brightness,
                StarlightLightingDuration.Medium),
            "waveRightward" => action.Settings == LightingSettingsFactory.Wave(
                LightingDirection.Rightward,
                brightness),
            "waveLeftward" => action.Settings == LightingSettingsFactory.Wave(
                LightingDirection.Leftward,
                brightness),
            "wheelClockwise" => action.Settings == LightingSettingsFactory.Wheel(
                LightingRotationDirection.Clockwise,
                brightness),
            "wheelCounterclockwise" => action.Settings == LightingSettingsFactory.Wheel(
                LightingRotationDirection.Counterclockwise,
                brightness),
            "logoBreathing" => action.Settings ==
                LightingSettingsFactory.Static(
                    new RgbColor(0, byte.MaxValue, 0),
                    brightness) with
                {
                    LogoMode = LogoLightingMode.Breathing,
                },
            "logoOff" => action.Settings == LightingSettingsFactory.Static(
                new RgbColor(0, byte.MaxValue, 0),
                brightness) with
                {
                    LogoMode = LogoLightingMode.Off,
                },
            "logoStaticRestore" => action.Settings ==
                LightingSettingsFactory.Static(
                    new RgbColor(0, byte.MaxValue, 0),
                    brightness) with
                {
                    LogoMode = LogoLightingMode.Static,
                },
            _ => false,
        };

    private static bool ValidSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    private static void ValidateOracle(ProductionLightingOracleEvidence? evidence)
    {
        if (evidence is null
            || evidence.SchemaVersion != 3
            || evidence.EvidenceKind != "synapseOracleCapture"
            || evidence.Device != TargetDeviceEvidence
            || !ValidOracleCapture(
                evidence.CoreCapture,
                "F05E233135E3A2AB87688EF08C282DA6949F0B32FA9CC0F486ABE823F532D314")
            || !ValidOracleCapture(
                evidence.ReactiveCapture,
                "BFCB03633CBD1F5644AEC885E8830D9489BA5A7100FFFECA0A4DDAB347E13202")
            || !ValidOracleCapture(
                evidence.VariantCapture,
                "8CD72FD3F0AB7AF95B21168C96C371B2C56D7410509046371D46305205E193A6")
            || !ValidOracleCapture(
                evidence.ReactiveTimingCapture,
                "F5A45F5A27B860BFBAE294A692381493619FACBB4E9EB68828B7A459A99ED495")
            || !ValidOracleCapture(
                evidence.DualBreathingCapture,
                "4A99DEECC64D7BEDA6813F691CB1C71A0131B21F8EB2FFD9E19EE9F8C21F45D8")
            || !ValidOracleCapture(
                evidence.ReactiveMainKeyMapCapture,
                "FC28B466691FE7E77A70613E455A18AB673DB2CE62E8ADC9B4DA6808E7B619EB")
            || !ValidOracleCapture(
                evidence.ReactiveFunctionKeyMapCapture,
                "2B8D6D11EA6BE76F6AA0C9CAED4520CFC1DA0171388423A99FFAC9960F0C6F78")
            || !ValidOracleCapture(
                evidence.RippleCapture,
                "5A38B7B453470EFECE9A383F761EEE073290E357801C8F9E9140886A152F17A9")
            || evidence.Protocol.BrightnessQuery != "03/83:010500"
            || evidence.Protocol.BrightnessSetPrefix != "03/03:0105"
            || !evidence.Protocol.TransitionRequests.SequenceEqual(
                ["00/86:000000", "00/84:0000", "03/0A:0500"])
            || !evidence.Protocol.TransitionResponses.SequenceEqual(
                ["00/86:010000", "00/84:0300", "03/0A:0500"])
            || evidence.Protocol.MatrixCommand != "03/0B"
            || evidence.Protocol.MatrixEnvelopeLength != 374
            || evidence.Protocol.MatrixFeatureReportLength != 375
            || evidence.Protocol.MatrixSlotCount != 102)
        {
            throw new UnvalidatedCommandException(
                "Lighting oracle evidence does not match the approved exact-device captures.");
        }
    }

    private static ProductionLightingDeviceEvidence TargetDeviceEvidence => new(
        TargetBladeDevice.Model,
        TargetBladeDevice.VendorId,
        TargetBladeDevice.ProductId,
        TargetBladeDevice.BiosVersion);

    private static bool ValidOracleCapture(
        ProductionLightingOracleCaptureEvidence capture,
        string expectedSha256) =>
        string.Equals(capture.Sha256, expectedSha256, StringComparison.Ordinal)
        && capture.Captured
        && capture.Decoded;

    private static byte[] ReadRequiredEmbeddedResource(string resourceName)
    {
        Assembly assembly = typeof(ProductionLightingEvidenceSource).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            throw new UnvalidatedCommandException(
                $"The embedded resource {resourceName} is absent.");
        }

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static void RequireApprovedHash(
        byte[] bytes,
        string approvedHash,
        string description)
    {
        string actualHash = Convert.ToHexString(SHA256.HashData(bytes));
        if (!string.Equals(actualHash, approvedHash, StringComparison.Ordinal))
        {
            throw new UnvalidatedCommandException(
                $"{description} SHA-256 {actualHash} is not code-approved.");
        }
    }

    private static ProductionLightingBuildIdentity ReadBuildIdentity(Assembly assembly)
    {
        string informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?.Trim()
            ?? string.Empty;
        string commitHash = informationalVersion.LastIndexOf('+') is int separator
            && separator >= 0
                ? informationalVersion[(separator + 1)..]
                : string.Empty;
        string assemblyPath = assembly.Location;
        string sha256 = File.Exists(assemblyPath)
            ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assemblyPath)))
            : string.Empty;
        return new(informationalVersion, commitHash, sha256);
    }
}
