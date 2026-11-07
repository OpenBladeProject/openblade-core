using System;
using System.ComponentModel;
using System.IO;
using LibreHardwareMonitor;
using LibreHardwareMonitor.Hardware;
using LibreHardwareMonitor.Hardware.Cpu;
using LibreHardwareMonitor.PawnIo;
using Xunit;

namespace OpenBlade.LibreHardwareMonitor.Tests;

public sealed class ThermalContractTests
{
    [Theory]
    [InlineData(0u)]
    [InlineData(4u)]
    [InlineData(7u)]
    [InlineData(9u)]
    public void IoctlSuccessWithWrongLengthCannotBecomeAZeroTemperature(uint returned)
        => Assert.Throws<InvalidDataException>(() => PawnIo.ValidateExecutionResult(true, returned, 8, 0));

    [Fact]
    public void FailedIoctlRetainsNativeError()
    {
        var error = Assert.Throws<Win32Exception>(() => PawnIo.ValidateExecutionResult(false, 8, 8, 5));
        Assert.Equal(5, error.NativeErrorCode);
    }

    [Fact]
    public void ExactIoctlSuccessPasses() => PawnIo.ValidateExecutionResult(true, 8, 8, 0);

    [Theory]
    [InlineData(0f)]
    [InlineData(59f)]
    [InlineData(126f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void InvalidTargetCannotBeUsedAsTemperature(float target)
        => Assert.Throws<InvalidDataException>(() => IntelCpu.DecodeTemperature(0, target));

    [Fact]
    public void IntelPackageDecodeDoesNotRequireReservedBit31()
        => Assert.Equal(60f, IntelCpu.DecodeTemperature(40u << 16, 100f));

    [Fact]
    public void ImpossibleIntelDeltaIsUnavailable()
        => Assert.Throws<InvalidDataException>(() => IntelCpu.DecodeTemperature(127u << 16, 100f));

    [Theory]
    [InlineData("AMD Ryzen 9 7945HX", 70f, 70f, false)]
    [InlineData("AMD Ryzen 7 1700X", 70f, 50f, true)]
    [InlineData("AMD Ryzen 7 2700X", 70f, 60f, true)]
    [InlineData("AMD Ryzen Threadripper 1950X", 70f, 43f, true)]
    public void AmdKeepsControlOffsetDistinctFromDieTemperature(string name, float control, float die, bool split)
    {
        var value = Amd17Cpu.DecodeTemperature(560u << 21, name);
        Assert.Equal(control, value.Tctl, 3);
        Assert.Equal(die, value.Tdie, 3);
        Assert.Equal(split, value.Split);
    }

    [Theory]
    [InlineData(0x80000u)]
    [InlineData(0x30000u)]
    public void AmdRangeAdjustmentIsNotAnIntelTarget(uint flag)
    {
        var value = Amd17Cpu.DecodeTemperature((800u << 21) | flag, "AMD Ryzen 9 7945HX");
        Assert.Equal(51f, value.Tdie, 3);
    }

    [Fact]
    public void ImpossibleAmdTemperatureIsUnavailable()
        => Assert.Throws<InvalidDataException>(() => Amd17Cpu.DecodeTemperature(uint.MaxValue, "AMD Ryzen"));

    [Fact]
    public void CachedValueDoesNotSurviveAnUpdateThatDoesNotAssign()
    {
        var hardware = new FakeHardware();
        hardware.Sensor.Value = 42f;
        CpuTemperatureComputer.UpdateHardware([hardware]);
        Assert.Null(hardware.Sensor.Value);
    }

    [Fact]
    public void LaterFailureInvalidatesEarlierSuccessfulAssignment()
    {
        var first = new FakeHardware();
        first.UpdateAction = () => first.Sensor.Value = 42f;
        var second = new FakeHardware { UpdateAction = () => throw new IOException("synthetic failure") };
        Assert.Throws<IOException>(() => CpuTemperatureComputer.UpdateHardware([first, second]));
        Assert.Null(first.Sensor.Value);
        Assert.Null(second.Sensor.Value);
    }

    [Fact]
    public void OnlyReviewedCpuModulesAreEmbedded()
    {
        var names = typeof(CpuTemperatureComputer).Assembly.GetManifestResourceNames();
        Assert.Equal(2, names.Length);
        Assert.Contains("LibreHardwareMonitor.Resources.PawnIo.IntelMSR.bin", names);
        Assert.Contains("LibreHardwareMonitor.Resources.PawnIo.AMDFamily17.bin", names);
    }

    private sealed class FakeHardware : Hardware
    {
        public FakeHardware() : base("CPU", new Identifier("testcpu"), new NoSettings())
        {
            Sensor = new Sensor("CPU Package", 0, SensorType.Temperature, this, new NoSettings());
            ActivateSensor(Sensor);
        }
        public Sensor Sensor { get; }
        public Action UpdateAction { get; set; } = () => { };
        public override HardwareType HardwareType => HardwareType.Cpu;
        public override void Update() => UpdateAction();
    }

    private sealed class NoSettings : ISettings
    {
        public bool Contains(string name) => false;
        public void SetValue(string name, string value) { }
        public string GetValue(string name, string value) => value;
        public void Remove(string name) { }
    }
}
