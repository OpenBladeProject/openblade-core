namespace OpenSynapse.Protocol;

internal sealed class WindowsRazerHidSessionOpener
{
    private static readonly HidOpenParameters OperationalOpenParameters =
        WindowsRazerHidEvidenceEvaluator.OperationalOpenParameters;

    private readonly WindowsRazerHidEvidenceEvaluator evaluator;
    private readonly IHidCandidateNative native;

    public WindowsRazerHidSessionOpener(
        IHostIdentitySource hostIdentity,
        IHardwareAccessGuard hardwareAccessGuard,
        IHidCandidateNative native)
    {
        ArgumentNullException.ThrowIfNull(hostIdentity);
        ArgumentNullException.ThrowIfNull(hardwareAccessGuard);
        ArgumentNullException.ThrowIfNull(native);
        evaluator = new WindowsRazerHidEvidenceEvaluator(
            hostIdentity,
            hardwareAccessGuard,
            native);
        this.native = native;
    }

    public HidSessionResult<OpenedHidHandle> Open()
    {
        HidEvidenceEvaluation evaluation;
        try
        {
            evaluation = evaluator.Evaluate();
        }
        catch
        {
            return Failure(HidSessionFailureKind.NativeIoFailure);
        }

        using (evaluation)
        {
            if (evaluation.Result.Classification != BatteryValidationProbeClassification.Ready)
            {
                return Failure(MapFailure(evaluation.Result.Classification));
            }

            OpenedCandidateHandle selected = evaluation.TakeSoleExactCandidate();
            try
            {
                OpenedHidHandle opened = native.BindSelected(selected);
                return new(opened, null);
            }
            catch
            {
                selected.Dispose();
                return Failure(HidSessionFailureKind.NativeIoFailure);
            }
        }
    }

    private static HidSessionResult<OpenedHidHandle> Failure(HidSessionFailureKind failure) =>
        new(null, failure);

    private static HidSessionFailureKind MapFailure(
        BatteryValidationProbeClassification classification) => classification switch
        {
            BatteryValidationProbeClassification.HostIdentityBlocked =>
                HidSessionFailureKind.IdentityBlocked,
            BatteryValidationProbeClassification.ConflictBlocked =>
                HidSessionFailureKind.ConflictBlocked,
            BatteryValidationProbeClassification.ConflictDetectionFailed =>
                HidSessionFailureKind.ConflictDetectionFailed,
            BatteryValidationProbeClassification.NoExactHandle or
            BatteryValidationProbeClassification.AmbiguousExactHandles =>
                HidSessionFailureKind.IdentityBlocked,
            _ => HidSessionFailureKind.NativeIoFailure,
        };
}
