using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class WindowsCpuLiveCurveRangeMetadataTests
{
    [Theory]
    [InlineData("2.02")]
    [InlineData("2.03")]
    [InlineData("Unknown")]
    public void FixedGettersReturnMagnitudeRangeAndKeepCurrentAsFirmwareShadow(string bios)
    {
        var session = new Session();
        session.Pack.Values["Current"] = (uint)400;
        session.Pack.Values["Min"] = (uint)2;
        session.Pack.Values["Max"] = (uint)12;
        var result = Reader(session).Read(Context() with { Identity = Context().Identity with { BiosVersion = bios } }, new Scope());
        Assert.Equal(CpuLiveCurveRangeStatus.Complete, result.Status);
        Assert.Equal(new CpuLiveCurveTrustedRange(0x5000A, 2, 12, 1, true), result.Range);
        Assert.Equal((uint)400, result.MinMax.FirmwareShadow);
        Assert.Equal(new[] { "version", "minmax" }, session.Calls);
        Assert.True(session.Disposed); Assert.True(session.Version.Disposed); Assert.True(session.MinMax.Disposed); Assert.True(session.Pack.Disposed);
        Assert.True(CpuLiveCurveCommand.TryCreate(0, -3, 0xFF55, out var negative));
        Assert.True(result.Range!.Allows(negative));
        Assert.True(CpuLiveCurveCommand.TryCreate(0, 3, 0xFF55, out var positive));
        Assert.False(result.Range.Allows(positive));
    }

    [Theory]
    [InlineData("absent", true, CpuLiveCurveRangeReturnState.Absent)]
    [InlineData("true", true, CpuLiveCurveRangeReturnState.Boolean)]
    [InlineData("false", false, CpuLiveCurveRangeReturnState.Boolean)]
    [InlineData("null", false, CpuLiveCurveRangeReturnState.WrongType)]
    [InlineData("uint", false, CpuLiveCurveRangeReturnState.WrongType)]
    [InlineData("unreadable", false, CpuLiveCurveRangeReturnState.Unreadable)]
    [InlineData("enumeration", false, CpuLiveCurveRangeReturnState.NotRead)]
    [InlineData("absent-enumeration", false, CpuLiveCurveRangeReturnState.NotRead)]
    public void VersionCompatibilityRequiresConfirmedAbsence(string fault, bool valid, object expectedState)
    {
        var session = new Session();
        ChangeReturn(session.Version, fault);
        var result = Reader(session).Read(Context(), new Scope());
        Assert.Equal(valid, result.Range is not null);
        Assert.Equal((CpuLiveCurveRangeReturnState)expectedState, result.Version.ReturnState);
        Assert.Equal(valid ? 2 : 1, session.Calls.Count);
        Assert.True(session.Disposed);
    }

    [Theory]
    [InlineData("absent", true, CpuLiveCurveRangeReturnState.Absent)]
    [InlineData("true", true, CpuLiveCurveRangeReturnState.Boolean)]
    [InlineData("false", false, CpuLiveCurveRangeReturnState.Boolean)]
    [InlineData("null", false, CpuLiveCurveRangeReturnState.WrongType)]
    [InlineData("uint", false, CpuLiveCurveRangeReturnState.WrongType)]
    [InlineData("unreadable", false, CpuLiveCurveRangeReturnState.Unreadable)]
    [InlineData("enumeration", false, CpuLiveCurveRangeReturnState.NotRead)]
    [InlineData("absent-enumeration", false, CpuLiveCurveRangeReturnState.NotRead)]
    public void MinMaxCompatibilityRequiresConfirmedAbsence(string fault, bool valid, object expectedState)
    {
        var session = new Session(); ChangeReturn(session.MinMax, fault);
        var result = Reader(session).Read(Context(), new Scope());
        Assert.Equal(valid, result.Range is not null); Assert.True(session.Disposed); Assert.True(result.CleanupComplete);
        Assert.Equal((CpuLiveCurveRangeReturnState)expectedState, result.MinMax.ReturnState);
    }

    [Theory]
    [MemberData(nameof(MalformedCases))]
    public void MalformedOrBroadenedMetadataIsIncomplete(string fault, bool absent)
    {
        var session = new Session();
        if (absent) { ChangeReturn(session.MinMax, "absent"); }
        switch (fault)
        {
            case "selector": session.Pack.Values["Value"] = (uint)0x50001; break;
            case "int": session.Pack.Values["Min"] = 0; break;
            case "current-int": session.Pack.Values["Current"] = 0; break;
            case "max-int": session.Pack.Values["Max"] = 50; break;
            case "step-int": session.Pack.Values["Stepping"] = 1; break;
            case "selector-int": session.Pack.Values["Value"] = 0x5000A; break;
            case "null": session.Pack.Values["Current"] = null; break;
            case "missing": session.Pack.Values.Remove("Current"); break;
            case "duplicate": session.Pack.Extra = "Min"; break;
            case "alias": session.Pack.Values.Remove("Min"); session.Pack.Values["min"] = (uint)0; break;
            case "unknown": session.Pack.Values["Other"] = (uint)0; break;
            case "step": session.Pack.Values["Stepping"] = (uint)2; break;
            case "wide": session.Pack.Values["Max"] = (uint)51; break;
            case "ordered": session.Pack.Values["Min"] = (uint)51; break;
            case "header-alias": session.MinMax.Values.Remove("pack"); session.MinMax.Values["Pack"] = session.Pack; break;
            case "header-unknown": session.MinMax.Values["Other"] = true; break;
            case "header-duplicate": session.MinMax.Extra = "pack"; break;
            case "return-alias": session.MinMax.Values.Remove("ReturnValue"); session.MinMax.Values["returnValue"] = true; break;
            case "header-unreadable": session.MinMax.ThrowProperty = "pack"; break;
            case "pack-enumeration": session.Pack.ThrowEnumeration = true; break;
            case "not-pack": session.MinMax.Values["pack"] = new object(); break;
            case "version-int": session.Version.Values["version"] = 7; break;
        }
        var result = Reader(session).Read(Context(), new Scope());
        Assert.Null(result.Range); Assert.True(session.Disposed); Assert.True(result.CleanupComplete);
    }

    public static IEnumerable<object[]> MalformedCases()
    {
        foreach (string fault in new[] { "selector", "int", "current-int", "max-int", "step-int", "selector-int", "null",
            "missing", "duplicate", "alias", "unknown", "step", "wide", "ordered", "header-alias", "header-unknown",
            "header-duplicate", "return-alias", "header-unreadable", "pack-enumeration", "not-pack", "version-int" })
        {
            yield return [fault, false];
            yield return [fault, true];
        }
    }

    [Theory]
    [InlineData(6u, false)]
    [InlineData(7u, true)]
    [InlineData(8u, false)]
    public void BothMissingAcknowledgementsRequireCompleteExactVersion7(uint version, bool valid)
    {
        var session = new Session(); session.Version.Values["version"] = version;
        ChangeReturn(session.Version, "absent"); ChangeReturn(session.MinMax, "absent");
        var result = Reader(session).Read(Context(), new Scope());
        Assert.Equal(valid, result.Range is not null);
        Assert.Equal(valid ? CpuLiveCurveRangeStatus.Complete : CpuLiveCurveRangeStatus.Incomplete, result.Status);
        Assert.Equal(valid ? new[] { "version", "minmax" } : new[] { "version" }, session.Calls);
        Assert.True(result.CleanupComplete); Assert.True(session.Disposed);
    }

    [Theory]
    [InlineData(1u, false, false)]
    [InlineData(2u, false, true)]
    [InlineData(12u, false, true)]
    [InlineData(13u, false, false)]
    [InlineData(1u, true, true)]
    [InlineData(13u, true, true)]
    public void ShadowGeometryOnlyRejectsMissingAcknowledgement(uint shadow, bool explicitTrue, bool valid)
    {
        var session = new Session();
        if (!explicitTrue) { ChangeReturn(session.MinMax, "absent"); }
        session.Pack.Values["Current"] = shadow; session.Pack.Values["Min"] = (uint)2; session.Pack.Values["Max"] = (uint)12;
        var result = Reader(session).Read(Context(), new Scope());
        Assert.Equal(valid, result.Range is not null); Assert.Equal(shadow, result.MinMax.FirmwareShadow);
        Assert.True(result.CleanupComplete);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("scope")]
    [InlineData("revoked")]
    [InlineData("model")]
    [InlineData("source")]
    [InlineData("cancel")]
    public void InvalidTrustNeverOpensProvider(string fault)
    {
        int opens = 0;
        var reader = new WindowsCpuLiveCurveRangeMetadata(_ => { opens++; throw new Exception(); });
        var context = fault == "null" ? null : Context();
        if (fault == "model") { context = context! with { Identity = context.Identity with { Model = "RZ09-9999" } }; }
        if (fault == "source") { context = context! with { BootSequence = new(CpuLiveCurveBootSequenceSource.Unknown, 1) }; }
        Scope? scope = fault == "scope" ? null : new() { Current = fault != "revoked" };
        var result = reader.Read(context, scope, fault == "cancel" ? new CancellationToken(true) : default);
        Assert.Null(result.Range); Assert.Equal(0, opens);
    }

    [Theory]
    [InlineData("open")]
    [InlineData("version")]
    [InlineData("minmax")]
    [InlineData("property")]
    [InlineData("cleanup")]
    public void RevocationAtEveryBoundaryDeniesButDisposes(string boundary)
    {
        var scope = new Scope(); var session = new Session();
        if (boundary == "version") { session.OnVersion = () => scope.Current = false; }
        if (boundary == "minmax") { session.OnMinMax = () => scope.Current = false; }
        if (boundary == "property") { session.Pack.OnGet = () => scope.Current = false; }
        if (boundary == "cleanup") { session.MinMax.OnDispose = () => scope.Current = false; }
        var reader = new WindowsCpuLiveCurveRangeMetadata(_ => { if (boundary == "open") { scope.Current = false; } return session; });
        var result = reader.Read(Context(), scope);
        Assert.Null(result.Range); Assert.Equal(CpuLiveCurveRangeStatus.AdmissionDenied, result.Status);
        Assert.True(session.Disposed); Assert.True(result.CleanupComplete);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CleanupFailureIsStickyEvenWhenAdmissionAlsoRevokes(bool absent)
    {
        var scope = new Scope(); var session = new Session();
        if (absent) { ChangeReturn(session.Version, "absent"); ChangeReturn(session.MinMax, "absent"); }
        session.MinMax.OnDispose = () => { scope.Current = false; throw new InvalidOperationException(); };
        var result = Reader(session).Read(Context(), scope);
        Assert.Equal(CpuLiveCurveRangeStatus.CleanupUncertain, result.Status);
        Assert.False(result.CleanupComplete); Assert.Null(result.Range); Assert.True(session.Disposed); Assert.True(session.Version.Disposed);
    }

    [Theory]
    [InlineData("minmax")]
    [InlineData("property")]
    [InlineData("cleanup")]
    public void MissingAcknowledgementCompatibilityNeverSurvivesRevocation(string boundary)
    {
        var scope = new Scope(); var session = new Session();
        ChangeReturn(session.Version, "absent"); ChangeReturn(session.MinMax, "absent");
        if (boundary == "minmax") { session.OnMinMax = () => scope.Current = false; }
        if (boundary == "property") { session.Pack.OnGet = () => scope.Current = false; }
        if (boundary == "cleanup") { session.MinMax.OnDispose = () => scope.Current = false; }
        var result = Reader(session).Read(Context(), scope);
        Assert.Null(result.Range); Assert.Equal(CpuLiveCurveRangeStatus.AdmissionDenied, result.Status);
        Assert.True(result.CleanupComplete); Assert.True(session.Disposed);
    }

    [Fact]
    public async Task CancellationDoesNotPretendBlockedSynchronousGetterHasTerminated()
    {
        var session = new Session(); using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var cancel = new CancellationTokenSource();
        session.OnMinMax = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); };
        var worker = Task.Run(() => Reader(session).Read(Context(), new Scope(), cancel.Token));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10))); cancel.Cancel();
            Assert.False(worker.IsCompleted); Assert.False(session.Disposed);
        }
        finally { release.Set(); }
        var result = await worker.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(result.Range); Assert.True(result.CleanupComplete); Assert.True(session.Disposed);
    }

    private static CpuLiveCurveContext Context() => CpuLiveCurveTuningTransactionTests.ContextFixture();
    private static WindowsCpuLiveCurveRangeMetadata Reader(Session session) => new(_ => session);
    private static void ChangeReturn(Output output, string fault)
    {
        if (fault is "absent" or "absent-enumeration")
        { output.Values.Remove("ReturnValue"); output.ThrowEnumeration = fault == "absent-enumeration"; }
        else if (fault == "unreadable") { output.ThrowProperty = "ReturnValue"; }
        else if (fault == "enumeration") { output.ThrowEnumeration = true; }
        else { output.Values["ReturnValue"] = fault switch { "true" => true, "false" => false, "uint" => (uint)1, _ => null }; }
    }
    private sealed class Scope : CpuLiveCurveRangeTrustedScope
    {
        internal bool Current = true;
        internal override bool IsCurrent(CpuLiveCurveContext exactContext) => Current && Context().MatchesHardwareContext(exactContext);
    }
    private sealed class Session : IWindowsCpuCurveRangeSession
    {
        internal readonly Output Version = new(new() { ["version"] = (uint)7, ["ReturnValue"] = true });
        internal readonly Output Pack = new(new()
        {
            ["Current"] = (uint)0,
            ["Min"] = (uint)0,
            ["Max"] = (uint)50,
            ["Stepping"] = (uint)1,
            ["Value"] = (uint)0x5000A,
        });
        internal readonly Output MinMax;
        internal readonly List<string> Calls = [];
        internal Action? OnVersion, OnMinMax;
        internal bool Disposed;
        internal Session() { MinMax = new(new() { ["ReturnValue"] = true, ["pack"] = Pack }); }
        public IWindowsCpuCurveRangeObject? QueryVersion(Action checkpoint) { Calls.Add("version"); OnVersion?.Invoke(); return Version; }
        public IWindowsCpuCurveRangeObject? GetCurveMinMax(Action checkpoint) { Calls.Add("minmax"); OnMinMax?.Invoke(); return MinMax; }
        public void Dispose() { Disposed = true; }
    }
    private sealed class Output(Dictionary<string, object?> values) : IWindowsCpuCurveRangeObject
    {
        internal readonly Dictionary<string, object?> Values = values;
        internal string? Extra, ThrowProperty;
        internal bool ThrowEnumeration, Disposed;
        internal Action? OnGet, OnDispose;
        public IEnumerable<string> Names
        {
            get
            {
                foreach (var name in Values.Keys) { yield return name; }
                if (ThrowEnumeration) { throw new InvalidOperationException(); }
                if (Extra is not null) { yield return Extra; }
            }
        }
        public object? Get(string name) { OnGet?.Invoke(); if (name == ThrowProperty) { throw new InvalidOperationException(); } return Values[name]; }
        public void Dispose()
        {
            Disposed = true;
            foreach (var child in Values.Values.OfType<Output>()) { child.Dispose(); }
            OnDispose?.Invoke();
        }
    }
}
