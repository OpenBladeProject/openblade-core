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
}
