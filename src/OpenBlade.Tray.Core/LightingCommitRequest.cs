using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public sealed record LightingCommitRequest(
    LightingSettings? Lighting,
    LightingPowerPolicy? PowerPolicy)
{
    public long EditorGeneration { get; init; }

    public bool HasWork => Lighting is not null || PowerPolicy is not null;
}
