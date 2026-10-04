using System.Buffers.Binary;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class Rz090528CpuLiveQueryTests
{
    private static readonly DeviceIdentity Exact = new("RZ09-0528", 0x1532, 0x02C6, "2.02");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingMailboxWaitFreezesDistinctPhaseAndZeroCounts(bool afterSubmission)
    {
        var sync = new FakeSynchronization();
        var wire = new ScriptedIoctl(sync) { IdleResponse = afterSubmission ? 1u : 0u, PendingPolls = afterSubmission ? 1 : 0 };
        var progress = new CpuQueryProgress();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        sync.OnDelay = () =>
        {
            entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            wire.IdleResponse = 1; sync.OnDelay = null;
        };
        var worker = Task.Run(() => new Rz090528CpuLiveQuery(wire, sync, progress).Query(Exact));
        string frozen;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            frozen = await Task.Run(() => progress.Snapshot(freeze: true)).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Contains("current mailbox " + (afterSubmission ? "ResponseWait" : "ReadinessWait"), frozen);
            Assert.Contains(afterSubmission ? "readiness zeros 0, response zeros 1" : "readiness zeros 1, response zeros 0", frozen);
            Assert.Contains("clear attempted/completed " + (afterSubmission ? "True/True" : "False/False"), frozen);
            Assert.Contains("submission attempted/completed " + (afterSubmission ? "True/True" : "False/False"), frozen);
            Assert.False(worker.IsCompleted);
        }
        finally { release.Set(); }
        Assert.Equal(CpuLiveQueryStatus.Observed, (await worker.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.Equal(frozen, progress.Snapshot());
    }

    [Theory]
    [InlineData(12, "Preparation", "True/False", "False/False", false)]
    [InlineData(14, "Preparation", "True/True", "False/False", false)]
    [InlineData(16, "Submission", "True/True", "True/False", false)]
    [InlineData(16, "Submission", "True/True", "True/False", true)]
    public void TransportFailureNeverClaimsClearOrSubmissionCompleted(int failedCall, string phase, string clear, string submission, bool throws)
    {
        var sync = new FakeSynchronization();
        var wire = new ScriptedIoctl(sync) { Fault = throws ? "entered-throw" : "ioctl-failure", FailureCall = failedCall };
        var progress = new CpuQueryProgress();
        Assert.Equal(CpuLiveQueryStatus.Unavailable, new Rz090528CpuLiveQuery(wire, sync, progress).Query(Exact).Status);
        string snapshot = progress.Snapshot();
        Assert.Contains("current mailbox " + phase, snapshot);
        Assert.Contains("clear attempted/completed " + clear, snapshot);
        Assert.Contains("submission attempted/completed " + submission, snapshot);
        Assert.Equal(failedCall, wire.Calls.Count);
    }

    [Fact]
    public void QueriesExactWireContractAndPreservesPhysicalCoreGaps()
    {
        var sync = new FakeSynchronization();
        var wire = new ScriptedIoctl(sync);
        var result = new Rz090528CpuLiveQuery(wire, sync).Query(Exact);
        Assert.Equal(CpuLiveQueryStatus.Observed, result.Status);
        var observation = Assert.IsType<Rz090528CpuLiveObservation>(result.Observation);
        Assert.Equal(0xF6BDu, observation.EnabledCoreMask);
        Assert.Equal(0x005D0008u, observation.PmVersion);
        Assert.Equal(0xD54, observation.PmTableLength);
        var ids = new[] { 0, 2, 3, 4, 5, 7, 9, 10, 12, 13, 14, 15 };
        Assert.Equal(ids, observation.CoreOffsets.Select(value => value.PhysicalCoreId));
        Assert.Equal(ids.Select(id => (short)(-id - 1)), observation.CoreOffsets.Select(value => value.Offset));
        Assert.Equal(new uint[] { 6 }.Concat(Enumerable.Repeat(0x5Eu, 12)), wire.Messages.Select(value => value.Command));
        Assert.Equal(new uint[] { 0 }.Concat(ids.Select(id => (uint)(((id / 8) << 8) | id % 8) << 20)),
            wire.Messages.Select(value => value.Argument));
        Assert.All(wire.Calls.Where(call => call.Code == 0x81112FF8), call =>
        {
            Assert.Equal(12, call.Input.Length);
            Assert.InRange(U32(call.Input, 0), 0u, 1u);
            Assert.Equal(0u, U32(call.Input, 8));
            Assert.Equal(0, call.OutputLength);
        });
        Assert.Equal(2, wire.Calls.Count(call => call.Code == 0x81112F18));
        Assert.Equal(28, wire.Calls[0].OutputLength);
        Assert.Equal(184, wire.Calls[1].OutputLength);
        Assert.Equal(new[] { (0u, 0u, 0u), (0x18u, 0u, 0u), (0x18u, 1u, 0u), (0x18u, 1u, 0x140u) },
            wire.Calls.Skip(2).Take(4).Select(call => (U32(call.Input, 4), U32(call.Input, 8), U32(call.Input, 12))));
        Assert.All(wire.Calls.Skip(2).Take(4), call => Assert.Equal(0x81112F24u, call.Code));
        Assert.Equal(TimeSpan.FromSeconds(20), sync.AcquisitionTimeout);
        Assert.Equal(1, sync.Acquisitions);
        Assert.Equal(1, sync.Releases);
        Assert.Null(result.Diagnostic);
    }

    [Theory]
    [InlineData(0x005D0009u, 0xD58)]
    [InlineData(0x005D000Au, 0xD60)]
    [InlineData(0x005D000Bu, 0xD50)]
    [InlineData(0x005D000Cu, 0xD68)]
    public void RecognizesOnlyExactVersionLayouts(uint version, int length)
    {
        var sync = new FakeSynchronization();
        var wire = new ScriptedIoctl(sync) { Version = version };
        var result = new Rz090528CpuLiveQuery(wire, sync).Query(Exact);
        Assert.Equal(length, result.Observation!.PmTableLength);
    }

    [Fact]
    public void UnknownVersionStopsBeforeCoreCommandsAndReturnsNoZeroBaseline()
    {
        var sync = new FakeSynchronization();
        var wire = new ScriptedIoctl(sync) { Version = 0x005D000D };
        var result = new Rz090528CpuLiveQuery(wire, sync).Query(Exact);
        Assert.Equal(CpuLiveQueryStatus.UnsupportedVersion, result.Status);
        Assert.Null(result.Observation);
        Assert.Single(wire.Messages);
        Assert.Equal(1, sync.Releases);
    }

    [Theory]
    [InlineData("RZ09-0581", 0x1532, 0x02C6)]
    [InlineData("RZ09-0528", 1, 0x02C6)]
    [InlineData("RZ09-0528", 0x1532, 0x02E0)]
    public void UnsupportedDeviceNeverAcquiresOrTouchesTransport(string model, int vendor, int product)
    {
        var sync = new FakeSynchronization();
        var wire = new ScriptedIoctl(sync);
        var result = new Rz090528CpuLiveQuery(wire, sync).Query(new(model, (ushort)vendor, (ushort)product, "2.02"));
        Assert.Equal(CpuLiveQueryStatus.UnsupportedIdentity, result.Status);
        Assert.Empty(wire.Calls);
        Assert.Equal(0, sync.Acquisitions);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("zero-count")]
    [InlineData("oversize-count")]
    [InlineData("missing")]
    [InlineData("wrong-slot")]
    [InlineData("stale-id")]
    [InlineData("invalid-bus")]
    [InlineData("count-change")]
    [InlineData("short-reply")]
    [InlineData("header-change")]
    [InlineData("mask-high")]
    [InlineData("mask-count")]
    [InlineData("mask-disagree")]
    public void MalformedDiscoveryOrTopologyRefusesBeforeMailbox(string fault)
    {
        var sync = new FakeSynchronization();
        var wire = new ScriptedIoctl(sync) { Fault = fault };
        var result = new Rz090528CpuLiveQuery(wire, sync).Query(Exact);
        Assert.Equal(CpuLiveQueryStatus.InvalidData, result.Status);
        Assert.Null(result.Observation);
        Assert.Empty(wire.Messages);
        Assert.Equal(1, sync.Releases);
    }

    public static IEnumerable<object[]> InvalidPciIdentities => new[]
    {
        "old-d0-node", "swapped", "duplicate", "duplicate-root", "duplicate-orientation", "duplicate-bdf",
        "wrong-slot", "wrong-function", "missing", "missing-root", "missing-orientation", "cross-bus",
        "unvalidated-bus", "stale-root", "stale-node", "stale-orientation",
    }.Select(fault => new object[] { fault });

    [Theory]
    [MemberData(nameof(InvalidPciIdentities))]
    public void InvalidRootNodeOrOrientationCannotReachAnySmnWrite(string fault)
    {
        var sync = new FakeSynchronization();
        var wire = new ScriptedIoctl(sync) { Fault = fault };
        var result = new Rz090528CpuLiveQuery(wire, sync).Query(Exact);
        Assert.Equal(CpuLiveQueryStatus.InvalidData, result.Status);
        Assert.Null(result.Observation);
        Assert.DoesNotContain(wire.Calls, call => call.Code == 0x81112FF8);
        Assert.Empty(wire.Messages);
        Assert.Equal(1, sync.Releases);
    }

    [Fact]
    public void DiscoveryAllowsUnrelatedUniqueFunctionsAndDifferentRecordOrder()
    {
        var sync = new FakeSynchronization();
        var wire = new ScriptedIoctl(sync) { Fault = "unrelated-functions" };
        Assert.Equal(CpuLiveQueryStatus.Observed, new Rz090528CpuLiveQuery(wire, sync).Query(Exact).Status);
    }

    [Fact]
    public void IdleMailboxBusyCannotBeOverwritten()
    {
        var sync = new FakeSynchronization();
        var wire = new ScriptedIoctl(sync) { IdleResponse = 0 };
        Assert.Equal(CpuLiveQueryStatus.Busy, new Rz090528CpuLiveQuery(wire, sync).Query(Exact).Status);
        Assert.Empty(wire.Messages);
        Assert.DoesNotContain(wire.Calls, call => call.Code == 0x81112FF8 && U32(call.Input, 0) == 1);
    }

    [Theory]
    [InlineData(2u)]
    [InlineData(0xFDu)]
    public void PriorTerminalErrorAllowsGetterWithoutAdditionalTraffic(uint response)
    {
        var sync = new FakeSynchronization();
        var baseline = new ScriptedIoctl(sync);
        Assert.Equal(CpuLiveQueryStatus.Observed, new Rz090528CpuLiveQuery(baseline, sync).Query(Exact).Status);
        var wire = new ScriptedIoctl(sync) { IdleResponse = response };
        Assert.Equal(CpuLiveQueryStatus.Observed, new Rz090528CpuLiveQuery(wire, sync).Query(Exact).Status);
        Assert.Equal(baseline.Calls.Count, wire.Calls.Count);
        for (int i = 0; i < baseline.Calls.Count; i++)
        {
            Assert.Equal(baseline.Calls[i].Code, wire.Calls[i].Code);
            Assert.Equal(baseline.Calls[i].Input, wire.Calls[i].Input);
        }
        var strict = new ScriptedIoctl(sync) { IdleResponse = response };
        using var lease = sync.Acquire(TimeSpan.FromSeconds(20), default);
        var failure = Assert.Throws<Rz090528CpuMailboxSession.QueryFailure>(() =>
            new Rz090528CpuMailboxSession(strict, sync).Observe(default));
        Assert.Equal(CpuLiveQueryStatus.InvalidData, failure.Status);
        Assert.Empty(strict.Messages);
    }

    [Fact]
    public void CompletionErrorIsNotAValue()
    {
        var sync = new FakeSynchronization();
        var wire = new ScriptedIoctl(sync) { CompletionResponse = 0xFE };
        var progress = new CpuQueryProgress();
        var result = new Rz090528CpuLiveQuery(wire, sync, progress).Query(Exact);
        string frozen = progress.Snapshot();
        Assert.Contains("current mailbox ResponseWait", frozen);
        Assert.Contains("clear attempted/completed True/True, submission attempted/completed True/True", frozen);
        Assert.Contains("rejected mailbox response 0x000000FE", frozen);
        progress.MailboxPhase(CpuQueryMailboxPhase.ArgumentRead); progress.CompletedIoctl();
        Assert.Equal(frozen, progress.Snapshot());
        Assert.Equal(CpuLiveQueryStatus.InvalidData, result.Status);
        Assert.Null(result.Observation);
        Assert.Single(wire.Messages);
    }

    [Fact]
    public void IdleMailboxCanSettleBeforeSubmissionWithoutLosingSharedLease()
    {
        var sync = new FakeSynchronization();
        var wire = new ScriptedIoctl(sync) { IdleResponse = 0 };
        sync.OnDelay = () => wire.IdleResponse = 1;
        var result = new Rz090528CpuLiveQuery(wire, sync).Query(Exact);
        Assert.Equal(CpuLiveQueryStatus.Observed, result.Status);
        Assert.Equal(1, sync.Delays);
        Assert.Equal(1, sync.Releases);
    }

    [Fact]
    public void IdleMailboxPollingHasADeadlineWithoutSubmittingAnyCommand()
    {
        var sync = new FakeSynchronization();
        var wire = new ScriptedIoctl(sync) { IdleResponse = 0 };
        var result = new Rz090528CpuLiveQuery(wire, sync).Query(Exact);
        Assert.Equal(CpuLiveQueryStatus.Busy, result.Status);
        Assert.Equal(1000, sync.Delays);
        Assert.Empty(wire.Messages);
        Assert.Equal(1, sync.Releases);
    }

    [Fact]
    public void IdlePollingCancellationDoesNotSubmitOrReturnPartialState()
    {
        using var cancellation = new CancellationTokenSource();
        var sync = new FakeSynchronization { OnDelay = cancellation.Cancel };
        var wire = new ScriptedIoctl(sync) { IdleResponse = 0 };
        Assert.ThrowsAny<OperationCanceledException>(() =>
            new Rz090528CpuLiveQuery(wire, sync).Query(Exact, cancellation.Token));
        Assert.Empty(wire.Messages);
        Assert.Equal(1, sync.Releases);
        Assert.DoesNotContain(wire.Calls, call => call.Code == 0x81112FF8 && U32(call.Input, 0) == 1);
    }

    [Fact]
    public void DelayExceptionKeepsQueryStageAndReleasesLease()
    {
        var sync = new FakeSynchronization { OnDelay = () => throw new IOException("private wait details") };
        var wire = new ScriptedIoctl(sync) { IdleResponse = 0 };
        var result = new Rz090528CpuLiveQuery(wire, sync).Query(Exact);
        Assert.Equal(CpuLiveQueryStatus.Unavailable, result.Status);
        Assert.Equal(new(CpuQueryStage.PmVersion, CpuQueryFailureCode.IoError), result.Diagnostic);
        Assert.Empty(wire.Messages);
        Assert.Equal(1, sync.Releases);
    }

    [Fact]
    public void PollingIsBoundedAndLeaseReleasedOnTimeout()
    {
        var sync = new FakeSynchronization();
        var wire = new ScriptedIoctl(sync) { PendingPolls = int.MaxValue };
        var result = new Rz090528CpuLiveQuery(wire, sync).Query(Exact);
        Assert.Equal(CpuLiveQueryStatus.Busy, result.Status);
        Assert.Equal(1000, wire.CompletionPolls);
        Assert.Equal(1000, sync.Delays);
        Assert.Equal(1, sync.Releases);
    }

    [Fact]
    public void PollingCanBeCancelledWithoutReturningPartialObservation()
    {
        using var cancellation = new CancellationTokenSource();
        var sync = new FakeSynchronization { OnDelay = cancellation.Cancel };
        var wire = new ScriptedIoctl(sync) { PendingPolls = 5 };
        Assert.ThrowsAny<OperationCanceledException>(() =>
            new Rz090528CpuLiveQuery(wire, sync).Query(Exact, cancellation.Token));
        Assert.Equal(1, wire.CompletionPolls);
        Assert.Equal(1, sync.Releases);
    }

    [Fact]
    public void SynchronizationFailureAndIoctlFailureRemainUnavailable()
    {
        var sync = new FakeSynchronization { Denied = true };
        var wire = new ScriptedIoctl(sync);
        var result = new Rz090528CpuLiveQuery(wire, sync).Query(Exact);
        Assert.Equal(CpuLiveQueryStatus.Busy, result.Status);
        Assert.Empty(wire.Calls);
        sync.Denied = false;
        wire.Fault = "ioctl-failure";
        result = new Rz090528CpuLiveQuery(wire, sync).Query(Exact);
        Assert.Equal(CpuLiveQueryStatus.Unavailable, result.Status);
        Assert.Null(result.Observation);
        Assert.Equal(1, sync.Releases);
    }

    [Theory]
    [InlineData(1, "PciCount", "PciEnumeration")]
    [InlineData(2, "PciRecords", "PciEnumeration")]
    [InlineData(3, "PciIdentity", "PciRead")]
    [InlineData(6, "CoreMask", "PciRead")]
    [InlineData(7, "FuseMask", "SmnWrite")]
    [InlineData(8, "FuseMask", "PciRead")]
    [InlineData(9, "PmVersion", "SmnWrite")]
    [InlineData(21, "CoreOffset", "SmnWrite")]
    public void TransportFailureReportsExactStageWithoutRetryOrPartialObservation(
        int call, string stage, string operation)
    {
        var sync = new FakeSynchronization();
        var wire = new ScriptedIoctl(sync) { Fault = "ioctl-failure", FailureCall = call };
        var result = new Rz090528CpuLiveQuery(wire, sync).Query(Exact);
        Assert.Equal(CpuLiveQueryStatus.Unavailable, result.Status);
        Assert.Null(result.Observation);
        Assert.Equal(stage, result.Diagnostic!.Stage.ToString());
        Assert.Equal(operation, result.Diagnostic.Operation.ToString());
        Assert.Equal(CpuQueryFailureCode.NativeIoctlFailed, result.Diagnostic.Failure);
        Assert.Equal(50, result.Diagnostic.NativeErrorCode);
        Assert.Equal(call, wire.Calls.Count);
        Assert.Equal(1, sync.Releases);
    }

    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
    private static void Put(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
    private sealed record Call(uint Code, byte[] Input, int OutputLength);

    private sealed class ScriptedIoctl(FakeSynchronization synchronization) : IRz090528CpuQueryIoctl
    {
        public List<Call> Calls { get; } = [];
        public List<(uint Command, uint Argument)> Messages { get; } = [];
        public string? Fault { get; set; }
        public int? FailureCall { get; set; }
        public uint Version { get; set; } = 0x005D0008;
        public uint IdleResponse { get; set; } = 1;
        public uint CompletionResponse { get; set; } = 1;
        public int PendingPolls { get; set; }
        public int CompletionPolls { get; private set; }
        private uint selectedAddress;
        private uint argument;
        private uint replyValue;
        private bool pending;
        private int remainingPolls;

        public CpuQueryIoctlReply Invoke(uint code, byte[] input, int outputLength)
        {
            Assert.True(synchronization.Held);
            Calls.Add(new(code, input.ToArray(), outputLength));
            if (Fault == "entered-throw" && FailureCall == Calls.Count) { throw new IOException("synthetic entered transport"); }
            if (Fault == "ioctl-failure" && (FailureCall is null || FailureCall == Calls.Count))
            {
                return new(false, [], CpuQueryFailureCode.NativeIoctlFailed, 50);
            }

            if (code == 0x81112F18)
            {
                var records = CpuQueryPciTestRecords.Create(Fault);
                uint count = U32(records, 24);
                var output = outputLength == 28 ? new byte[28] : records;
                Put(output, 24, Fault switch
                {
                    "zero-count" => 0,
                    "oversize-count" => 65537,
                    "count-change" when outputLength > 28 => count + 1,
                    _ => count,
                });
                if (outputLength > 28) { Assert.Equal(count, U32(input, 24)); }
                return new(true, Fault == "short-reply" ? output[..^1] : output);
            }
            if (code == 0x81112FF8)
            {
                Assert.Equal(12, input.Length);
                Assert.Equal(0u, U32(input, 8));
                var index = U32(input, 0);
                var value = U32(input, 4);
                Assert.InRange(index, 0u, 1u);
                if (index == 0)
                {
                    Assert.Contains(value, new uint[] { 0x03820AB0, 0x03B10A80, 0x03B10A88, 0x03B10A20 });
                    selectedAddress = value;
                }
                else if (selectedAddress == 0x03B10A88)
                {
                    argument = value;
                }
                else if (selectedAddress == 0x03B10A80)
                {
                    Assert.Equal(0u, value);
                }
                else
                {
                    Assert.Equal(0x03B10A20u, selectedAddress);
                    Assert.Contains(value, new uint[] { 6, 0x5E });
                    Messages.Add((value, argument));
                    replyValue = value == 6 ? Version : unchecked((uint)(-(int)(((argument >> 28) * 8) + ((argument >> 20) & 7)) - 1));
                    pending = true;
                    remainingPolls = PendingPolls;
                }
                Assert.Equal(0, outputLength);
                return new(true, []);
            }
            Assert.Equal(0x81112F24u, code);
            Assert.Equal(20, input.Length);
            Assert.Equal(24, outputLength);
            Assert.Equal(4u, U32(input, 16));
            var response = new byte[24];
            input.CopyTo(response, 0);
            uint data;
            var offsetInConfig = U32(input, 12);
            if (offsetInConfig == 0xC8)
            {
                Assert.Equal(0u, U32(input, 0));
                Assert.Equal(0u, U32(input, 4));
                Assert.Equal(0u, U32(input, 8));
                if (selectedAddress == 0x03820AB0)
                {
                    data = (~(Fault == "mask-disagree" ? 0xF6BCu : 0xF6BDu) & 0xFFFF) << 5;
                }
                else if (selectedAddress == 0x03B10A88)
                {
                    data = replyValue;
                }
                else
                {
                    Assert.Equal(0x03B10A80u, selectedAddress);
                    data = IdleResponse;
                    if (pending)
                    {
                        CompletionPolls++;
                        data = remainingPolls-- > 0 ? 0 : CompletionResponse;
                        if (data != 0)
                        {
                            pending = false;
                        }
                    }
                }
            }
            else if (U32(input, 4) == 0x18)
            {
                Assert.Equal(0u, U32(input, 0));
                if (U32(input, 8) == 0)
                {
                    Assert.Equal(0u, offsetInConfig);
                    data = Fault == "stale-node" ? 0u : 0x16F81022u;
                }
                else
                {
                    Assert.Equal(1u, U32(input, 8));
                    data = offsetInConfig == 0 ? Fault == "stale-orientation" ? 0u : 0x16F91022u : Fault switch
                    {
                        "mask-high" => 0x1F6BD,
                        "mask-count" => 0xF6BC,
                        _ => 0xF6BD,
                    };
                }
                Assert.Contains(offsetInConfig, new uint[] { 0, 0x140 });
            }
            else
            {
                Assert.Equal(0u, U32(input, 0));
                Assert.Equal(0u, offsetInConfig);
                data = Fault is "stale-id" or "stale-root" ? 0u : 0x15071022u;
            }
            Put(response, 20, data);
            if (Fault == "header-change")
            {
                Put(response, 16, 0);
            }

            return new(true, response);
        }
    }

    private sealed class FakeSynchronization : IRz090528CpuQuerySynchronization
    {
        public bool Held { get; private set; }
        public bool Denied { get; set; }
        public int Acquisitions { get; private set; }
        public int Releases { get; private set; }
        public int Delays { get; private set; }
        public TimeSpan AcquisitionTimeout { get; private set; }
        public Action? OnDelay { get; set; }
        public IDisposable? Acquire(TimeSpan timeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.False(Held);
            Acquisitions++;
            AcquisitionTimeout = timeout;
            if (Denied)
            {
                return null;
            }

            Held = true;
            return new Lease(this);
        }
        public void Delay(TimeSpan duration, CancellationToken cancellationToken)
        {
            Assert.True(Held);
            Assert.Equal(TimeSpan.FromMilliseconds(1), duration);
            Delays++;
            OnDelay?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();

        }
        private sealed class Lease(FakeSynchronization owner) : IDisposable
        {
            public void Dispose() { Assert.True(owner.Held); owner.Held = false; owner.Releases++; }
        }
    }
}

// Synthetic driver records matching the independently observed exact host BDFs;
// the SDK's node record and its D0F0 SMN transport are distinct PCI functions.
internal static class CpuQueryPciTestRecords
{
    internal static byte[] Create(string? fault = null)
    {
        var records = new List<(uint Bus, uint Device, uint Function, uint Id)>
        {
            (0, 0, 0, 0x15071022), (0, 0x18, 0, 0x16F81022), (0, 0x18, 1, 0x16F91022),
        };
        switch (fault)
        {
            case "unrelated-functions": records.Add((0, 1, 0, 0x12341022)); records.Reverse(); break;
            case "old-d0-node": records = [(0, 0, 0, 0x16F81022)]; break;
            case "swapped": records[0] = (0, 0, 0, 0x16F81022); records[1] = (0, 0x18, 0, 0x15071022); break;
            case "duplicate": records.Add((1, 0x18, 0, 0x16F81022)); break;
            case "duplicate-root": records.Add((1, 0, 0, 0x15071022)); break;
            case "duplicate-orientation": records.Add((1, 0x18, 1, 0x16F91022)); break;
            case "duplicate-bdf": records.Add((0, 0x18, 0, 0x12341022)); break;
            case "invalid-bus": records[0] = (256, 0, 0, 0x15071022); break;
            case "wrong-slot": records[1] = (0, 1, 0, 0x16F81022); break;
            case "wrong-function": records[1] = (0, 0x18, 2, 0x16F81022); break;
            case "missing": records[1] = (0, 0x18, 0, 0x12341022); break;
            case "missing-root": records[0] = (0, 0, 0, 0x12341022); break;
            case "missing-orientation": records[2] = (0, 0x18, 1, 0x12341022); break;
            case "cross-bus": records[1] = (1, 0x18, 0, 0x16F81022); break;
            case "unvalidated-bus": records = records.Select(r => (7u, r.Device, r.Function, r.Id)).ToList(); break;
        }
        var bytes = new byte[28 + records.Count * 52];
        Put(24, (uint)records.Count);
        for (int i = 0; i < records.Count; i++)
        {
            int offset = 28 + i * 52;
            var record = records[i];
            Put(offset, record.Bus); Put(offset + 4, record.Device); Put(offset + 8, record.Function); Put(offset + 12, record.Id);
        }
        return bytes;
        void Put(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
    }
}
