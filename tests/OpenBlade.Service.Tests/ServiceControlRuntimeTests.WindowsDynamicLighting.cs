using System.Security.Principal;
using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed partial class ServiceControlRuntimeTests
{
    private static readonly SecurityIdentifier AdvisoryUser =
        new("S-1-5-21-1-2-3-1001");
    private static readonly SecurityIdentifier OtherAdvisoryUser =
        new("S-1-5-21-1-2-3-1002");

    [Theory]
    [InlineData("RZ09-0528")]
    [InlineData("RZ09-0529")]
    [InlineData("RZ09-0509")]
    [InlineData("RZ09-0510")]
    [InlineData("RZ09-0530")]
    public async Task WindowsDynamicLightingAdvisory_IsPublishedWithoutTouchingLighting(
        string modelNumber)
    {
        TestContext context = await CreateLampArrayAdvisoryContextAsync(modelNumber);
        int writes = context.Device.LightingSetCalls;
        int invalidations = context.Provider.InvalidationCount;

        ControlOperationResult mayControl = await ReportAdvisoryAsync(
            context,
            modelNumber,
            WindowsDynamicLightingAdvisory.MayControl);

        Assert.Equal(ControlOperationStatus.Stored, mayControl.Status);
        Assert.Contains("No lighting change was applied", mayControl.Message, StringComparison.Ordinal);
        ServiceStatusSnapshot status = context.Runtime.GetStatus();
        Assert.Equal(WindowsDynamicLightingAdvisory.MayControl, status.WindowsDynamicLightingAdvisory);
        // Advisory only: no renderer retirement, no delegation, no capability.
        Assert.Equal(LightingRendererOwnershipState.Static, status.LightingRendererOwnership);
        Assert.Equal(KeyboardLightingControlMode.OpenBlade, status.KeyboardLightingControlMode);
        Assert.Null(status.Capabilities!.LampArrayLightingAccess);
        Assert.Equal(invalidations, context.Provider.InvalidationCount);
        Assert.Equal(writes, context.Device.LightingSetCalls);

        ControlOperationResult applied = await context.Runtime.SetLightingOverrideAsync(
            new ManualOverride<LightingSettings>(
                LightingSettingsFactory.Static(new RgbColor(255, 0, 0), brightness: 40),
                OverrideLifetime.UntilPowerSourceChange),
            CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Applied, applied.Status);
        Assert.Equal(writes + 1, context.Device.LightingSetCalls);

        ControlOperationResult off = await ReportAdvisoryAsync(
            context,
            modelNumber,
            WindowsDynamicLightingAdvisory.Off);
        Assert.Equal(ControlOperationStatus.Stored, off.Status);
        Assert.Equal(
            WindowsDynamicLightingAdvisory.Off,
            context.Runtime.GetStatus().WindowsDynamicLightingAdvisory);
        Assert.Equal(writes + 1, context.Device.LightingSetCalls);
        Assert.Equal(invalidations, context.Provider.InvalidationCount);
    }

    [Fact]
    public async Task WindowsDynamicLightingAdvisory_LeavesTheSavedDelegationPreferenceAlone()
    {
        TestContext context = await CreateLampArrayAdvisoryContextAsync("RZ09-0529");

        await ReportAdvisoryAsync(context, "RZ09-0529", WindowsDynamicLightingAdvisory.MayControl);

        // Only the saved preference blocks the always-override policy.
        ControlOperationResult policy =
            await context.Runtime.SetExternalLightingOverridePolicyAsync(true, CancellationToken.None);
        Assert.NotEqual(ControlOperationStatus.Blocked, policy.Status);
    }

    [Fact]
    public async Task WindowsDynamicLightingAdvisory_RejectsAReportForAnotherModel()
    {
        TestContext context = await CreateLampArrayAdvisoryContextAsync("RZ09-0529");

        ControlOperationResult mismatched = await ReportAdvisoryAsync(
            context,
            "RZ09-0528",
            WindowsDynamicLightingAdvisory.MayControl);

        Assert.Equal(ControlOperationStatus.Blocked, mismatched.Status);
        Assert.Equal(
            WindowsDynamicLightingAdvisory.Unknown,
            context.Runtime.GetStatus().WindowsDynamicLightingAdvisory);
    }

    [Theory]
    // Neither model is in the reviewed LampArray list.
    [InlineData("RZ09-0483")]
    [InlineData("RZ09-0581")]
    public async Task WindowsDynamicLightingAdvisory_RejectsModelsWithoutAReviewedLampArray(
        string modelNumber)
    {
        TestContext context = await CreateLampArrayAdvisoryContextAsync(modelNumber);

        ControlOperationResult rejected = await ReportAdvisoryAsync(
            context,
            modelNumber,
            WindowsDynamicLightingAdvisory.MayControl);

        Assert.NotEqual(ControlOperationStatus.Stored, rejected.Status);
        Assert.Equal(
            WindowsDynamicLightingAdvisory.Unknown,
            context.Runtime.GetStatus().WindowsDynamicLightingAdvisory);
    }

    [Fact]
    public async Task WindowsDynamicLightingAdvisory_RequiresTheActiveUsersSession()
    {
        TestContext context = await CreateLampArrayAdvisoryContextAsync("RZ09-0529");

        ControlOperationResult inactive = await ReportAdvisoryAsync(
            context,
            "RZ09-0529",
            WindowsDynamicLightingAdvisory.MayControl,
            stillControlsSession: static () => false);
        ControlOperationResult otherUser = await ReportAdvisoryAsync(
            context,
            "RZ09-0529",
            WindowsDynamicLightingAdvisory.MayControl,
            reportingUser: OtherAdvisoryUser);

        Assert.Equal(ControlOperationStatus.Blocked, inactive.Status);
        Assert.Equal(ControlOperationStatus.Blocked, otherUser.Status);
        Assert.Equal(
            WindowsDynamicLightingAdvisory.Unknown,
            context.Runtime.GetStatus().WindowsDynamicLightingAdvisory);
    }

    [Fact]
    public async Task WindowsDynamicLightingAdvisory_IsNotInheritedByAnotherWindowsUser()
    {
        TestContext context = await CreateLampArrayAdvisoryContextAsync("RZ09-0529");
        Assert.Equal(
            ControlOperationStatus.Stored,
            (await ReportAdvisoryAsync(
                context,
                "RZ09-0529",
                WindowsDynamicLightingAdvisory.MayControl)).Status);

        // Fast user switch. The second user's Session Agent has not reported.
        await context.Runtime.ActivateUserAsync(
            OtherAdvisoryUser,
            static () => true,
            CancellationToken.None);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        Assert.Equal(
            WindowsDynamicLightingAdvisory.Unknown,
            context.Runtime.GetStatus().WindowsDynamicLightingAdvisory);

        // The first user returning does not revive the dropped observation.
        await context.Runtime.ActivateUserAsync(
            AdvisoryUser,
            static () => true,
            CancellationToken.None);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        Assert.Equal(
            WindowsDynamicLightingAdvisory.Unknown,
            context.Runtime.GetStatus().WindowsDynamicLightingAdvisory);
    }

    [Fact]
    public async Task WindowsDynamicLightingAdvisory_ProjectsUnknownAfterTheReportingSessionIsLost()
    {
        TestContext context = await CreateLampArrayAdvisoryContextAsync("RZ09-0529");
        bool sessionActive = true;
        await ReportAdvisoryAsync(
            context,
            "RZ09-0529",
            WindowsDynamicLightingAdvisory.MayControl,
            stillControlsSession: () => Volatile.Read(ref sessionActive));
        Assert.Equal(
            WindowsDynamicLightingAdvisory.MayControl,
            context.Runtime.GetStatus().WindowsDynamicLightingAdvisory);

        Volatile.Write(ref sessionActive, false);
        await context.Runtime.ObserveAsync(CancellationToken.None);

        Assert.Equal(
            WindowsDynamicLightingAdvisory.Unknown,
            context.Runtime.GetStatus().WindowsDynamicLightingAdvisory);
    }


    private static Task<ControlOperationResult> ReportAdvisoryAsync(
        TestContext context,
        string modelNumber,
        WindowsDynamicLightingAdvisory advisory,
        SecurityIdentifier? reportingUser = null,
        Func<bool>? stillControlsSession = null) =>
        context.Runtime.ReportWindowsDynamicLightingAdvisoryAsync(
            new ReportWindowsDynamicLightingAdvisoryRequest(
                OpenBladeIpc.ProtocolVersion,
                Guid.NewGuid(),
                modelNumber,
                advisory),
            (reportingUser ?? AdvisoryUser).Value,
            stillControlsSession ?? (static () => true),
            CancellationToken.None);

    private async Task<TestContext> CreateLampArrayAdvisoryContextAsync(string modelNumber)
    {
        DeviceCapabilities lighting = DeviceCapabilities.Unavailable() with
        {
            LightingEffects = new HashSet<LightingEffect>
            {
                LightingEffect.Off,
                LightingEffect.Static,
            },
        };
        TestContext context = CreateContext(
            providerAvailable: true,
            firmwareVersions: new FirmwareVersions(
                "1.00",
                "1.00",
                "1.0.0.0",
                "Unavailable",
                modelNumber),
            profileValidator: ProfileSetValidator.CreateForLightingCapabilities(lighting),
            userConfigurationRepository: new RecordingUserConfigurationRepository(
                new Dictionary<string, ProfileSet>
                {
                    [AdvisoryUser.Value] = ProfileDefaults.CreateSafeDefaults(),
                    [OtherAdvisoryUser.Value] = ProfileDefaults.CreateSafeDefaults(),
                },
                directory));
        // The advisory needs no LampArray capability.
        context.Device.Capabilities = DeviceCapabilities.SafeTargetDefaults() with
        {
            LightingEffects = lighting.LightingEffects,
            LampArrayLightingAccess = null,
        };
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            PerformancePreset.Balanced,
            FanMode.Automatic,
            ConfirmedBattery(80),
            null);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        await context.Runtime.ActivateUserAsync(
            AdvisoryUser,
            static () => true,
            CancellationToken.None);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        Assert.Equal(modelNumber, context.Runtime.GetStatus().Firmware?.ModelNumber);

        ControlOperationResult applied = await context.Runtime.SetLightingOverrideAsync(
            new ManualOverride<LightingSettings>(
                LightingSettingsFactory.Static(new RgbColor(0, 128, 255), brightness: 60),
                OverrideLifetime.UntilPowerSourceChange),
            CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Applied, applied.Status);
        Assert.Equal(
            LightingRendererOwnershipState.Static,
            context.Runtime.GetStatus().LightingRendererOwnership);
        return context;
    }
}
