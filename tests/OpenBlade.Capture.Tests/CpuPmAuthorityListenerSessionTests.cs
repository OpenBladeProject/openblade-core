using System.Buffers.Binary;
using OpenBlade.Capture;

namespace OpenBlade.Capture.Tests;

// All peers, clocks, transports and proof are fake. No Windows provider is invoked.
public sealed class CpuPmAuthorityListenerSessionTests
{
    private static readonly Guid Run = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Lifetime = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly CpuPmAuthorityPeer Child = new(32, 200, @"C:\pinned\child.exe", new('B', 64));
    private static byte[] Frame(int counter = 1, char nonce = 'a')
    {
        byte[] body = CpuPmAuthorityProtocol.Encode(new(Run, Lifetime, counter, new(nonce, 64), null));
        byte[] result = new byte[body.Length + 4]; BinaryPrimitives.WriteInt32LittleEndian(result, body.Length); body.CopyTo(result, 4); return result;
    }

    [Fact]
    public async Task FortySecondIdleBetweenRequestsIsAllowedAndRequestsRemainSequential()
    {
        var clock = new Clock(); var input = Frame().Concat(Frame(2, 'b')).ToArray();
        var stream = new Duplex(input) { BeforeRead = (_, size) => { if (size == 1) { clock.Now += TimeSpan.FromSeconds(40); } } };
        // Do not simulate another idle wait for immediate EOF.
        var transport = new Transport(stream); var proof = new Proof();
        await using var listener = new CpuPmAuthorityListenerSession(Run, Lifetime, transport, clock);
        Assert.Equal(CpuPmListenerResult.ChannelClosed, await listener.ServeOnceAsync(Child, proof, CancellationToken.None));
        Assert.Equal(2, proof.Reads); Assert.Equal(1, transport.Connects); Assert.Equal(1, transport.Binds);
        using var replies = new MemoryStream(stream.Output.ToArray());
        for (int counter = 1; counter <= 2; counter++)
        {
            var reply = CpuPmAuthorityProtocol.Decode(await WindowsCpuPmAuthorityChannel.ReadFrameAsync(replies, CancellationToken.None), true);
            Assert.True(reply!.Active); Assert.Equal(counter, reply.Counter);
        }
        Assert.Equal(replies.Length, replies.Position); Assert.Equal(1, stream.Disposals);
        Assert.Equal(CpuPmListenerResult.AlreadyUsed, await listener.ServeOnceAsync(Child, proof, CancellationToken.None));
    }

    [Theory]
    [InlineData("prefix")]
    [InlineData("body")]
    [InlineData("clock-regression")]
    [InlineData("session")]
    public async Task DeadlineStartsAtFirstByteAndClockFailuresPreventProof(string fault)
    {
        var clock = new Clock(); var stream = new Duplex(Frame());
        stream.BeforeRead = (index, _) =>
        {
            if ((fault == "prefix" && index == 2) || (fault == "body" && index == 3)) { clock.Now += TimeSpan.FromSeconds(2); }
            if (fault == "clock-regression" && index == 2) { clock.Now = TimeSpan.FromTicks(-1); }
            if (fault == "session" && index == 1) { clock.Now = TimeSpan.FromSeconds(90); }
        };
        var proof = new Proof(); await using var listener = new CpuPmAuthorityListenerSession(Run, Lifetime, new Transport(stream), clock);
        Assert.Equal(CpuPmListenerResult.BudgetExpired, await listener.ServeOnceAsync(Child, proof, CancellationToken.None));
        Assert.Equal(0, proof.Reads); Assert.Empty(stream.Output.ToArray()); Assert.Equal(1, stream.Disposals);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(4097)]
    [InlineData(int.MaxValue)]
    public async Task OversizedOrInvalidLengthRefusesWithoutReadingBody(int length)
    {
        byte[] prefix = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(prefix, length);
        var stream = new Duplex(prefix); var proof = new Proof();
        await using var listener = new CpuPmAuthorityListenerSession(Run, Lifetime, new Transport(stream), new Clock());
        Assert.Equal(CpuPmListenerResult.Denied, await listener.ServeOnceAsync(Child, proof, CancellationToken.None));
        Assert.Equal(2, stream.Reads); Assert.Equal(0, proof.Reads); Assert.Empty(stream.Output.ToArray());
    }

    [Theory]
    [InlineData("missing-owner")]
    [InlineData("wrong-peer")]
    [InlineData("replay")]
    [InlineData("tag-drift")]
    [InlineData("proof-exception")]
    public async Task DenialClosesOneConnectionAndNeverReconnects(string fault)
    {
        var stream = new Duplex(Frame().Concat(Frame(fault == "replay" ? 1 : 2, 'b')).ToArray());
        var transport = new Transport(stream); var proof = new Proof();
        if (fault == "wrong-peer") { transport.Peer = Child with { ProcessId = 33 }; }
        if (fault == "tag-drift") { proof.OnRead = count => count < 2; }
        if (fault == "proof-exception") { proof.OnRead = _ => throw new FormatException("private"); }
        await using var listener = new CpuPmAuthorityListenerSession(Run, Lifetime, transport, new Clock());
        Assert.Equal(CpuPmListenerResult.Denied, await listener.ServeOnceAsync(Child, fault == "missing-owner" ? null : proof, CancellationToken.None));
        Assert.Equal(1, transport.Connects); Assert.Equal(1, transport.Binds); Assert.Equal(1, stream.Disposals);
        Assert.Equal(CpuPmListenerResult.AlreadyUsed, await listener.ServeOnceAsync(Child, proof, CancellationToken.None));
        Assert.Equal(1, transport.Connects);
    }

    [Theory]
    [InlineData("connect")]
    [InlineData("idle")]
    [InlineData("body")]
    [InlineData("write")]
    public async Task CancellationDrainsSamePendingOperationBeforeClosingOwnedHandles(string phase)
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new Duplex(Frame()); var transport = new Transport(stream);
        Task Hold() { entered.TrySetResult(); return pending.Task; }
        if (phase == "connect") { transport.Connect = Hold; }
        else if (phase == "write") { stream.HoldWrite = Hold; }
        else { stream.HoldRead = index => index == (phase == "idle" ? 1 : 3) ? Hold() : Task.CompletedTask; }
        var listener = new CpuPmAuthorityListenerSession(Run, Lifetime, transport, new Clock());
        Task<CpuPmListenerResult> serve = listener.ServeOnceAsync(Child, new Proof(), CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task dispose = listener.DisposeAsync().AsTask();
        Assert.False(serve.IsCompleted); Assert.False(dispose.IsCompleted); Assert.Equal(0, stream.Disposals);
        pending.SetResult();
        Assert.Equal(CpuPmListenerResult.Canceled, await serve.WaitAsync(TimeSpan.FromSeconds(5)));
        await dispose.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, stream.Disposals); Assert.Equal(1, transport.Connects);
    }

    [Theory]
    [InlineData("bind")]
    [InlineData("short-frame")]
    [InlineData("invalid-child")]
    [InlineData("disposed")]
    public async Task EarlyFailuresReleaseTransportExactlyOnce(string fault)
    {
        var stream = new Duplex(fault == "short-frame" ? [1, 0] : Frame());
        var transport = new Transport(stream) { ThrowBind = fault == "bind" };
        var listener = new CpuPmAuthorityListenerSession(Run, Lifetime, transport, new Clock());
        if (fault == "disposed") { await listener.DisposeAsync(); }
        var result = await listener.ServeOnceAsync(fault == "invalid-child" ? Child with { ProcessId = 0 } : Child, new Proof(), CancellationToken.None);
        Assert.Equal(fault switch { "disposed" => CpuPmListenerResult.AlreadyUsed, "invalid-child" => CpuPmListenerResult.Denied, _ => CpuPmListenerResult.Failed }, result);
        await listener.DisposeAsync(); Assert.Equal(1, stream.Disposals);
    }

    [Theory]
    [InlineData("already")]
    [InlineData("connect")]
    [InlineData("bind")]
    public async Task CancellationAtSynchronousAdmissionBoundaryNeverReadsARequest(string phase)
    {
        using var cancellation = new CancellationTokenSource();
        var stream = new Duplex(Frame()); var transport = new Transport(stream);
        if (phase == "already") { cancellation.Cancel(); }
        if (phase == "connect") { transport.Connect = () => { cancellation.Cancel(); return Task.CompletedTask; }; }
        if (phase == "bind") { transport.OnBind = cancellation.Cancel; }
        await using var listener = new CpuPmAuthorityListenerSession(Run, Lifetime, transport, new Clock());
        Assert.Equal(CpuPmListenerResult.Canceled, await listener.ServeOnceAsync(Child, new Proof(), cancellation.Token));
        Assert.Equal(0, stream.Reads); Assert.Empty(stream.Output.ToArray()); Assert.Equal(1, stream.Disposals);
        Assert.Equal(phase == "already" ? 0 : 1, transport.Connects);
    }

    [Fact]
    public async Task SlowResponseWriteStopsBeforeAcceptingAnotherRequest()
    {
        var clock = new Clock(); var stream = new Duplex(Frame().Concat(Frame(2, 'b')).ToArray());
        stream.HoldWrite = () => { clock.Now = TimeSpan.FromSeconds(2); return Task.CompletedTask; };
        var proof = new Proof(); await using var listener = new CpuPmAuthorityListenerSession(Run, Lifetime, new Transport(stream), clock);
        Assert.Equal(CpuPmListenerResult.BudgetExpired, await listener.ServeOnceAsync(Child, proof, CancellationToken.None));
        Assert.Equal(1, proof.Reads); Assert.Equal(3, stream.Reads); Assert.Equal(1, stream.Disposals);
    }

    private sealed class Clock : ICpuPmAuthorityClock { public TimeSpan Now { get; set; } }
    private sealed class Proof : ICpuPmOwnedDriverProofSource
    {
        internal int Reads; internal Func<int, bool>? OnRead;
        public CpuPmOwnedDriverProof? ReadFresh(Guid runId, Guid lifetimeId)
        { Reads++; return new(Run, Lifetime, true, true, 1, true, true, true, OnRead?.Invoke(Reads) ?? true, true, true, true); }
    }
    private sealed class Transport(Duplex stream) : ICpuPmAuthorityListenerTransport
    {
        internal int Connects, Binds; internal bool ThrowBind; internal CpuPmAuthorityPeer Peer = Child; internal Func<Task>? Connect; internal Action? OnBind;
        private bool transferred; private bool disposed;
        public Stream Stream => stream;
        public Task WaitForConnectionAsync(CancellationToken token) { Connects++; return Connect?.Invoke() ?? Task.CompletedTask; }
        public ICpuPmAuthorityChannel BindRetainedChild(CpuPmAuthorityPeer expectedChild)
        { Binds++; if (ThrowBind) { throw new IOException(); } OnBind?.Invoke(); transferred = true; return new Channel(this, stream); }
        public void Dispose() { if (!disposed && !transferred) { stream.Dispose(); } disposed = true; }
        private sealed class Channel(Transport owner, Stream stream) : ICpuPmAuthorityChannel
        {
            public CpuPmAuthorityPeer? ReadFreshPeer() => owner.Peer;
            public Task<byte[]> ExchangeAsync(byte[] request, CancellationToken token) => throw new NotSupportedException();
            public void Dispose() => stream.Dispose();
        }
    }
    private sealed class Duplex(byte[] bytes) : Stream
    {
        private readonly MemoryStream input = new(bytes); internal readonly MemoryStream Output = new();
        internal int Reads, Disposals; internal Action<int, int>? BeforeRead; internal Func<int, Task>? HoldRead; internal Func<Task>? HoldWrite;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Reads++;
            if (input.Position < input.Length) { BeforeRead?.Invoke(Reads, buffer.Length); }
            if (HoldRead is not null) { await HoldRead(Reads); }
            return input.Read(buffer.Span);
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { if (HoldWrite is not null) { await HoldWrite(); } cancellationToken.ThrowIfCancellationRequested(); Output.Write(buffer.Span); }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        protected override void Dispose(bool disposing) { if (disposing) { Disposals++; input.Dispose(); } base.Dispose(disposing); }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException(); public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
