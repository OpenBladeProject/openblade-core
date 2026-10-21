using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public enum TelemetrySource
{
    LiveService,
    LocalCsvReplay,
}

public enum TelemetryFreshness
{
    Fresh,
    Stale,
    Replay,
}

public enum TelemetryMetric
{
    BatteryPowerFlowMilliwatts,
    Fan1Rpm,
    Fan2Rpm,
    CpuTemperatureCelsius,
    GpuTemperatureCelsius,
}

public enum TelemetryEventKind
{
    PowerSourceChanged,
    ProfileChanged,
}

public sealed record TelemetryProfileIdentity(
    PowerSource BaseProfile,
    Guid? ApplicationProfileId,
    string? ApplicationProfileName)
{
    public string Value => ApplicationProfileId is { } applicationId
        ? $"{BaseProfile}|{applicationId:D}|{ApplicationProfileName}"
        : $"{BaseProfile}|base";

    public static TelemetryProfileIdentity FromStatus(ServiceStatusSnapshot status)
    {
        ArgumentNullException.ThrowIfNull(status);
        ApplicationProfileDefinition? application = status.ActiveApplicationProfileId is { } id
            ? status.Profiles.ApplicationProfiles.FirstOrDefault(candidate =>
                candidate.Enabled && candidate.Id == id)
            : null;
        return new TelemetryProfileIdentity(
            status.ActivePowerSource,
            application?.Id ?? status.ActiveApplicationProfileId,
            application?.Name);
    }
}

public sealed record TelemetrySample(
    DateTimeOffset TimestampUtc,
    long ServiceRevision,
    TelemetrySource Source,
    PowerSource PowerSource,
    string ProfileIdentity,
    int? BatteryPowerFlowMilliwatts,
    int? Fan1Rpm,
    int? Fan2Rpm,
    int? CpuTemperatureCelsius,
    int? GpuTemperatureCelsius)
{
    public TelemetrySource? RecordedSource { get; init; }

    public static TelemetrySample Project(
        ServiceStatusSnapshot status,
        DateTimeOffset timestampUtc,
        string? profileIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(status);
        LiveReadings? readings = status.DeviceVerified ? status.LiveReadings : null;
        string projectedIdentity = TelemetryProfileIdentity.FromStatus(status).Value;
        var sample = new TelemetrySample(
            timestampUtc,
            status.Revision,
            TelemetrySource.LiveService,
            status.ActivePowerSource,
            string.IsNullOrWhiteSpace(profileIdentity)
                ? projectedIdentity
                : profileIdentity.Trim(),
            readings?.BatteryPowerFlowMilliwatts,
            readings?.Fan1Rpm,
            readings?.Fan2Rpm,
            readings?.CpuTemperatureCelsius,
            readings?.GpuTemperatureCelsius);
        TelemetryValidation.ValidateSample(sample);
        return sample;
    }

    public int? Value(TelemetryMetric metric) => metric switch
    {
        TelemetryMetric.BatteryPowerFlowMilliwatts => BatteryPowerFlowMilliwatts,
        TelemetryMetric.Fan1Rpm => Fan1Rpm,
        TelemetryMetric.Fan2Rpm => Fan2Rpm,
        TelemetryMetric.CpuTemperatureCelsius => CpuTemperatureCelsius,
        TelemetryMetric.GpuTemperatureCelsius => GpuTemperatureCelsius,
        _ => throw new ArgumentOutOfRangeException(nameof(metric)),
    };

    public TelemetryFreshness GetFreshness(
        DateTimeOffset nowUtc,
        TimeSpan expectedInterval)
    {
        if (Source == TelemetrySource.LocalCsvReplay)
        {
            return TelemetryFreshness.Replay;
        }
        if (expectedInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedInterval));
        }
        TimeSpan age = nowUtc - TimestampUtc;
        TimeSpan staleAfter = expectedInterval > TimeSpan.MaxValue / 2
            ? TimeSpan.MaxValue
            : expectedInterval * 2;
        return age <= staleAfter ? TelemetryFreshness.Fresh : TelemetryFreshness.Stale;
    }
}

public sealed record TelemetryEvent(
    DateTimeOffset TimestampUtc,
    TelemetryEventKind Kind,
    string PreviousValue,
    string CurrentValue);

public sealed record TelemetrySessionMetadata(
    Guid SessionId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? EndedAtUtc,
    TelemetrySource Source)
{
    public TelemetrySource? RecordedSource { get; init; }

    public DateTimeOffset? ImportedAtUtc { get; init; }

    public string? OriginPath { get; init; }

    public static TelemetrySessionMetadata CreateLive(
        DateTimeOffset startedAtUtc,
        Guid? sessionId = null) => new(
            sessionId ?? Guid.NewGuid(),
            startedAtUtc,
            null,
            TelemetrySource.LiveService);
}

public sealed record TelemetrySessionDocument(
    TelemetrySessionMetadata Metadata,
    IReadOnlyList<TelemetrySample> Samples,
    IReadOnlyList<TelemetryEvent> Events);

internal static class TelemetryValidation
{
    public const int MaximumProfileIdentityLength = 256;
    public const int MaximumEventValueLength = 512;

    public static void ValidateSample(TelemetrySample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        ValidateUtc(sample.TimestampUtc, nameof(sample.TimestampUtc));
        if (sample.ServiceRevision < 0)
        {
            throw new InvalidDataException("A telemetry service revision cannot be negative.");
        }
        if (!Enum.IsDefined(sample.Source) || !Enum.IsDefined(sample.PowerSource))
        {
            throw new InvalidDataException("A telemetry sample contains an unknown source or power state.");
        }
        ValidateText(sample.ProfileIdentity, MaximumProfileIdentityLength, "profile identity");
        ValidateRange(sample.BatteryPowerFlowMilliwatts, -1_000_000, 1_000_000, "battery power");
        ValidateRange(sample.Fan1Rpm, 0, 20_000, "fan 1 speed");
        ValidateRange(sample.Fan2Rpm, 0, 20_000, "fan 2 speed");
        ValidateRange(sample.CpuTemperatureCelsius, 0, 115, "CPU temperature");
        ValidateRange(sample.GpuTemperatureCelsius, 0, 115, "GPU temperature");
    }

    public static void ValidateEvent(TelemetryEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ValidateUtc(value.TimestampUtc, nameof(value.TimestampUtc));
        if (!Enum.IsDefined(value.Kind))
        {
            throw new InvalidDataException("A telemetry event contains an unknown kind.");
        }
        ValidateText(value.PreviousValue, MaximumEventValueLength, "previous event value");
        ValidateText(value.CurrentValue, MaximumEventValueLength, "current event value");
    }

    public static void ValidateMetadata(TelemetrySessionMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (metadata.SessionId == Guid.Empty || !Enum.IsDefined(metadata.Source))
        {
            throw new InvalidDataException("Telemetry session metadata is invalid.");
        }
        ValidateUtc(metadata.StartedAtUtc, nameof(metadata.StartedAtUtc));
        if (metadata.EndedAtUtc is { } ended)
        {
            ValidateUtc(ended, nameof(metadata.EndedAtUtc));
            if (ended < metadata.StartedAtUtc)
            {
                throw new InvalidDataException("Telemetry session end precedes its start.");
            }
        }
    }

    public static void ValidateUtc(DateTimeOffset value, string name)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException($"{name} must use UTC.");
        }
    }

    private static void ValidateRange(int? value, int minimum, int maximum, string name)
    {
        if (value.HasValue && (value.Value < minimum || value.Value > maximum))
        {
            throw new InvalidDataException($"Telemetry {name} is outside its supported range.");
        }
    }

    private static void ValidateText(string value, int maximumLength, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength
            || value.Any(char.IsControl))
        {
            throw new InvalidDataException($"Telemetry {name} is invalid.");
        }
    }
}
