using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed partial class ServiceControlRuntimeTests
{
    private const string WindowsOwnsKeyboardMessage =
        "Keyboard lighting is delegated to Windows Dynamic Lighting. OpenBlade did not write the keyboard.";

    [Theory]
    [InlineData("RZ09-0528", false)]
    [InlineData("RZ09-0529", true)]
    [InlineData("RZ09-0509", true)]
    [InlineData("RZ09-0510", true)]
    [InlineData("RZ09-0530", true)]
    public async Task ObservedWindowsDynamicLighting_BlocksWritesAndRetiresRendererWithoutWriting(
        string modelNumber,
        bool experimentalBackend)
    {
        TestContext context = await CreateLampArrayLightingContextAsync(modelNumber);
        context.Provider.AvailableWhenExperimentalAllowed = experimentalBackend;
        int writes = context.Device.LightingSetCalls;
        int invalidations = context.Provider.InvalidationCount;

        ControlOperationResult owned = await ReportOwnershipAsync(
            context,
            modelNumber,
            WindowsDynamicLightingOwnership.Owned);

        Assert.Equal(ControlOperationStatus.Stored, owned.Status);
        Assert.Equal(WindowsOwnsKeyboardMessage, owned.Message);
        ServiceStatusSnapshot status = context.Runtime.GetStatus();
        Assert.Equal(WindowsDynamicLightingOwnership.Owned, status.WindowsDynamicLightingOwnership);
        Assert.Equal(LightingRendererOwnershipState.None, status.LightingRendererOwnership);
        // The saved preference is the user's; an observation never rewrites it.
        Assert.Equal(KeyboardLightingControlMode.OpenBlade, status.KeyboardLightingControlMode);
        Assert.Equal(invalidations + 1, context.Provider.InvalidationCount);
        // The experimental backend's ordinary close restores its owned frame,
        // so Windows ownership must take the write-free close there.
        Assert.Equal(experimentalBackend ? 1 : 0, context.Provider.LowPowerInvalidationCount);
        Assert.Equal(writes, context.Device.LightingSetCalls);

        ControlOperationResult effect = await context.Runtime.SetLightingOverrideAsync(
            new ManualOverride<LightingSettings>(
                LightingSettingsFactory.Static(new RgbColor(255, 0, 0), brightness: 40),
                OverrideLifetime.UntilPowerSourceChange),
            CancellationToken.None);
        ControlOperationResult brightness = await context.Runtime.StepKeyboardBrightnessAsync(
            KeyboardBrightnessDirection.Increase,
            OverrideLifetime.UntilPowerSourceChange,
            CancellationToken.None);
        ControlOperationResult ambient = await context.Runtime.PushAmbientLightingFrameAsync(
            new AmbientLightingFrame(new RgbColor[AmbientLightingFrame.SlotCount]),
            CancellationToken.None);
        ControlOperationResult uniform = await context.Runtime.PushUniformLightingFrameAsync(
            new UniformLightingFrame(new RgbColor(12, 34, 56)),
            CancellationToken.None);

        Assert.All(
            new[] { effect, brightness, ambient, uniform },
            result =>
            {
                // An effect request keeps the user's intent, as the saved
                // delegation does; its hardware result carries the same message.
                Assert.Equal(ControlOperationStatus.Blocked, result.Status);
                Assert.EndsWith(WindowsOwnsKeyboardMessage, result.Message, StringComparison.Ordinal);
            });
        Assert.Equal(writes, context.Device.LightingSetCalls);
    }

    [Theory]
    [InlineData("RZ09-0528")]
    [InlineData("RZ09-0529")]
    [InlineData("RZ09-0509")]
    [InlineData("RZ09-0510")]
    [InlineData("RZ09-0530")]
    public async Task ObservedWindowsDynamicLighting_ReleaseUnblocksWithoutRestoringOverWindows(
        string modelNumber)
    {
        TestContext context = await CreateLampArrayLightingContextAsync(modelNumber);
        Assert.Equal(
            ControlOperationStatus.Stored,
            (await ReportOwnershipAsync(
                context,
                modelNumber,
                WindowsDynamicLightingOwnership.Owned)).Status);
        // Intent stored while Windows owns the keyboard is not replayed later.
        Assert.Equal(
            ControlOperationStatus.Blocked,
            (await context.Runtime.SetLightingOverrideAsync(
                new ManualOverride<LightingSettings>(
                    LightingSettingsFactory.Static(new RgbColor(255, 0, 0), brightness: 40),
                    OverrideLifetime.UntilPowerSourceChange),
                CancellationToken.None)).Status);
        int writes = context.Device.LightingSetCalls;
        int invalidations = context.Provider.InvalidationCount;

        ControlOperationResult released = await ReportOwnershipAsync(
            context,
            modelNumber,
            WindowsDynamicLightingOwnership.Released);

        Assert.Equal(ControlOperationStatus.Stored, released.Status);
        Assert.Contains("No lighting change was applied", released.Message, StringComparison.Ordinal);
        Assert.Equal(
            WindowsDynamicLightingOwnership.Released,
            context.Runtime.GetStatus().WindowsDynamicLightingOwnership);
        // Release reapplies nothing: the next explicit action re-establishes intent.
        Assert.Equal(writes, context.Device.LightingSetCalls);
        Assert.Equal(invalidations, context.Provider.InvalidationCount);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        Assert.Equal(writes, context.Device.LightingSetCalls);

        ControlOperationResult applied = await context.Runtime.SetLightingOverrideAsync(
            new ManualOverride<LightingSettings>(
                LightingSettingsFactory.Static(new RgbColor(0, 255, 0), brightness: 50),
                OverrideLifetime.UntilPowerSourceChange),
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, applied.Status);
        Assert.Equal(writes + 1, context.Device.LightingSetCalls);
    }

    [Fact]
    public async Task ObservedWindowsDynamicLighting_RepeatedOwnedReportsAreIdempotentAndWriteNothing()
    {
        TestContext context = await CreateLampArrayLightingContextAsync("RZ09-0529");
        context.Provider.AvailableWhenExperimentalAllowed = true;
        await ReportOwnershipAsync(context, "RZ09-0529", WindowsDynamicLightingOwnership.Owned);
        int writes = context.Device.LightingSetCalls;

        ControlOperationResult repeated = await ReportOwnershipAsync(
            context,
            "RZ09-0529",
            WindowsDynamicLightingOwnership.Owned);

        Assert.Equal(ControlOperationStatus.Stored, repeated.Status);
        Assert.Contains("still controls", repeated.Message, StringComparison.Ordinal);
        Assert.Equal(writes, context.Device.LightingSetCalls);
        Assert.Equal(2, context.Provider.LowPowerInvalidationCount);
    }

    [Fact]
    public async Task ObservedWindowsDynamicLighting_RejectsAReportForAnotherModel()
    {
        TestContext context = await CreateLampArrayLightingContextAsync("RZ09-0529");
        int invalidations = context.Provider.InvalidationCount;

        ControlOperationResult mismatched = await ReportOwnershipAsync(
            context,
            "RZ09-0528",
            WindowsDynamicLightingOwnership.Owned);

        Assert.Equal(ControlOperationStatus.Blocked, mismatched.Status);
        Assert.Equal(
            WindowsDynamicLightingOwnership.Unknown,
            context.Runtime.GetStatus().WindowsDynamicLightingOwnership);
        Assert.Equal(invalidations, context.Provider.InvalidationCount);
        Assert.Equal(
            ControlOperationStatus.Applied,
            (await context.Runtime.SetLightingOverrideAsync(
                new ManualOverride<LightingSettings>(
                    LightingSettingsFactory.Static(new RgbColor(0, 0, 255), brightness: 30),
                    OverrideLifetime.UntilPowerSourceChange),
                CancellationToken.None)).Status);
    }

    [Theory]
    // SO690 declares no LampArray; the RZ09-0581 descriptor is unreviewed.
    [InlineData("RZ09-0483")]
    [InlineData("RZ09-0581")]
    public async Task ObservedWindowsDynamicLighting_RejectsModelsWithoutAReviewedLampArray(
        string modelNumber)
    {
        TestContext context = await CreateLampArrayLightingContextAsync(modelNumber);

        ControlOperationResult rejected = await ReportOwnershipAsync(
            context,
            modelNumber,
            WindowsDynamicLightingOwnership.Owned);

        Assert.NotEqual(ControlOperationStatus.Stored, rejected.Status);
        Assert.Equal(
            WindowsDynamicLightingOwnership.Unknown,
            context.Runtime.GetStatus().WindowsDynamicLightingOwnership);
    }

    [Fact]
    public async Task ObservedWindowsDynamicLighting_RequiresTheAdvertisedLampArrayAndActiveSession()
    {
        TestContext context = await CreateLampArrayLightingContextAsync(
            "RZ09-0529",
            lampArrayAccess: null);

        ControlOperationResult unadvertised = await ReportOwnershipAsync(
            context,
            "RZ09-0529",
            WindowsDynamicLightingOwnership.Owned);
        context.Device.Capabilities = context.Device.Capabilities with
        {
            LampArrayLightingAccess = DeviceFeatureAccess.ObservedOnly,
        };
        await context.Runtime.ObserveAsync(CancellationToken.None);
        ControlOperationResult inactive =
            await context.Runtime.ReportWindowsDynamicLightingOwnershipAsync(
                new ReportWindowsDynamicLightingOwnershipRequest(
                    OpenBladeIpc.ProtocolVersion,
                    Guid.NewGuid(),
                    "RZ09-0529",
                    WindowsDynamicLightingOwnership.Owned),
                static () => false,
                CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Blocked, unadvertised.Status);
        Assert.Equal(ControlOperationStatus.Blocked, inactive.Status);
        Assert.Equal(
            WindowsDynamicLightingOwnership.Unknown,
            context.Runtime.GetStatus().WindowsDynamicLightingOwnership);
    }

    [Fact]
    public async Task ObservedWindowsDynamicLighting_KeepsBlockingWhenRendererRetirementFails()
    {
        TestContext context = await CreateLampArrayLightingContextAsync("RZ09-0529");
        context.Provider.AvailableWhenExperimentalAllowed = true;
        context.Provider.InvalidationFailure =
            new InvalidOperationException("Synthetic invalidation failure.");
        int writes = context.Device.LightingSetCalls;

        ControlOperationResult failed = await ReportOwnershipAsync(
            context,
            "RZ09-0529",
            WindowsDynamicLightingOwnership.Owned);

        Assert.Equal(ControlOperationStatus.Failed, failed.Status);
        Assert.Equal(
            WindowsDynamicLightingOwnership.Owned,
            context.Runtime.GetStatus().WindowsDynamicLightingOwnership);
        ControlOperationResult effect = await context.Runtime.SetLightingOverrideAsync(
            new ManualOverride<LightingSettings>(
                LightingSettingsFactory.Off(),
                OverrideLifetime.UntilPowerSourceChange),
            CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Blocked, effect.Status);
        Assert.Equal(writes, context.Device.LightingSetCalls);

        // The next observation retries the write-free retirement.
        Assert.Equal(
            ControlOperationStatus.Stored,
            (await ReportOwnershipAsync(
                context,
                "RZ09-0529",
                WindowsDynamicLightingOwnership.Owned)).Status);
        Assert.Equal(writes, context.Device.LightingSetCalls);
    }

    [Fact]
    public async Task ObservedWindowsDynamicLighting_ProjectsUnknownAfterTheDeviceChanges()
    {
        TestContext context = await CreateLampArrayLightingContextAsync("RZ09-0529");
        await ReportOwnershipAsync(context, "RZ09-0529", WindowsDynamicLightingOwnership.Owned);

        context.Device.Capabilities = context.Device.Capabilities with
        {
            LampArrayLightingAccess = null,
        };
        await context.Runtime.ObserveAsync(CancellationToken.None);

        Assert.Equal(
            WindowsDynamicLightingOwnership.Unknown,
            context.Runtime.GetStatus().WindowsDynamicLightingOwnership);
    }

    private static Task<ControlOperationResult> ReportOwnershipAsync(
        TestContext context,
        string modelNumber,
        WindowsDynamicLightingOwnership ownership) =>
        context.Runtime.ReportWindowsDynamicLightingOwnershipAsync(
            new ReportWindowsDynamicLightingOwnershipRequest(
                OpenBladeIpc.ProtocolVersion,
                Guid.NewGuid(),
                modelNumber,
                ownership),
            static () => true,
            CancellationToken.None);

    private async Task<TestContext> CreateLampArrayLightingContextAsync(
        string modelNumber,
        DeviceFeatureAccess? lampArrayAccess = DeviceFeatureAccess.ObservedOnly)
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
            profileValidator: ProfileSetValidator.CreateForLightingCapabilities(lighting));
        context.Device.Capabilities = DeviceCapabilities.SafeTargetDefaults() with
        {
            LightingEffects = lighting.LightingEffects,
            LampArrayLightingAccess = lampArrayAccess,
        };
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            PerformancePreset.Balanced,
            FanMode.Automatic,
            ConfirmedBattery(80),
            null);
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
