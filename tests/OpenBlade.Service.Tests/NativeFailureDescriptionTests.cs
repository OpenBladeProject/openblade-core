using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class NativeFailureDescriptionTests
{
    [Fact]
    public void QueryWithoutNativeDetailsRetainsKnownQueryAndFailure()
    {
        var failure = new NativeDeviceReadException(
            HidSessionFailureKind.NativeIoFailure, null, DeviceStateQuery.ThermalChannelOne);

        string message = NativeFailureDescription.Describe(failure);

        Assert.Contains("query=ThermalChannelOne", message);
        Assert.Contains("failure=NativeIoFailure", message);
        Assert.Contains("phase=unavailable", message);
    }

    [Fact]
    public void SharedAdmissionWithoutQueryDoesNotImplyBatteryFailure()
    {
        var failure = new NativeDeviceReadException(
            HidSessionFailureKind.Exhausted,
            new(HidSessionFailurePhase.SharedGateAdmission,
                HidSessionFailureKind.Exhausted, null, null),
            stateQuery: null);

        string message = NativeFailureDescription.Describe(failure);

        Assert.Contains("phase=SharedGateAdmission", message);
        Assert.Contains("failure=Exhausted", message);
        Assert.DoesNotContain("battery", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WriteWrapperPreservesNumericDiagnosticWithoutInnerExceptionText()
    {
        var failure = new BladeWriteException(WriteSubmissionState.Indeterminate,
            "Fan control could not be confirmed.", new IOException("private device path and raw payload"))
        {
            Diagnostic = new(HidSessionFailurePhase.SetCompletion,
                HidSessionFailureKind.NativeIoFailure, 31, 0),
            CommandClass = 0x0D,
            CommandId = 0x02,
        };
        var wrapper = new BladeWriteException(WriteSubmissionState.Indeterminate,
            "Fan operation stopped.", failure);

        string message = NativeFailureDescription.Describe(wrapper);

        Assert.Contains("command=0D/02", message);
        Assert.Contains("nativeError=31", message);
        Assert.Contains("phase=SetCompletion", message);
        Assert.DoesNotContain("private", message);
        Assert.DoesNotContain("payload", message);
    }
}
