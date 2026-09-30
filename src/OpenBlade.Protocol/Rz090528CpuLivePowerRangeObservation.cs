namespace OpenBlade.Protocol;

internal enum CpuPmObservationProfile { IdleThreeSamples, RangeSingleSample }
internal enum CpuLivePowerRangeReturnValueState { NotRead, Absent, PresentBoolean, WrongType, Unreadable }
// Pinned SSDA-AOD PPT entry 0x50001 is OBIT type One: it has continuous
// min/max metadata; the type-Zero discrete getter cannot match this entry.
internal enum CpuLivePowerRangeMetadataApplicability { Unknown, SourceMappedContinuousTypeOnePpt50001 }
internal enum CpuLivePowerRangeMetadataStatus { Complete, InvalidData, NativeFailure, DisposalFailed }
[Flags]
internal enum CpuLivePowerRangeMetadataStages
{
    None = 0, InvocationEntered = 1, InvocationReturned = 2, OutputEnumerated = 4,
    PropertiesRead = 8, Disposed = 16,
}
internal sealed record CpuLivePowerRangeMetadataCall<T>(T? Value, CpuLivePowerRangeMetadataStatus Status,
    CpuLivePowerRangeMetadataStages Stages, DateTimeOffset CapturedUtc, long MonotonicTimestamp,
    int? HResult = null, bool PostAdmissionPassed = false, CpuLivePowerRangeMetadataRaw? Raw = null) where T : class;
internal sealed record CpuLivePowerRangeVersion(uint Version, bool MissingReturnValueCompatibility);
internal sealed class CpuLivePowerRangeDValues
{
    private readonly uint[] values;
    internal CpuLivePowerRangeDValues(uint id, uint current, uint length, uint[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (id != 0x50001 || values.Length > 100 || length != values.Length)
        { throw new ArgumentException("Invalid bounded range metadata."); }
        Id = id; Current = current; Length = length; this.values = (uint[])values.Clone();
    }
    internal uint Id { get; }
    internal uint Current { get; }
    internal uint Length { get; }
    internal uint[] CopyValues() => (uint[])values.Clone();
}
internal sealed record CpuLivePowerRangeMinMax(uint Value, uint Current, uint Min, uint Max, uint Stepping);
// Three fixed getter operations only. The checkpoint must run after synchronous
// invocation, property enumeration and disposal before the reader continues.
internal interface IRz090528CpuLivePowerRangeMetadataReader
{
    CpuLivePowerRangeMetadataCall<CpuLivePowerRangeVersion> QueryVersion(Action checkpoint);
    CpuLivePowerRangeMetadataCall<CpuLivePowerRangeDValues> GetDValues(Action checkpoint);
    CpuLivePowerRangeMetadataCall<CpuLivePowerRangeMinMax> GetMinMaxSteppingValues(Action checkpoint);
}
internal sealed record CpuPmCapabilityObservation(uint Command, uint Argument, bool InvocationEntered,
    bool ResponseAcknowledged, uint? RawValue, DateTimeOffset? CapturedUtc, long? MonotonicTimestamp,
    bool PostReadAdmissionPassed, bool ReturnedWordTransportSucceeded = false);
internal sealed record CpuLivePowerRangeObservation(CpuPmCapRegisterRead? A0F, CpuPmCapRegisterRead? A0B,
    CpuPmCapRegisterRead? A13, CpuPmCapRegisterRead? A90, CpuPmCapabilityObservation? Capability,
    CpuLivePowerRangeMetadataCall<CpuLivePowerRangeVersion>? QueryVersion,
    CpuLivePowerRangeMetadataCall<CpuLivePowerRangeDValues>? DValues,
    CpuLivePowerRangeMetadataCall<CpuLivePowerRangeMinMax>? MinMax,
    CpuLivePowerRangeMetadataApplicability MetadataApplicability = CpuLivePowerRangeMetadataApplicability.Unknown)
{
    internal bool MetadataComplete => MetadataApplicability == CpuLivePowerRangeMetadataApplicability.SourceMappedContinuousTypeOnePpt50001
        && DValues is null && QueryVersion is { Value: { Version: 7 } } version && CompleteCall(version)
        && MinMax is { Value: { Value: 0x50001 } } minMax && CompleteCall(minMax);
    private static bool CompleteCall<T>(CpuLivePowerRangeMetadataCall<T> call) where T : class =>
        call.Status == CpuLivePowerRangeMetadataStatus.Complete && call.PostAdmissionPassed
        && call.Stages == (CpuLivePowerRangeMetadataStages.InvocationEntered | CpuLivePowerRangeMetadataStages.InvocationReturned
            | CpuLivePowerRangeMetadataStages.OutputEnumerated | CpuLivePowerRangeMetadataStages.PropertiesRead | CpuLivePowerRangeMetadataStages.Disposed);
    internal const uint RegisterA13 = 0x03820A13, RegisterA90 = 0x03820A90;
}

// Copied successfully decoded properties survive an incomplete getter. They are
// diagnostic evidence and cannot complete a metadata acquisition.
internal sealed class CpuLivePowerRangeMetadataRaw
{
    private readonly uint[]? values;
    internal CpuLivePowerRangeMetadataRaw(bool? returnValue = null, uint? version = null, uint? id = null,
        uint? current = null, uint? length = null, uint? min = null, uint? max = null,
        uint? stepping = null, uint? value = null, uint[]? dValuesBuffer = null,
        CpuLivePowerRangeReturnValueState returnValueState = CpuLivePowerRangeReturnValueState.NotRead)
    {
        if (dValuesBuffer?.Length > 100) { throw new ArgumentException("Range metadata exceeds its bound."); }
        ReturnValueState = returnValueState; ReturnValue = returnValue; Version = version; Id = id; Current = current; Length = length;
        Min = min; Max = max; Stepping = stepping; Value = value;
        values = dValuesBuffer is null ? null : (uint[])dValuesBuffer.Clone();
    }
    internal CpuLivePowerRangeReturnValueState ReturnValueState { get; }
    internal bool? ReturnValue { get; }
    internal uint? Version { get; }
    internal uint? Id { get; }
    internal uint? Current { get; }
    internal uint? Length { get; }
    internal uint? Min { get; }
    internal uint? Max { get; }
    internal uint? Stepping { get; }
    internal uint? Value { get; }
    internal uint[]? CopyDValuesBuffer() => values is null ? null : (uint[])values.Clone();
}
