using OpenBlade.Tray;
using DeviceEntry = OpenBlade.SessionAgent.WindowsReactiveKeyboardSource.RawInputDeviceListEntry;

namespace OpenBlade.SessionAgent.Tests;

public sealed class Blade18KeyboardTopologyTests
{
    [Theory]
    [InlineData(0x19)]
    [InlineData(0x3C)]
    public void LaptopFnWithForeignKeyboardPassesThroughWholePress(uint scanCode)
    {
        var topology = new Topology { ForeignKeyboard = true };
        var state = new Blade18DefaultFnInputState();
        int canceled = 0;

        Assert.False(Process(state, topology, scanCode, true, false, () => canceled++, out var down));
        Assert.Null(down);
        topology.ForeignKeyboard = false;
        Assert.False(Process(state, topology, scanCode, true, true, () => canceled++, out var repeat));
        Assert.Null(repeat);
        Assert.False(Process(state, topology, scanCode, false, false, () => canceled++, out var up));
        Assert.Null(up);
        Assert.Equal(1, canceled);
    }

    [Theory]
    [InlineData(0x19)]
    [InlineData(0x3C)]
    public void TopologyLossClearsConsumedLatchAndPendingAction(uint scanCode)
    {
        var topology = new Topology();
        var state = new Blade18DefaultFnInputState();
        var pending = new Blade18FnHookCorrelation();
        int canceled = 0;
        void Cancel()
        {
            canceled++;
            pending.Clear();
        }

        Assert.True(Process(state, topology, scanCode, true, false, Cancel, out var down));
        Assert.True(pending.Record(scanCode, false, true, 1, down));
        topology.ForeignKeyboard = true;
        Assert.False(Process(state, topology, scanCode, true, true, Cancel, out var repeat));
        Assert.Null(repeat);
        Assert.False(pending.TryConsume(scanCode, false, true, 1, out _));
        // The newly connected keyboard must also get its matching release.
        Assert.False(Process(state, topology, scanCode, false, false, Cancel, out var up));
        Assert.Null(up);
        Assert.Equal(1, canceled);

        topology.ForeignKeyboard = false;
        Assert.True(Process(state, topology, scanCode, true, false, Cancel, out var nextDown));
        Assert.Equal(FunctionRowInputPhase.Down, nextDown?.Phase);
    }

    [Theory]
    [InlineData(0x19, false)]
    [InlineData(0x3C, true)]
    public void SingleLaptopPreservesActionsRepeatsAndConsumptionAfterFnRelease(uint scanCode, bool repeats)
    {
        var topology = new Topology();
        var state = new Blade18DefaultFnInputState();
        Assert.True(Process(state, topology, scanCode, true, false, () => Assert.Fail(), out var down));
        Assert.Equal(FunctionRowInputPhase.Down, down?.Phase);
        Assert.True(Process(state, topology, scanCode, true, true, () => Assert.Fail(), out var repeat));
        Assert.Equal(repeats ? FunctionRowInputPhase.Repeat : (FunctionRowInputPhase?)null, repeat?.Phase);
        Assert.True(WindowsReactiveKeyboardSource.ProcessBlade18KeyboardInput(
            state, scanCode, false, false, false, functionHeld: false, admitted: false,
            topology.Read, () => Assert.Fail(), out var up));
        Assert.Equal(FunctionRowInputPhase.Up, up?.Phase);
        Assert.Equal(3, topology.Queries);
    }

    [Theory]
    [InlineData("remote")]
    [InlineData("unknown-device")]
    [InlineData("zero-device-handle")]
    [InlineData("empty")]
    [InlineData("too-many")]
    [InlineData("count-error")]
    [InlineData("list-error")]
    [InlineData("empty-list")]
    [InlineData("list-overflow")]
    [InlineData("no-keyboard")]
    public void UncertainTopologyPassesThroughAndCancelsHeldAction(string failure)
    {
        var topology = new Topology();
        var state = new Blade18DefaultFnInputState();
        int canceled = 0;
        Assert.True(Process(state, topology, 0x3C, true, false, () => canceled++, out _));
        topology.Failure = failure;
        Assert.False(Process(state, topology, 0x3C, true, true, () => canceled++, out var repeat));
        Assert.Null(repeat);
        Assert.False(Process(state, topology, 0x3C, false, false, () => canceled++, out var up));
        Assert.Null(up);
        Assert.Equal(1, canceled);
    }

    [Theory]
    [InlineData(0x20, true)]
    [InlineData(0x19, false)]
    public void OrdinaryUnmodifiedKeysDoNotEnumerateDevices(uint scanCode, bool functionHeld)
    {
        var state = new Blade18DefaultFnInputState();
        Assert.False(WindowsReactiveKeyboardSource.ProcessBlade18KeyboardInput(
            state, scanCode, false, true, false, functionHeld, admitted: true,
            () => throw new InvalidOperationException("Unexpected topology query."),
            () => Assert.Fail(), out var input));
        Assert.Null(input);
    }

    [Fact]
    public void EnumerationUsesReturnedEntriesAndIgnoresOtherRawDeviceTypes()
    {
        var topology = new Topology { Capacity = 5 };
        Assert.True(topology.Read());
        Assert.Equal(1, topology.Resolutions);
    }

    private static bool Process(
        Blade18DefaultFnInputState state,
        Topology topology,
        uint scanCode,
        bool keyDown,
        bool repeated,
        Action cancel,
        out Blade18DefaultFnInputEvent? input) =>
        WindowsReactiveKeyboardSource.ProcessBlade18KeyboardInput(
            state, scanCode, false, keyDown, repeated, functionHeld: true, admitted: true,
            topology.Read, cancel, out input);

    private sealed class Topology
    {
        public bool ForeignKeyboard { get; set; }
        public string? Failure { get; set; }
        public uint Capacity { get; set; } = 3;
        public int Queries { get; private set; }
        public int Resolutions { get; private set; }

        public bool Read()
        {
            Queries++;
            return WindowsReactiveKeyboardSource.HasExclusiveBlade18Keyboard(
                Failure != "remote", ReadDevices, Resolve);
        }

        private RazerBladeInputDeviceKind? Resolve(nint device)
        {
            Resolutions++;
            return Failure == "unknown-device" ? null
                : device == 1 ? RazerBladeInputDeviceKind.Rz09_0484
                : RazerBladeInputDeviceKind.Unsupported;
        }

        private uint ReadDevices(DeviceEntry[]? devices, ref uint count, uint size)
        {
            Assert.Equal((uint)(IntPtr.Size == 8 ? 16 : 8), size);
            if (devices is null)
            {
                count = Failure switch { "empty" => 0, "too-many" => 129, _ => Capacity };
                return Failure == "count-error" ? uint.MaxValue : 0;
            }
            if (Failure == "list-error")
            {
                return uint.MaxValue;
            }
            if (Failure == "empty-list")
            {
                return 0;
            }
            if (Failure == "list-overflow")
            {
                return count + 1;
            }
            devices[0] = new()
            {
                Device = Failure == "zero-device-handle" ? 0 : 1,
                Type = Failure == "no-keyboard" ? 2u : 1u,
            };
            devices[1] = new() { Device = 2, Type = ForeignKeyboard ? 1u : 0u };
            devices[2] = new() { Device = 3, Type = 2 };
            // Deliberately leave count at its allocated capacity, as USER32 may.
            return 3;
        }
    }
}
