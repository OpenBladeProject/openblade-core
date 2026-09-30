using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using OpenBlade.Capture;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class CpuPmSupervisorCompositionTests
{
    private static readonly Guid Run = new("11111111-1111-1111-1111-111111111111");
    private static readonly string Source = new('a', 40), Hash = new('A', 64);
    private static CpuPmReviewedLaunchBootstrap Preset => new(Source, Hash);
    private static CpuPmAuthorityPeer Outer => new(10, 123, @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", Hash);
    private static CpuPmAuthorityPeer Supervisor => new(11, 456, Path.Combine(Preset.PayloadPath, "OpenBlade.PmSupervisor.exe"), Hash);
    private static byte[] Record() => JsonSerializer.SerializeToUtf8Bytes(new
    {
        version = 1,
        run = Run.ToString("N"),
        source = Source,
        manifest = Hash,
        presetHash = Hash,
        outer = new { pid = Outer.ProcessId, created = Outer.CreationFileTimeUtc, image = Outer.ExecutablePath, sha256 = Outer.ExecutableSha256 },
        supervisor = new { pid = Supervisor.ProcessId, created = Supervisor.CreationFileTimeUtc, image = Supervisor.ExecutablePath, sha256 = Supervisor.ExecutableSha256 }
    });
    [Fact]
    public void BootstrapRequiresIndependentPresetAndActualReturnedSupervisorBinding()
    {
        var record = CpuPmOuterLaunchSchema.ReadRecord(Record());
        Assert.True(CpuPmOuterLaunchSchema.Matches(record, Run, Preset, Hash, Supervisor, Outer.ProcessId));
        Assert.False(CpuPmOuterLaunchSchema.Matches(record, Guid.NewGuid(), Preset, Hash, Supervisor, Outer.ProcessId));
        Assert.False(CpuPmOuterLaunchSchema.Matches(record, Run, Preset, new('B', 64), Supervisor, Outer.ProcessId));
        Assert.False(CpuPmOuterLaunchSchema.Matches(record, Run, Preset, Hash, Supervisor with { ProcessId = 12 }, Outer.ProcessId));
        Assert.False(CpuPmOuterLaunchSchema.Matches(record, Run, Preset, Hash, Supervisor with { CreationFileTimeUtc = 457 }, Outer.ProcessId));
        Assert.False(CpuPmOuterLaunchSchema.Matches(record, Run, Preset, Hash, Supervisor, 99));
        Assert.False(CpuPmOuterLaunchSchema.Matches(record, Run, Preset, Hash, Supervisor with { ExecutableSha256 = new('B', 64) }, Outer.ProcessId));
    }
    [Theory]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("missing")]
    [InlineData("version")]
    [InlineData("guid")]
    [InlineData("same-pid")]
    [InlineData("relative-image")]
    [InlineData("huge")]
    public void MalformedBootstrapCannotCreateScope(string kind)
    {
        string text = Encoding.UTF8.GetString(Record());
        text = kind switch
        {
            "duplicate" => text.Insert(1, "\"version\":1,"),
            "unknown" => text.Insert(1, "\"approved\":true,"),
            "missing" => text.Replace("\"version\":1,", "", StringComparison.Ordinal),
            "version" => text.Replace("\"version\":1", "\"version\":2", StringComparison.Ordinal),
            "guid" => text.Replace(Run.ToString("N"), "not-a-run", StringComparison.Ordinal),
            "same-pid" => text.Replace("\"pid\":11", "\"pid\":10", StringComparison.Ordinal),
            "relative-image" => text.Replace("C:", ".", StringComparison.Ordinal),
            _ => new(' ', 16385)
        };
        Assert.ThrowsAny<Exception>(() => CpuPmOuterLaunchSchema.ReadRecord(Encoding.UTF8.GetBytes(text)));
    }
    [Fact]
    public void PresetStrictShapeCannotClaimApprovalOrUseSelfDefinedPath()
    {
        var good = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, source = Source, manifest = Hash });
        Assert.Equal(Preset, CpuPmOuterLaunchSchema.ReadPreset(good));
        Assert.ThrowsAny<Exception>(() => CpuPmOuterLaunchSchema.ReadPreset(JsonSerializer.SerializeToUtf8Bytes(new { version = 1, source = Source, manifest = Hash, approved = true })));
    }
    [Fact]
    public void LiveChildCannotTriggerDrainOrCleanupAndTerminalRequiresDrainFirst()
    {
        var child = new Child(); var events = new List<string>(); int delays = 0;
        Assert.True(CpuPmSupervisorCompletion.Finish(child, () => false, () => events.Add("validate"), () => events.Add("revoke"),
            () => events.Add("drain"), () => { events.Add("cleanup"); return CpuPmDriverCleanup.Complete; },
            () => { Assert.DoesNotContain("cleanup", events); Assert.DoesNotContain("drain", events); if (++delays == 3) { child.State = CpuPmChildState.Terminal; } }));
        Assert.Equal(3, delays); Assert.True(events.IndexOf("revoke") < events.IndexOf("drain")); Assert.True(events.IndexOf("drain") < events.IndexOf("cleanup"));
    }
    [Theory]
    [InlineData("unknown")]
    [InlineData("identity")]
    [InlineData("deadline")]
    public void UnknownTerminalOrOwnershipNeverCleansUp(string failure)
    {
        var child = new Child { State = failure == "unknown" ? CpuPmChildState.Unknown : CpuPmChildState.Terminal, Drift = failure == "identity" };
        int mutations = 0;
        Assert.False(CpuPmSupervisorCompletion.Finish(child, () => failure == "deadline", () => { }, () => { }, () => throw new InvalidOperationException(),
            () => { mutations++; return CpuPmDriverCleanup.Complete; }, () => throw new InvalidOperationException()));
        Assert.Equal(0, mutations);
    }
    [Fact]
    public async Task PendingListenerDrainRetainsTheSameInvocationAndNeverRacesCleanup()
    {
        var child = new Child { State = CpuPmChildState.Terminal }; using var entered = new ManualResetEventSlim(); using var finish = new ManualResetEventSlim();
        int cleanups = 0, ownerThread = 0, cleanupThread = 0;
        var pending = Task.Run(() =>
        {
            ownerThread = Environment.CurrentManagedThreadId; return CpuPmSupervisorCompletion.Finish(child, () => false, () => { }, () => { },
            () => { entered.Set(); finish.Wait(); }, () => { cleanupThread = Environment.CurrentManagedThreadId; Interlocked.Increment(ref cleanups); return CpuPmDriverCleanup.Complete; }, () => { });
        });
        try { Assert.True(entered.Wait(TimeSpan.FromSeconds(5))); Assert.False(pending.IsCompleted); Assert.Equal(0, Volatile.Read(ref cleanups)); }
        finally { finish.Set(); }
        Assert.True(await pending.WaitAsync(TimeSpan.FromSeconds(5))); Assert.Equal(ownerThread, cleanupThread); Assert.Equal(1, cleanups);
    }
    [Fact]
    public void UncertaintyDuringDrainPreventsAllCleanupAndPendingStopIsBounded()
    {
        var child = new Child { State = CpuPmChildState.Terminal }; bool uncertain = false; int cleanup = 0;
        Assert.False(CpuPmSupervisorCompletion.Finish(child, () => uncertain, () => { }, () => { }, () => uncertain = true,
            () => { cleanup++; return CpuPmDriverCleanup.Complete; }, () => { })); Assert.Equal(0, cleanup);
        Assert.False(CpuPmSupervisorCompletion.Finish(child, () => false, () => { }, () => { }, () => { },
            () => { cleanup++; return CpuPmDriverCleanup.PendingStopped; }, () => { })); Assert.Equal(64, cleanup);
    }
    [Fact]
    public void FailedDrainNeverRunsCleanup()
    {
        var child = new Child { State = CpuPmChildState.Terminal }; int cleanups = 0;
        Assert.Throws<IOException>(() => CpuPmSupervisorCompletion.Finish(child, () => false, () => { }, () => { },
            () => throw new IOException(), () => { cleanups++; return CpuPmDriverCleanup.Complete; }, () => { }));
        Assert.Equal(0, cleanups);
    }
    [Fact]
    public void ReturnedCancelledBatchIsStoredExactlyOnceAfterRevocation()
    {
        using var cancel = new CancellationTokenSource(); int calls = 0; bool revoked = false;
        var sink = new Sink(() => Assert.True(revoked));
        var result = CpuPmAttendedCapture.CaptureAndStore(() => { calls++; cancel.Cancel(); return new(false, [], [], 0, 0, null); },
            () => revoked = true, new(Run, Guid.NewGuid(), Source, Hash, 1000), sink, cancel.Token);
        Assert.Equal(1, calls); Assert.Equal(CpuPmCaptureWriteStatus.StoredIncomplete, result.Status); Assert.Single(sink.Files);
        Assert.Throws<OperationCanceledException>(() => CpuPmAttendedCapture.CaptureAndStore(() => throw new InvalidOperationException(), () => { },
            new(Run, Guid.NewGuid(), Source, Hash, 1000), sink, cancel.Token));
    }
    [Fact]
    public void ThrowingAuthorityCleanupPreservesAllReturnedBytesAsIncompleteWithoutRecapture()
    {
        var samples = Enumerable.Range(0, 3).Select(index =>
        {
            byte[] reply = new byte[0xD54]; BinaryPrimitives.WriteUInt32LittleEndian(reply, 0xD50);
            for (int word = 0; word < 6; word++) { BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(4 + word * 4), 0x3F800000); }
            Assert.True(CpuPmRawSnapshot.TryCreate(0x005D000B, new(true, reply), DateTimeOffset.UnixEpoch.AddSeconds(index), index * 1000L, out var snapshot));
            return new CpuPmCompletedSample(snapshot!, 0x12345678);
        }).ToArray();
        var batch = new CpuPmObservationBatch(true, samples, [], 0xFF55, 0xFF55, null); int captures = 0, revokes = 0;
        var sink = new Sink(() => Assert.Equal(1, revokes));
        var result = CpuPmAttendedCapture.CaptureAndStore(() => { captures++; return batch; },
            () => { revokes++; throw new IOException("private/authority/path"); },
            new(Run, Guid.NewGuid(), Source, Hash, 1000), sink, CancellationToken.None);
        Assert.Equal(1, captures); Assert.Equal(1, revokes); Assert.Equal(CpuPmCaptureWriteStatus.StoredIncomplete, result.Status);
        Assert.Equal(3, result.VerifiedSamples); Assert.Equal(10, result.VerifiedFiles);
        for (int index = 0; index < samples.Length; index++)
        {
            Assert.Equal(samples[index].Snapshot.CopyReply(), sink.Files.Single(file => file.Name == $"sample-{index:00}.reply.bin").CopyBytes());
            Assert.Equal(samples[index].Snapshot.CopyPayload(), sink.Files.Single(file => file.Name == $"sample-{index:00}.payload.bin").CopyBytes());
        }
        byte[] report = sink.Files.Single(file => file.Name == "capture-result.json").CopyBytes();
        using var json = JsonDocument.Parse(report);
        Assert.Equal("Cleanup", json.RootElement.GetProperty("diagnostic").GetProperty("stage").GetString());
        Assert.Equal("CleanupFailed", json.RootElement.GetProperty("diagnostic").GetProperty("failure").GetString());
        Assert.DoesNotContain("private/authority/path", Encoding.UTF8.GetString(report));
        Assert.True(batch.Complete); // original immutable returned batch is untouched
    }
    [Fact]
    public void FreshDirectorySecurityAbiUsesNonInheritedHandleLayout()
    {
        Assert.Equal(24, Marshal.SizeOf<WindowsCpuPmFreshRunTree.SecurityAttributes>());
        Assert.Equal(8, Marshal.OffsetOf<WindowsCpuPmFreshRunTree.SecurityAttributes>("Descriptor").ToInt32());
        Assert.Equal(0, new WindowsCpuPmFreshRunTree.SecurityAttributes().InheritHandle);
    }
    private sealed class Child : ICpuPmRetainedChild
    {
        internal CpuPmChildState State = CpuPmChildState.Running; internal bool Drift;
        public CpuPmAuthorityPeer Identity => Supervisor;
        public CpuPmAuthorityPeer? ReadFreshIdentity() => Drift ? null : Identity;
        public CpuPmChildState ReadState() => State;
    }
    private sealed class Sink(Action verify) : ICpuPmCaptureEvidenceSink
    {
        internal readonly List<CpuPmEvidenceFile> Files = [];
        public bool BeginFreshRun(Guid runId) { verify(); return runId == Run; }
        public bool TryWriteImmutable(CpuPmEvidenceFile file) { verify(); Files.Add(file); return true; }
    }
}
