using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class Rz090528CpuControllerPresenceTests
{
    [Theory]
    [InlineData("Razer")]
    [InlineData("Ryzen")]
    [InlineData("RGS")]
    public void GetterActivityRechecksBothPoliciesAndRevokesPermanently(string controller)
    {
        string[] processes = []; int full = 0, checks = 0, processReads = 0;
        var reasons = new List<string>();
        var current = Rz090528CpuControllerPresence.CaptureGetterContinuity(() => { processReads++; return processes; },
            () => { full++; return []; }, _ => { checks++; return []; }, reasons.Add);
        Assert.NotNull(current);
        for (int i = 0; i < 164; i++) { Assert.True(current()); }
        Assert.Equal(1, full); Assert.Equal(164, checks); Assert.Equal(330, processReads); Assert.Empty(reasons);
        processes = [controller]; Assert.False(current());
        processes = []; Assert.False(current());
        Assert.Equal(["controller process present"], reasons);
        Assert.Equal(1, full); Assert.Equal(164, checks); Assert.Equal(331, processReads);
    }

    [Fact]
    public void GetterRejectsControllerServiceAppearingWithoutAProcess()
    {
        var service = new CpuServiceInventoryEntry("RGS", "RGS", 0x10, 1, "");
        var reasons = new List<string>();
        var current = Rz090528CpuControllerPresence.CaptureGetterContinuity(() => [],
            () => [service], _ => [service], reasons.Add);
        Assert.NotNull(current); Assert.True(current());
        service = service with { State = 4 }; Assert.False(current());
        Assert.Equal(["controller service active"], reasons);
    }

    [Theory]
    [InlineData("Getter service disappeared.", true, "Getter service disappeared.")]
    [InlineData("Getter template changed.", true, "Getter template changed.")]
    [InlineData("Getter service identity changed.", true, "Getter service identity changed.")]
    [InlineData("Getter service runtime type changed.", true, "Getter service runtime type changed.")]
    [InlineData("Getter service display name changed.", true, "Getter service display name changed.")]
    [InlineData("Getter service configured/runtime incompatible.", true, "Getter service configured/runtime incompatible.")]
    [InlineData("Getter controller configuration changed.", true, "Getter controller configuration changed.")]
    [InlineData("Getter service inventory changed.", true, "Getter service inventory changed.")]
    [InlineData("private path and service identity", true, "service inventory unavailable")]
    [InlineData("Getter service disappeared.", false, "service inventory unavailable")]
    public void GetterActivityReportsOnlyAllowlistedIoFailures(string message, bool io, string expected)
    {
        int activityReads = 0;
        var reasons = new List<string>();
        var current = Rz090528CpuControllerPresence.CaptureGetterContinuity(() => [], () => [],
            _ => { activityReads++; throw io ? new IOException(message) : new InvalidOperationException(message); }, reasons.Add)!;
        Assert.False(current()); Assert.False(current());
        Assert.Equal([expected], reasons); Assert.Equal(1, activityReads);
    }

    [Fact]
    public void GetterReportsProcessFailureBeforeServiceActivityAndSanitizesCaptureFailure()
    {
        var reasons = new List<string>(); bool fail = false; int activityReads = 0;
        var current = Rz090528CpuControllerPresence.CaptureGetterContinuity(
            () => fail ? throw new IOException("private process identity") : [], () => [],
            _ => { activityReads++; return []; }, reasons.Add)!;
        fail = true; Assert.False(current());
        Assert.Equal(["process inventory unavailable"], reasons); Assert.Equal(0, activityReads);
        reasons.Clear();
        Assert.Null(Rz090528CpuControllerPresence.CaptureGetterContinuity(() => [],
            () => throw new IOException("private inventory path"), _ => [], reasons.Add));
        Assert.Equal(["service inventory unavailable"], reasons);
    }

    [Fact]
    public void GetterReportsInvalidProcessAndServiceInventoriesWithoutRawValues()
    {
        var reasons = new List<string>();
        Assert.Null(Rz090528CpuControllerPresence.CaptureGetterContinuity(() => [""], () => [], _ => [], reasons.Add));
        Assert.Equal(["process inventory invalid"], reasons);
        reasons.Clear();
        Assert.Null(Rz090528CpuControllerPresence.CaptureGetterContinuity(() => [],
            () => [new("private service", "private display", 0, 1, "private path")], _ => [], reasons.Add));
        Assert.Equal(["service inventory unavailable"], reasons);
    }

    [Fact]
    public void ThrowingDiagnosticCannotPreventStickyRevocationOrAlterCaptureRejection()
    {
        string[] processes = []; int reports = 0, activityReads = 0;
        void Reject(string _) { reports++; throw new InvalidOperationException(); }
        var current = Rz090528CpuControllerPresence.CaptureGetterContinuity(() => processes, () => [],
            _ => { activityReads++; return []; }, Reject)!;
        Assert.True(current()); processes = ["RGS"]; Assert.False(current());
        processes = []; Assert.False(current()); Assert.Equal(1, reports); Assert.Equal(1, activityReads);
        processes = ["Razer"];
        Assert.Null(Rz090528CpuControllerPresence.CaptureGetterContinuity(() => processes, () => [], _ => [], Reject));
        Assert.Equal(2, reports);
    }

    [Fact]
    public void StoppedControllerAndUnrelatedRunningServicePermitBoundedAbsence()
    {
        int reads = 0;
        Assert.True(Rz090528CpuControllerPresence.ControllersAbsent(() => { reads++; return ["ordinary"]; },
            () => [new("Razer", "Razer controller", 0x10, 1, ""), new("Other", "Other", 0xE0, 4, "")]));
        Assert.Equal(2, reads);
    }
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void EveryNonStoppedControllerStateDeniesIncludingDisplayAlias(int state) => Assert.False(
        Rz090528CpuControllerPresence.ControllersAbsent(() => [], () => [new("Other", "Ryzen agent", 0xD0, (uint)state, "")]));
    [Fact]
    public void ControllerAppearingDuringServiceScanDenies()
    {
        int reads = 0;
        Assert.False(Rz090528CpuControllerPresence.ControllersAbsent(() => ++reads == 1 ? [] : ["Razer"], () => []));
    }
    [Fact]
    public void FailedPartialOrUnknownInventoryNeverBecomesAbsence()
    {
        Assert.False(Rz090528CpuControllerPresence.ControllersAbsent(() => throw new IOException(), () => []));
        Assert.False(Rz090528CpuControllerPresence.ControllersAbsent(() => [], () => throw new IOException()));
        Assert.False(Rz090528CpuControllerPresence.ControllersAbsent(() => [], () => [new("Other", "Other", 0, 1, "")]));
        Assert.False(Rz090528CpuControllerPresence.ControllersAbsent(() => [], () => [new("Other", "Other", 0x10, 0, "")]));
        Assert.False(Rz090528CpuControllerPresence.ControllersAbsent(() => [""], () => []));
    }
}
