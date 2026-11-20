using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed partial class MainViewModelTests
{
    [Fact]
    public async Task Blade14KeyboardEditionOnlyStatus_RefreshesTheImportMapCapability()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(
            client,
            new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot baseline = CreateStatus(
            verified: true,
            blocked: false) with
        {
            Revision = 10,
            Capabilities = DeviceCapabilities.SafeTargetDefaults() with
            {
                KeyboardLightingLayoutId = KeyboardLightingLayoutIds.Rz09_0530,
                Blade14KeyboardEdition = null,
            },
        };
        client.Publish(baseline);
        await WaitUntilAsync(() => viewModel.IsConnected);
        Assert.Null(viewModel.Capabilities.Blade14KeyboardEdition);

        // Keyboard .chroma imports read this value; an edition-only change must not
        // be dropped as an unchanged status.
        client.Publish(baseline with
        {
            Revision = 11,
            Capabilities = baseline.Capabilities! with { Blade14KeyboardEdition = 12 },
        });
        await WaitUntilAsync(() => viewModel.Capabilities.Blade14KeyboardEdition == 12);

        client.Publish(baseline with
        {
            Revision = 12,
            Capabilities = baseline.Capabilities! with { Blade14KeyboardEdition = 2 },
        });
        await WaitUntilAsync(() => viewModel.Capabilities.Blade14KeyboardEdition == 2);
        Assert.Equal((byte)2, viewModel.Capabilities.Blade14KeyboardEdition);
    }
}
