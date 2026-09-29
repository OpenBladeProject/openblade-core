using System.Buffers.Binary;
using System.ComponentModel;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class WindowsRz090528CpuQueryAdapterTests
{
    private static readonly DeviceIdentity Exact = new("RZ09-0528", 0x1532, 0x02C6, "2.02");
    private static CpuQueryAttendedApproval Approval() => CpuQueryAttendedApproval.AcknowledgePciTransportWrites(true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProductionOptInNeverAdmitsNativeAccess(bool installedOptIn)
    {
        Assert.Equal(CpuLiveQueryStatus.Unavailable,
            WindowsRz090528CpuQueryAdapter.QueryProduction(installedOptIn).Status);
    }

    [Fact]
    public void ExplicitApprovalIsRequiredBeforeAnyPlatformOperationAndIsSingleUse()
    {
        var platform = new FakePlatform();
        var result = Run(platform, null);
        Assert.Equal(CpuLiveQueryStatus.Unavailable, result.Status);
        Assert.Empty(platform.Events);
        Assert.Throws<ArgumentException>(() => CpuQueryAttendedApproval.AcknowledgePciTransportWrites(false));
        var approval = Approval();
        Assert.Equal(CpuLiveQueryStatus.Observed, Run(platform, approval).Status);
        int calls = platform.Events.Count;
        Assert.Equal(CpuLiveQueryStatus.Unavailable, Run(platform, approval).Status);
        Assert.Equal(calls, platform.Events.Count);
    }

    [Theory]
    [InlineData("host")]
    [InlineData("sku")]
    [InlineData("family")]
    [InlineData("model")]
    [InlineData("stepping")]
    [InlineData("vendor")]
    [InlineData("hash")]
    [InlineData("signature")]
    [InlineData("signer")]
    [InlineData("service")]
    [InlineData("path")]
    [InlineData("disabled")]
    [InlineData("controllers")]
    public void FailedFreshPreflightNeverOpensDevice(string failure)
    {
        var platform = new FakePlatform { Failure = failure };
        Assert.Equal(CpuLiveQueryStatus.Unavailable, Run(platform, Approval()).Status);
        Assert.DoesNotContain("open", platform.Events);
        Assert.Contains("unpin", platform.Events);
        Assert.Equal(1, platform.Mutex.Releases);
    }

    [Fact]
    public void UsbIdentityMustBeFreshAfterMutexWait()
    {
        var platform = new FakePlatform();
        int reads = 0;
        var result = WindowsRz090528CpuQueryAdapter.QueryForAttendedValidation(Approval(),
            () => ++reads == 1 ? Exact : new("RZ09-0581", 0x1532, 0x02E0, "4.00"), platform);
        Assert.Equal(CpuLiveQueryStatus.Unavailable, result.Status);
        Assert.DoesNotContain("open", platform.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QueryOwnsMutexAndFilePinsThroughAllTrafficAndRediscoversAfterAbandonment(bool abandoned)
    {
        var platform = new FakePlatform();
        platform.Mutex.Outcome = abandoned ? CpuQueryMutexWait.Abandoned : CpuQueryMutexWait.Acquired;
        var result = Run(platform, Approval());
        Assert.Equal(CpuLiveQueryStatus.Observed, result.Status);
        Assert.Equal(12, result.Observation!.CoreOffsets.Count);
        Assert.Equal(new uint[] { 6 }.Concat(Enumerable.Repeat(0x5Eu, 12)), platform.Device.Commands);
        Assert.Equal(2, platform.Events.Count(item => item == "backend"));
        Assert.Equal(2, platform.Device.Codes.Count(code => code == 0x81112F18));
        Assert.Equal(0x81112F18u, platform.Device.Codes[0]);
        Assert.Equal(["close", "unpin", "release", "mutex-dispose"], platform.Events.TakeLast(4));
        Assert.Equal(1, platform.Mutex.Releases);
        Assert.All(platform.Threads, id => Assert.Equal(Environment.CurrentManagedThreadId, id));
    }

    [Fact]
    public void ChangedPostOpenAdmissionClosesWithoutAnyIoctl()
    {
        var platform = new FakePlatform { ChangeAfterOpen = true };
        Assert.Equal(CpuLiveQueryStatus.Unavailable, Run(platform, Approval()).Status);
        Assert.Empty(platform.Device.Codes);
        Assert.Contains("close", platform.Events);
        Assert.Equal(1, platform.Mutex.Releases);
    }

    [Fact]
    public void MutexTimeoutDoesNotPinOrOpen()
    {
        var platform = new FakePlatform();
        platform.Mutex.Outcome = CpuQueryMutexWait.Timeout;
        Assert.Equal(CpuLiveQueryStatus.Busy, Run(platform, Approval()).Status);
        Assert.Equal(["mutex-create", "wait", "mutex-dispose"], platform.Events);
        Assert.Equal(0, platform.Mutex.Releases);
    }

    [Theory]
    [InlineData("open-error")]
    [InlineData("ioctl-error")]
    [InlineData("short-reply")]
    public void NativeErrorsFailClosedAndReleaseResources(string failure)
    {
        var platform = new FakePlatform { Failure = failure };
        Assert.Equal(CpuLiveQueryStatus.Unavailable, Run(platform, Approval()).Status);
        Assert.Equal(1, platform.Mutex.Releases);
        Assert.Contains("unpin", platform.Events);
    }

    [Fact]
    public void CancellationDuringMailboxPollingReleasesOnQueryThread()
    {
        using var cancellation = new CancellationTokenSource();
        var platform = new FakePlatform();
        platform.Device.CancelOnResponse = cancellation;
        Assert.Throws<OperationCanceledException>(() => Run(platform, Approval(), cancellation.Token));
        Assert.Equal(1, platform.Mutex.Releases);
        Assert.Contains("close", platform.Events);
        Assert.All(platform.Threads, id => Assert.Equal(Environment.CurrentManagedThreadId, id));
    }

    [Theory]
    [InlineData(0x81112F08u)]
    [InlineData(0x81112EE0u)]
    [InlineData(0x81112EE4u)]
    [InlineData(0x81112FF4u)]
    public void UnknownIoctlsAreDenied(uint code)
    {
        Assert.False(Discover().Allows(code, Words(0), 0));
    }

    [Theory]
    [InlineData(0u, 0x03B10934u, 7u)] // another mailbox
    [InlineData(0u, 0xC0010015u, 7u)] // MSR-shaped address
    [InlineData(0u, 0x03B10A80u, 0u)] // wrong bus
    [InlineData(2u, 0u, 7u)] // only indices 0/1
    [InlineData(1u, 0u, 7u)] // no index selection
    public void RawRegisterPayloadsAreDenied(uint index, uint value, uint bus)
    {
        Assert.False(Discover().Allows(0x81112FF8, Words(index, value, bus), 0));
    }

    [Theory]
    [InlineData(0x32u)]
    [InlineData(0x33u)]
    [InlineData(0x53u)]
    [InlineData(0x5Du)]
    [InlineData(0x65u)]
    [InlineData(0x66u)]
    public void TuningRefreshAndAddressCommandsAreDenied(uint command)
    {
        var wire = PrepareMailbox();
        Write(wire, 0, 0x03B10A20);
        Assert.False(wire.Allows(0x81112FF8, Words(1, command, 7), 0));
    }

    [Theory]
    [InlineData(0u, 0u, 0u, 0xB8u)]
    [InlineData(7u, 0u, 0u, 0xC4u)]
    [InlineData(0u, 0x18u, 1u, 0x144u)]
    [InlineData(256u, 0u, 0u, 0u)]
    public void PciReadScopeCannotBeGeneralized(uint bus, uint device, uint function, uint offset)
    {
        Assert.False(Discover().Allows(0x81112F24, Words(bus, device, function, offset, 4), 24));
    }

    [Fact]
    public void SchemasAndReplyLengthsAreExactAndFailurePoisonsSession()
    {
        var wire = Discover();
        Assert.False(wire.Allows(0x81112F24, Words(7, 0, 0, 0, 8), 28));
        Assert.False(wire.Allows(0x81112F24, Words(7, 0, 0, 0, 4), 24));
        wire = Discover();
        var request = Words(7, 0, 0, 0, 4);
        Assert.True(wire.Allows(0x81112F24, request, 24));
        Assert.False(wire.AcceptReply(0x81112F24, request, 24, new(true, new byte[25])));
        wire = Discover();
        Write(wire, 0, 0x03B10A88);
        Assert.False(wire.Allows(0x81112FF8, Words(1, 0, 7), 0)); // no response clear
        wire = PrepareMailbox();
        Assert.False(wire.Allows(0x81112FF8, Words(1, 0xFFFFFFFF, 7), 0));
    }

    private static Rz090528CpuLiveQueryResult Run(FakePlatform platform, CpuQueryAttendedApproval? approval,
        CancellationToken cancellationToken = default) =>
        WindowsRz090528CpuQueryAdapter.QueryForAttendedValidation(approval, () => Exact, platform, cancellationToken);

    private static CpuQueryWireAdmission Discover()
    {
        var wire = new CpuQueryWireAdmission();
        var first = new byte[28];
        Put(first, 24, 1);
        Exchange(wire, 0x81112F18, new byte[28], first);
        var input = new byte[80];
        Put(input, 24, 1);
        var output = (byte[])input.Clone();
        Put(output, 28, 7);
        Put(output, 40, 0x16F81022);
        Exchange(wire, 0x81112F18, input, output);
        return wire;
    }

    private static CpuQueryWireAdmission PrepareMailbox()
    {
        var wire = Discover();
        Write(wire, 0, 0x03B10A80);
        var read = Words(7, 0, 0, 0xC8, 4);
        Exchange(wire, 0x81112F24, read, [.. read, 1, 0, 0, 0]);
        Write(wire, 1, 0);
        Write(wire, 0, 0x03B10A88);
        Write(wire, 1, 0);
        return wire;
    }

    private static void Write(CpuQueryWireAdmission wire, uint index, uint value) =>
        Exchange(wire, 0x81112FF8, Words(index, value, 7), []);
    private static void Exchange(CpuQueryWireAdmission wire, uint code, byte[] input, byte[] output)
    {
        Assert.True(wire.Allows(code, input, output.Length));
        Assert.True(wire.AcceptReply(code, input, output.Length, new(true, output)));
    }
    private static byte[] Words(params uint[] values)
    {
        var result = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++) { Put(result, i * 4, values[i]); }
        return result;
    }
    private static void Put(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
    private static uint Word(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));

    private sealed class FakePlatform : ICpuQueryWindowsPlatform
    {
        internal readonly List<string> Events = [];
        internal readonly List<int> Threads = [];
        internal readonly FakeMutex Mutex;
        internal readonly FakeDevice Device;
        internal string? Failure;
        internal bool ChangeAfterOpen, Pinned;
        internal FakePlatform() { Mutex = new(this); Device = new(this); }
        internal void Event(string name) { Events.Add(name); Threads.Add(Environment.CurrentManagedThreadId); }
        public HostIdentityReadResult ReadHost() => new(new("Razer",
            Failure == "host" ? "other" : "Blade 16 - RZ09-0528",
            Failure == "sku" ? "other" : "RZ09-05289EN4", "2.02"), null);
        public CpuQueryProcessorIdentity ReadProcessor() => new(Failure == "vendor" ? "other" : "AuthenticAMD",
            Failure == "family" ? 0x19 : 0x1A, Failure == "model" ? 0x30 : 0x20, Failure == "stepping" ? 0 : 1);
        public bool ControllersAbsent() => Failure != "controllers";
        public IDisposable PinBackendFiles()
        {
            Event("pin"); Pinned = true;
            return new ActionDisposable(() => { Event("unpin"); Pinned = false; });
        }
        public InstalledCpuBackendSnapshot ReadBackend()
        {
            Assert.True(Pinned); Event("backend");
            var files = Rz090528InstalledCpuBackendInspector.ExpectedHashes.Select(pair => new CpuBackendFileMetadata(pair.Key,
                Failure == "hash" ? "wrong" : pair.Value, Failure != "signature",
                Failure == "signer" ? "wrong" : Rz090528InstalledCpuBackendInspector.SignerThumbprint)).ToArray();
            return new(files, true, Failure == "path" ? "other" : @"\??\" + Rz090528InstalledCpuBackendInspector.DriverPath,
                1, Failure == "disabled" ? 4 : 3, Failure != "service" && !(ChangeAfterOpen && Events.Contains("open")));
        }
        public ICpuQueryDevice OpenExistingDevice()
        {
            Assert.True(Pinned); Assert.True(Mutex.Held); Event("open");
            if (Failure == "open-error") { throw new Win32Exception(5); }
            return Device;
        }
        public ICpuQueryMutex CreateSharedMutex() { Event("mutex-create"); return Mutex; }
    }

    private sealed class FakeMutex(FakePlatform platform) : ICpuQueryMutex
    {
        internal CpuQueryMutexWait Outcome = CpuQueryMutexWait.Acquired;
        internal bool Held;
        internal int Releases;
        public CpuQueryMutexWait Wait(TimeSpan timeout, CancellationToken cancellationToken)
        {
            Assert.Equal(TimeSpan.FromSeconds(20), timeout); cancellationToken.ThrowIfCancellationRequested();
            platform.Event("wait"); Held = Outcome != CpuQueryMutexWait.Timeout; return Outcome;
        }
        public void Release() { Assert.True(Held); platform.Event("release"); Held = false; Releases++; }
        public void Dispose() { Assert.False(Held); platform.Event("mutex-dispose"); }
    }

    private sealed class FakeDevice(FakePlatform platform) : ICpuQueryDevice
    {
        internal readonly List<uint> Codes = [], Commands = [];
        internal CancellationTokenSource? CancelOnResponse;
        private uint selected, argument, replyValue;
        public CpuQueryIoctlReply Invoke(uint code, byte[] input, int outputLength)
        {
            Assert.True(platform.Pinned); Assert.True(platform.Mutex.Held);
            platform.Event("ioctl"); Codes.Add(code);
            if (platform.Failure == "ioctl-error") { return new(false, []); }
            if (platform.Failure == "short-reply") { return new(true, new byte[outputLength - 1]); }
            var output = new byte[outputLength];
            if (code == 0x81112F18)
            {
                Put(output, 24, 1);
                if (outputLength > 28) { Put(output, 28, 7); Put(output, 40, 0x16F81022); }
            }
            else if (code == 0x81112F24)
            {
                input.CopyTo(output, 0);
                uint offset = Word(input, 12);
                uint value = offset == 0 ? Word(input, 0) == 7 ? 0x16F81022u : 0x1022u
                    : offset == 0x140 ? 0xFFFu : selected switch
                    {
                        0x03820AB0 => (~0xFFFu & 0xFFFF) << 5,
                        0x03B10A80 => 1,
                        0x03B10A88 => replyValue,
                        _ => throw new InvalidOperationException("Unexpected fake read"),
                    };
                if (selected == 0x03B10A80 && CancelOnResponse is not null)
                { CancelOnResponse.Cancel(); value = 0; }
                Put(output, 20, value);
            }
            else if (code == 0x81112FF8)
            {
                uint value = Word(input, 4);
                if (Word(input, 0) == 0) { selected = value; }
                else if (selected == 0x03B10A88) { argument = value; }
                else if (selected == 0x03B10A20)
                { Commands.Add(value); replyValue = value == 6 ? 0x005D0008 : (argument >> 20) & 0xFFFF; }
            }
            else { throw new InvalidOperationException("Unexpected fake IOCTL"); }
            return new(true, output);
        }
        public void Dispose() { Assert.True(platform.Mutex.Held); platform.Event("close"); }
    }

    private sealed class ActionDisposable(Action action) : IDisposable { public void Dispose() => action(); }
}
