using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed partial class MainViewModelTests
{
    [Theory]
    [InlineData("RZ09-0528", false)]
    [InlineData("RZ09-0529", true)]
    [InlineData("RZ09-0509", false)]
    [InlineData("RZ09-0510", false)]
    [InlineData("RZ09-0530", false)]
    public async Task WindowsDynamicLightingOwnership_DisablesLightingUntilWindowsReleasesIt(
        string modelNumber,
        bool coversVaporChamber)
    {
        using var culture = new CultureScope("en-US");
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot status = CreateStatus(
            verified: true,
            blocked: false,
            capabilities: DeviceCapabilities.SafeTargetDefaults() with
            {
                LampArrayLightingAccess = DeviceFeatureAccess.ObservedOnly,
                SupportedLightingRefreshRates = new HashSet<LightingRefreshRate>
                {
                    LightingRefreshRate.ThirtyHz,
                    LightingRefreshRate.SixtyHz,
                },
            }) with
        {
            Firmware = new FirmwareVersions(
                "1.00",
                "1.00",
                "1.0.0.0",
                "Unavailable",
                modelNumber),
        };
        client.Publish(status);
        await WaitUntilAsync(() => viewModel.CanControlLighting);
        Assert.False(viewModel.IsLightingOwnedByWindows);

        client.Publish(status with
        {
            Revision = 2,
            WindowsDynamicLightingOwnership = WindowsDynamicLightingOwnership.Owned,
        });
        await WaitUntilAsync(() => viewModel.IsLightingOwnedByWindows);

        Assert.False(viewModel.CanControlLighting);
        Assert.False(viewModel.CanSetLightingRefreshRate);
        Assert.True(viewModel.IsLightingControlUnavailable);
        Assert.True(viewModel.IsLightingControlUnavailableNoticeVisible);
        Assert.Equal(
            coversVaporChamber
                ? "Windows Dynamic Lighting controls the keyboard and Vapor Chamber. To use OpenBlade lighting, turn it off for this device in Settings > Personalization > Dynamic Lighting."
                : "Windows Dynamic Lighting controls the keyboard. To use OpenBlade lighting, turn it off for this device in Settings > Personalization > Dynamic Lighting.",
            viewModel.LightingControlUnavailableMessage);
        Assert.Equal(
            "Windows Dynamic Lighting manages keyboard lighting.",
            viewModel.LightingRefreshRateAvailabilityText);
        // An observation never rewrites the saved OpenBlade preference.
        Assert.False(viewModel.UseWindowsDynamicLighting);
        Assert.False(await viewModel.SetLightingRefreshRateAsync(
            LightingRefreshRate.SixtyHz,
            CancellationToken.None));

        client.Publish(status with
        {
            Revision = 3,
            WindowsDynamicLightingOwnership = WindowsDynamicLightingOwnership.Released,
        });
        await WaitUntilAsync(() => viewModel.CanControlLighting);
        Assert.False(viewModel.IsLightingOwnedByWindows);
        Assert.False(viewModel.IsLightingControlUnavailableNoticeVisible);
    }

    [Fact]
    public async Task WindowsDynamicLightingOwnership_ExcludesLightingDriftFromReapply()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot status = CreateStatus(verified: true, blocked: false) with
        {
            ExternalChanges = [DeviceSubsystem.Lighting],
        };
        client.Publish(status);
        await WaitUntilAsync(() => viewModel.CanReapplyChangedSettings);

        client.Publish(status with
        {
            Revision = 2,
            WindowsDynamicLightingOwnership = WindowsDynamicLightingOwnership.Owned,
        });
        await WaitUntilAsync(() => viewModel.IsLightingOwnedByWindows);

        Assert.False(viewModel.CanReapplyChangedSettings);
    }
}
