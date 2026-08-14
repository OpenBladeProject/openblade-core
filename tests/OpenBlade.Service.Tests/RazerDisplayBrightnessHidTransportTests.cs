using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class RazerDisplayBrightnessHidTransportTests
{
    [Theory]
    [InlineData(DisplayBrightnessHidUsage.Released, 0x00, 0x00)]
    [InlineData(DisplayBrightnessHidUsage.Increment, 0x6F, 0x00)]
    [InlineData(DisplayBrightnessHidUsage.Decrement, 0x70, 0x00)]
    public void ControlBuffer_IsExactTypedThirtyTwoByteContract(
        DisplayBrightnessHidUsage usage,
        byte usageLow,
        byte usageHigh)
    {
        byte[] buffer = WindowsRazerDisplayBrightnessHidTransport.CreateControlBuffer(usage);

        Assert.Equal(WindowsRazerDisplayBrightnessHidTransport.ControlBufferLength, buffer.Length);
        Assert.Equal([0x0A, 0x00, 0x00, 0x00], buffer[4..8]);
        Assert.Equal(usageLow, buffer[WindowsRazerDisplayBrightnessHidTransport.UsageOffset]);
        Assert.Equal(usageHigh, buffer[WindowsRazerDisplayBrightnessHidTransport.UsageOffset + 1]);
        Assert.All(buffer[..4], value => Assert.Equal(0, value));
        Assert.All(buffer[10..], value => Assert.Equal(0, value));
    }

    [Fact]
    public void AdmissionConstants_AreExactToValidatedRz09_0581Stack()
    {
        Assert.Equal(
            new Guid("e3be005d-d130-4910-88ff-09ae02f680e9"),
            WindowsRazerDisplayBrightnessHidTransport.ControlInterfaceGuid);
        Assert.Equal(
            @"RZCONTROL\VID_1532&PID_02E0&MI_00\",
            WindowsRazerDisplayBrightnessHidTransport.ControlInstanceIdPrefix);
        Assert.Equal(
            @"USB\VID_1532&PID_02E0&MI_02\",
            WindowsRazerDisplayBrightnessHidTransport.PhysicalInterfaceAncestorPrefix);
        Assert.Equal("RzCommon", WindowsRazerDisplayBrightnessHidTransport.ControlServiceName);
        Assert.Equal(0x88883020u, WindowsRazerDisplayBrightnessHidTransport.SubmitInputIoctl);
        Assert.Equal(10u, WindowsRazerDisplayBrightnessHidTransport.ConsumerControlInputType);
    }

    [Fact]
    public void ProductionComposition_InjectsControllerIntoUniqueLongestRuntimeConstructor()
    {
        System.Reflection.ConstructorInfo[] constructors = typeof(ServiceControlRuntime)
            .GetConstructors();
        int longest = constructors.Max(constructor => constructor.GetParameters().Length);
        System.Reflection.ConstructorInfo constructor = Assert.Single(
            constructors,
            candidate => candidate.GetParameters().Length == longest);

        Assert.Equal(12, longest);
        Assert.Equal(
            typeof(IDisplayBrightnessHidController),
            constructor.GetParameters()[^1].ParameterType);

        string program = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "OpenBlade.Service",
            "Program.cs"));
        Assert.Contains(
            "AddSingleton<IDisplayBrightnessHidController, DisplayBrightnessHidController>()",
            program,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Transport_CachesResolvedRazerControlPath()
    {
        int resolutionCount = 0;
        var writtenPaths = new List<string>();
        var transport = new WindowsRazerDisplayBrightnessHidTransport(
            () =>
            {
                resolutionCount++;
                return (
                    "cached-razer-path",
                    new DisplayBrightnessHidWriteResult(
                        DisplayBrightnessHidWriteStatus.Applied));
            },
            (path, _) =>
            {
                writtenPaths.Add(path);
                return new DisplayBrightnessHidWriteResult(
                    DisplayBrightnessHidWriteStatus.Applied);
            });

        DisplayBrightnessHidWriteResult first = transport.SetUsage(
            DisplayBrightnessHidUsage.Decrement);
        DisplayBrightnessHidWriteResult second = transport.SetUsage(
            DisplayBrightnessHidUsage.Released);

        Assert.Equal(DisplayBrightnessHidWriteStatus.Applied, first.Status);
        Assert.Equal(DisplayBrightnessHidWriteStatus.Applied, second.Status);
        Assert.Equal(1, resolutionCount);
        Assert.Equal(["cached-razer-path", "cached-razer-path"], writtenPaths);
    }

    [Fact]
    public void Transport_RediscoversRazerPathAfterConfirmedInvalidation()
    {
        int resolutionCount = 0;
        int firstPathWrites = 0;
        var transport = new WindowsRazerDisplayBrightnessHidTransport(
            () =>
            {
                resolutionCount++;
                return (
                    $"razer-path-{resolutionCount}",
                    new DisplayBrightnessHidWriteResult(
                        DisplayBrightnessHidWriteStatus.Applied));
            },
            (path, _) =>
            {
                if (path == "razer-path-1" && ++firstPathWrites == 2)
                {
                    return new DisplayBrightnessHidWriteResult(
                        DisplayBrightnessHidWriteStatus.Unavailable);
                }

                return new DisplayBrightnessHidWriteResult(
                    DisplayBrightnessHidWriteStatus.Applied);
            });

        _ = transport.SetUsage(DisplayBrightnessHidUsage.Decrement);
        DisplayBrightnessHidWriteResult result = transport.SetUsage(
            DisplayBrightnessHidUsage.Released);

        Assert.Equal(DisplayBrightnessHidWriteStatus.Applied, result.Status);
        Assert.Equal(2, resolutionCount);
    }

    [Fact]
    public void Transport_DoesNotRetryIndeterminateRazerSubmission()
    {
        int resolutionCount = 0;
        int writeCount = 0;
        var transport = new WindowsRazerDisplayBrightnessHidTransport(
            () =>
            {
                resolutionCount++;
                return (
                    "cached-razer-path",
                    new DisplayBrightnessHidWriteResult(
                        DisplayBrightnessHidWriteStatus.Applied));
            },
            (_, _) =>
            {
                writeCount++;
                return new DisplayBrightnessHidWriteResult(
                    writeCount == 1
                        ? DisplayBrightnessHidWriteStatus.Applied
                        : DisplayBrightnessHidWriteStatus.Failed,
                    writeCount == 1 ? null : "indeterminate submission");
            });

        _ = transport.SetUsage(DisplayBrightnessHidUsage.Decrement);
        DisplayBrightnessHidWriteResult result = transport.SetUsage(
            DisplayBrightnessHidUsage.Decrement);

        Assert.Equal(DisplayBrightnessHidWriteStatus.Failed, result.Status);
        Assert.Equal("indeterminate submission", result.Error);
        Assert.Equal(1, resolutionCount);
        Assert.Equal(2, writeCount);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "OpenBlade.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the OpenBlade repository root.");
    }
}
