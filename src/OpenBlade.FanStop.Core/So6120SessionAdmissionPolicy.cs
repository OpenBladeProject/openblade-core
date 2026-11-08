namespace OpenBlade.FanStop;

// A policy supplies fresh host/session admission. Session identity, durable
// intent and command confirmation remain enforced by the shared session.
internal interface ISo6120SessionAdmissionPolicy
{
    So6120AdmissionAssessment Assess(TimeProvider clock, So6120SupervisedMetadata metadata,
        So6120CallerChallengeContext challenge, So6120OpenBladeHandoff handoff);
}

internal sealed class So6120SupervisedSessionAdmissionPolicy : ISo6120SessionAdmissionPolicy
{
    internal static So6120SupervisedSessionAdmissionPolicy Instance { get; } = new();

    private So6120SupervisedSessionAdmissionPolicy() { }

    public So6120AdmissionAssessment Assess(TimeProvider clock, So6120SupervisedMetadata metadata,
        So6120CallerChallengeContext challenge, So6120OpenBladeHandoff handoff) =>
        So6120SupervisedAdmission.Assess(clock, metadata, challenge, handoff);
}
