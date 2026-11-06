using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text.Json;
using OpenBlade.Contracts;

namespace OpenBlade.Capture.Tests;

public sealed class So6120MailboxObservationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefaultAndPlanNeverEnterLivePath(bool explicitPlan)
    {
        using var output = new StringWriter();
        int result = await So6120MailboxObservationCommand.RunAsync(explicitPlan ? ["--plan"] : [],
            new StringReader(""), output, (_, _, _, _) => throw new InvalidOperationException("Live path invoked"));
        Assert.Equal(0, result);
        Assert.Contains("only reads port 301", output.ToString());
    }

    [Fact]
    public void StableObservationSendsOnlyFixedIndexSelectionsAndDataReads()
    {
        var native = new Native();
        var result = Observe(native);
        Assert.True(result.Success);
        Assert.Equal(new byte[] { 0x93, 0x90, 0x91, 0x92, 0x94, 0xA0, 0x93,
            0x93, 0x90, 0x91, 0x92, 0x94, 0xA0, 0x93 }, native.Selections);
        Assert.Equal(14, native.Reads);
        Assert.Equal(new[] { "reserve", "admit", "pin", "admit", "open", "admit", "admit", "close", "unpin", "release" },
            native.Lifecycle);
    }

    [Fact]
    public void FailedReadStopsTheCurrentScan()
    {
        var native = new Native { FailRead = 3 };
        var result = Observe(native);
        Assert.False(result.Success);
        Assert.Equal(new byte[] { 0x93, 0x90, 0x91 }, native.Selections);
        Assert.Equal(3, native.Reads);
        Assert.Contains("read failed", result.Failure);
        Assert.Equal(So6120NativeCloseState.Closed, result.CloseEvidence?.State);
    }

    [Theory]
    [InlineData(true, 7)]
    [InlineData(false, 14)]
    public void AChangedBracketOrChangedEnvelopeRefusesMatchingEvidence(bool commandChanged, int expectedReads)
    {
        var native = new Native();
        if (commandChanged) { native.ReadBytes[6] = 0; }
        else { native.ReadBytes[12]++; }
        var result = Observe(native);
        Assert.False(result.Success);
        Assert.False(result.ScansMatched);
        Assert.Equal(expectedReads, native.Reads);
        Assert.Equal(So6120NativeCloseState.Closed, result.CloseEvidence?.State);
    }

    [Fact]
    public void UnconfirmedCloseRetainsDriverPinAndReservation()
    {
        var native = new Native { CloseSucceeded = false };
        var result = Observe(native);
        Assert.False(result.Success);
        Assert.True(result.ScansMatched);
        Assert.Equal(So6120NativeCloseState.Failed, result.CloseEvidence?.State);
        Assert.Equal(5, result.CloseEvidence?.NativeError);
        Assert.NotNull(result.RetainedPin);
        Assert.NotNull(result.RetainedReservation);
        Assert.Equal("close", native.Lifecycle[^1]);
    }

    [Fact]
    public void ScopeRecheckAfterOpeningCanRefuseAllPortOperations()
    {
        var native = new Native { RefuseAdmission = 3 };
        var result = Observe(native);
        Assert.False(result.Success);
        Assert.True(result.OpenAttempted);
        Assert.Empty(native.Selections);
        Assert.Equal(0, native.Reads);
        Assert.Equal(So6120NativeCloseState.Closed, result.CloseEvidence?.State);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void PriorEvidenceRequiresFenceNativeCloseAndParentExit(bool fenced, bool closed, bool observedExit)
    {
        using var directory = new PriorRun();
        directory.Write(fenced, closed, observedExit);
        if (fenced && closed && observedExit)
        {
            using var evidence = So6120MailboxObservationEvidence.Open(directory.Attempt);
            Assert.Equal(directory.Identity, evidence.Identity);
            Assert.Equal(64, evidence.JournalSha256.Length);
            Assert.Equal(64, evidence.AttendedLogSha256.Length);
            Assert.Equal(64, evidence.ParentReceiptSha256.Length);
            return;
        }
        Assert.Throws<InvalidDataException>(() => So6120MailboxObservationEvidence.Open(directory.Attempt));
    }

    private static So6120MailboxObservationResult Observe(Native native) =>
        So6120MailboxObservation.Observe(native, TimeProvider.System, native.Admit, handle => new(handle, native),
            _ => { }, CancellationToken.None);

    private sealed class Native : ISo6120WindowsDutyNative, ISo6120InpOutNative
    {
        public byte[] ReadBytes { get; } = [0xA0, 0, 0xB9, 1, 0, 64, 0xA0, 0xA0, 0, 0xB9, 1, 0, 64, 0xA0];
        public List<byte> Selections { get; } = [];
        public List<string> Lifecycle { get; } = [];
        public int Reads { get; private set; }
        public int FailRead { get; init; }
        public bool CloseSucceeded { get; init; } = true;
        public int RefuseAdmission { get; init; }
        private int admissions;

        public So6120SupervisedMetadata Admit()
        {
            Lifecycle.Add("admit");
            if (++admissions == RefuseAdmission) { throw new InvalidOperationException("Synthetic scope change"); }
            return new(new(Guid.NewGuid(), 1, TimeProvider.System, 0), 0, Architecture.X64,
                new(null, null), null, [], new(true, 1, 1, 1),
                new("", "", null, ServiceType.KernelDriver, ServiceControllerStatus.Running, null, null, null),
                [], ServiceControllerStatus.Stopped, []);
        }

        public IDisposable ReserveRun()
        { Lifecycle.Add("reserve"); return new Release(() => Lifecycle.Add("release")); }
        public IDisposable PinDriverFile(So6120DriverMetadata driver)
        { Lifecycle.Add("pin"); return new Release(() => Lifecycle.Add("unpin")); }
        public So6120OwnedInpOutHandle OpenDevice()
        {
            Lifecycle.Add("open");
            return new(1, _ =>
            {
                Lifecycle.Add("close");
                return new(CloseSucceeded, CloseSucceeded ? 0 : 5);
            });
        }

        public So6120MetadataRead Capture(Guid runId, TimeProvider clock) => throw new NotSupportedException();
        public void VerifyUserOwnersStopped() => throw new NotSupportedException();
        public IDisposable CreateAttemptLatch(Guid runId, DateTimeOffset startedUtc) => throw new NotSupportedException();

        public So6120InpOutNativeResult Invoke(SafeHandle handle, uint controlCode, byte[] input, byte[]? output)
        {
            if (controlCode == 0x9C402008 && input.Length == 3 && input[0] == 0 && input[1] == 3 && output is null)
            {
                Selections.Add(input[2]);
                return new(true, 10, 0);
            }
            if (controlCode != 0x9C402004 || !input.SequenceEqual(new byte[] { 1, 3 }) || output is not { Length: 1 })
            { throw new InvalidOperationException("Unexpected native packet"); }
            if (++Reads == FailRead) { return new(false, 0, 5); }
            output[0] = ReadBytes[Reads - 1];
            return new(true, 1, 0);
        }

        private sealed class Release(Action release) : IDisposable
        {
            public void Dispose() => release();
        }
    }

    private sealed class PriorRun : IDisposable
    {
        private readonly string parent = Path.Combine(Path.GetTempPath(), "ob-observation-" + Guid.NewGuid().ToString("N"));
        public string Attempt => Path.Combine(parent, "attempt");
        public So6120JournalIdentity Identity { get; } = new(
            new(Guid.NewGuid(), "test device", "test firmware", Guid.NewGuid()),
            PerformancePreset.Balanced, null, null, new('A', 64), So6120SilentPwmCommand.FirmwareHash,
            Guid.NewGuid(), new('C', 64), "test boot");

        public void Write(bool fenced, bool closed, bool observedExit)
        {
            using (var journal = So6120DirectDutyJournal.Create(Attempt, Identity))
            {
                if (fenced) { journal.RecordFence(Identity.Owner, So6120DutyFailure.CommandUnresolved); }
            }
            File.WriteAllLines(Path.Combine(Attempt, "attended-run.jsonl"),
            [
                JsonSerializer.Serialize(new { phase = "created", run = Identity.Owner.OperationId, FirmwareHash = Identity.FirmwareEvidenceSha256 }),
                JsonSerializer.Serialize(new
                {
                    phase = "closed",
                    acquisitionStarted = true,
                    cleanupConfirmed = false,
                    acquisitionFailureClose = (object?)null,
                    releaseFailures = Array.Empty<string>(),
                    close = new { State = closed ? So6120NativeCloseState.Closed : So6120NativeCloseState.Failed, NativeError = 0 },
                }),
            ]);
            File.WriteAllText(Path.Combine(parent, "parent-result.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                childPid = 1,
                observedChildExit = observedExit,
                exitCode = 2,
                attemptRoot = Attempt,
            }));
        }

        public void Dispose()
        {
            if (Directory.Exists(parent)) { Directory.Delete(parent, recursive: true); }
        }
    }
}
