using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class WindowsCpuLivePowerRangeMetadataTests
{
    [Fact]
    public void VersionOnlyAcceptsCompletelyEnumeratedMissingAcknowledgement()
    {
        var missing = ReadVersion(new Object([Pair("version", 7u)]));
        Assert.Equal(CpuLivePowerRangeMetadataStatus.Complete, missing.Status);
        Assert.True(missing.Value!.MissingReturnValueCompatibility);
        foreach (object? ack in new object?[] { false, null, 1u, "true" })
        { Assert.Null(ReadVersion(new Object([Pair("version", 7u), Pair("ReturnValue", ack)])).Value); }
        Assert.Null(ReadVersion(new Object([Pair("version", 6u)])).Value);
        Assert.Null(ReadVersion(new Object([Pair("version", 7)])).Value);
    }

    [Fact]
    public void FailedEnumerationCannotBecomeMissingAcknowledgementCompatibility()
    {
        var result = ReadVersion(new Object([Pair("version", 7u)]) { FailEnumeration = true });
        Assert.Null(result.Value);
        Assert.Equal(CpuLivePowerRangeMetadataStatus.NativeFailure, result.Status);
        Assert.False(result.Stages.HasFlag(CpuLivePowerRangeMetadataStages.OutputEnumerated));
    }

    [Fact]
    public void MetadataRequiresPresentBooleanTrueAcknowledgement()
    {
        foreach (object? ack in new object?[] { false, null, 1u, "true" })
        { Assert.Null(ReadDValues(Envelope(DValues(), ack)).Value); }
        Assert.Null(ReadDValues(new Object([Pair("pack", DValues())])).Value);
        Assert.Null(ReadMinMax(new Object([Pair("pack", MinMax())])).Value);
    }

    [Fact]
    public void RawAcknowledgementDistinguishesAbsentBooleanWrongTypeAndUnreadable()
    {
        Assert.Equal(CpuLivePowerRangeReturnValueState.Absent,
            ReadDValues(new Object([Pair("pack", DValues())])).Raw!.ReturnValueState);
        var falseAck = ReadDValues(Envelope(DValues(), false));
        Assert.Equal(CpuLivePowerRangeReturnValueState.PresentBoolean, falseAck.Raw!.ReturnValueState);
        Assert.False(falseAck.Raw.ReturnValue);
        Assert.Equal(CpuLivePowerRangeReturnValueState.WrongType,
            ReadDValues(Envelope(DValues(), null)).Raw!.ReturnValueState);
        Assert.Equal(CpuLivePowerRangeReturnValueState.WrongType,
            ReadDValues(Envelope(DValues(), 1u)).Raw!.ReturnValueState);
        Assert.Equal(CpuLivePowerRangeReturnValueState.Unreadable,
            ReadDValues(Envelope(DValues()) with { FailRead = "ReturnValue" }).Raw!.ReturnValueState);
        Assert.Equal(CpuLivePowerRangeReturnValueState.Absent,
            ReadVersion(new Object([Pair("version", 7u)])).Raw!.ReturnValueState);
    }
    [Fact]
    public void ZeroValuesArePreservedAndArraysAreCopied()
    {
        uint[] values = [0, 85000, 80000];
        var result = ReadDValues(Envelope(DValues(values)));
        Assert.Equal(CpuLivePowerRangeMetadataStatus.Complete, result.Status);
        Assert.Equal(0u, result.Value!.Current);
        Assert.Equal(values, result.Value.CopyValues());
        values[1] = 1;
        var copy = result.Value.CopyValues(); copy[2] = 1;
        Assert.Equal(new uint[] { 0, 85000, 80000 }, result.Value.CopyValues());
        var minmax = ReadMinMax(Envelope(MinMax())).Value!;
        Assert.Equal(0u, minmax.Current); Assert.Equal(0u, minmax.Min); Assert.Equal(0u, minmax.Stepping);
        Assert.False(minmax.MissingReturnValueCompatibility);
    }

    [Fact]
    public void StrictEnvelopeRejectsMissingDuplicateUnexpectedWrongTypedAndUnreadableProperties()
    {
        Object[] failures =
        [
            new([Pair("ReturnValue", true)]),
            new([Pair("ReturnValue", true), Pair("pack", DValues()), Pair("other", 0u)]),
            new([Pair("ReturnValue", true), Pair("ReturnValue", true)]),
            new([Pair("ReturnValue", true), Pair("Pack", DValues())]),
            new([Pair("ReturnValue", true), Pair("pack", 0u)]),
            Envelope(DValues()) with { FailRead = "pack" },
        ];
        foreach (var failure in failures) { Assert.Null(ReadDValues(failure).Value); }
    }

    [Fact]
    public void DValuesRejectsEveryMalformedField()
    {
        foreach (string name in new[] { "Current", "ID", "Length", "DValuesBuffer" })
        {
            Assert.Null(ReadDValues(Envelope(DValues() with { Omit = name })).Value);
            Assert.Null(ReadDValues(Envelope(DValues() with { FailRead = name })).Value);
            var fields = DValues().Fields.Select(field => field.Key == name ? Pair(name, null) : field).ToArray();
            Assert.Null(ReadDValues(Envelope(new Object(fields))).Value);
        }
        Assert.Null(ReadDValues(Envelope(DValues() with { Fields = [Pair("Current", 0u), Pair("ID", 0x50002u), Pair("Length", 0u), Pair("DValuesBuffer", Array.Empty<uint>())] })).Value);
        Assert.Null(ReadDValues(Envelope(DValues(new uint[101]))).Value);
        Assert.Null(ReadDValues(Envelope(new Object([Pair("Current", 0u), Pair("ID", 0x50001u), Pair("Length", 1u), Pair("DValuesBuffer", Array.Empty<uint>())]))).Value);
        Assert.Null(ReadDValues(Envelope(DValues() with { Fields = [Pair("Current", 0u), Pair("ID", 0x50001u), Pair("Length", 0u), Pair("DValuesBuffer", Array.Empty<int>())] })).Value);
        Assert.Null(ReadDValues(Envelope(DValues() with { Duplicate = "ID" })).Value);
        Assert.Null(ReadDValues(Envelope(DValues() with { Extra = "Unexpected" })).Value);
        Assert.NotNull(ReadDValues(Envelope(DValues(Array.Empty<uint>()))).Value);
        Assert.NotNull(ReadDValues(Envelope(DValues(new uint[100]))).Value);
    }

    [Fact]
    public void MinMaxRejectsEveryMalformedFieldWithoutInterpretingInterval()
    {
        foreach (string name in new[] { "Current", "Max", "Min", "Stepping", "Value" })
        {
            Assert.Null(ReadMinMax(Envelope(MinMax() with { Omit = name })).Value);
            Assert.Null(ReadMinMax(Envelope(MinMax() with { FailRead = name })).Value);
            var fields = MinMax().Fields.Select(field => field.Key == name ? Pair(name, 0) : field).ToArray();
            Assert.Null(ReadMinMax(Envelope(new Object(fields))).Value);
        }
        var wrongId = MinMax().Fields.Select(field => field.Key == "Value" ? Pair("Value", 0x50002u) : field).ToArray();
        Assert.Null(ReadMinMax(Envelope(new Object(wrongId))).Value);
        Assert.Null(ReadMinMax(Envelope(MinMax() with { Duplicate = "Min" })).Value);
        Assert.Null(ReadMinMax(Envelope(MinMax() with { Extra = "Unexpected" })).Value);
    }

    [Fact]
    public void DisposalFailureRetainsDecodedTypedValueAndSanitizedHresult()
    {
        var output = Envelope(DValues()) with { FailDispose = true };
        var session = new Session(output);
        var result = new WindowsCpuLivePowerRangeMetadata(_ => session).GetDValues(() => { });
        Assert.NotNull(result.Value);
        Assert.Equal(CpuLivePowerRangeMetadataStatus.DisposalFailed, result.Status);
        Assert.NotNull(result.HResult);
        Assert.False(result.Stages.HasFlag(CpuLivePowerRangeMetadataStages.Disposed));
        Assert.True(session.Disposed);
        Assert.False(result.PostAdmissionPassed);
    }

    [Fact]
    public void PostInvocationAdmissionFailureStillDisposesResources()
    {
        var output = Envelope(DValues());
        var session = new Session(output);
        bool fail = false;
        session.AfterInvoke = () => fail = true;
        var result = new WindowsCpuLivePowerRangeMetadata(_ => session).GetDValues(() =>
        { if (fail) { throw new OperationCanceledException(); } });
        Assert.Equal(CpuLivePowerRangeMetadataStatus.NativeFailure, result.Status);
        Assert.True(result.Stages.HasFlag(CpuLivePowerRangeMetadataStages.InvocationReturned));
        Assert.True(output.Disposed); Assert.True(session.Disposed);
    }

    [Fact]
    public void IncompleteMetadataRetainsOnlySuccessfullyTypedRawProperties()
    {
        var result = ReadMinMax(Envelope(MinMax() with { Omit = "Max" }));
        Assert.Null(result.Value);
        Assert.Equal(CpuLivePowerRangeMetadataStatus.InvalidData, result.Status);
        Assert.True(result.Raw!.ReturnValue);
        Assert.Equal(0u, result.Raw.Current);
        Assert.Equal(0u, result.Raw.Min);
        Assert.Null(result.Raw.Max);
        Assert.Equal(0x50001u, result.Raw.Value);
        var failed = ReadMinMax(Envelope(MinMax() with { FailRead = "Max" }));
        Assert.Null(failed.Value); Assert.Equal(0u, failed.Raw!.Current); Assert.Null(failed.Raw.Max);
        var declined = ReadMinMax(Envelope(MinMax(), false));
        Assert.Null(declined.Value); Assert.False(declined.Raw!.ReturnValue); Assert.Equal(125000u, declined.Raw.Max);
    }

    [Fact]
    public void BoundaryFailureAfterDecodedOutputRetainsValueAndCompletesCleanup()
    {
        var output = Envelope(DValues());
        var session = new Session(output);
        var result = new WindowsCpuLivePowerRangeMetadata(_ => session).GetDValues(() =>
        { if (output.Disposed) { throw new Exception("private admission failure"); } });
        Assert.Equal(CpuLivePowerRangeMetadataStatus.NativeFailure, result.Status);
        Assert.NotNull(result.Value); Assert.NotNull(result.Raw);
        Assert.True(session.Disposed); Assert.True(output.Disposed);
        Assert.True(result.Stages.HasFlag(CpuLivePowerRangeMetadataStages.Disposed));
        Assert.False(result.PostAdmissionPassed);
    }
    [Fact]
    public void FixedMethodsAndIdentifierNeverSelectRunCommand()
    {
        var methods = new List<(string, uint?)>();
        var reader = new WindowsCpuLivePowerRangeMetadata(_ => new Session(new Object([Pair("version", 7u)]))
        { OnInvoke = (method, id) => methods.Add((method, id)) });
        reader.QueryVersion(() => { }); reader.GetDValues(() => { }); reader.GetMinMaxSteppingValues(() => { });
        Assert.Equal(new (string, uint?)[] { ("QueryVersion", null), ("Getdvalues", 0x50001), ("GetMinMaxSteppingValues", 0x50001) }, methods);
    }

    [Theory]
    [InlineData(0u, 0u, 125000u, 1u)] // Attended r6 raw tuple; Current is a shadow, not measured watts.
    [InlineData(70000u, 10000u, 150000u, 10u)]
    public void MissingMinMaxAcknowledgementIsSingleUseProfileCandidate(uint current, uint min, uint max, uint step)
    {
        var version = new Object([Pair("version", 7u)]);
        var output = MissingMinMax(current, min, max, step);
        int opens = 0;
        var reader = new WindowsCpuLivePowerRangeMetadata(_ => new Session(opens++ == 0 ? version : output), () => ExactHost());
        var q = reader.QueryVersion(() => { });
        var result = reader.GetMinMaxSteppingValues(() => { });
        Assert.Equal(CpuLivePowerRangeMetadataStatus.Complete, result.Status);
        Assert.Equal(new CpuLivePowerRangeMinMax(0x50001, current, min, max, step, true), result.Value);
        Assert.Null(result.Raw!.ReturnValue); Assert.Equal(CpuLivePowerRangeReturnValueState.Absent, result.Raw.ReturnValueState);
        Assert.False(result.PostAdmissionPassed);
        Assert.False(new CpuLivePowerRangeObservation(null, null, null, null, null, q, null, result,
            CpuLivePowerRangeMetadataApplicability.SourceMappedContinuousTypeOnePpt50001).MetadataComplete);
        Assert.Null(reader.GetMinMaxSteppingValues(() => { }).Value);
    }

    [Theory]
    [InlineData("bios")]
    [InlineData("no-identity")]
    [InlineData("no-version")]
    [InlineData("version")]
    [InlineData("version-cleanup")]
    [InlineData("intervening")]
    [InlineData("enumeration")]
    [InlineData("duplicate")]
    [InlineData("case")]
    [InlineData("false")]
    [InlineData("null")]
    [InlineData("unreadable")]
    [InlineData("step")]
    [InlineData("interval")]
    [InlineData("current")]
    [InlineData("cleanup")]
    [InlineData("post-revocation")]
    [InlineData("unknown")]
    [InlineData("wrong-type")]
    public void MissingAcknowledgementEligibilityCannotSurviveFailureOrBroadenThePackage(string fault)
    {
        var version = new Object([Pair("version", fault == "version" ? 8u : 7u)]) { FailDispose = fault == "version-cleanup" };
        var pack = (Object)MissingMinMax().Fields[0].Value!;
        if (fault == "duplicate") { pack = pack with { Duplicate = "Min" }; }
        if (fault == "unknown") { pack = pack with { Extra = "unexpected" }; }
        if (fault == "wrong-type") { pack = pack with { Fields = pack.Fields.Select(field => field.Key == "Current" ? Pair("Current", 0) : field).ToArray() }; }
        if (fault == "case") { pack = pack with { Omit = "Min", Extra = "min" }; }
        if (fault == "step") { pack = (Object)MissingMinMax(step: 0).Fields[0].Value!; }
        if (fault == "interval") { pack = (Object)MissingMinMax(min: 125001).Fields[0].Value!; }
        if (fault == "current") { pack = (Object)MissingMinMax(current: 125001).Fields[0].Value!; }
        var output = new Object([Pair("pack", pack)]) { FailEnumeration = fault == "enumeration", FailDispose = fault == "cleanup" };
        if (fault == "false") { output = Envelope(pack, false); }
        if (fault == "null") { output = Envelope(pack, null); }
        if (fault == "unreadable") { output = Envelope(pack) with { FailRead = "ReturnValue" }; }
        int opens = 0;
        var reader = new WindowsCpuLivePowerRangeMetadata(_ => new Session(opens++ == 0 && fault != "no-version" ? version : output),
            fault == "no-identity" ? null : () => ExactHost(fault == "bios" ? "2.03" : "2.02"));
        if (fault != "no-version") { reader.QueryVersion(() => { }); }
        if (fault == "intervening") { reader.GetDValues(() => { }); }
        var result = reader.GetMinMaxSteppingValues(() =>
        { if (fault == "post-revocation" && output.Disposed) { throw new OperationCanceledException(); } });
        Assert.Null(result.Value); Assert.NotEqual(CpuLivePowerRangeMetadataStatus.Complete, result.Status);
        Assert.False(result.PostAdmissionPassed);
    }

    [Fact]
    public void ConstructionDoesNotReadIdentityAndFreshIdentityRevocationCannotReuseVersion()
    {
        int identities = 0, opens = 0; bool revoked = false;
        var reader = new WindowsCpuLivePowerRangeMetadata(_ => new Session(opens++ == 0
            ? new Object([Pair("version", 7u)]) : MissingMinMax()),
            () => { identities++; return ExactHost(revoked ? "2.03" : "2.02"); });
        Assert.Equal(0, identities); Assert.Equal(0, opens);
        Assert.Equal(CpuLivePowerRangeMetadataStatus.Complete, reader.QueryVersion(() => { }).Status);
        revoked = true;
        Assert.Null(reader.GetMinMaxSteppingValues(() => { }).Value); Assert.Equal(1, opens);
        revoked = false;
        Assert.Null(reader.GetMinMaxSteppingValues(() => { }).Value);
    }

    private static HostIdentityReadResult ExactHost(string bios = "2.02") =>
        new(new("Razer", "Blade 16 - RZ09-0528", "RZ09-05289EN4", bios), null);
    private static Object MissingMinMax(uint current = 0, uint min = 0, uint max = 125000, uint step = 1) =>
        new([Pair("pack", new Object([Pair("Current", current), Pair("Min", min), Pair("Max", max),
            Pair("Stepping", step), Pair("Value", 0x50001u)]))]);

    private static CpuLivePowerRangeMetadataCall<CpuLivePowerRangeVersion> ReadVersion(Object value) =>
        new WindowsCpuLivePowerRangeMetadata(_ => new Session(value)).QueryVersion(() => { });
    private static CpuLivePowerRangeMetadataCall<CpuLivePowerRangeDValues> ReadDValues(Object value) =>
        new WindowsCpuLivePowerRangeMetadata(_ => new Session(value)).GetDValues(() => { });
    private static CpuLivePowerRangeMetadataCall<CpuLivePowerRangeMinMax> ReadMinMax(Object value) =>
        new WindowsCpuLivePowerRangeMetadata(_ => new Session(value)).GetMinMaxSteppingValues(() => { });
    private static KeyValuePair<string, object?> Pair(string name, object? value) => new(name, value);
    private static Object Envelope(Object pack) => Envelope(pack, true);
    private static Object Envelope(Object pack, object? ack) => new([Pair("ReturnValue", ack), Pair("pack", pack)]);
    private static Object DValues(uint[]? values = null) => new([Pair("Current", 0u), Pair("ID", 0x50001u),
        Pair("Length", (uint)(values?.Length ?? 2)), Pair("DValuesBuffer", values ?? [85000u, 80000u])]);
    private static Object MinMax() => new([Pair("Current", 0u), Pair("Max", 125000u), Pair("Min", 0u), Pair("Stepping", 0u), Pair("Value", 0x50001u)]);

    private sealed record Object(KeyValuePair<string, object?>[] Fields) : IWindowsCpuRangeWmiObject
    {
        public string? Omit { get; init; }
        public string? Duplicate { get; init; }
        public string? Extra { get; init; }
        public string? FailRead { get; init; }
        public bool FailEnumeration { get; init; }
        public bool FailDispose { get; init; }
        public bool Disposed { get; private set; }
        public CpuLivePowerRangeMetadataStages CompletedStages { get; set; }
        public CpuLivePowerRangeMetadataRaw? Raw { get; set; }
        public IEnumerable<string> Names
        {
            get
            {
                foreach (var propertyEntry in Fields) { if (propertyEntry.Key != Omit) { yield return propertyEntry.Key; } }
                if (Duplicate is not null) { yield return Duplicate; }
                if (Extra is not null) { yield return Extra; }
                if (FailEnumeration) { throw new InvalidOperationException("private path never published"); }
            }
        }
        public object? Get(string name) => name == FailRead ? throw new InvalidOperationException("private path never published")
            : Fields.Single(field => field.Key == name).Value;
        public void Dispose()
        {
            Disposed = true;
            foreach (var field in Fields) { if (field.Value is Object child) { child.Dispose(); } }
            if (FailDispose) { throw new InvalidOperationException("private path never published"); }
        }
    }
    private sealed class Session(Object output) : IWindowsCpuRangeWmiSession
    {
        public bool Disposed { get; private set; }
        public Action? AfterInvoke { get; set; }
        public Action<string, uint?>? OnInvoke { get; init; }
        public IWindowsCpuRangeWmiObject Invoke(string method, uint? id, Action checkpoint, Action invocationEntered)
        { invocationEntered(); OnInvoke?.Invoke(method, id); AfterInvoke?.Invoke(); return output; }
        public void Dispose() => Disposed = true;
    }
}
