using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public static class GpuClockOffsetCommitPolicy
{
    public static bool CanCommit(
        bool canEdit,
        GpuClockOffsetCapabilities? capabilities,
        GpuClockOffsetSettings requested) =>
        canEdit && capabilities?.Supports(requested) == true;
}
