namespace OpenBlade.Protocol;

public enum BatteryValidationProbeStage
{
    HostIdentity,
    ConflictCheck,
    Enumeration,
    CandidateOpen,
    HandleIdentity,
    Cardinality,
    Ready,
}

public enum BatteryValidationProbeClassification
{
    Ready,
    ReadyWithAccessFallback,
    HostIdentityBlocked,
    ConflictBlocked,
    ConflictDetectionFailed,
    EnumerationFailed,
    CandidateOpenFailed,
    HandleIdentityFailed,
    NoExactHandle,
    AmbiguousExactHandles,
}

public enum BatteryValidationFirmwareCompatibility
{
    Unknown,
    Tested,
    UntestedBlocked,
    UntestedAllowed,
    ModelScoped,
}

public sealed record BatteryValidationProbeResult(
    BatteryValidationProbeStage Stage,
    BatteryValidationProbeClassification Classification,
    bool HostIdentityMatched,
    bool ConflictCheckPassed,
    int EnumeratedCandidateCount,
    int CandidateOpenAttemptCount,
    int CandidateOpenSuccessCount,
    int? CandidateOpenWin32Error,
    int CandidateAccessFallbackCount,
    int IdentityReadCount,
    int VendorMatchCount,
    int ProductMatchCount,
    int UsagePageMatchCount,
    int UsageMatchCount,
    int FeatureLengthMatchCount,
    int ExactMatchCount,
    BatteryValidationFirmwareCompatibility FirmwareCompatibility =
        BatteryValidationFirmwareCompatibility.Unknown);

internal sealed class HidEvidenceEvaluation : IDisposable
{
    private readonly List<OpenedCandidateHandle> exactMatches;

    internal HidEvidenceEvaluation(
        BatteryValidationProbeResult result,
        List<OpenedCandidateHandle>? exactMatches = null)
    {
        Result = result;
        this.exactMatches = exactMatches ?? [];
    }

    public BatteryValidationProbeResult Result { get; }

    internal OpenedCandidateHandle TakeSoleExactCandidate()
    {
        if (Result.Classification is not (
                BatteryValidationProbeClassification.Ready or
                BatteryValidationProbeClassification.ReadyWithAccessFallback) ||
            exactMatches.Count != 1)
        {
            throw new InvalidOperationException("Evaluation does not own one ready candidate.");
        }

        OpenedCandidateHandle candidate = exactMatches[0];
        exactMatches.Clear();
        return candidate;
    }

    public void Dispose()
    {
        foreach (OpenedCandidateHandle candidate in exactMatches)
        {
            candidate.Dispose();
        }

        exactMatches.Clear();
    }
}

internal sealed class WindowsRazerHidEvidenceEvaluator
{
    private const int ErrorAccessDenied = 5;

    internal static readonly HidOpenParameters OperationalOpenParameters = new(
        DesiredAccess: 0xC0000000u,
        ShareMode: 0x00000003u,
        CreationDisposition: 3u,
        FlagsAndAttributes: 0x40000000u,
        Inheritable: false);

    internal static readonly HidOpenParameters MetadataOnlyOpenParameters =
        OperationalOpenParameters with { DesiredAccess = 0 };

    private static readonly HidHandleIdentity ProductionExactIdentity = new(
        VendorId: 0x1532,
        ProductId: 0x02E0,
        UsagePage: 0x0001,
        Usage: 0x0002,
        FeatureReportByteLength: 91);

    private readonly IHostIdentitySource hostIdentity;
    private readonly IHardwareAccessGuard hardwareAccessGuard;
    private readonly IHidCandidateNative native;
    private readonly ControllerExclusivityPolicy exclusivityPolicy;
    private readonly HidHandleIdentity exactIdentity;

    internal WindowsRazerHidEvidenceEvaluator(
        IHostIdentitySource hostIdentity,
        IHardwareAccessGuard hardwareAccessGuard,
        IHidCandidateNative native,
        ControllerExclusivityPolicy exclusivityPolicy)
        : this(
            hostIdentity,
            hardwareAccessGuard,
            native,
            exclusivityPolicy,
            ProductionExactIdentity)
    {
    }

    internal WindowsRazerHidEvidenceEvaluator(
        IHostIdentitySource hostIdentity,
        IHardwareAccessGuard hardwareAccessGuard,
        IHidCandidateNative native,
        ControllerExclusivityPolicy exclusivityPolicy,
        HidHandleIdentity exactIdentity)
    {
        ArgumentNullException.ThrowIfNull(hostIdentity);
        ArgumentNullException.ThrowIfNull(hardwareAccessGuard);
        ArgumentNullException.ThrowIfNull(native);
        this.hostIdentity = hostIdentity;
        this.hardwareAccessGuard = hardwareAccessGuard;
        this.native = native;
        this.exclusivityPolicy = exclusivityPolicy;
        this.exactIdentity = exactIdentity;
    }

    internal HidEvidenceEvaluation Evaluate()
    {
        HostIdentityReadResult identity;
        try
        {
            identity = hostIdentity.ReadExactIdentity();
        }
        catch
        {
            return Failure(
                BatteryValidationProbeStage.HostIdentity,
                BatteryValidationProbeClassification.HostIdentityBlocked);
        }

        if (identity.Evidence is null || identity.Failure is not null)
        {
            return Failure(
                BatteryValidationProbeStage.HostIdentity,
                BatteryValidationProbeClassification.HostIdentityBlocked,
                firmwareCompatibility: MapFirmwareCompatibility(
                    identity.FirmwareCompatibility));
        }

        BatteryValidationFirmwareCompatibility firmwareCompatibility =
            MapFirmwareCompatibility(identity.FirmwareCompatibility);

        bool conflictCheckPassed = exclusivityPolicy == ControllerExclusivityPolicy.Strict;
        if (exclusivityPolicy == ControllerExclusivityPolicy.Strict)
        {
            HardwareAccessDecision access;
            try
            {
                access = hardwareAccessGuard.Check();
            }
            catch
            {
                return Failure(
                    BatteryValidationProbeStage.ConflictCheck,
                    BatteryValidationProbeClassification.ConflictDetectionFailed,
                    hostIdentityMatched: true,
                    firmwareCompatibility: firmwareCompatibility);
            }

            if (access.DetectionFailure is not null)
            {
                return Failure(
                    BatteryValidationProbeStage.ConflictCheck,
                    BatteryValidationProbeClassification.ConflictDetectionFailed,
                    hostIdentityMatched: true,
                    firmwareCompatibility: firmwareCompatibility);
            }

            if (access.Conflicts.Count != 0)
            {
                return Failure(
                    BatteryValidationProbeStage.ConflictCheck,
                    BatteryValidationProbeClassification.ConflictBlocked,
                    hostIdentityMatched: true,
                    firmwareCompatibility: firmwareCompatibility);
            }
        }

        IReadOnlyList<string> paths;
        try
        {
            paths = native.EnumerateDevicePaths();
        }
        catch
        {
            return Failure(
                BatteryValidationProbeStage.Enumeration,
                BatteryValidationProbeClassification.EnumerationFailed,
                hostIdentityMatched: true,
                conflictCheckPassed: conflictCheckPassed,
                firmwareCompatibility: firmwareCompatibility);
        }

        int openAttempts = 0;
        int openSuccesses = 0;
        int? openError = null;
        int accessFallbacks = 0;
        int identityReads = 0;
        int vendorMatches = 0;
        int productMatches = 0;
        int usagePageMatches = 0;
        int usageMatches = 0;
        int featureLengthMatches = 0;
        int fallbackExactMatches = 0;
        List<OpenedCandidateHandle> exactMatches = [];

        foreach (string path in paths)
        {
            bool usedAccessFallback = false;
            openAttempts++;
            HidCandidateOpenResult opened;
            try
            {
                opened = native.TryOpen(path, OperationalOpenParameters);
            }
            catch
            {
                return FinishFailure(
                    BatteryValidationProbeStage.CandidateOpen,
                    BatteryValidationProbeClassification.CandidateOpenFailed);
            }

            OpenedCandidateHandle? candidate = opened.Candidate;
            if (candidate is null)
            {
                openError = opened.Win32Error;
                if (opened.Win32Error != ErrorAccessDenied)
                {
                    continue;
                }

                accessFallbacks++;
                usedAccessFallback = true;
                openAttempts++;
                try
                {
                    opened = native.TryOpen(path, MetadataOnlyOpenParameters);
                }
                catch
                {
                    return FinishFailure(
                        BatteryValidationProbeStage.CandidateOpen,
                        BatteryValidationProbeClassification.CandidateOpenFailed);
                }

                candidate = opened.Candidate;
                if (candidate is null)
                {
                    openError = opened.Win32Error;
                    continue;
                }
            }

            openSuccesses++;
            try
            {
                HidHandleIdentity handleIdentity;
                try
                {
                    handleIdentity = native.ReadIdentity(candidate);
                }
                catch
                {
                    return FinishFailure(
                        BatteryValidationProbeStage.HandleIdentity,
                        BatteryValidationProbeClassification.HandleIdentityFailed);
                }

                identityReads++;
                vendorMatches += handleIdentity.VendorId == exactIdentity.VendorId ? 1 : 0;
                productMatches += handleIdentity.ProductId == exactIdentity.ProductId ? 1 : 0;
                usagePageMatches += handleIdentity.UsagePage == exactIdentity.UsagePage ? 1 : 0;
                usageMatches += handleIdentity.Usage == exactIdentity.Usage ? 1 : 0;
                featureLengthMatches +=
                    handleIdentity.FeatureReportByteLength == exactIdentity.FeatureReportByteLength ? 1 : 0;

                if (handleIdentity.MatchesExpected(exactIdentity))
                {
                    exactMatches.Add(candidate);
                    fallbackExactMatches += usedAccessFallback ? 1 : 0;
                    candidate = null;
                }
            }
            finally
            {
                candidate?.Dispose();
            }
        }

        BatteryValidationProbeClassification classification = exactMatches.Count switch
        {
            0 => BatteryValidationProbeClassification.NoExactHandle,
            1 when fallbackExactMatches == 1 =>
                BatteryValidationProbeClassification.ReadyWithAccessFallback,
            1 => BatteryValidationProbeClassification.Ready,
            _ => BatteryValidationProbeClassification.AmbiguousExactHandles,
        };
        BatteryValidationProbeStage stage = exactMatches.Count == 1
            ? BatteryValidationProbeStage.Ready
            : BatteryValidationProbeStage.Cardinality;
        return new HidEvidenceEvaluation(Build(stage, classification), exactMatches);

        HidEvidenceEvaluation FinishFailure(
            BatteryValidationProbeStage stage,
            BatteryValidationProbeClassification classification) =>
            new(Build(stage, classification), exactMatches);

        BatteryValidationProbeResult Build(
            BatteryValidationProbeStage stage,
            BatteryValidationProbeClassification classification) =>
            new(
                stage,
                classification,
                HostIdentityMatched: true,
                ConflictCheckPassed: conflictCheckPassed,
                EnumeratedCandidateCount: paths.Count,
                CandidateOpenAttemptCount: openAttempts,
                CandidateOpenSuccessCount: openSuccesses,
                CandidateOpenWin32Error: openError,
                CandidateAccessFallbackCount: accessFallbacks,
                IdentityReadCount: identityReads,
                VendorMatchCount: vendorMatches,
                ProductMatchCount: productMatches,
                UsagePageMatchCount: usagePageMatches,
                UsageMatchCount: usageMatches,
                FeatureLengthMatchCount: featureLengthMatches,
                ExactMatchCount: exactMatches.Count,
                FirmwareCompatibility: firmwareCompatibility);
    }

    private static HidEvidenceEvaluation Failure(
        BatteryValidationProbeStage stage,
        BatteryValidationProbeClassification classification,
        bool hostIdentityMatched = false,
        bool conflictCheckPassed = false,
        BatteryValidationFirmwareCompatibility firmwareCompatibility =
            BatteryValidationFirmwareCompatibility.Unknown) =>
        new(new BatteryValidationProbeResult(
            stage,
            classification,
            hostIdentityMatched,
            conflictCheckPassed,
            EnumeratedCandidateCount: 0,
            CandidateOpenAttemptCount: 0,
            CandidateOpenSuccessCount: 0,
            CandidateOpenWin32Error: null,
            CandidateAccessFallbackCount: 0,
            IdentityReadCount: 0,
            VendorMatchCount: 0,
            ProductMatchCount: 0,
            UsagePageMatchCount: 0,
            UsageMatchCount: 0,
            FeatureLengthMatchCount: 0,
            ExactMatchCount: 0,
            FirmwareCompatibility: firmwareCompatibility));

    private static BatteryValidationFirmwareCompatibility MapFirmwareCompatibility(
        HostFirmwareCompatibility compatibility) => compatibility switch
        {
            HostFirmwareCompatibility.Tested =>
                BatteryValidationFirmwareCompatibility.Tested,
            HostFirmwareCompatibility.UntestedBlocked =>
                BatteryValidationFirmwareCompatibility.UntestedBlocked,
            HostFirmwareCompatibility.UntestedAllowed =>
                BatteryValidationFirmwareCompatibility.UntestedAllowed,
            HostFirmwareCompatibility.ModelScoped =>
                BatteryValidationFirmwareCompatibility.ModelScoped,
            _ => BatteryValidationFirmwareCompatibility.Unknown,
        };
}
