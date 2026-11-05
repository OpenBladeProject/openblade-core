using System.Runtime.InteropServices;
using OpenBlade.Capture;

namespace OpenBlade.Capture.Tests;

public sealed class So6120InpOutBytePortTests
{
    [Fact]
    public void FixedPacketsUseOnlyReviewedIndexAndDataPorts()
    {
        using var handle = new TrackingHandle();
        var native = new FakeNative();
        using var port = new So6120InpOutBytePort(handle, native);
        Assert.True(port.SelectIndex(0x93).Succeeded);
        Assert.True(port.WriteData(0xA1).Succeeded);
        var read = port.ReadData();
        Assert.Equal((byte)0x5A, read.Value);
        Assert.Collection(native.Calls,
            call => { Assert.Equal(0x9C402008u, call.Control); Assert.Equal(new byte[] { 0, 3, 0x93 }, call.Input); Assert.Null(call.Output); },
            call => { Assert.Equal(0x9C402008u, call.Control); Assert.Equal(new byte[] { 1, 3, 0xA1 }, call.Input); Assert.Null(call.Output); },
            call => { Assert.Equal(0x9C402004u, call.Control); Assert.Equal(new byte[] { 1, 3 }, call.Input); Assert.Single(call.Output!); });
        Assert.All(native.Calls, call => Assert.Same(handle, call.Handle));
        Assert.NotSame(native.Calls[2].Input, native.Calls[2].Output);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(10u)]
    [InlineData(123u)]
    public void WritePreservesSuccessfulBooleanRegardlessOfReturnedInformation(uint returned)
    {
        using var handle = new TrackingHandle();
        var native = new FakeNative { Returned = returned };
        using var port = new So6120InpOutBytePort(handle, native);
        var result = port.WriteData(7);
        Assert.True(result.Succeeded);
        Assert.Equal(returned, result.BytesReturned);
        Assert.Equal(So6120PortEffect.Performed, result.Effect);
        Assert.Equal(0, result.NativeError);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(2u)]
    [InlineData(10u)]
    public void MalformedReadPreservesBooleanButExposesNoUsableValue(uint returned)
    {
        using var handle = new TrackingHandle();
        var native = new FakeNative { Returned = returned };
        using var port = new So6120InpOutBytePort(handle, native);
        var result = port.ReadData();
        Assert.True(result.Succeeded);
        Assert.Equal(returned, result.BytesReturned);
        Assert.Equal((byte)0, result.Value);
        Assert.Equal(So6120PortEffect.MayHavePerformed, result.Effect);
        Assert.Equal(0, result.NativeError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeFailurePreservesCountAndErrorWithoutRetry(bool read)
    {
        using var handle = new TrackingHandle();
        var native = new FakeNative { Success = false, Returned = 10, Error = 1234 };
        using var port = new So6120InpOutBytePort(handle, native);
        var result = read ? port.ReadData() : port.WriteData(0xA1);
        Assert.False(result.Succeeded);
        Assert.Equal(10u, result.BytesReturned);
        Assert.Equal(1234, result.NativeError);
        Assert.Equal((byte)0, result.Value);
        Assert.Equal(So6120PortEffect.MayHavePerformed, result.Effect);
        Assert.Single(native.Calls);
    }

    [Fact]
    public void NativeExceptionPropagatesWithoutInventingUnperformedEvidence()
    {
        using var handle = new TrackingHandle();
        var native = new FakeNative { Exception = new IOException("Injected native boundary failure") };
        using var port = new So6120InpOutBytePort(handle, native);
        // The adapter does not invent a not-performed result for an entered call.
        Assert.Same(native.Exception, Assert.Throws<IOException>(() => port.WriteData(0xA1)));
        Assert.Single(native.Calls);
        Assert.Equal(0, handle.Releases);
        port.Dispose();
        Assert.Equal(1, handle.Releases);
    }

    [Theory]
    [InlineData(0x0F)]
    [InlineData(0x12)]
    [InlineData(0xFF)]
    public void UnreviewedMailboxIndexIsRejectedBeforeNativeCall(int index)
    {
        using var handle = new TrackingHandle();
        var native = new FakeNative();
        using var port = new So6120InpOutBytePort(handle, native);
        var result = port.SelectIndex((byte)index);
        Assert.False(result.Succeeded);
        Assert.Equal(87, result.NativeError);
        Assert.Equal(So6120PortEffect.NotPerformed, result.Effect);
        Assert.Empty(native.Calls);
    }

    [Fact]
    public void DisposeClosesOwnedHandleOnceAndLaterCallsAreKnownUnperformed()
    {
        var handle = new TrackingHandle();
        var native = new FakeNative();
        var port = new So6120InpOutBytePort(handle, native);
        port.Dispose();
        port.Dispose();
        Assert.Equal(1, handle.Releases);
        foreach (var result in new[] { port.SelectIndex(0x93), port.ReadData(), port.WriteData(0xA1) })
        {
            Assert.False(result.Succeeded);
            Assert.Equal(6, result.NativeError);
            Assert.Equal(So6120PortEffect.NotPerformed, result.Effect);
        }
        Assert.Empty(native.Calls);
    }

    [Fact]
    public void InvalidConstructorDoesNotTakeOwnership()
    {
        using var handle = new TrackingHandle(invalid: true);
        Assert.Throws<ArgumentException>(() => new So6120InpOutBytePort(handle, new FakeNative()));
        Assert.False(handle.IsClosed);
        Assert.Equal(0, handle.Releases);
    }

    [Fact]
    public void ClosedHandleIsRejectedBeforeNativeCall()
    {
        var handle = new TrackingHandle();
        var native = new FakeNative();
        using var port = new So6120InpOutBytePort(handle, native);
        handle.Dispose();
        var result = port.ReadData();
        Assert.Equal(So6120PortEffect.NotPerformed, result.Effect);
        Assert.Empty(native.Calls);
        Assert.Equal(1, handle.Releases);
    }

    [Fact]
    public async Task ExternalDisposeCannotReleaseHandleDuringEnteredCall()
    {
        var handle = new TrackingHandle();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var native = new FakeNative { Inside = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); } };
        using var port = new So6120InpOutBytePort(handle, native);
        Task<So6120PortResult> operation = Task.Run(port.ReadData);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            handle.Dispose();
            Assert.Equal(0, handle.Releases);
        }
        finally { release.Set(); }
        Assert.True((await operation.WaitAsync(TimeSpan.FromSeconds(5))).Succeeded);
        Assert.Equal(1, handle.Releases);
    }

    [Fact]
    public async Task AdapterDisposeWaitsForEnteredCallAndPreventsLaterCalls()
    {
        var handle = new TrackingHandle();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var disposing = new ManualResetEventSlim();
        var native = new FakeNative { Inside = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); } };
        var port = new So6120InpOutBytePort(handle, native);
        Task<So6120PortResult> operation = Task.Run(port.ReadData);
        Task? disposal = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            disposal = Task.Run(() => { disposing.Set(); port.Dispose(); });
            Assert.True(disposing.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(0, handle.Releases);
            Assert.False(disposal.IsCompleted);
        }
        finally { release.Set(); }
        await operation.WaitAsync(TimeSpan.FromSeconds(5));
        await disposal!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, handle.Releases);
        Assert.Equal(So6120PortEffect.NotPerformed, port.ReadData().Effect);
        Assert.Single(native.Calls);
    }

    [Fact]
    public void CoreRejectsMalformedReadWhileLedgerRetainsNativeBoolean()
    {
        using var handle = new TrackingHandle();
        var native = new FakeNative { Returned = 2 };
        using var port = new So6120InpOutBytePort(handle, native);
        var result = new So6120HostMailboxTransaction(port, new FakePrerequisites())
            .Read(So6120MailboxRegister.Selector);
        Assert.Equal(So6120MailboxFailure.PortFailure, result.Failure);
        Assert.Equal(0, result.CommandsPossiblySubmitted);
        Assert.Equal(2, native.Calls.Count);
        var observation = result.Operations[^1].Result!;
        Assert.True(observation.Succeeded);
        Assert.Equal(2u, observation.BytesReturned);
        Assert.Equal(So6120PortEffect.MayHavePerformed, observation.Effect);
    }

    [Fact]
    public void LiveAcquisitionIsExplicitlyUnavailable()
    {
        Assert.Throws<NotSupportedException>(So6120InpOutBytePort.Acquire);
    }

    private sealed class FakePrerequisites : ISo6120MailboxPrerequisites
    {
        public bool Prepare(So6120MailboxRequest request) => true;
        public bool AdmitPublication(So6120MailboxRequest request) => true;
    }

    private sealed class TrackingHandle : SafeHandle
    {
        private int releases;
        public int Releases => Volatile.Read(ref releases);
        public TrackingHandle(bool invalid = false) : base(IntPtr.Zero, ownsHandle: true) =>
            SetHandle(invalid ? IntPtr.Zero : new IntPtr(1234));
        public override bool IsInvalid => handle == IntPtr.Zero;
        protected override bool ReleaseHandle()
        {
            Interlocked.Increment(ref releases);
            return true; // Synthetic handle: never calls Windows CloseHandle.
        }
    }

    private sealed record Call(SafeHandle Handle, uint Control, byte[] Input, byte[]? Output);
    private sealed class FakeNative : ISo6120InpOutNative
    {
        public List<Call> Calls { get; } = [];
        public bool Success = true;
        public uint? Returned;
        public int Error;
        public Exception? Exception;
        public Action? Inside;
        public So6120InpOutNativeResult Invoke(SafeHandle handle, uint controlCode, byte[] input, byte[]? output)
        {
            Calls.Add(new(handle, controlCode, input, output));
            Inside?.Invoke();
            if (Exception is not null) { throw Exception; }
            if (output is not null) { output[0] = 0x5A; }
            return new(Success, Returned ?? (output is null ? 10u : 1u), Error);
        }
    }
}
