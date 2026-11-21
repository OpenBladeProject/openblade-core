using OpenBlade.Contracts;

namespace OpenBlade.Protocol.Tests;

public sealed partial class ExperimentalBladeFactoryTests
{
    [Fact]
    public async Task Blade14Edition_IsReadOnceForTheDeviceAcrossBatteryAndLightingSessions()
    {
        ExperimentalBladeModel model = ExperimentalBladeModels.All.Single(item => item.ProductId == 0x02C5);
        var native = new Native(1) { ProductId = model.ProductId };
        var io = new Io();
        var factory = new ExperimentalBladeFactory(model, new Identity(model.ProductName), native,
            new Guard(), io, () => PowerSource.ExternalUnknown);
        Assert.Null(factory.Capabilities.Blade14KeyboardEdition);

        await using (ExperimentalBladePort battery = Assert.IsType<ExperimentalBladePort>(
            (await factory.OpenAsync(CancellationToken.None)).Value))
        {
            Assert.Equal((byte)12, battery.Capabilities.Blade14KeyboardEdition);
        }
        HidSessionResult<IProductionLightingPort> lighting =
            await factory.CreateLightingFactory().OpenAsync(CancellationToken.None);
        await using (ExperimentalBladePort port = Assert.IsType<ExperimentalBladePort>(lighting.Value))
        {
            Assert.Equal((byte)12, port.Capabilities.Blade14KeyboardEdition);
        }
        await using (ExperimentalBladePort battery = Assert.IsType<ExperimentalBladePort>(
            (await factory.OpenAsync(CancellationToken.None)).Value))
        {
            Assert.Equal((byte)12, battery.Capabilities.Blade14KeyboardEdition);
        }

        // Each session repeated its own fresh interface admission, but not the metadata GET.
        Assert.Equal(1, io.Commands.Count(command => command == 0x86));
        Assert.Equal(3, native.BoundCount);
        Assert.Equal((byte)12, factory.Capabilities.Blade14KeyboardEdition);
    }

    [Fact]
    public async Task Blade14EditionRejection_IsNotReprobedUntilTheDeviceIdentityChanges()
    {
        ExperimentalBladeModel model = ExperimentalBladeModels.All.Single(item => item.ProductId == 0x02C5);
        var native = new Native(1) { ProductId = model.ProductId, VersionNumber = 0x0104 };
        var io = new Io
        {
            StatusFailure = (0x00, 0x86),
            StatusFailuresRemaining = 1,
        };
        var factory = new ExperimentalBladeFactory(model, new Identity(model.ProductName), native,
            new Guard(), io, () => PowerSource.ExternalUnknown);

        await using (ExperimentalBladePort first = Assert.IsType<ExperimentalBladePort>(
            (await factory.OpenAsync(CancellationToken.None)).Value))
        {
            Assert.Null(first.Capabilities.Blade14KeyboardEdition);
        }
        await using (ExperimentalBladePort second = Assert.IsType<ExperimentalBladePort>(
            (await factory.OpenAsync(CancellationToken.None)).Value))
        {
            Assert.Null(second.Capabilities.Blade14KeyboardEdition);
        }

        // The rejected GET poisoned one session and was retired once; the next session
        // keeps the common map without sending it again.
        Assert.Equal(new byte[] { 0x82, 0x82, 0x86, 0x82, 0x82, 0x82, 0x82 }, io.Commands);
        Assert.Equal(3, native.BoundCount);
        Assert.Null(factory.Capabilities.Blade14KeyboardEdition);

        // A changed HID identity, such as an MCU update, is a different device result.
        native.VersionNumber = 0x0105;
        await using (ExperimentalBladePort updated = Assert.IsType<ExperimentalBladePort>(
            (await factory.OpenAsync(CancellationToken.None)).Value))
        {
            Assert.Equal((byte)12, updated.Capabilities.Blade14KeyboardEdition);
        }
        Assert.Equal(2, io.Commands.Count(command => command == 0x86));
        Assert.Equal((byte)12, factory.Capabilities.Blade14KeyboardEdition);
    }

    [Fact]
    public async Task Blade14Recovery_PublishesTheConfirmedEditionWithoutReadingItAgain()
    {
        ExperimentalBladeModel model = ExperimentalBladeModels.All.Single(item => item.ProductId == 0x02C5);
        var native = new Native(1) { ProductId = model.ProductId };
        var io = new RecoveryIo { IgnoreThermalWrites = true, Edition = 2 };
        var factory = new ExperimentalBladeFactory(model, new Identity(model.ProductName), native,
            new Guard(), io, () => PowerSource.PluggedIn);
        ExperimentalBladePort port = Assert.IsType<ExperimentalBladePort>((await factory.OpenAsync(default)).Value);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetPerformanceAsync(PerformancePreset.Silent, default));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.True(factory.IsWritesPaused);
        await port.DisposeAsync();

        await factory.ResumeWritesAsync(_ => true, () => true, default);

        // The runtime publishes these factory capabilities after recovery.
        Assert.False(factory.IsWritesPaused);
        Assert.Equal((byte)2, factory.Capabilities.Blade14KeyboardEdition);
        Assert.Equal(KeyboardLightingLayoutIds.Rz09_0530, factory.Capabilities.KeyboardLightingLayoutId);
        Assert.Single(io.Commands, command => command == (0x00, 0x86));
    }

    [Fact]
    public async Task Blade18Layout_IsReadOnceForTheDeviceAndKeptForRecovery()
    {
        ExperimentalBladeModel model = ExperimentalBladeModels.All.Single(item => item.ProductId == 0x02A0);
        var native = new Native(1) { ProductId = model.ProductId };
        var io = new Io();
        var factory = new ExperimentalBladeFactory(model, new Identity(model.ProductName), native,
            new Guard(), io, () => PowerSource.ExternalUnknown);

        for (int open = 0; open < 2; open++)
        {
            await using ExperimentalBladePort port = Assert.IsType<ExperimentalBladePort>(
                (await factory.OpenAsync(CancellationToken.None)).Value);
            Assert.Equal((byte)12, port.Capabilities.Blade18KeyboardLayoutId);
        }

        Assert.Equal(1, io.Commands.Count(command => command == 0x86));
        Assert.Equal((byte)12, factory.Capabilities.Blade18KeyboardLayoutId);
        Assert.Null(factory.Capabilities.Blade14KeyboardEdition);
    }

    private sealed class MutableHostIdentity(string product) : IHostIdentitySource
    {
        internal string? Bios { get; set; } = "1.00";
        internal string? Sku { get; set; }
        public HostIdentityReadResult ReadExactIdentity() =>
            new(new HostIdentityEvidence("Razer", product, Sku, Bios), null);
    }

    private static byte? KeyboardLayoutValue(ExperimentalBladeModel model, DeviceCapabilities capabilities) =>
        model.ProductId == 0x02C5 ? capabilities.Blade14KeyboardEdition : capabilities.Blade18KeyboardLayoutId;

    [Theory]
    [InlineData(0x02C5)]
    [InlineData(0x02A0)]
    public async Task KeyboardLayoutGuardRefusalBeforeTheGet_IsNotCachedAndTheNextSessionReads(int productId)
    {
        ExperimentalBladeModel model = ExperimentalBladeModels.All.Single(item => item.ProductId == productId);
        var native = new Native(1) { ProductId = model.ProductId };
        var guard = new Guard();
        int gets = 0;
        // A detection failure appears after interface admission and before the layout GET.
        var io = new Io { AfterGet = () => { if (++gets == 2) { guard.Fail = true; } } };
        var factory = new ExperimentalBladeFactory(model, new Identity(model.ProductName), native,
            guard, io, () => PowerSource.ExternalUnknown);

        HidSessionResult<IProductionBatteryPort> refused = await factory.OpenAsync(CancellationToken.None);
        if (refused.Value is ExperimentalBladePort refusedPort)
        {
            Assert.Null(KeyboardLayoutValue(model, refusedPort.Capabilities));
            await refusedPort.DisposeAsync();
        }
        Assert.DoesNotContain((byte)0x86, io.Commands);
        guard.Fail = false;

        await using ExperimentalBladePort later = Assert.IsType<ExperimentalBladePort>(
            (await factory.OpenAsync(CancellationToken.None)).Value);
        Assert.Equal((byte)12, KeyboardLayoutValue(model, later.Capabilities));
        Assert.Equal((byte)12, KeyboardLayoutValue(model, factory.Capabilities));
        Assert.Equal(1, io.Commands.Count(command => command == 0x86));
    }

    [Theory]
    [InlineData(0x02C5, HidSessionFailureKind.Exhausted, HidSessionFailurePhase.SharedGateAdmission, null, false, false)]
    [InlineData(0x02A0, HidSessionFailureKind.Timeout, HidSessionFailurePhase.SharedGateAdmission, null, false, false)]
    [InlineData(0x02C5, HidSessionFailureKind.ConflictDetectionFailed, HidSessionFailurePhase.Guard, null, false, false)]
    [InlineData(0x02A0, HidSessionFailureKind.ConflictBlocked, HidSessionFailurePhase.Guard, null, false, false)]
    [InlineData(0x02C5, HidSessionFailureKind.QueuedCanceled, HidSessionFailurePhase.SharedGateAdmission, null, false, false)]
    [InlineData(0x02A0, HidSessionFailureKind.Timeout, HidSessionFailurePhase.GetCompletion, null, false, false)]
    [InlineData(0x02C5, HidSessionFailureKind.Timeout, HidSessionFailurePhase.GetCompletion, null, false, false)]
    [InlineData(0x02C5, HidSessionFailureKind.Timeout, HidSessionFailurePhase.GetCompletion, null, true, true)]
    [InlineData(0x02A0, HidSessionFailureKind.NativeIoFailure, HidSessionFailurePhase.GetSubmit, null, true, true)]
    [InlineData(0x02C5, HidSessionFailureKind.ProtocolFailure, HidSessionFailurePhase.ProtocolValidation, HidProtocolValidationFailure.Status, false, true)]
    [InlineData(0x02A0, HidSessionFailureKind.ProtocolFailure, HidSessionFailurePhase.ProtocolValidation, HidProtocolValidationFailure.PayloadValidator, false, true)]
    [InlineData(0x02A0, HidSessionFailureKind.ProtocolFailure, HidSessionFailurePhase.ProtocolValidation, HidProtocolValidationFailure.PayloadLength, false, true)]
    public void KeyboardLayoutFailure_IsSettledOnlyWhenTheDeviceAnsweredOrThePoisonedSession(
        int productId, HidSessionFailureKind failure, HidSessionFailurePhase phase,
        HidProtocolValidationFailure? validation, bool sessionRetired, bool settled)
    {
        Assert.Contains(ExperimentalBladeModels.All, item => item.ProductId == productId);
        var diagnostic = new HidSessionFailureDiagnostic(phase, failure, null, null)
        {
            ProtocolValidationFailure = validation,
        };

        Assert.Equal(settled, ExperimentalBladePort.KeyboardLayoutFailureIsDeviceResult(
            new NativeDeviceReadException(failure, diagnostic, null),
            sessionRetired ? RazerCommandSessionDisposition.RecoveryRequired : RazerCommandSessionDisposition.Healthy));
    }

    [Fact]
    public void KeyboardLayoutPayloadMismatchAfterTheReply_IsADeviceResult() =>
        Assert.True(ExperimentalBladePort.KeyboardLayoutFailureIsDeviceResult(
            new IOException("Experimental response did not match the command contract."),
            RazerCommandSessionDisposition.Healthy));

    [Theory]
    [InlineData(0x02C5)]
    [InlineData(0x02A0)]
    public async Task KeyboardLayoutStatusRejection_IsCachedForTheDeviceIdentity(int productId)
    {
        ExperimentalBladeModel model = ExperimentalBladeModels.All.Single(item => item.ProductId == productId);
        var native = new Native(1) { ProductId = model.ProductId };
        var io = new Io { StatusFailure = (0x00, 0x86), StatusFailuresRemaining = 1 };
        var factory = new ExperimentalBladeFactory(model, new Identity(model.ProductName), native,
            new Guard(), io, () => PowerSource.ExternalUnknown);

        for (int open = 0; open < 3; open++)
        {
            await using ExperimentalBladePort port = Assert.IsType<ExperimentalBladePort>(
                (await factory.OpenAsync(CancellationToken.None)).Value);
            Assert.Null(KeyboardLayoutValue(model, port.Capabilities));
        }

        Assert.Equal(1, io.Commands.Count(command => command == 0x86));
        Assert.Null(KeyboardLayoutValue(model, factory.Capabilities));
    }

    [Theory]
    [InlineData(0x02C5, 1)]
    [InlineData(0x02A0, 2)]
    public async Task KeyboardLayoutPayloadValidationFailure_IsCachedForTheDeviceIdentity(
        int productId, int replyDataSize)
    {
        ExperimentalBladeModel model = ExperimentalBladeModels.All.Single(item => item.ProductId == productId);
        var native = new Native(1) { ProductId = model.ProductId };
        var io = new Io { LayoutReplyDataSize = (byte)replyDataSize };
        var factory = new ExperimentalBladeFactory(model, new Identity(model.ProductName), native,
            new Guard(), io, () => PowerSource.ExternalUnknown);

        for (int open = 0; open < 3; open++)
        {
            await using ExperimentalBladePort port = Assert.IsType<ExperimentalBladePort>(
                (await factory.OpenAsync(CancellationToken.None)).Value);
            Assert.Null(KeyboardLayoutValue(model, port.Capabilities));
        }

        Assert.Equal(1, io.Commands.Count(command => command == 0x86));
    }

    [Fact]
    public async Task CanceledEditionRead_IsNotCachedAsAFailure()
    {
        ExperimentalBladeModel model = ExperimentalBladeModels.All.Single(item => item.ProductId == 0x02C5);
        var native = new Native(1) { ProductId = model.ProductId };
        using var cancellation = new CancellationTokenSource();
        int gets = 0;
        // Cancel while the edition GET is in flight, after interface admission.
        var io = new Io { AfterGet = () => { if (++gets == 3) { cancellation.Cancel(); } } };
        var factory = new ExperimentalBladeFactory(model, new Identity(model.ProductName), native,
            new Guard(), io, () => PowerSource.ExternalUnknown);

        HidSessionResult<IProductionBatteryPort> canceled = await factory.OpenAsync(cancellation.Token);

        Assert.Equal(HidSessionFailureKind.Canceled, canceled.Failure);
        Assert.Null(canceled.Value);
        Assert.Null(factory.Capabilities.Blade14KeyboardEdition);
        await using ExperimentalBladePort port = Assert.IsType<ExperimentalBladePort>(
            (await factory.OpenAsync(CancellationToken.None)).Value);
        Assert.Equal((byte)12, port.Capabilities.Blade14KeyboardEdition);
        Assert.Equal(2, io.Commands.Count(command => command == 0x86));
    }

    [Fact]
    public async Task EditionIsReadAgainWhenTheBiosOrSkuChanges()
    {
        ExperimentalBladeModel model = ExperimentalBladeModels.All.Single(item => item.ProductId == 0x02C5);
        var native = new Native(1) { ProductId = model.ProductId };
        var io = new Io();
        var identity = new MutableHostIdentity(model.ProductName);
        var factory = new ExperimentalBladeFactory(model, identity, native,
            new Guard(), io, () => PowerSource.ExternalUnknown);

        for (int open = 0; open < 2; open++)
        {
            await using ExperimentalBladePort port = Assert.IsType<ExperimentalBladePort>(
                (await factory.OpenAsync(CancellationToken.None)).Value);
            Assert.Equal((byte)12, port.Capabilities.Blade14KeyboardEdition);
        }
        Assert.Equal(1, io.Commands.Count(command => command == 0x86));

        identity.Bios = "1.01";
        await using (ExperimentalBladePort port = Assert.IsType<ExperimentalBladePort>(
            (await factory.OpenAsync(CancellationToken.None)).Value))
        {
            Assert.Equal((byte)12, port.Capabilities.Blade14KeyboardEdition);
        }
        Assert.Equal(2, io.Commands.Count(command => command == 0x86));

        identity.Sku = "RZ09-05301EM3";
        await using (ExperimentalBladePort port = Assert.IsType<ExperimentalBladePort>(
            (await factory.OpenAsync(CancellationToken.None)).Value))
        {
            Assert.Equal((byte)12, port.Capabilities.Blade14KeyboardEdition);
        }
        Assert.Equal(3, io.Commands.Count(command => command == 0x86));
    }

    [Fact]
    public async Task PausedWrites_KeepPublishedCapabilitiesWellFormed()
    {
        ExperimentalBladeModel model = ExperimentalBladeModels.All.Single(item => item.ProductId == 0x02C5);
        var native = new Native(1) { ProductId = model.ProductId };
        var io = new RecoveryIo { IgnoreThermalWrites = true, Edition = 2 };
        var factory = new ExperimentalBladeFactory(model, new Identity(model.ProductName), native,
            new Guard(), io, () => PowerSource.PluggedIn);
        ExperimentalBladePort port = Assert.IsType<ExperimentalBladePort>((await factory.OpenAsync(default)).Value);
        await AssertStatusAcceptsAsync(port.Capabilities);
        await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetPerformanceAsync(PerformancePreset.Silent, default));
        Assert.True(factory.IsWritesPaused);

        DeviceCapabilities pausedPort = port.Capabilities;
        DeviceCapabilities pausedFactory = factory.Capabilities;
        await port.DisposeAsync();

        // An edition without its layout id is malformed, so a paused view must carry neither or both.
        await AssertStatusAcceptsAsync(pausedPort);
        await AssertStatusAcceptsAsync(pausedFactory);
        Assert.Equal(pausedPort.KeyboardLightingLayoutId is null, pausedPort.Blade14KeyboardEdition is null);
        Assert.Equal(pausedFactory.KeyboardLightingLayoutId is null, pausedFactory.Blade14KeyboardEdition is null);
    }

    // The service rejects a status whose capabilities are malformed, such as a regional
    // edition without the RZ09-0530 layout id, so this is the contract's own validation.
    private static async Task AssertStatusAcceptsAsync(DeviceCapabilities capabilities)
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        DesiredDeviceState desired = new(
            new ResolvedSetting<PerformancePreset>(PerformancePreset.Balanced, SettingValueSource.PowerProfile),
            new ResolvedSetting<FanMode>(FanMode.Automatic, SettingValueSource.PowerProfile),
            new ResolvedSetting<LightingSettings>(profiles.PluggedIn.Lighting, SettingValueSource.PowerProfile),
            new DesiredBatteryState(profiles.BatteryPolicy, protectionEnabled: true, temporaryFullCharge: false));
        var status = new ServiceStatusSnapshot(1, true, false, [], null, [], "Ready", capabilities,
            null, profiles, desired, PowerSource.PluggedIn, null, null, null, null, false, null,
            ServiceVersion: "0.1.0-test");
        using var stream = new MemoryStream();

        await IpcMessageCodec.WriteServerFrameAsync(stream,
            new ServiceStatusEvent(OpenBladeIpc.ProtocolVersion, status), CancellationToken.None);
    }
}
