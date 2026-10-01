using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class WindowsSavedCpuTuningBackendTests
{
    private static readonly DeviceIdentity Exact = new("RZ09-0528", 0x1532, 0x02C6, "2.02");
    private static readonly CpuSavedTuningCommand Curve = new(CpuSavedTuningCommandKind.CurveOptimizer, -1);

    [Theory]
    [InlineData("identity", "saved command device identity", 0, 0)]
    [InlineData("encoding", "saved command semantic encoding", 0, 0)]
    [InlineData("runtime", "fresh runtime admission", 1, 0)]
    [InlineData("host", "fresh host identity", 1, 1)]
    public async Task FirstDeniedStageStopsBeforeLaterChecks(string failure, string stage,
        int expectedAdmissions, int expectedIdentityReads)
    {
        var diagnostic = new CpuSavedAdmissionDiagnostic();
        var source = new Source { Identity = failure == "host" ? Exact with { ProductId = 0 } : Exact };
        var invoker = new Invoker();
        int admissions = 0;
        var backend = new WindowsSavedCpuTuningBackend(source, (_, _) =>
        {
            admissions++;
            return failure != "runtime";
        }, invoker, diagnostic);
        Assert.False(await backend.CheckWriteAdmissionAsync(
            failure == "identity" ? Exact with { ProductId = 0 } : Exact,
            failure == "encoding" ? new(CpuSavedTuningCommandKind.CurveOptimizer, 1) : Curve, default));
        Assert.Equal(stage, diagnostic.FirstFailure);
        Assert.Equal(expectedAdmissions, admissions);
        Assert.Equal(expectedIdentityReads, source.IdentityReads);
        Assert.Equal(0, invoker.Calls);
        Assert.False(diagnostic.Check(false, "later failure"));
        Assert.True(diagnostic.Check(true, "later success"));
        Assert.Equal(stage, diagnostic.FirstFailure);
    }

    [Fact]
    public async Task SpecificRuntimeFailureSurvivesBackendFallbackAndStopsBeforeIdentity()
    {
        var diagnostic = new CpuSavedAdmissionDiagnostic();
        var source = new Source();
        var invoker = new Invoker();
        var backend = new WindowsSavedCpuTuningBackend(source,
            (_, _) => diagnostic.Check(false, "runtime controlling session"), invoker, diagnostic);
        Assert.False(await backend.CheckWriteAdmissionAsync(Exact, Curve, default));
        Assert.Equal("runtime controlling session", diagnostic.FirstFailure);
        Assert.Equal(0, source.IdentityReads);
        Assert.Equal(0, invoker.Calls);
    }

    [Theory]
    [InlineData("2.02")]
    [InlineData("2.03")]
    [InlineData("Unknown")]
    public async Task SourceAdmittedCommandsStillUseFreshIdentityAndTheInjectedInvoker(string bios)
    {
        var source = new Source { Identity = Exact with { BiosVersion = bios } };
        var invoker = new Invoker();
        var diagnostic = new CpuSavedAdmissionDiagnostic();
        var backend = new WindowsSavedCpuTuningBackend(source,
            (_, command) => WindowsSavedCpuTuningBackend.IsProductionCommandAdmitted(command), invoker, diagnostic);
        foreach (var command in new[] { Curve, new(CpuSavedTuningCommandKind.CurveOptimizer, 0),
            new(CpuSavedTuningCommandKind.CurveOptimizer, -30), new(CpuSavedTuningCommandKind.PowerLimit, 45),
            new(CpuSavedTuningCommandKind.PowerLimit, 95), new(CpuSavedTuningCommandKind.ResetPowerLimit, 0) })
        {
            Assert.True(await backend.CheckWriteAdmissionAsync(Exact, command, CancellationToken.None));
            Assert.Equal(CpuSavedTuningSubmission.Acknowledged,
                await backend.SendAsync(Exact, command, CancellationToken.None));
        }
        Assert.Equal(6, invoker.Calls);
        Assert.Equal(18, source.IdentityReads);
        Assert.Null(diagnostic.FirstFailure);
    }

    [Fact]
    public async Task InvalidFamilyBoundsNeverReachEvenTheInjectedInvoker()
    {
        var source = new Source();
        var invoker = new Invoker();
        var backend = new WindowsSavedCpuTuningBackend(source, (_, _) => true, invoker);
        foreach (var command in new[] { new CpuSavedTuningCommand(CpuSavedTuningCommandKind.CurveOptimizer, 1),
            new(CpuSavedTuningCommandKind.CurveOptimizer, -31), new(CpuSavedTuningCommandKind.PowerLimit, 0),
            new(CpuSavedTuningCommandKind.PowerLimit, 44), new(CpuSavedTuningCommandKind.PowerLimit, 96),
            new(CpuSavedTuningCommandKind.ResetPowerLimit, 1), new((CpuSavedTuningCommandKind)99, 0) })
        {
            Assert.False(WindowsSavedCpuTuningBackend.IsProductionCommandAdmitted(command));
            Assert.Equal(CpuSavedTuningSubmission.NotSubmitted,
                await backend.SendAsync(Exact, command, default));
        }
        Assert.Equal(0, invoker.Calls);
        Assert.Equal(0, source.IdentityReads);
    }

    [Fact]
    public async Task ChangedHostIdentityFailsBeforeInvoker()
    {
        var source = new Source { Identity = Exact with { Model = "RZ09-9999" } };
        var invoker = new Invoker();
        var backend = new WindowsSavedCpuTuningBackend(source, (_, _) => true, invoker);
        Assert.Equal(CpuSavedTuningSubmission.NotSubmitted,
            await backend.SendAsync(Exact, Curve, CancellationToken.None));
        Assert.Equal(0, invoker.Calls);
    }

    [Fact]
    public async Task InvokerReceivesFreshSafetyAndIdentityCheckRatherThanCachedAdmission()
    {
        var source = new Source();
        bool allowed = true;
        var invoker = new Invoker { BeforeCheck = () => allowed = false };
        var backend = new WindowsSavedCpuTuningBackend(source, (_, _) => allowed, invoker);
        Assert.Equal(CpuSavedTuningSubmission.NotSubmitted,
            await backend.SendAsync(Exact, Curve, CancellationToken.None));
        Assert.Equal(1, invoker.Calls);
        allowed = true;
        invoker.BeforeCheck = () => source.Identity = Exact with { ProductId = 0x02E0 };
        Assert.Equal(CpuSavedTuningSubmission.NotSubmitted,
            await backend.SendAsync(Exact, Curve, CancellationToken.None));
    }

    [Fact]
    public void AdmissionThatFinishesAfterCancellationCannotAuthorizeNativeInvocation()
    {
        using var cancellation = new CancellationTokenSource();
        int nativeInvocations = 0;
        Assert.ThrowsAny<OperationCanceledException>(() =>
        {
            if (WindowsSavedCpuTuningBackend.CheckFreshAdmission(() =>
            {
                cancellation.Cancel();
                return true;
            }, cancellation.Token))
            {
                nativeInvocations++;
            }
        });
        Assert.Equal(0, nativeInvocations);
    }

    [Fact]
    public async Task PreCancelledRequestNeverInvokesFirmware()
    {
        var invoker = new Invoker();
        var backend = new WindowsSavedCpuTuningBackend(new Source(), (_, _) => true, invoker);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            backend.SendAsync(Exact, Curve, new CancellationToken(true)));
        Assert.Equal(0, invoker.Calls);
    }

    private sealed class Invoker : ICpuSavedFirmwareCommandInvoker
    {
        internal int Calls;
        internal Action? BeforeCheck;
        public Task<CpuSavedTuningSubmission> SendAsync(CpuSavedTuningCommand command,
            Func<bool> freshAdmission, CancellationToken token)
        {
            Calls++;
            BeforeCheck?.Invoke();
            return Task.FromResult(freshAdmission()
                ? CpuSavedTuningSubmission.Acknowledged : CpuSavedTuningSubmission.NotSubmitted);
        }
    }

    private sealed class Source : ICpuTuningObservationSource
    {
        internal DeviceIdentity Identity = Exact;
        internal int IdentityReads;
        public DeviceIdentity? ReadIdentity() { IdentityReads++; return Identity; }
        public SavedCpuFirmwareReadResult ReadSaved(DeviceIdentity identity) =>
            new(SavedCpuFirmwareReadStatus.Unavailable, null, null, 0, 0);
        public CpuTuningBackendStatus InspectInstalled(DeviceIdentity identity, bool enabled) =>
            new(DeviceFeatureAccess.ObservedOnly, CpuTuningBackendAvailability.Unavailable);
    }
}
