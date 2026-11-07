using OpenBlade.Contracts;

namespace OpenBlade.Protocol.Tests;

public sealed partial class ExperimentalBladeFactoryTests
{
    [Theory]
    [InlineData(0x028A)]
    [InlineData(0x028B)]
    [InlineData(0x028C)]
    public async Task ThermalRecovery_UnsupportedBatteryGetterDoesNotBlockFreshThermalReconciliation(int product)
    {
        ExperimentalBladeModel model = ExperimentalBladeModels.All.Single(item => item.ProductId == product);
        var native = new Native(1) { ProductId = model.ProductId };
        var io = new RecoveryIo { IgnoreThermalWrites = true, RejectBatteryGet = true };
        var factory = new ExperimentalBladeFactory(model, new Identity(model.ProductName), native,
            new Guard(), io, () => PowerSource.PluggedIn);
        ExperimentalBladePort port = Assert.IsType<ExperimentalBladePort>((await factory.OpenAsync(default)).Value);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetPerformanceAsync(PerformancePreset.Silent, default));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.True(factory.IsWritesPaused);
        await port.DisposeAsync();
        int beforeRecovery = io.Commands.Count;

        ConfirmedDeviceState recovered = await factory.ResumeWritesAsync(observed =>
        {
            Assert.Equal(PerformancePreset.Balanced, observed.Performance);
            Assert.Null(observed.Battery);
            Assert.True(factory.IsWritesPaused);
            Assert.Equal(2, native.DisposeCounts[0]);
            return true;
        }, () => true, default);

        Assert.Null(recovered.Battery);
        Assert.False(factory.IsWritesPaused);
        Assert.All(io.Commands.Skip(beforeRecovery), command => Assert.True(command.Id >= 0x80));
        Assert.DoesNotContain(io.Commands, command => command == (0x07, 0x92));
        Assert.Equal(2, io.Commands.Count(command => command == (0x0D, 0x02)));
        io.IgnoreThermalWrites = false;
        await using ExperimentalBladePort next = Assert.IsType<ExperimentalBladePort>((await factory.OpenAsync(default)).Value);
        await next.SetPerformanceAsync(PerformancePreset.Silent, default);
        Assert.Equal(PerformancePreset.Silent, (await next.QueryPerformanceFanStateAsync(default)).Performance);
    }

    [Theory]
    [InlineData(0x028A)]
    [InlineData(0x028B)]
    [InlineData(0x028C)]
    public async Task BatteryRecovery_UnsupportedGetterCannotDischargeUncertainPolicyWrite(int product)
    {
        ExperimentalBladeModel model = ExperimentalBladeModels.All.Single(item => item.ProductId == product);
        var native = new Native(1) { ProductId = model.ProductId };
        var io = new RecoveryIo { IgnoreBatteryWrites = true };
        var factory = new ExperimentalBladeFactory(model, new Identity(model.ProductName), native,
            new Guard(), io, () => PowerSource.PluggedIn);
        ExperimentalBladePort port = Assert.IsType<ExperimentalBladePort>((await factory.OpenAsync(default)).Value);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetChargeLimitAsync(75, default));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.True(factory.IsWritesPaused);
        await port.DisposeAsync();
        int beforeRecovery = io.Commands.Count;
        io.RejectBatteryGet = true;
        bool accepted = false;

        await Assert.ThrowsAnyAsync<IOException>(() => factory.ResumeWritesAsync(_ =>
        {
            accepted = true;
            return true;
        }, () => true, default));

        Assert.False(accepted);
        Assert.True(factory.IsWritesPaused);
        Assert.Contains(io.Commands.Skip(beforeRecovery), command => command == (0x07, 0x92));
        Assert.All(io.Commands.Skip(beforeRecovery), command => Assert.True(command.Id >= 0x80));
        Assert.Single(io.Commands, command => command == (0x07, 0x12));
        io.RejectBatteryGet = false;
        ConfirmedDeviceState recovered = await factory.ResumeWritesAsync(observed => observed.Battery is not null, () => true, default);
        Assert.NotNull(recovered.Battery);
        Assert.False(factory.IsWritesPaused);
        Assert.Single(io.Commands, command => command == (0x07, 0x12));
    }

    [Theory]
    [InlineData(0x028A)]
    [InlineData(0x028B)]
    [InlineData(0x028C)]
    public async Task UnsupportedReadOnlyBatteryQuery_DoesNotCreateWriteRecoveryObligation(int product)
    {
        ExperimentalBladeModel model = ExperimentalBladeModels.All.Single(item => item.ProductId == product);
        var native = new Native(1) { ProductId = model.ProductId };
        var io = new RecoveryIo { RejectBatteryGet = true };
        var factory = new ExperimentalBladeFactory(model, new Identity(model.ProductName), native,
            new Guard(), io, () => PowerSource.PluggedIn);
        ExperimentalBladePort port = Assert.IsType<ExperimentalBladePort>((await factory.OpenAsync(default)).Value);
        await Assert.ThrowsAnyAsync<IOException>(() => port.QueryProtectionAsync(default));
        Assert.False(factory.IsWritesPaused);
        await port.DisposeAsync();
        await using ExperimentalBladePort fresh = Assert.IsType<ExperimentalBladePort>((await factory.OpenAsync(default)).Value);
        await fresh.SetPerformanceAsync(PerformancePreset.Silent, default);
        Assert.Equal(PerformancePreset.Silent, (await fresh.QueryPerformanceFanStateAsync(default)).Performance);
        Assert.False(factory.IsWritesPaused);
    }

    [Fact]
    public async Task ThermalRecovery_CallerCannotAcceptUnresolvedBatteryObligation()
    {
        ExperimentalBladeModel model = ExperimentalBladeModels.All.Single(item => item.ProductId == 0x028C);
        var native = new Native(1) { ProductId = model.ProductId };
        var io = new RecoveryIo { IgnoreThermalWrites = true, RejectBatteryGet = true };
        var factory = new ExperimentalBladeFactory(model, new Identity(model.ProductName), native,
            new Guard(), io, () => PowerSource.PluggedIn);
        ExperimentalBladePort port = Assert.IsType<ExperimentalBladePort>((await factory.OpenAsync(default)).Value);
        await Assert.ThrowsAsync<BladeWriteException>(() => port.SetPerformanceAsync(PerformancePreset.Silent, default));
        await port.DisposeAsync();
        int beforeRecovery = io.Commands.Count;
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() => factory.ResumeWritesAsync(
            observed => observed.Battery is not null, () => true, default));
        Assert.True(factory.IsWritesPaused);
        Assert.All(io.Commands.Skip(beforeRecovery), command => Assert.True(command.Id >= 0x80));
    }

    private sealed class RecoveryIo : IHidFeatureIo
    {
        private byte[] request = [];
        private byte mode;
        private byte battery = 0xD0;
        internal bool IgnoreThermalWrites { get; set; }
        internal bool IgnoreBatteryWrites { get; init; }
        internal bool RejectBatteryGet { get; set; }
        internal List<(byte Class, byte Id)> Commands { get; } = [];

        public Task<HidFeatureIoResult> ExecuteAsync(OpenedHidHandle handle, uint controlCode,
            byte[] buffer, CancellationToken callerCancellation, CancellationToken deadlineCancellation)
        {
            if (controlCode == 0x000B0191)
            {
                request = buffer.ToArray();
                Commands.Add((request[7], request[8]));
                if (request[7] == 0x0D && request[8] == 0x02 && !IgnoreThermalWrites)
                {
                    mode = request[11];
                }
                if (request[7] == 0x07 && request[8] == 0x12 && !IgnoreBatteryWrites)
                {
                    battery = request[9];
                }
            }
            else
            {
                request.CopyTo(buffer, 0);
                buffer[1] = 0x02;
                switch (request[7], request[8])
                {
                    case (0x0D, 0x82): buffer[11] = mode; break;
                    case (0x07, 0x8C): buffer[9] = 0x11; buffer[10] = 0x11; break;
                    case (0x07, 0x92):
                        buffer[9] = battery;
                        if (RejectBatteryGet)
                        {
                            buffer[1] = 0x05;
                        }
                        break;
                    case (0x0D, 0x02):
                    case (0x07, 0x12):
                        buffer[6] = 0;
                        Array.Clear(buffer, 9, 80);
                        break;
                    default: throw new InvalidOperationException($"Unexpected recovery command {request[7]:X2}/{request[8]:X2}");
                }
                buffer[89] = 0;
                for (int index = 3; index <= 88; index++)
                {
                    buffer[89] ^= buffer[index];
                }
            }
            return Task.FromResult(new HidFeatureIoResult(new(true, buffer.Length, 0), true, HidFeatureTermination.Completed));
        }
    }
}
