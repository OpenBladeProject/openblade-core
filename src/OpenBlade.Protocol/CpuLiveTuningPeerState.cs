namespace OpenBlade.Protocol;

// Immutable expectations only, never native authority. A null original means
// the peer has no owned restoration; observing it must not invent ownership.
internal sealed record CpuLivePowerPeerState(CpuLivePowerPair Expected, CpuLivePowerPair? Original);
internal sealed record CpuLiveCurvePeerState(CpuLiveCurveSnapshot Expected, CpuLiveCurveSnapshot? Original);
