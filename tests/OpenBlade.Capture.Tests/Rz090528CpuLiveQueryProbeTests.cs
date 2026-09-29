using OpenBlade.Capture;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class Rz090528CpuLiveQueryProbeTests
{
    private static readonly DeviceIdentity Exact = new("RZ09-0528", 0x1532, 0x02C6, "Unknown");
    private static readonly string[] Approved =
        [Rz090528CpuLiveQueryProbe.PciApprovalFlag, Rz090528CpuLiveQueryProbe.LoadedImageFlag];

    [Fact]
    public void BothDistinctExplicitFlagsAreRequiredBeforeAnyPlatformOrQueryCall()
    {
        string[][] refused = [[], [Approved[0]], [Approved[1]], [Approved[0], Approved[0]],
            [Approved[0], "--use-installed-backend"], [Approved[0], Approved[1], "--set-co=-20"]];
        foreach (var args in refused)
        {
            var fake = new FakeQuery();
            var output = new StringWriter();
            var error = new StringWriter();
            Assert.Equal(2, Run(args, fake, output, error));
            Assert.Empty(fake.Events);
            Assert.Empty(output.ToString());
            Assert.Contains("freshly loaded", error.ToString());
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("RZ09-0581")]
    public void MissingOrWrongFreshIdentityNeverInvokesQuery(string? model)
    {
        var fake = new FakeQuery { Identity = model is null ? null : Exact with { Model = model } };
        var output = new StringWriter();
        Assert.Equal(3, Run(Approved, fake, output));
        Assert.Equal(["usb"], fake.Events);
        Assert.Contains("Readiness=RefusedFreshExactUsbIdentity", output.ToString());
    }

    [Fact]
    public void OneQueryReceivesFreshCallbackAndOneUseApprovalAfterReviewableReadiness()
    {
        var output = new StringWriter();
        var fake = new FakeQuery
        {
            BeforeQuery = () =>
        {
            Assert.Contains("Readiness=FreshUsbPresent", output.ToString());
            Assert.Contains("LoadedKernelImageMachineAttestation=Unavailable", output.ToString());
            Assert.Contains("NativeQuery=StartingSingleAllowlisted", output.ToString());
        }
        };
        Assert.Equal(0, Run(Approved.Reverse().ToArray(), fake, output));
        Assert.Equal(["usb", "query", "usb", "disposed"], fake.Events);
        Assert.Contains("LiveEnabledPhysicalCoreMask=0x00000FFF", output.ToString());
        Assert.Contains("LivePmVersion=0x005D000C", output.ToString());
        Assert.Contains("LivePmTableLength=0xD68", output.ToString());
        Assert.Contains("LiveCurveOptimizerPhysicalCoreOffsets=0:-20,1:-19", output.ToString());
        Assert.Contains("LivePpt=Unknown", output.ToString());
        Assert.Contains("SavedConfiguration=NotQueried", output.ToString());
    }

    [Theory]
    [InlineData((int)CpuLiveQueryStatus.Unavailable)]
    [InlineData((int)CpuLiveQueryStatus.Busy)]
    [InlineData((int)CpuLiveQueryStatus.UnsupportedIdentity)]
    [InlineData((int)CpuLiveQueryStatus.UnsupportedVersion)]
    [InlineData((int)CpuLiveQueryStatus.InvalidData)]
    public void FailedQueryReportsNoMeasuredLiveVector(int statusCode)
    {
        var status = (CpuLiveQueryStatus)statusCode;
        var fake = new FakeQuery { Result = new(status, null) };
        var output = new StringWriter();
        Assert.Equal(3, Run(Approved, fake, output));
        Assert.Contains($"NativeQueryStatus={status}", output.ToString());
        Assert.DoesNotContain("LiveCurveOptimizerPhysicalCoreOffsets=", output.ToString());
        Assert.Contains("disposed", fake.Events);
    }

    [Fact]
    public void NativeFailureReportsOnlyTypedStageOperationAndErrorCode()
    {
        var fake = new FakeQuery
        {
            Result = new(CpuLiveQueryStatus.Unavailable, null,
                new(CpuQueryStage.PciCount, CpuQueryFailureCode.NativeIoctlFailed, 5,
                    CpuQueryOperation.PciEnumeration))
        };
        var output = new StringWriter();
        Assert.Equal(3, Run(Approved, fake, output));
        Assert.Contains("NativeQueryStage=PciCount", output.ToString());
        Assert.Contains("NativeQueryFailure=NativeIoctlFailed", output.ToString());
        Assert.Contains("NativeQueryOperation=PciEnumeration", output.ToString());
        Assert.Contains("NativeQueryErrorCode=5", output.ToString());
        Assert.DoesNotContain("LiveObservedAtUtc=", output.ToString());
        Assert.DoesNotContain("LiveCurveOptimizerPhysicalCoreOffsets=", output.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public void AbsentOrInvalidNativeErrorIsNotPresentedAsWin32Evidence(int? errorCode)
    {
        var fake = new FakeQuery
        {
            Result = new(CpuLiveQueryStatus.Unavailable, null,
                new(CpuQueryStage.DeviceOpen, CpuQueryFailureCode.Win32Error, errorCode))
        };
        var output = new StringWriter();
        Assert.Equal(3, Run(Approved, fake, output));
        Assert.Contains("NativeQueryStage=DeviceOpen", output.ToString());
        Assert.DoesNotContain("NativeQueryOperation=", output.ToString());
        Assert.DoesNotContain("NativeQueryErrorCode=", output.ToString());
    }

    [Fact]
    public void PreCancelledInvocationDoesNotReadPlatformOrInvokeQuery()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var fake = new FakeQuery();
        Assert.Equal(130, Run(Approved, fake, new StringWriter(), token: cancellation.Token));
        Assert.Empty(fake.Events);
    }

    [Fact]
    public void CancellationBetweenFreshIdentityAndQueryPreventsNativeEntry()
    {
        using var cancellation = new CancellationTokenSource();
        var fake = new FakeQuery { AfterUsb = cancellation.Cancel };
        Assert.Equal(130, Run(Approved, fake, new StringWriter(), token: cancellation.Token));
        Assert.Equal(["usb"], fake.Events);
    }

    [Fact]
    public void QueryCancellationDisposesFakeLeaseAndSuppressesIncompleteObservation()
    {
        using var cancellation = new CancellationTokenSource();
        var fake = new FakeQuery { BeforeQuery = cancellation.Cancel };
        var output = new StringWriter();
        Assert.Equal(130, Run(Approved, fake, output, token: cancellation.Token));
        Assert.Contains("disposed", fake.Events);
        Assert.Contains("NativeQueryStatus=Cancelled", output.ToString());
        Assert.DoesNotContain("LiveObservedAtUtc=", output.ToString());
    }

    [Fact]
    public void PassiveFailureDoesNotInvokeQueryOrExposeExceptionPaths()
    {
        var fake = new FakeQuery { AfterUsb = () => throw new IOException("private device path") };
        var error = new StringWriter();
        Assert.Equal(3, Run(Approved, fake, new StringWriter(), error));
        Assert.Equal(["usb"], fake.Events);
        Assert.DoesNotContain("private device path", error.ToString());
    }

    [Fact]
    public void PassiveUnavailableBackendRefusesBeforeAttendedQueryEntry()
    {
        var fake = new FakeQuery();
        var output = new StringWriter();
        Assert.Equal(3, Rz090528CpuLiveQueryProbe.Run(Approved, output, new StringWriter(),
            fake.ReadUsb, fake.Query, CancellationToken.None, identity =>
            {
                Assert.Equal(Exact, identity);
                return new(DeviceFeatureAccess.ObservedOnly, CpuTuningBackendAvailability.Unavailable);
            }));
        Assert.Equal(["usb"], fake.Events);
        Assert.Contains("PassiveInstalledBackend=Unavailable", output.ToString());
        Assert.DoesNotContain("NativeQuery=Starting", output.ToString());
    }

    [Theory]
    [InlineData("USB\\VID_1532&PID_02C6\\example", true)]
    [InlineData("usb\\vid_1532&pid_02c6\\example", true)]
    [InlineData("USB\\VID_1532&PID_02C6\\", false)]
    [InlineData("USB\\VID_1532&PID_02C6&MI_00\\example", false)]
    [InlineData("HID\\VID_1532&PID_02C6\\example", false)]
    [InlineData("USB\\VID_1532&PID_02E0\\example", false)]
    [InlineData("USB\\VID_1532&PID_02C60\\example", false)]
    public void PnpIdentityMatchesOnlyExactUsbDeviceNode(string instance, bool expected) =>
        Assert.Equal(expected, Rz090528CpuLiveQueryProbe.IsExactUsbInstance(instance));

    private static int Run(string[] args, FakeQuery fake, StringWriter output,
        StringWriter? error = null, CancellationToken token = default) =>
        Rz090528CpuLiveQueryProbe.Run(args, output, error ?? new StringWriter(), fake.ReadUsb, fake.Query, token);

    private sealed class FakeQuery
    {
        internal readonly List<string> Events = [];
        internal DeviceIdentity? Identity = Exact;
        internal Action? BeforeQuery;
        internal Action? AfterUsb;
        internal Rz090528CpuLiveQueryResult Result = new(CpuLiveQueryStatus.Observed,
            new(0xFFF, 0x005D000C, 0xD68, [new(0, -20), new(1, -19)]));
        internal DeviceIdentity? ReadUsb()
        {
            Events.Add("usb");
            AfterUsb?.Invoke();
            return Identity;
        }
        internal Rz090528CpuLiveQueryResult Query(CpuQueryAttendedApproval approval,
            Func<DeviceIdentity?> freshUsb, CancellationToken token)
        {
            Events.Add("query");
            try
            {
                BeforeQuery?.Invoke();
                Assert.True(approval.Consume());
                Assert.False(approval.Consume());
                token.ThrowIfCancellationRequested();
                Assert.Equal(Identity, freshUsb());
                return Result;
            }
            finally { Events.Add("disposed"); }
        }
    }
}
