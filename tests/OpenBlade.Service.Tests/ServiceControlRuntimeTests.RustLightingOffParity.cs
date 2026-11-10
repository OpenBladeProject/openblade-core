using OpenBlade.Contracts;

namespace OpenBlade.Service.Tests;

public sealed partial class ServiceControlRuntimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RustLightingOffParity_TypedRequestStagesIntentAndRequiresFinalObservation(bool loseFinalRead)
    {
        string overridePath = Path.Combine(directory, "rust-lighting-off-parity-overrides.json");
        TestContext context = CreateContext(
            providerAvailable: true, persistentOverridePath: overridePath, initialize: false);
        Assert.Equal("RZ09-0581|1532:02E0", context.Provider.LaptopControlRecoveryIdentity);
        context.Device.AdapterPowerState = AdapterPowerState.FullPower280W;
        context.Device.Capabilities = DeviceCapabilities.SafeTargetDefaults();
        var configuration = await context.Store.LoadAsync(default);
        var off = LightingSettingsFactory.Off();
        PowerProfile profile = configuration.Profiles.PluggedIn with { Lighting = off };
        var lit = new ConfirmedDeviceState(
            profile.Performance, profile.Fans, ConfirmedBattery(80), null)
        {
            LogoLighting = LogoLightingMode.Breathing,
        };
        context.Device.ConfirmedState = lit;
        context.Device.ScopedLightingReadback = lit;
        context.Runtime.Initialize(
            configuration with { Profiles = configuration.Profiles with { PluggedIn = profile } },
            PowerSource.PluggedIn, PersistentOverrideState.Empty, recoveringUnexpectedExit: false);
        await context.Runtime.ObserveAsync(default);
        int writes = context.Device.LightingSetCalls;

        var store = new PersistentOverrideStore(overridePath);
        List<string> calls = [];
        bool finalReadUnavailable = false;
        context.Device.BeforeReadLightingState = () =>
        {
            calls.Add("ReadKeyboard");
            if (finalReadUnavailable)
            {
                finalReadUnavailable = false;
                throw new IOException("Scripted final keyboard getter failure.");
            }
        };
        context.Device.AfterSetter = subsystem =>
        {
            Assert.Equal(DeviceSubsystem.Lighting, subsystem);
            Assert.Equal(off, store.LoadAsync(default).GetAwaiter().GetResult().Overrides.Lighting);
            calls.Add("SetKeyboard");
            finalReadUnavailable = loseFinalRead;
            context.Device.ScopedLightingReadback = lit with { Lighting = off };
        };
        var handler = new ControlRequestHandler(context.Runtime, new RecoveryArchiveProvider());
        var request = new SetLightingOverrideRequest(
            OpenBladeIpc.ProtocolVersion, Guid.NewGuid(), new(off, OverrideLifetime.Persistent));

        ServiceResponse response = await handler.HandleAsync(request, default);

        Assert.True(response.Result?.Status == (loseFinalRead ? ControlOperationStatus.Failed : ControlOperationStatus.Applied),
            response.Result?.Message ?? response.Error);
        Assert.Equal(["ReadKeyboard", "SetKeyboard", "ReadKeyboard"], calls);
        Assert.Equal(writes + 1, context.Device.LightingSetCalls);
        ServiceStatusSnapshot status = context.Runtime.GetStatus();
        Assert.Equal(request.Value, status.LightingOverride);
        Assert.Equal(loseFinalRead ? null : off, status.ConfirmedState?.Lighting);
        Assert.Equal(LogoLightingMode.Breathing, status.ConfirmedState?.LogoLighting);
        Assert.Equal(LightingRendererOwnershipState.None, status.LightingRendererOwnership);

        if (loseFinalRead)
        {
            Assert.Contains(DeviceSubsystem.Lighting, status.UnconfirmedWrites!);
            ServiceResponse retry = await handler.HandleAsync(request with { RequestId = Guid.NewGuid() }, default);
            Assert.Equal(ControlOperationStatus.Blocked, retry.Result?.Status);
            Assert.Equal(writes + 1, context.Device.LightingSetCalls);
        }
        else
        {
            ControlOperationResult repeated = await context.Runtime.SetLightingOverrideAsync(
                new(off, OverrideLifetime.UntilPowerSourceChange), default);
            Assert.Equal(ControlOperationStatus.Applied, repeated.Status);
            Assert.Equal(writes + 1, context.Device.LightingSetCalls);
            Assert.Null((await store.LoadAsync(default)).Overrides.Lighting);
            Assert.Equal(OverrideLifetime.UntilPowerSourceChange, context.Runtime.GetStatus().LightingOverride?.Lifetime);
        }
    }
}
