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
    public void AdmissionConstants_AreExactToValidatedSharedStack()
    {
        Assert.Equal(
            new Guid("e3be005d-d130-4910-88ff-09ae02f680e9"),
            WindowsRazerDisplayBrightnessHidTransport.ControlInterfaceGuid);
        Assert.Equal(
            new Guid("745a17a0-74d3-11d0-b6fe-00a0c90f57da"),
            WindowsRazerDisplayBrightnessHidTransport.HidClassGuid);
        Assert.Equal("RzCommon", WindowsRazerDisplayBrightnessHidTransport.ControlServiceName);
        Assert.Equal(0x88883020u, WindowsRazerDisplayBrightnessHidTransport.SubmitInputIoctl);
        Assert.Equal(10u, WindowsRazerDisplayBrightnessHidTransport.ConsumerControlInputType);
        Assert.Equal(
            [
                WindowsRazerDisplayBrightnessHidTransport.Rz09_0581Stack,
                WindowsRazerDisplayBrightnessHidTransport.Rz09_0528Stack,
            ],
            WindowsRazerDisplayBrightnessHidTransport.AdmittedStacks);
    }

    [Theory]
    [InlineData(
        "02E0",
        "RzDev_02e0")]
    [InlineData(
        "02C6",
        "RzDev_02c6")]
    public void ModelStacks_BindEveryIdentityToOneExactProduct(
        string productId,
        string serviceName)
    {
        RazerDisplayBrightnessDriverStack stack = Assert.Single(
            WindowsRazerDisplayBrightnessHidTransport.AdmittedStacks,
            candidate => candidate.ProductId == productId);

        Assert.Equal($@"RZCONTROL\VID_1532&PID_{productId}&MI_00\", stack.ControlInstanceIdPrefix);
        Assert.Equal(
            $@"RZVIRTUAL\VID_1532&PID_{productId}&MI_00&COL03\",
            stack.VirtualConsumerInstanceIdPrefix);
        Assert.Equal($@"USB\VID_1532&PID_{productId}&MI_02\", stack.PhysicalInterfaceAncestorPrefix);
        Assert.Equal($@"USB\VID_1532&PID_{productId}&MI_01\", stack.VirtualConsumerAncestorPrefix);
        Assert.Equal(serviceName, stack.VirtualConsumerServiceName);
        Assert.Equal(serviceName + ".sys", stack.DeviceDriverFileName);
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

    [Theory]
    [InlineData((int)DisplayBrightnessHidWriteStatus.Applied, DisplayBrightnessHidAvailability.Available)]
    [InlineData((int)DisplayBrightnessHidWriteStatus.Unavailable, DisplayBrightnessHidAvailability.Unavailable)]
    [InlineData((int)DisplayBrightnessHidWriteStatus.Failed, DisplayBrightnessHidAvailability.Unknown)]
    public void Availability_ProbesIdentityWithoutSubmittingUsage(
        int resolutionStatusValue,
        DisplayBrightnessHidAvailability expected)
    {
        DisplayBrightnessHidWriteStatus resolutionStatus =
            (DisplayBrightnessHidWriteStatus)resolutionStatusValue;
        int writes = 0;
        var transport = new WindowsRazerDisplayBrightnessHidTransport(
            () => (
                resolutionStatus == DisplayBrightnessHidWriteStatus.Applied
                    ? "admitted-razer-path"
                    : null,
                new DisplayBrightnessHidWriteResult(resolutionStatus)),
            (_, _) =>
            {
                writes++;
                return new DisplayBrightnessHidWriteResult(
                    DisplayBrightnessHidWriteStatus.Applied);
            });

        Assert.Equal(expected, transport.GetAvailability());
        Assert.Equal(0, writes);
    }

    [Fact]
    public void Availability_DoesNotSeedTheWritePathCache()
    {
        int resolutions = 0;
        var transport = new WindowsRazerDisplayBrightnessHidTransport(
            () =>
            {
                resolutions++;
                return (
                    "admitted-razer-path",
                    new DisplayBrightnessHidWriteResult(
                        DisplayBrightnessHidWriteStatus.Applied));
            },
            (_, _) => new DisplayBrightnessHidWriteResult(
                DisplayBrightnessHidWriteStatus.Applied));

        Assert.Equal(
            DisplayBrightnessHidAvailability.Available,
            transport.GetAvailability());
        _ = transport.SetUsage(DisplayBrightnessHidUsage.Increment);

        Assert.Equal(2, resolutions);
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
