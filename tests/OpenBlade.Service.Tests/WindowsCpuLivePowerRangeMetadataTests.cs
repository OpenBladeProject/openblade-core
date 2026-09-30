using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class WindowsCpuLivePowerRangeMetadataTests
{
    [Fact]
    public void FixedContinuousGettersPreserveShadowAndRawIntervalWithoutGrantingRangeAuthority()
    {
        var sessions = new List<Session>();
        var reader = new WindowsCpuLivePowerRangeMetadata(Context(), new Scope(), _ =>
        {
            var session = new Session(); sessions.Add(session);
            session.Pack.Values["Current"] = 400u;
            session.Pack.Values["Min"] = 200000u; session.Pack.Values["Max"] = 0u; session.Pack.Values["Stepping"] = 0u;
            return session;
        });
        var version = reader.QueryVersion(() => { }); var minMax = reader.GetMinMaxSteppingValues(() => { });
        Assert.Equal(CpuLivePowerRangeMetadataStatus.Complete, version.Status);
        Assert.Equal(CpuLivePowerRangeMetadataStatus.Complete, minMax.Status);
        Assert.Equal(new CpuLivePowerRangeMinMax(0x50001, 400, 200000, 0, 0), minMax.Value);
        Assert.Equal(new[] { "version", "minmax" }, sessions.SelectMany(session => session.Calls));
        Assert.All(sessions, session => Assert.True(session.Disposed));
        Assert.True(sessions[0].Version.Disposed); Assert.True(sessions[1].MinMax.Disposed); Assert.True(sessions[1].Pack.Disposed);
        Assert.Equal(CompleteStages, minMax.Stages); Assert.False(minMax.PostAdmissionPassed);
        Assert.False(new CpuLivePowerRangeObservation(null, null, null, null, null, version, null, minMax,
            CpuLivePowerRangeMetadataApplicability.SourceMappedContinuousTypeOnePpt50001).MetadataComplete);
        Assert.Throws<NotSupportedException>(() => reader.GetDValues(() => throw new Exception()));
        Assert.Equal(2, sessions.Count);
    }

    [Theory]
    [InlineData("absent", true, CpuLivePowerRangeReturnValueState.Absent)]
    [InlineData("true", true, CpuLivePowerRangeReturnValueState.PresentBoolean)]
    [InlineData("false", false, CpuLivePowerRangeReturnValueState.PresentBoolean)]
    [InlineData("null", false, CpuLivePowerRangeReturnValueState.WrongType)]
    [InlineData("uint", false, CpuLivePowerRangeReturnValueState.WrongType)]
    [InlineData("unreadable", false, CpuLivePowerRangeReturnValueState.Unreadable)]
    [InlineData("enumeration", false, CpuLivePowerRangeReturnValueState.NotRead)]
    public void VersionCompatibilityRequiresExactSuccessfulEnumeration(string fault, bool complete, object expectedState)
    {
        var session = new Session(); ChangeReturn(session.Version, fault);
        var result = Reader(session).QueryVersion(() => { });
        Assert.Equal(complete, result.Status == CpuLivePowerRangeMetadataStatus.Complete);
        Assert.Equal((CpuLivePowerRangeReturnValueState)expectedState, result.Raw!.ReturnValueState);
        Assert.Equal(complete, result.Value is not null);
        if (complete) { Assert.Equal(fault == "absent", result.Value!.MissingReturnValueCompatibility); }
        Assert.True(session.Disposed); Assert.True(result.Stages.HasFlag(CpuLivePowerRangeMetadataStages.Disposed));
        if (fault == "enumeration") { Assert.False(result.Stages.HasFlag(CpuLivePowerRangeMetadataStages.OutputEnumerated)); }
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("false")]
    [InlineData("null")]
    [InlineData("uint")]
    [InlineData("unreadable")]
    public void MinMaxAlwaysRequiresActualBooleanTrue(string fault)
    {
        var session = new Session(); ChangeReturn(session.MinMax, fault);
        var result = Reader(session).GetMinMaxSteppingValues(() => { });
        Assert.Null(result.Value); Assert.NotEqual(CpuLivePowerRangeMetadataStatus.Complete, result.Status);
        Assert.True(session.Disposed); Assert.True(result.Stages.HasFlag(CpuLivePowerRangeMetadataStages.Disposed));
        if (fault != "unreadable") { Assert.Equal(125000u, result.Raw!.Max); }
    }

    [Theory]
    [InlineData("selector")]
    [InlineData("signed")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("alias")]
    [InlineData("extra")]
    [InlineData("pack")]
    [InlineData("header-extra")]
    [InlineData("version")]
    [InlineData("version-signed")]
    public void StrictFieldTypesAndNamesRejectBroadenedMetadata(string fault)
    {
        var session = new Session();
        switch (fault)
        {
            case "selector": session.Pack.Values["Value"] = 0x5000Au; break;
            case "signed": session.Pack.Values["Min"] = 0; break;
            case "missing": session.Pack.Values.Remove("Max"); break;
            case "duplicate": session.Pack.ExtraName = "Min"; break;
            case "alias": session.Pack.Values.Remove("Min"); session.Pack.Values["min"] = 0u; break;
            case "extra": session.Pack.Values["extra"] = 0u; break;
            case "pack": session.MinMax.Values["pack"] = new object(); break;
            case "header-extra": session.MinMax.Values["extra"] = 0u; break;
            case "version": session.Version.Values["version"] = 8u; break;
            case "version-signed": session.Version.Values["version"] = 7; break;
        }
        var reader = Reader(session);
        var result = fault.StartsWith("version", StringComparison.Ordinal)
            ? reader.QueryVersion(() => { }).Status : reader.GetMinMaxSteppingValues(() => { }).Status;
        Assert.Equal(CpuLivePowerRangeMetadataStatus.InvalidData, result); Assert.True(session.Disposed);
    }

    [Fact]
    public void MissingOrUnreadableFieldsRetainOnlySuccessfullyTypedPartialEvidence()
    {
        var session = new Session(); session.Pack.Values.Remove("Max");
        var missing = Reader(session).GetMinMaxSteppingValues(() => { });
        Assert.Null(missing.Value); Assert.Equal(0u, missing.Raw!.Current); Assert.Equal(0u, missing.Raw.Min);
        Assert.Null(missing.Raw.Max); Assert.Equal(0x50001u, missing.Raw.Value);
        session = new Session(); session.Pack.ThrowProperty = "Max";
        var unreadable = Reader(session).GetMinMaxSteppingValues(() => { });
        Assert.Null(unreadable.Value); Assert.Equal(CpuLivePowerRangeMetadataStatus.NativeFailure, unreadable.Status);
        Assert.Equal(0u, unreadable.Raw!.Current); Assert.Equal(0u, unreadable.Raw.Min); Assert.Null(unreadable.Raw.Max);
        Assert.False(unreadable.Stages.HasFlag(CpuLivePowerRangeMetadataStages.PropertiesRead));
        Assert.True(unreadable.Stages.HasFlag(CpuLivePowerRangeMetadataStages.Disposed));
    }

    [Theory]
    [InlineData("context")]
    [InlineData("scope")]
    [InlineData("revoked")]
    [InlineData("bios")]
    [InlineData("source")]
    [InlineData("cancel")]
    public void InvalidTrustOrCompatibilityContextDoesNotOpenProvider(string fault)
    {
        var context = fault == "context" ? null : Context();
        if (fault == "bios") { context = context! with { Identity = context.Identity with { BiosVersion = "2.03" } }; }
        if (fault == "source") { context = context! with { BootSequence = new(CpuLiveCurveBootSequenceSource.Unknown, 42) }; }
        var scope = fault == "scope" ? null : new Scope { Current = fault != "revoked" };
        int opens = 0;
        var reader = new WindowsCpuLivePowerRangeMetadata(context, scope, _ => { opens++; throw new Exception(); },
            fault == "cancel" ? new CancellationToken(true) : default);
        var result = reader.QueryVersion(() => { });
        Assert.Null(result.Value); Assert.Equal(0, opens);
        Assert.Equal(CpuLivePowerRangeMetadataStages.Disposed, result.Stages); // Known pre-open denial, no resources acquired.
    }

    [Theory]
    [InlineData("open")]
    [InlineData("invoke")]
    [InlineData("property")]
    [InlineData("dispose")]
    public void RevocationNeverSkipsConfirmedCleanup(string boundary)
    {
        var session = new Session(); var scope = new Scope();
        if (boundary == "invoke") { session.OnInvoke = () => scope.Current = false; }
        if (boundary == "property") { session.Pack.OnGet = () => scope.Current = false; }
        if (boundary == "dispose") { session.MinMax.OnDispose = () => scope.Current = false; }
        var reader = new WindowsCpuLivePowerRangeMetadata(Context(), scope, _ => { if (boundary == "open") { scope.Current = false; } return session; });
        var result = reader.GetMinMaxSteppingValues(() => { });
        Assert.Equal(CpuLivePowerRangeMetadataStatus.NativeFailure, result.Status); Assert.True(session.Disposed);
        Assert.True(result.Stages.HasFlag(CpuLivePowerRangeMetadataStages.Disposed)); Assert.False(result.PostAdmissionPassed);
        if (boundary == "dispose") { Assert.NotNull(result.Value); Assert.Equal(125000u, result.Raw!.Max); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisposalErrorWinsOverEarlierRevocationAndAllOtherResourcesStillDispose(bool cancellationFailure)
    {
        var session = new Session(); var scope = new Scope(); Action? check = null;
        // The output's cleanup completes, then reports revoked admission. A
        // later session disposal failure must not be hidden by that exception.
        session.MinMax.OnDispose = () => { scope.Current = false; check!(); };
        session.OnDispose = () => { if (cancellationFailure) { throw new OperationCanceledException(); } throw new IOException(); };
        var reader = new WindowsCpuLivePowerRangeMetadata(Context(), scope, checkpoint => { check = checkpoint; return session; });
        var result = reader.GetMinMaxSteppingValues(() => { });
        Assert.Equal(CpuLivePowerRangeMetadataStatus.DisposalFailed, result.Status);
        Assert.False(result.Stages.HasFlag(CpuLivePowerRangeMetadataStages.Disposed)); Assert.NotNull(result.Value);
        Assert.True(session.MinMax.Disposed); Assert.True(session.Pack.Disposed); Assert.True(session.Disposed);
    }

    [Theory]
    [InlineData("clean", true)]
    [InlineData("uncertain", false)]
    [InlineData("unclassified", false)]
    public void PartialOpenRequiresExplicitActualCleanupEvidence(string fault, bool clean)
    {
        bool cleanupAttempted = false;
        var reader = new WindowsCpuLivePowerRangeMetadata(Context(), new Scope(), _ =>
        {
            if (fault == "unclassified") { throw new IOException(); }
            cleanupAttempted = true;
            throw new CpuLivePowerRangeOpenFailure(new IOException(), fault == "clean");
        });
        var result = reader.QueryVersion(() => { });
        Assert.Equal(fault != "unclassified", cleanupAttempted);
        Assert.Equal(clean ? CpuLivePowerRangeMetadataStatus.NativeFailure : CpuLivePowerRangeMetadataStatus.DisposalFailed, result.Status);
        Assert.Equal(clean, result.Stages.HasFlag(CpuLivePowerRangeMetadataStages.Disposed));
        Assert.False(result.Stages.HasFlag(CpuLivePowerRangeMetadataStages.InvocationEntered)); Assert.Null(result.Value);
    }

    [Fact]
    public async Task CancellationCannotDetachBlockedSynchronousGetterOrDisposeItsLiveResources()
    {
        var session = new Session(); using var entered = new ManualResetEventSlim(); using var finish = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        session.OnInvoke = () => { entered.Set(); Assert.True(finish.Wait(TimeSpan.FromSeconds(10))); };
        var reader = new WindowsCpuLivePowerRangeMetadata(Context(), new Scope(), _ => session, cancellation.Token);
        var actual = Task.Run(() => reader.GetMinMaxSteppingValues(() => { }));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10))); cancellation.Cancel();
            Assert.False(actual.IsCompleted); Assert.False(session.Disposed); Assert.False(session.MinMax.Disposed);
        }
        finally { finish.Set(); }
        var result = await actual.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(CpuLivePowerRangeMetadataStatus.NativeFailure, result.Status);
        Assert.True(result.Stages.HasFlag(CpuLivePowerRangeMetadataStages.Disposed)); Assert.True(session.Disposed); Assert.True(session.MinMax.Disposed);
    }

    private const CpuLivePowerRangeMetadataStages CompleteStages = CpuLivePowerRangeMetadataStages.InvocationEntered
        | CpuLivePowerRangeMetadataStages.InvocationReturned | CpuLivePowerRangeMetadataStages.OutputEnumerated
        | CpuLivePowerRangeMetadataStages.PropertiesRead | CpuLivePowerRangeMetadataStages.Disposed;
    private static CpuLivePowerContext Context() => CpuLivePowerTuningTransactionTests.ContextFixture();
    private static WindowsCpuLivePowerRangeMetadata Reader(Session session) => new(Context(), new Scope(), _ => session);
    private static void ChangeReturn(Output output, string fault)
    {
        if (fault == "absent") { output.Values.Remove("ReturnValue"); }
        else if (fault == "unreadable") { output.ThrowProperty = "ReturnValue"; }
        else if (fault == "enumeration") { output.ThrowEnumeration = true; }
        else { output.Values["ReturnValue"] = fault switch { "true" => true, "false" => false, "uint" => 1u, _ => null }; }
    }
    private sealed class Scope : CpuLivePowerRangeTrustedScope
    {
        internal bool Current = true;
        internal override bool IsCurrent(CpuLivePowerContext context) => Current && context == Context();
    }
    private sealed class Session : IWindowsCpuPowerRangeSession
    {
        internal readonly Output Version = new(new() { ["version"] = 7u, ["ReturnValue"] = true });
        internal readonly Output Pack = new(new() { ["Current"] = 0u, ["Min"] = 0u, ["Max"] = 125000u, ["Stepping"] = 1u, ["Value"] = 0x50001u });
        internal readonly Output MinMax;
        internal readonly List<string> Calls = [];
        internal Action? OnInvoke, OnDispose; internal bool Disposed;
        internal Session() { MinMax = new(new() { ["ReturnValue"] = true, ["pack"] = Pack }); }
        public IWindowsCpuPowerRangeObject? QueryVersion(Action checkpoint, Action entered)
        { checkpoint(); Calls.Add("version"); entered(); OnInvoke?.Invoke(); return Version; }
        public IWindowsCpuPowerRangeObject? GetPowerMinMax(Action checkpoint, Action entered)
        { checkpoint(); Calls.Add("minmax"); entered(); OnInvoke?.Invoke(); return MinMax; }
        public void Dispose() { Disposed = true; OnDispose?.Invoke(); }
    }
    private sealed class Output(Dictionary<string, object?> values) : IWindowsCpuPowerRangeObject
    {
        internal readonly Dictionary<string, object?> Values = values;
        internal string? ExtraName, ThrowProperty; internal bool ThrowEnumeration, Disposed;
        internal Action? OnGet, OnDispose;
        public IEnumerable<string> Names
        {
            get
            {
                foreach (string name in Values.Keys) { yield return name; }
                if (ExtraName is not null) { yield return ExtraName; }
                if (ThrowEnumeration) { throw new IOException(); }
            }
        }
        public object? Get(string name) { if (name == ThrowProperty) { throw new IOException(); } OnGet?.Invoke(); return Values[name]; }
        public void Dispose()
        {
            foreach (var child in Values.Values.OfType<Output>()) { child.Dispose(); }
            Disposed = true; OnDispose?.Invoke();
        }
    }
}
