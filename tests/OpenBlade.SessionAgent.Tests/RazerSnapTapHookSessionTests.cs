using System.Buffers.Binary;
using System.ComponentModel;
using OpenBlade.SessionAgent;

namespace OpenBlade.SessionAgent.Tests;

public sealed class RazerSnapTapHookSessionTests
{
    [Fact]
    public void SubscribesAfterOwningPairAndClosesWithoutGlobalDisableOrClear()
    {
        var transport = new FakeTransport();
        var session = new RazerSnapTapHookSession(transport);
        Assert.True(session.Start());
        Assert.Equal(RazerSnapTapHookSessionState.Active, session.State);
        Assert.True(session.Stop());
        Assert.Equal(RazerSnapTapHookSessionState.Stopped, session.State);
        Assert.Equal(2, session.OwnedHooks.Count);
        Assert.Equal(2, transport.Hooks.Count);
        Assert.Equal(new[]
        {
            "read", "set:30", "read", "set:32", "read", "enable", "read", "close",
        }, transport.Calls);
    }

    [Theory]
    [InlineData(0x1E)]
    [InlineData(0x20)]
    public void RejectsPreexistingPairHookIncludingDifferentSelector(int scanCode)
    {
        var transport = new FakeTransport();
        byte[] existing = RazerSnapTapDriverProtocol.EncodeHook((ushort)scanCode);
        existing[0x10] = 4;
        transport.Hooks.Add(existing);
        var session = new RazerSnapTapHookSession(transport);
        Assert.False(session.Start());
        Assert.False(session.FailureMayHaveSubmitted);
        Assert.Equal(RazerSnapTapHookSessionState.Faulted, session.State);
        Assert.Equal(new[] { "read" }, transport.Calls);
        Assert.Equal(existing, Assert.Single(transport.Hooks));
    }

    [Fact]
    public void StopPreservesUnrelatedAndExternallyChangedRecordsForGuardianReconciliation()
    {
        var transport = new FakeTransport();
        byte[] original = UnrelatedHook(0x2C);
        transport.Hooks.Add(original);
        var session = new RazerSnapTapHookSession(transport);
        Assert.True(session.Start());
        byte[] replacement = UnrelatedHook(0x2C);
        replacement[0x10] = 8;
        transport.Hooks[0] = replacement;
        transport.Hooks[1][0x10] = 4;
        byte[] added = UnrelatedHook(0x2D);
        transport.Hooks.Add(added);
        Assert.True(session.Stop());
        Assert.Equal(4, transport.Hooks.Count);
        Assert.Equal(replacement, transport.Hooks[0]);
        Assert.Equal(4, transport.Hooks[1][0x10]);
        Assert.Equal(added, transport.Hooks[3]);
        Assert.Equal(original, Assert.Single(session.BaselineHooks));
    }

    [Fact]
    public void BaselineAndOwnedSnapshotsCannotBeMutatedByCallers()
    {
        var transport = new FakeTransport();
        byte[] original = UnrelatedHook(0x2C);
        transport.Hooks.Add(original);
        var session = new RazerSnapTapHookSession(transport);
        Assert.True(session.Start());
        session.BaselineHooks[0][0x10] = 0xFF;
        session.OwnedHooks[0][0x10] = 0xFF;
        Assert.Equal(original, Assert.Single(session.BaselineHooks));
        Assert.Equal(RazerSnapTapDriverProtocol.EncodeHook(0x1E), session.OwnedHooks[0]);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    [InlineData(5, true)]
    [InlineData(6, true)]
    [InlineData(7, true)]
    public void SetupFailureLatchesWithoutRollbackButAllowsOneHandleClose(int failureCall, bool ambiguous)
    {
        var transport = new FakeTransport { FailureCall = failureCall };
        var session = new RazerSnapTapHookSession(transport);
        Assert.False(session.Start());
        Assert.Equal(RazerSnapTapHookSessionState.Faulted, session.State);
        Assert.Equal(ambiguous, session.FailureMayHaveSubmitted);
        Assert.NotNull(session.FailureReason);
        Assert.Equal(ambiguous, Assert.IsType<OpenBlade.Contracts.DiagnosticFailure>(session.Failure).MayHaveSubmitted);
        Assert.Equal(failureCall, transport.Calls.Count);
        Assert.False(session.Start());
        Assert.False(session.Stop());
        Assert.False(session.Stop());
        Assert.Equal(failureCall + 1, transport.Calls.Count);
        Assert.Equal("close", transport.Calls[^1]);
        Assert.Equal(RazerSnapTapHookSessionState.Faulted, session.State);
    }

    [Fact]
    public void FailurePreservesNativeInnerCauseWithoutPersistingMessages()
    {
        var transport = new FakeTransport
        {
            FailureCall = 2,
            Failure = new IOException("path must not escape", new Win32Exception(5, "native message")),
        };
        var session = new RazerSnapTapHookSession(transport);

        Assert.False(session.Start());

        OpenBlade.Contracts.DiagnosticFailure failure = Assert.IsType<OpenBlade.Contracts.DiagnosticFailure>(session.Failure);
        Assert.Equal("SnapTapWorker", failure.Component);
        Assert.Equal("HookMutation", failure.Stage);
        Assert.Equal("SetHookFailed", failure.Reason);
        Assert.True(failure.MayHaveSubmitted);
        Assert.Equal(nameof(IOException), failure.ExceptionType);
        Assert.Equal(nameof(Win32Exception), failure.Cause?.ExceptionType);
        Assert.Equal(5, failure.Cause?.NativeErrorCode);
        string serialized = System.Text.Json.JsonSerializer.Serialize(failure);
        Assert.DoesNotContain("path", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("native message", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CleanupFailureWrapsOriginalAmbiguousMutationFailure()
    {
        var transport = new FakeTransport { FailureCalls = new HashSet<int> { 2, 3 } };
        var session = new RazerSnapTapHookSession(transport);

        Assert.False(session.Start());
        Assert.False(session.Stop());

        OpenBlade.Contracts.DiagnosticFailure failure = Assert.IsType<OpenBlade.Contracts.DiagnosticFailure>(session.Failure);
        Assert.Equal("SubscriptionClose", failure.Stage);
        Assert.Equal("CloseFailed", failure.Reason);
        Assert.True(failure.MayHaveSubmitted);
        Assert.Equal("HookMutation", failure.Cause?.Stage);
        Assert.Equal("SetHookFailed", failure.Cause?.Reason);
    }

    [Fact]
    public void CloseFailureLatchesWithoutRetryOrFurtherDriverCalls()
    {
        var transport = new FakeTransport { FailureCall = 8 };
        var session = new RazerSnapTapHookSession(transport);
        Assert.True(session.Start());
        Assert.False(session.Stop());
        Assert.False(session.Stop());
        Assert.False(session.Start());
        Assert.Equal(RazerSnapTapHookSessionState.Faulted, session.State);
        Assert.False(session.FailureMayHaveSubmitted);
        Assert.Equal(8, transport.Calls.Count);
    }

    [Fact]
    public void MissingSetReadbackStopsBeforeSecondHookOrSubscription()
    {
        var transport = new FakeTransport { IgnoreSet = true };
        var session = new RazerSnapTapHookSession(transport);
        Assert.False(session.Start());
        Assert.True(session.FailureMayHaveSubmitted);
        Assert.Equal(new[] { "read", "set:30", "read" }, transport.Calls);
    }

    [Fact]
    public void DuplicateOwnedRecordIsNotMistakenForConfirmedOwnership()
    {
        var transport = new FakeTransport { DuplicateSet = true };
        var session = new RazerSnapTapHookSession(transport);
        Assert.False(session.Start());
        Assert.True(session.FailureMayHaveSubmitted);
        Assert.DoesNotContain("enable", transport.Calls);
    }

    [Fact]
    public void InvalidEnumerationNeverPermitsMutation()
    {
        var transport = new FakeTransport();
        transport.Hooks.Add(new byte[3]);
        var session = new RazerSnapTapHookSession(transport);
        Assert.False(session.Start());
        Assert.False(session.FailureMayHaveSubmitted);
        Assert.Equal(new[] { "read" }, transport.Calls);
    }

    [Theory]
    [InlineData(255)]
    [InlineData(257)]
    public void EnumerationLimitMustAllowBothOwnedHooksBeforeAnyMutation(int count)
    {
        var transport = new FakeTransport();
        transport.Hooks.AddRange(Enumerable.Range(0, count).Select(_ => UnrelatedHook(0x2C)));
        var session = new RazerSnapTapHookSession(transport);
        Assert.False(session.Start());
        Assert.Equal(new[] { "read" }, transport.Calls);
    }

    [Fact]
    public void UnusedSessionClosesAndSuccessfulOperationsAreIdempotent()
    {
        var unusedTransport = new FakeTransport();
        var unused = new RazerSnapTapHookSession(unusedTransport);
        Assert.True(unused.Stop());
        Assert.True(unused.Stop());
        Assert.False(unused.Start());
        Assert.Equal(new[] { "close" }, unusedTransport.Calls);

        var transport = new FakeTransport();
        var session = new RazerSnapTapHookSession(transport);
        Assert.True(session.Start());
        Assert.True(session.Start());
        Assert.True(session.Stop());
        Assert.True(session.Stop());
        Assert.Equal(8, transport.Calls.Count);
    }

    private static byte[] UnrelatedHook(ushort scanCode)
    {
        byte[] hook = RazerSnapTapDriverProtocol.EncodeHook(0x1E);
        BinaryPrimitives.WriteUInt16LittleEndian(hook.AsSpan(0x0A), scanCode);
        return hook;
    }

    private sealed class FakeTransport : IRazerSnapTapHookTransport
    {
        internal List<byte[]> Hooks { get; } = [];
        internal List<string> Calls { get; } = [];
        internal int FailureCall { get; init; }
        internal IReadOnlySet<int>? FailureCalls { get; init; }
        internal Exception? Failure { get; init; }
        internal bool IgnoreSet { get; init; }
        internal bool DuplicateSet { get; init; }

        public IReadOnlyList<byte[]> ReadHooks()
        {
            Record("read");
            return Hooks.Select(record => (byte[])record.Clone()).ToArray();
        }

        public void SetHook(byte[] hook)
        {
            if (!IgnoreSet)
            {
                Hooks.Add((byte[])hook.Clone());
                if (DuplicateSet)
                {
                    Hooks.Add((byte[])hook.Clone());
                }
            }
            // Throw after the side effect to exercise possibly submitted failures.
            Record($"set:{BinaryPrimitives.ReadUInt16LittleEndian(hook.AsSpan(0x0A))}");
        }

        public void ClearHook(byte[] hook) => throw new InvalidOperationException("Only the independent guardian may clear hooks.");

        public void SetHookEnabled(bool enabled)
        {
            Assert.True(enabled);
            Record("enable");
        }

        public void CloseSubscription() => Record("close");

        private void Record(string call)
        {
            Calls.Add(call);
            if (Calls.Count == FailureCall || FailureCalls?.Contains(Calls.Count) == true)
            {
                throw Failure ?? new IOException("Injected transport failure after possible submission.");
            }
        }
    }
}
