using Backend = OpenBlade.Display;

namespace OpenBlade.Tray;

internal sealed class WindowsDisplaySettingsAdapter : IDisplaySettingsAdapter, IDisposable
{
    private readonly Backend.IDisplaySettingsController controller;

    internal WindowsDisplaySettingsAdapter(Backend.IDisplaySettingsController controller)
    {
        this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
    }

    public async Task<DisplaySettingsSnapshot> GetStateAsync(CancellationToken token) =>
        Map(await controller.QueryAsync(token).ConfigureAwait(false));

    public async Task<DisplaySettingsSnapshot> SetRefreshRateAsync(
        string refreshRateKey,
        CancellationToken token) =>
        Map(await controller.SetRefreshAsync(refreshRateKey, token).ConfigureAwait(false));

    public async Task<DisplaySettingsSnapshot> SetHdrEnabledAsync(
        bool enabled,
        CancellationToken token) =>
        Map(await controller.SetHdrAsync(enabled, token).ConfigureAwait(false));

    public async Task<DisplaySettingsSnapshot> SetColorProfileAsync(
        string colorProfileKey,
        CancellationToken token) =>
        Map(await controller.SetColorProfileAsync(colorProfileKey, token).ConfigureAwait(false));

    public void Dispose()
    {
        if (controller is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private static DisplaySettingsSnapshot Map(Backend.DisplayOperationResult result)
    {
        Backend.DisplaySettingsSnapshot state = result.State ?? new(
            false, null, 0, 0, null, [], false, false, false, null, [], result.Error);
        return Map(state) with
        {
            StatusMessage = result.Error ?? state.Message,
            OperationSucceeded = result.Succeeded,
        };
    }

    private static DisplaySettingsSnapshot Map(Backend.DisplaySettingsSnapshot state) => new(
        state.IsAvailable,
        state.RefreshOptions
            .Select(option => new DisplayRefreshRateOption(
                option.Key,
                option.Name,
                option.IsDynamic))
            .ToArray(),
        state.CurrentRefreshKey,
        state.HdrSupported,
        state.HdrEnabled,
        state.ColorProfiles
            .Select(profile => new DisplayColorProfileOption(profile.Id, profile.Name))
            .ToArray(),
        state.ActiveColorProfileId,
        state.Message ?? (state.IsAvailable
            ? $"{state.DisplayName ?? "Built-in display"}, {state.Width} × {state.Height}"
            : "Display settings are unavailable."));
}
