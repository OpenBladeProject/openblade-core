using OpenBlade.Capture;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class Rz090528PadlessLoadedHyperBoostValidationCommandTests
{
    [Fact]
    public void RequiresExactConfirmationPlanAndExplicitLocalPaths()
    {
        string[] args = Arguments();
        Assert.True(Rz090528PadlessLoadedHyperBoostValidationCommand.ValidArguments(args));
        args[5] = new string('0', 64);
        Assert.False(Rz090528PadlessLoadedHyperBoostValidationCommand.ValidArguments(args));
        args = Arguments(); args[11] = "sensors.json";
        Assert.False(Rz090528PadlessLoadedHyperBoostValidationCommand.ValidArguments(args));
        args = Arguments(); args[11] = @"\\example\share\sensors.json";
        Assert.False(Rz090528PadlessLoadedHyperBoostValidationCommand.ValidArguments(args));
        args = Arguments(); args[11] = Path.Combine(Path.GetTempPath(), "sensors.csv");
        Assert.False(Rz090528PadlessLoadedHyperBoostValidationCommand.ValidArguments(args));
        args = Arguments(); args[9] = Path.Combine(Path.GetTempPath(), "Other.exe");
        Assert.False(Rz090528PadlessLoadedHyperBoostValidationCommand.ValidArguments(args));
    }

    [Theory]
    [InlineData("Valid", "REALiX, s.r.o.", true)]
    [InlineData("NotSigned", "REALiX, s.r.o.", false)]
    [InlineData("Valid", "REALiX imitation", false)]
    public void SignatureRequiresWindowsTrustAndExactPublisher(string status, string publisher, bool expected)
    {
        string json = System.Text.Json.JsonSerializer.Serialize(new { Status = status, Publisher = publisher });
        Assert.Equal(expected, VerifiedHwinfoProcess.TrustedSignature(json));
    }

    [Fact]
    public void SignatureHelperUsesOnlyInboxModulesAndPassesLiteralPathOutsideScript()
    {
        string path = Path.Combine(Path.GetTempPath(), "space ' quoted", "HWiNFO64.exe");
        var start = VerifiedHwinfoProcess.CreateSignatureStartInfo(path);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "Modules"), start.Environment["PSModulePath"]);
        Assert.Equal(path, start.Environment["OPENBLADE_HWINFO_SIGNATURE_PATH"]);
        Assert.DoesNotContain(path, string.Join(' ', start.ArgumentList));
        Assert.False(start.UseShellExecute);
        Assert.True(start.CreateNoWindow);
    }

    [Fact]
    public void AdapterPreservesNewHotPeakAndRejectsLateSourceOrNativeError()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var cpu = new HwinfoCpuLogObservation(now, now, 60, 0, "digest", null, false, 80, now, now);
        var gpu = new StockGpuTelemetry(now, now, true, 100, 65, new Dictionary<string, int>(), null);
        var workload = new StockLoadWorkloadObservation(true, 100, null);
        var result = Rz090528PadlessLoadedHyperBoostValidationCommand.ComposeReading(cpu, gpu, workload, "binding", now);
        Assert.Equal(80, result.CpuCelsius);
        Assert.Null(result.Failure);
        Assert.Equal(60, cpu.Celsius);
        Assert.NotNull(Rz090528PadlessLoadedHyperBoostValidationCommand.ComposeReading(cpu, gpu, workload, "binding", now.AddMilliseconds(4001)).Failure);
        Assert.Equal("NativeFailed", Rz090528PadlessLoadedHyperBoostValidationCommand.ComposeReading(cpu,
            gpu with { Failure = "NativeFailed" }, workload, "binding", now).Failure);
        Assert.NotNull(Rz090528PadlessLoadedHyperBoostValidationCommand.ComposeReading(cpu,
            gpu with { ExactDeviceConfirmed = false }, workload, "binding", now).Failure);
    }

    [Fact]
    public async Task BlockedReadTimesOutWithoutLatePublicationOrDisposalRace()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int published = 0, disposed = 0;
        var lane = new OwnedReadOnlyLane<string>(() => Interlocked.Increment(ref disposed), TimeSpan.FromMilliseconds(200));
        Task<string> read = lane.InvokeAsync(() =>
        {
            entered.SetResult();
            release.Wait();
            return "late";
        }, _ => Interlocked.Increment(ref published), CancellationToken.None).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Assert.ThrowsAsync<TimeoutException>(() => read.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.False(await lane.CloseAsync());
            Assert.False(lane.OperationQuiesced);
            Assert.Equal(0, Volatile.Read(ref disposed));
            await Assert.ThrowsAsync<InvalidOperationException>(() => lane.InvokeAsync(() => "retry", null, CancellationToken.None).AsTask());
        }
        finally { release.Set(); }
        Assert.True(await lane.CloseAsync());
        Assert.True(lane.OperationQuiesced);
        Assert.Equal(1, Volatile.Read(ref disposed));
        Assert.Equal(0, Volatile.Read(ref published));
    }

    [Fact]
    public async Task AcceptedReadPublishesOnceAndClosesBeforeDisposalClaim()
    {
        int published = 0, disposed = 0;
        var lane = new OwnedReadOnlyLane<string>(() => disposed++);
        Assert.Equal("sample", await lane.InvokeAsync(() => "sample", _ => published++, CancellationToken.None));
        Assert.Equal(1, published);
        Assert.Equal(0, disposed);
        Assert.True(await lane.CloseAsync());
        Assert.Equal(1, disposed);
    }

    [Fact]
    public async Task CanceledBlockedReadCannotPublishAndKeepsResourcesUntilCompletion()
    {
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int published = 0, disposed = 0;
        var lane = new OwnedReadOnlyLane<string>(() => Interlocked.Increment(ref disposed), TimeSpan.FromMilliseconds(200));
        Task<string> read = lane.InvokeAsync(() =>
        {
            entered.SetResult(); release.Wait(); return "late";
        }, _ => Interlocked.Increment(ref published), cancellation.Token).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
            Assert.False(await lane.CloseAsync());
            Assert.Equal(0, Volatile.Read(ref disposed));
            await Assert.ThrowsAsync<InvalidOperationException>(() => lane.InvokeAsync(() => "retry", null, CancellationToken.None).AsTask());
        }
        finally { release.Set(); }
        Assert.True(await lane.CloseAsync());
        Assert.Equal(1, Volatile.Read(ref disposed));
        Assert.Equal(0, Volatile.Read(ref published));
    }

    [Fact]
    public async Task OffsetEvidenceIsBoundedAndLateNativeCompletionCannotChangeFrozenReport()
    {
        var evidence = new GpuOffsetEvidenceLog();
        for (int index = 0; index < 513; index++) { evidence.Add(index); }
        Assert.True(evidence.Truncated);
        using var release = new ManualResetEventSlim();
        Task late = Task.Run(() => { release.Wait(); evidence.Add("late"); });
        object[] frozen = evidence.Close();
        release.Set();
        await late;
        Assert.Equal(512, frozen.Length);
        Assert.Equal(512, evidence.Close().Length);
        Assert.DoesNotContain("late", frozen);
    }

    [Fact]
    public void SixtySecondVariantsRequireTheirOwnConfirmationsPlansAndArgumentShapes()
    {
        var loaded = Rz090528PadlessLoadedHyperBoostValidationCommand.Loaded60;
        var idle = Rz090528PadlessLoadedHyperBoostValidationCommand.NoLoad60;
        string[] Loaded(Rz090528PadlessLoadedHyperBoostValidationCommand.Variant v) =>
        [
            "--confirm-target", v.TargetConfirmation, "--confirm-pad-disconnected", v.BoundaryConfirmation,
            "--plan-sha256", v.Profile.PlanSha256,
            "--workload-exe", Path.Combine(Path.GetTempPath(), StockSteelNomadWorkload.ExpectedExecutableName),
            "--hwinfo-exe", Path.Combine(Path.GetTempPath(), "HWiNFO64.exe"),
            "--hwinfo-log", Path.Combine(Path.GetTempPath(), "sensors.json"),
        ];
        string[] NoLoad(Rz090528PadlessLoadedHyperBoostValidationCommand.Variant v) =>
        [
            "--confirm-target", v.TargetConfirmation, "--confirm-pad-disconnected", v.BoundaryConfirmation,
            "--plan-sha256", v.Profile.PlanSha256,
            "--hwinfo-exe", Path.Combine(Path.GetTempPath(), "HWiNFO64.exe"),
            "--hwinfo-log", Path.Combine(Path.GetTempPath(), "sensors.json"),
        ];
        Assert.True(Rz090528PadlessLoadedHyperBoostValidationCommand.ValidArguments(Loaded(loaded), loaded));
        Assert.True(Rz090528PadlessLoadedHyperBoostValidationCommand.ValidArguments(NoLoad(idle), idle));
        Assert.False(Rz090528PadlessLoadedHyperBoostValidationCommand.ValidArguments(NoLoad(loaded), loaded));
        Assert.False(Rz090528PadlessLoadedHyperBoostValidationCommand.ValidArguments(Loaded(idle), idle));
        Assert.False(Rz090528PadlessLoadedHyperBoostValidationCommand.ValidArguments(Arguments(), loaded));
        Assert.False(Rz090528PadlessLoadedHyperBoostValidationCommand.ValidArguments(Loaded(loaded)));
        Assert.True(Rz090528PadlessLoadedHyperBoostValidationCommand.ValidArguments(Arguments()));
        Assert.False(idle.Profile.RequiresWorkload);
        Assert.Equal(60000, loaded.Profile.ObservationMilliseconds);
    }

    [Fact]
    public void OnlyExactReviewedHwinfoBuildsAreAccepted()
    {
        Assert.Equal(new[] { "8.48-5990", "8.52-6060" }, VerifiedHwinfoProcess.ReviewedVersions);
        Assert.DoesNotContain("8.52", VerifiedHwinfoProcess.ReviewedVersions);
        Assert.DoesNotContain("8.53-6100", VerifiedHwinfoProcess.ReviewedVersions);
    }
    [Fact]
    public void NoLoadTreatsAnyBenchmarkProcessAsRunningWorkload()
    {
        Assert.False(Rz090528PadlessLoadedHyperBoostValidationCommand.IdleWorkloadObservation(false, () => true).RunningExact);
        Assert.True(Rz090528PadlessLoadedHyperBoostValidationCommand.IdleWorkloadObservation(false, () => false).RunningExact);
        int calls = 0;
        Assert.False(Rz090528PadlessLoadedHyperBoostValidationCommand.IdleWorkloadObservation(true, () => { calls++; return false; }).RunningExact);
        Assert.Equal(0, calls);
    }

    private static string[] Arguments() =>
    [
        "--confirm-target", Rz090528PadlessLoadedHyperBoostValidationCommand.Confirmation,
        "--confirm-pad-disconnected", Rz090528PadlessLoadedHyperBoostValidationCommand.AttendedConfirmation,
        "--plan-sha256", Rz090528PadlessLoadedValidation.PlanSha256,
        "--workload-exe", Path.Combine(Path.GetTempPath(), StockSteelNomadWorkload.ExpectedExecutableName),
        "--hwinfo-exe", Path.Combine(Path.GetTempPath(), "HWiNFO64.exe"),
        "--hwinfo-log", Path.Combine(Path.GetTempPath(), "sensors.json"),
    ];
}
