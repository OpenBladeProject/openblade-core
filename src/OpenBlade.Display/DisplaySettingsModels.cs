namespace OpenBlade.Display;

public static class DisplayRefreshKeys
{
    public static string Fixed(int hertz) => $"fixed-{hertz}";

    public static string Dynamic(int minimumHertz, int maximumHertz) =>
        $"dynamic-{minimumHertz}-{maximumHertz}";
}

public sealed record DisplayRefreshOption(
    string Key,
    string Name,
    int MinimumHertz,
    int MaximumHertz,
    int DesktopHertz,
    bool IsDynamic);

public sealed record DisplayColorProfile(
    string Id,
    string Name,
    bool IsActive);

public sealed record DisplaySettingsSnapshot(
    bool IsAvailable,
    string? DisplayName,
    int Width,
    int Height,
    string? CurrentRefreshKey,
    IReadOnlyList<DisplayRefreshOption> RefreshOptions,
    bool HdrSupported,
    bool HdrEnabled,
    bool ColorProfilesEnabled,
    string? ActiveColorProfileId,
    IReadOnlyList<DisplayColorProfile> ColorProfiles,
    string? Message,
    int? BitsPerColorChannel = null);

public sealed record DisplayOperationResult(
    bool Succeeded,
    string? Error,
    DisplaySettingsSnapshot? State)
{
    public static DisplayOperationResult Success(DisplaySettingsSnapshot state) => new(true, null, state);

    public static DisplayOperationResult Failure(string error, DisplaySettingsSnapshot? state = null) =>
        new(false, error, state);
}

public interface IDisplaySettingsController
{
    Task<DisplaySettingsSnapshot> QueryAsync(CancellationToken cancellationToken = default);

    Task<DisplayOperationResult> SetRefreshAsync(string key, CancellationToken cancellationToken = default);

    Task<DisplayOperationResult> SetHdrAsync(bool enabled, CancellationToken cancellationToken = default);

    Task<DisplayOperationResult> SetColorProfileAsync(string id, CancellationToken cancellationToken = default);
}
