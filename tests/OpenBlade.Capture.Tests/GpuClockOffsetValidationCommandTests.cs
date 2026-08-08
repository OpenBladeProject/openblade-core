using System.Text;
using OpenBlade.Capture;

namespace OpenBlade.Capture.Tests;

public sealed class GpuClockOffsetValidationCommandTests
{
    [Fact]
    public void Run_AppliesAdjacentValuesReadsBackAndRestores()
    {
        var controller = new FakeController();
        var output = new StringWriter();

        int result = GpuClockOffsetValidationCommand.Run(controller, output);

        Assert.Equal(0, result);
        Assert.Equal(
            [
                new GpuClockOffsetValidationSettings(1, 1),
                new GpuClockOffsetValidationSettings(0, 0),
            ],
            controller.Writes);
        Assert.Contains("Requested=Core=1;Memory=1", output.ToString());
        Assert.Contains("ReadBack=True", output.ToString());
        Assert.Contains("Restored=True", output.ToString());
        Assert.Contains("Success=True", output.ToString());
    }

    [Fact]
    public void Run_TargetFailureStillRestoresAndFailsClosed()
    {
        var controller = new FakeController
        {
            ThrowAfterFirstWrite = true,
        };
        var output = new StringWriter();

        int result = GpuClockOffsetValidationCommand.Run(controller, output);

        Assert.Equal(2, result);
        Assert.Equal(new GpuClockOffsetValidationSettings(0, 0), controller.Current);
        Assert.True(controller.Writes.Count >= 2);
        Assert.Contains("Restored=True", output.ToString());
        Assert.Contains("Success=False", output.ToString());
    }

    [Fact]
    public void Run_RestorationFailureIsReported()
    {
        var controller = new FakeController
        {
            RejectRestore = true,
        };
        var output = new StringWriter();

        int result = GpuClockOffsetValidationCommand.Run(controller, output);

        Assert.Equal(2, result);
        Assert.Equal(new GpuClockOffsetValidationSettings(1, 1), controller.Current);
        Assert.Contains("Restored=False", output.ToString());
        Assert.Contains("restoration failed", output.ToString());
        Assert.Contains("Success=False", output.ToString());
    }

    [Fact]
    public void Run_WrongGpuDoesNotWrite()
    {
        var controller = new FakeController
        {
            DeviceName = "NVIDIA Different GPU",
        };
        var output = new StringWriter();

        int result = GpuClockOffsetValidationCommand.Run(controller, output);

        Assert.Equal(2, result);
        Assert.Empty(controller.Writes);
        Assert.Contains("exact NVIDIA laptop GPU", output.ToString());
        Assert.Contains("Success=False", output.ToString());
    }

    [Fact]
    public void Run_RechecksIsolationImmediatelyBeforeMutation()
    {
        var controller = new FakeController();
        var output = new StringWriter();

        int result = GpuClockOffsetValidationCommand.Run(
            controller,
            output,
            mutationAllowed: static () => false);

        Assert.Equal(2, result);
        Assert.Empty(controller.Writes);
        Assert.Contains("isolation checks changed", output.ToString());
        Assert.Contains("Success=False", output.ToString());
    }

    [Fact]
    public void Run_Rz090581PlanUsesFiveMegahertzStepAndRestores()
    {
        var controller = new FakeController();
        var output = new StringWriter();

        int result = GpuClockOffsetValidationCommand.Run(
            controller,
            output,
            GpuClockOffsetValidationPlan.Rz090581Bios400);

        Assert.Equal(0, result);
        Assert.Equal(
            [
                new GpuClockOffsetValidationSettings(5, 5),
                new GpuClockOffsetValidationSettings(0, 0),
            ],
            controller.Writes);
        Assert.Contains(
            "Target=RZ09-0581 1532:02E0 BIOS 4.00",
            output.ToString());
        Assert.Contains("Requested=Core=5;Memory=5", output.ToString());
        Assert.Contains("Restored=True", output.ToString());
        Assert.Contains("Success=True", output.ToString());
    }

    [Fact]
    public void Run_Rz090581PlanRejectsNonzeroBaselineWithoutWriting()
    {
        var controller = new FakeController
        {
            Current = new(5, 0),
        };
        var output = new StringWriter();

        int result = GpuClockOffsetValidationCommand.Run(
            controller,
            output,
            GpuClockOffsetValidationPlan.Rz090581Bios400);

        Assert.Equal(2, result);
        Assert.Empty(controller.Writes);
        Assert.Contains("required safe baseline", output.ToString());
        Assert.Contains("Success=False", output.ToString());
    }

    [Fact]
    public void Controller_PartialMemoryFailureRestoresBothPriorOffsets()
    {
        var api = new FakeApi
        {
            CoreCurrent = 10,
            MemoryCurrent = 40,
            FailFirstMemoryWrite = true,
        };
        var controller = new NvidiaNvmlGpuClockOffsetValidationController(api);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => controller.Set(new GpuClockOffsetValidationSettings(11, 21)));

        Assert.Contains("prior offsets were restored", exception.Message);
        Assert.Equal(10, api.CoreCurrent);
        Assert.Equal(40, api.MemoryCurrent);
        Assert.Equal(
            [
                (NvmlValidationClockType.Graphics, 11),
                (NvmlValidationClockType.Memory, 42),
                (NvmlValidationClockType.Graphics, 10),
                (NvmlValidationClockType.Memory, 40),
            ],
            api.Writes);
        Assert.Equal(1, api.InitializeCalls);
        Assert.Equal(1, api.ShutdownCalls);
    }

    private sealed class FakeController : IGpuClockOffsetValidationController
    {
        private int setCalls;

        internal string DeviceName { get; set; } =
            "NVIDIA GeForce RTX 5090 Laptop GPU";

        internal GpuClockOffsetValidationSettings Current { get; set; } =
            new(0, 0);

        internal bool ThrowAfterFirstWrite { get; set; }

        internal bool RejectRestore { get; set; }

        internal List<GpuClockOffsetValidationSettings> Writes { get; } = [];

        public GpuClockOffsetCapabilities Query() => new(
            DeviceName,
            new GpuClockOffsetRange(-1000, 1000, Current.CoreMegahertz),
            new GpuClockOffsetRange(-1000, 3000, Current.MemoryMegahertz));

        public void Set(GpuClockOffsetValidationSettings settings)
        {
            Writes.Add(settings);
            setCalls++;
            if (RejectRestore && setCalls > 1)
            {
                throw new InvalidOperationException("scripted restoration failed");
            }

            Current = settings;
            if (ThrowAfterFirstWrite && setCalls == 1)
            {
                throw new InvalidOperationException("scripted target failure");
            }
        }
    }

    private sealed class FakeApi : INvidiaNvmlGpuClockOffsetValidationApi
    {
        internal bool FailFirstMemoryWrite { get; set; }

        internal int CoreCurrent { get; set; }

        internal int MemoryCurrent { get; set; }

        internal int InitializeCalls { get; private set; }

        internal int ShutdownCalls { get; private set; }

        internal List<(NvmlValidationClockType Type, int Value)> Writes { get; } = [];

        public int Initialize()
        {
            InitializeCalls++;
            return 0;
        }

        public int Shutdown()
        {
            ShutdownCalls++;
            return 0;
        }

        public int GetDeviceCount(out uint count)
        {
            count = 1;
            return 0;
        }

        public int GetDeviceHandleByIndex(uint index, out nint device)
        {
            device = new nint(1);
            return 0;
        }

        public int GetDeviceName(nint device, StringBuilder name, uint length)
        {
            _ = name.Append("NVIDIA GeForce RTX 5090 Laptop GPU");
            return 0;
        }

        public int GetClockOffsets(
            nint device,
            ref NvmlValidationClockOffset info)
        {
            info.MinimumMegahertz = info.Type == NvmlValidationClockType.Graphics
                ? -1000
                : -2000;
            info.MaximumMegahertz = info.Type == NvmlValidationClockType.Graphics
                ? 1000
                : 6000;
            info.CurrentMegahertz = info.Type == NvmlValidationClockType.Graphics
                ? CoreCurrent
                : MemoryCurrent;
            return 0;
        }

        public int SetClockOffsets(
            nint device,
            ref NvmlValidationClockOffset info)
        {
            Writes.Add((info.Type, info.CurrentMegahertz));
            if (info.Type == NvmlValidationClockType.Memory
                && FailFirstMemoryWrite)
            {
                FailFirstMemoryWrite = false;
                return 3;
            }

            if (info.Type == NvmlValidationClockType.Graphics)
            {
                CoreCurrent = info.CurrentMegahertz;
            }
            else
            {
                MemoryCurrent = info.CurrentMegahertz;
            }
            return 0;
        }
    }
}
