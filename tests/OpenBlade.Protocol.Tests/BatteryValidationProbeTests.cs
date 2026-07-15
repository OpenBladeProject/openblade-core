using Microsoft.Win32.SafeHandles;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class BatteryValidationProbeTests
{
    private static readonly HostIdentityReadResult ExactHost = new(
        new HostIdentityEvidence("Razer", "Blade 16 - RZ09-0581", "RZ09-05819EN4", "3.01"),
        null);

    private static readonly HidHandleIdentity ExactHandle = new(
        0x1532, 0x02E0, 0x0001, 0x0002, 91);

    [Fact]
    public void Evaluate_HostBlockStopsBeforeGuardAndNativeEvidence()
    {
        List<string> events = [];
        ScriptedNative native = new(events);
        var evaluator = new WindowsRazerHidEvidenceEvaluator(
            new ScriptedIdentitySource(
                new HostIdentityReadResult(null, HidSessionFailureKind.IdentityBlocked),
                events),
            new ScriptedGuard(new HardwareAccessDecision([], null), events),
            native,
            ControllerExclusivityPolicy.Strict);

        using HidEvidenceEvaluation evaluation = evaluator.Evaluate();

        Assert.Equal(BatteryValidationProbeStage.HostIdentity, evaluation.Result.Stage);
        Assert.Equal(
            BatteryValidationProbeClassification.HostIdentityBlocked,
            evaluation.Result.Classification);
        Assert.False(evaluation.Result.HostIdentityMatched);
        Assert.Equal(["identity"], events);
        Assert.Equal(0, native.BindCalls);
    }

    [Theory]
    [InlineData(false, BatteryValidationProbeClassification.ConflictBlocked)]
    [InlineData(true, BatteryValidationProbeClassification.ConflictDetectionFailed)]
    public void Evaluate_ConflictGateStopsBeforeEnumeration(
        bool detectionFailure,
        BatteryValidationProbeClassification expected)
    {
        List<string> events = [];
        ScriptedNative native = new(events);
        HardwareAccessDecision access = detectionFailure
            ? new([], "unavailable")
            : new(["Razer Synapse Service"], null);
        var evaluator = CreateEvaluator(native, events, access);

        using HidEvidenceEvaluation evaluation = evaluator.Evaluate();

        Assert.Equal(BatteryValidationProbeStage.ConflictCheck, evaluation.Result.Stage);
        Assert.Equal(expected, evaluation.Result.Classification);
        Assert.Equal(["identity", "guard"], events);
        Assert.Equal(0, native.EnumerateCalls);
        Assert.Equal(0, native.BindCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Evaluate_AdvisorySkipsOnlyControllerPresenceClassification(
        bool detectionFailure)
    {
        List<string> events = [];
        ScriptedNative native = new(events);
        native.Add("exact", ExactHandle);
        HardwareAccessDecision access = detectionFailure
            ? new([], "unavailable")
            : new(["Razer Synapse Service"], null);

        using HidEvidenceEvaluation evaluation = CreateEvaluator(
            native,
            events,
            access,
            ControllerExclusivityPolicy.Advisory).Evaluate();

        Assert.Equal(BatteryValidationProbeClassification.Ready, evaluation.Result.Classification);
        Assert.False(evaluation.Result.ConflictCheckPassed);
        Assert.DoesNotContain("guard", events);
        Assert.Equal(1, evaluation.Result.ExactMatchCount);
    }

    [Fact]
    public void Evaluate_AdvisoryStillRejectsWrongHostIdentity()
    {
        var evaluator = new WindowsRazerHidEvidenceEvaluator(
            new ScriptedIdentitySource(
                new HostIdentityReadResult(null, HidSessionFailureKind.IdentityBlocked),
                null),
            new ScriptedGuard(new HardwareAccessDecision(["controller"], null), null),
            new ScriptedNative([]),
            ControllerExclusivityPolicy.Advisory);

        using HidEvidenceEvaluation evaluation = evaluator.Evaluate();

        Assert.Equal(
            BatteryValidationProbeClassification.HostIdentityBlocked,
            evaluation.Result.Classification);
    }

    [Fact]
    public void Evaluate_ReadyReportsClosedMetadataAndDisposesWithoutBinding()
    {
        List<string> events = [];
        ScriptedNative native = new(events);
        OpenedCandidateHandle partial = native.Add(
            "partial",
            ExactHandle with { ProductId = 0x029F });
        OpenedCandidateHandle exact = native.Add("exact", ExactHandle);
        native.AddOpenFailure("busy", 32);

        BatteryValidationProbeResult result;
        using (HidEvidenceEvaluation evaluation = CreateEvaluator(native, events).Evaluate())
        {
            result = evaluation.Result;
            Assert.True(partial.FileHandle.IsClosed);
            Assert.False(exact.FileHandle.IsClosed);
        }

        Assert.Equal(BatteryValidationProbeStage.Ready, result.Stage);
        Assert.Equal(BatteryValidationProbeClassification.Ready, result.Classification);
        Assert.True(result.HostIdentityMatched);
        Assert.True(result.ConflictCheckPassed);
        Assert.Equal(3, result.EnumeratedCandidateCount);
        Assert.Equal(3, result.CandidateOpenAttemptCount);
        Assert.Equal(2, result.CandidateOpenSuccessCount);
        Assert.Equal(32, result.CandidateOpenWin32Error);
        Assert.Equal(0, result.CandidateAccessFallbackCount);
        Assert.Equal(2, result.IdentityReadCount);
        Assert.Equal(2, result.VendorMatchCount);
        Assert.Equal(1, result.ProductMatchCount);
        Assert.Equal(2, result.UsagePageMatchCount);
        Assert.Equal(2, result.UsageMatchCount);
        Assert.Equal(2, result.FeatureLengthMatchCount);
        Assert.Equal(1, result.ExactMatchCount);
        Assert.All(native.OpenParameters, parameters => Assert.Equal(
            new HidOpenParameters(0xC0000000u, 3u, 3u, 0x40000000u, false),
            parameters));
        Assert.True(partial.FileHandle.IsClosed);
        Assert.True(exact.FileHandle.IsClosed);
        Assert.Equal(1, native.DisposeCounts["partial"]);
        Assert.Equal(1, native.DisposeCounts["exact"]);
        Assert.Equal(0, native.BindCalls);
    }

    [Fact]
    public void Evaluate_AmbiguousExactHandlesReportsCardinalityAndDisposesAll()
    {
        ScriptedNative native = new([]);
        OpenedCandidateHandle first = native.Add("first", ExactHandle);
        OpenedCandidateHandle second = native.Add("second", ExactHandle);

        BatteryValidationProbeResult result;
        using (HidEvidenceEvaluation evaluation = CreateEvaluator(native).Evaluate())
        {
            result = evaluation.Result;
        }

        Assert.Equal(BatteryValidationProbeStage.Cardinality, result.Stage);
        Assert.Equal(
            BatteryValidationProbeClassification.AmbiguousExactHandles,
            result.Classification);
        Assert.Equal(2, result.ExactMatchCount);
        Assert.True(first.FileHandle.IsClosed);
        Assert.True(second.FileHandle.IsClosed);
        Assert.Equal(0, native.BindCalls);
    }

    [Fact]
    public void Evaluate_EnumerationFailureIsTypedAndOpensNothing()
    {
        ScriptedNative native = new([]) { ThrowOnEnumeration = true };

        using HidEvidenceEvaluation evaluation = CreateEvaluator(native).Evaluate();

        Assert.Equal(BatteryValidationProbeStage.Enumeration, evaluation.Result.Stage);
        Assert.Equal(
            BatteryValidationProbeClassification.EnumerationFailed,
            evaluation.Result.Classification);
        Assert.Empty(native.OpenParameters);
        Assert.Equal(0, native.BindCalls);
    }

    [Fact]
    public void Evaluate_IdentityFailureAfterExactMatchDisposesEveryOwnedCandidate()
    {
        ScriptedNative native = new([]) { IdentityFailurePath = "failing" };
        OpenedCandidateHandle retained = native.Add("retained", ExactHandle);
        OpenedCandidateHandle failing = native.Add("failing", ExactHandle);

        BatteryValidationProbeResult result;
        using (HidEvidenceEvaluation evaluation = CreateEvaluator(native).Evaluate())
        {
            result = evaluation.Result;
        }

        Assert.Equal(BatteryValidationProbeStage.HandleIdentity, result.Stage);
        Assert.Equal(
            BatteryValidationProbeClassification.HandleIdentityFailed,
            result.Classification);
        Assert.True(retained.FileHandle.IsClosed);
        Assert.True(failing.FileHandle.IsClosed);
        Assert.Equal(1, native.DisposeCounts["retained"]);
        Assert.Equal(1, native.DisposeCounts["failing"]);
        Assert.Equal(0, native.BindCalls);
    }

    [Fact]
    public void Evaluate_NoExactHandleReportsCardinalityWithoutRawIdentity()
    {
        ScriptedNative native = new([]);
        native.Add("candidate", ExactHandle with { Usage = 1 });

        using HidEvidenceEvaluation evaluation = CreateEvaluator(native).Evaluate();

        Assert.Equal(BatteryValidationProbeStage.Cardinality, evaluation.Result.Stage);
        Assert.Equal(
            BatteryValidationProbeClassification.NoExactHandle,
            evaluation.Result.Classification);
        Assert.Equal(0, evaluation.Result.ExactMatchCount);
        Assert.Equal(1, evaluation.Result.VendorMatchCount);
        Assert.Equal(0, evaluation.Result.UsageMatchCount);
        Assert.Equal(0, native.BindCalls);
    }

    [Fact]
    public void Evaluate_AccessDeniedRetriesMetadataOnlyWithOtherwiseExactParameters()
    {
        ScriptedNative native = new([]);
        OpenedCandidateHandle exact = native.Add("exact", ExactHandle);
        native.AccessDeniedUntilMetadataOnly.Add("exact");

        BatteryValidationProbeResult result;
        using (HidEvidenceEvaluation evaluation = CreateEvaluator(native).Evaluate())
        {
            result = evaluation.Result;
        }

        Assert.Equal(
            BatteryValidationProbeClassification.ReadyWithAccessFallback,
            result.Classification);
        Assert.Equal(2, result.CandidateOpenAttemptCount);
        Assert.Equal(1, result.CandidateOpenSuccessCount);
        Assert.Equal(1, result.CandidateAccessFallbackCount);
        Assert.Equal(5, result.CandidateOpenWin32Error);
        Assert.Collection(
            native.OpenParameters,
            parameters => Assert.Equal(
                new HidOpenParameters(0xC0000000u, 3u, 3u, 0x40000000u, false),
                parameters),
            parameters => Assert.Equal(
                new HidOpenParameters(0u, 3u, 3u, 0x40000000u, false),
                parameters));
        Assert.True(exact.FileHandle.IsClosed);
        Assert.Equal(1, native.DisposeCounts["exact"]);
        Assert.Equal(0, native.BindCalls);
    }

    [Fact]
    public void Evaluate_NonAccessDeniedOpenErrorDoesNotFallback()
    {
        ScriptedNative native = new([]);
        native.AddOpenFailure("busy", 32);

        using HidEvidenceEvaluation evaluation = CreateEvaluator(native).Evaluate();

        Assert.Equal(BatteryValidationProbeClassification.NoExactHandle, evaluation.Result.Classification);
        Assert.Equal(1, evaluation.Result.CandidateOpenAttemptCount);
        Assert.Equal(0, evaluation.Result.CandidateAccessFallbackCount);
        Assert.Equal(32, evaluation.Result.CandidateOpenWin32Error);
        Assert.Single(native.OpenParameters);
        Assert.Equal(0xC0000000u, native.OpenParameters[0].DesiredAccess);
    }

    private static WindowsRazerHidEvidenceEvaluator CreateEvaluator(
        ScriptedNative native,
        List<string>? events = null,
        HardwareAccessDecision? access = null,
        ControllerExclusivityPolicy policy = ControllerExclusivityPolicy.Strict) =>
        new(
            new ScriptedIdentitySource(ExactHost, events),
            new ScriptedGuard(access ?? new HardwareAccessDecision([], null), events),
            native,
            policy);

    private sealed class ScriptedIdentitySource(
        HostIdentityReadResult result,
        List<string>? events) : IHostIdentitySource
    {
        public HostIdentityReadResult ReadExactIdentity()
        {
            events?.Add("identity");
            return result;
        }
    }

    private sealed class ScriptedGuard(
        HardwareAccessDecision result,
        List<string>? events) : IHardwareAccessGuard
    {
        public HardwareAccessDecision Check()
        {
            events?.Add("guard");
            return result;
        }
    }

    private sealed class ScriptedNative(List<string> events) : IHidCandidateNative
    {
        private readonly Dictionary<string, (OpenedCandidateHandle Handle, HidHandleIdentity Identity)>
            candidates = [];
        private readonly Dictionary<string, int> openFailures = [];

        public int EnumerateCalls { get; private set; }
        public int BindCalls { get; private set; }
        public bool ThrowOnEnumeration { get; set; }
        public string? IdentityFailurePath { get; set; }
        public HashSet<string> AccessDeniedUntilMetadataOnly { get; } = [];
        public List<HidOpenParameters> OpenParameters { get; } = [];
        public Dictionary<string, int> DisposeCounts { get; } = [];

        public OpenedCandidateHandle Add(string path, HidHandleIdentity identity)
        {
            SafeFileHandle file = new(new IntPtr(500 + candidates.Count), ownsHandle: false);
            DisposeCounts[path] = 0;
            var handle = new OpenedCandidateHandle(file, () =>
            {
                DisposeCounts[path]++;
                file.Dispose();
            });
            candidates[path] = (handle, identity);
            return handle;
        }

        public void AddOpenFailure(string path, int error) => openFailures[path] = error;

        public IReadOnlyList<string> EnumerateDevicePaths()
        {
            EnumerateCalls++;
            events.Add("enumerate");
            if (ThrowOnEnumeration)
            {
                throw new InvalidOperationException("scripted enumeration failure");
            }

            return candidates.Keys.Concat(openFailures.Keys).ToArray();
        }

        public HidCandidateOpenResult TryOpen(string path, HidOpenParameters parameters)
        {
            OpenParameters.Add(parameters);
            events.Add($"open:{path}");
            if (AccessDeniedUntilMetadataOnly.Contains(path) && parameters.DesiredAccess != 0)
            {
                return new(null, 5);
            }

            return openFailures.TryGetValue(path, out int error)
                ? new(null, error)
                : new(candidates[path].Handle, null);
        }

        public HidHandleIdentity ReadIdentity(OpenedCandidateHandle handle)
        {
            KeyValuePair<string, (OpenedCandidateHandle Handle, HidHandleIdentity Identity)> candidate =
                candidates.Single(pair => ReferenceEquals(pair.Value.Handle, handle));
            events.Add($"read:{candidate.Key}");
            if (candidate.Key == IdentityFailurePath)
            {
                throw new InvalidOperationException("scripted identity failure");
            }

            return candidate.Value.Identity;
        }

        public OpenedHidHandle BindSelected(OpenedCandidateHandle handle)
        {
            BindCalls++;
            throw new InvalidOperationException("Probe must never bind.");
        }
    }
}
