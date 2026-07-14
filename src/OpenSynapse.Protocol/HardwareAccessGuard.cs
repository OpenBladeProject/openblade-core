namespace OpenSynapse.Protocol;

public sealed record HardwareAccessDecision(
    IReadOnlyList<string> Conflicts,
    string? DetectionFailure)
{
    public bool Safe => Conflicts.Count == 0 && DetectionFailure is null;
}

public interface IHardwareAccessGuard
{
    HardwareAccessDecision Check();
}
