using System.Buffers.Binary;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

internal static class CpuPowerTestData
{
    internal sealed class MemoryStore : ICpuLivePowerOwnershipJournalStore
    {
        internal CpuLivePowerOwnershipJournal? Journal;
        internal bool Invalid;
        internal string? Hash => Journal is null ? null : "legacy";
        public Task<CpuLivePowerJournalLoadResult> LoadAsync(CancellationToken token) => Task.FromResult(new CpuLivePowerJournalLoadResult(Journal, Invalid, Hash));
    }
    internal sealed class FakeBackend : ICpuLivePowerBackend
    {
        internal CpuLivePowerContext Context = ContextFixture();
        internal CpuLivePowerPair Pair = PairFixture();
        internal CpuLivePowerFusedCap? Cap = CpuLivePowerFusedCap.TryDecode(4, 7u << 28);
        internal CpuLivePowerCapability? Capability = CpuLivePowerCapability.TryDecode(2);
        internal CpuLivePowerTrustedRange? Range = new(0x50001, 0, 125000, 1);
        internal int Reads;
        internal bool Unavailable;
        internal string? ObservationFailure;
        internal Action? OnObserve;
        internal Func<CancellationToken, Task<CpuLivePowerBackendObservation>>? ObserveResult;
        public Task<CpuLivePowerBackendObservation> ObserveAsync(CancellationToken token)
        { Reads++; OnObserve?.Invoke(); return ObserveResult?.Invoke(token) ?? Task.FromResult(new CpuLivePowerBackendObservation(Context, Unavailable ? null : Raw(Pair), Cap, Capability, Range)); }
        public Task<CpuLivePowerBackendObservation> ObserveAsync(CancellationToken token, CpuSavedAdmissionDiagnostic? diagnostic)
        {
            if (ObservationFailure is { } failure) { diagnostic?.Check(false, failure); }
            return ObserveAsync(token);
        }
    }

    internal static CpuLivePowerOwnershipJournal Legacy(CpuLivePowerOwnershipState state)
    {
        var original = PairFixture();
        var target = PairFixture(90000, original.Slow.Milliwatts);
        bool restored = state == CpuLivePowerOwnershipState.Restored;
        bool completed = state == CpuLivePowerOwnershipState.OwnedVerified;
        var expected = completed ? target : original;
        return new(1, Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"), ContextFixture(),
            CpuLivePowerStoredPair.From(original), CpuLivePowerStoredPair.From(original),
            restored ? [] : [new(CpuLivePowerLimit.Fast, CpuLivePowerStoredValue.From(original.Fast), CpuLivePowerStoredValue.From(target.Fast))],
            CpuLivePowerStoredPair.From(expected), state, completed ? 1 : 0, restored, DateTimeOffset.UnixEpoch);
    }
    internal static CpuLivePowerContext ContextFixture()
    {
        var context = CpuLiveCurveTuningTransactionTests.ContextFixture();
        return new(context.Identity, context.EnabledCoreMask, context.PmVersion, context.PmTableLength, context.Backend, context.BootSequence);
    }
    internal static uint Bits(uint value) => BitConverter.SingleToUInt32Bits((float)value * 0.001f);
    internal static CpuLivePowerPair PairFixture(uint fast = 100123, uint slow = 80765)
    {
        Assert.True(CpuLivePowerValue.TryFromMilliwatts(fast, out var f)); Assert.True(CpuLivePowerValue.TryFromMilliwatts(slow, out var s));
        return new(f, s, Bits(60000));
    }
    internal static CpuPmRawSnapshot Raw(CpuLivePowerPair pair)
    {
        var reply = new byte[0xD54];
        BinaryPrimitives.WriteUInt32LittleEndian(reply, 0xD50);
        BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(4), pair.StapmRawBits);
        BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(12), pair.Fast.RawBits);
        BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(20), pair.Slow.RawBits);
        Assert.True(CpuPmRawSnapshot.TryCreate(0x005D000B, new(true, reply), DateTimeOffset.UtcNow, 123, out var raw));
        return raw!;
    }
}
