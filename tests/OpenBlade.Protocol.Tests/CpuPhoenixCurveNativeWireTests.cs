using System.Buffers.Binary;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class CpuPhoenixCurveNativeWireTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FullVectorAndPowerPeersAreVerifiedBeforeAndAfterOneSetterAndExactRestore(bool blade2023)
    {
        var platform = new Platform { Contract = blade2023 ? CpuLiveCurveContract.Rz090482 : CpuLiveCurveContract.Rz090508 };
        var original = platform.Offsets.ToArray();
        var request = Request(platform);
        var result = Run(platform, request, new Proof(request));
        Assert.True(result.Verified);
        Assert.Equal(CpuLiveCurveNativeDisposition.Acknowledged, result.Disposition);
        Assert.Equal(1, platform.Setters);
        Assert.Equal(16, platform.Commands.Count(command => command == 0xE1));
        Assert.Equal(2, platform.Copies);
        Assert.Equal(2, platform.Commands.Count(command => command == 0x82));
        Assert.Equal(0x82u, platform.Commands[1]);
        int setter = platform.Commands.IndexOf(0x53);
        Assert.Equal(0x82u, platform.Commands[setter + 2]);
        Assert.DoesNotContain(0x5Eu, platform.Commands);
        Assert.DoesNotContain(2u, platform.Commands);
        Assert.DoesNotContain(0x03820A0Fu, platform.Selected);
        Assert.DoesNotContain(0x03820A0Bu, platform.Selected);
        Assert.Equal(3u << 20, platform.LastSetterArgument);
        var reverse = new CpuLiveCurveNativeRequest(request.Scope, request.ImmediateReverse,
            CpuLiveCurveCommandPurpose.Restore, result.After!, request.PowerPeer);
        Assert.True(Run(platform, reverse, new Proof(reverse)).Verified);
        Assert.Equal(original, platform.Offsets);
        Assert.Equal(2, platform.Setters);
        Assert.False(platform.Held);
        Assert.False(platform.Pinned);
    }

    [Theory]
    [InlineData("capability", false)]
    [InlineData("before-peer", false)]
    [InlineData("capability-after", true)]
    [InlineData("after-peer", true)]
    [InlineData("native-false", true)]
    [InlineData("curve-drift", true)]
    public void FailureStopsTheExactSequenceWithoutAnotherSetter(string fault, bool submitted)
    {
        var platform = new Platform { Fault = fault };
        var request = Request(platform);
        var result = Run(platform, request, new Proof(request));
        Assert.False(result.Verified);
        Assert.Equal(submitted ? 1 : 0, platform.Setters);
        Assert.Equal(submitted, result.Disposition != CpuLiveCurveNativeDisposition.NotSubmitted);
        if (fault == "capability") { Assert.DoesNotContain(0xE1u, platform.Commands); }
        if (fault == "capability-after") { Assert.Equal(8, platform.Commands.Count(command => command == 0xE1)); }
        Assert.True(result.CleanupComplete);
        Assert.False(platform.Held);
        Assert.False(platform.Pinned);
    }

    [Fact]
    public void RejectedFinalProofCannotSubmitAndMissingPeerCannotOpenDevice()
    {
        var platform = new Platform();
        var request = Request(platform);
        Assert.Equal(CpuLiveCurveNativeDisposition.NotSubmitted,
            Run(platform, request, new Proof(request) { AllowPeer = false }).Disposition);
        Assert.Equal(0, platform.Setters);
        int opened = platform.Opens;
        var missing = new CpuLiveCurveNativeRequest(request.Scope, request.Command, request.Purpose, request.Before);
        Assert.Equal(CpuLiveCurveNativeDisposition.NotSubmitted, Run(platform, missing, new Proof(missing)).Disposition);
        Assert.Equal(opened, platform.Opens);
    }

    private static CpuLiveCurveNativeResult Run(Platform platform, CpuLiveCurveNativeRequest request, Proof proof) =>
        WindowsRz090528CpuQueryAdapter.ExecuteCurveWithPlatform(request, proof, () => new(platform.Contract.ModelNumber, 0x1532, platform.Contract.ProductId, "source-provenance"), platform);

    private static CpuLiveCurveNativeRequest Request(Platform platform)
    {
        var contract = platform.Contract;
        var before = CpuLiveCurveSnapshot.TryCreate(contract, new(0xFF, 0x004C0008, 0xAF0,
            platform.Offsets.Select((value, id) => new CpuLiveCoreOffset(id, value)).ToArray()))!;
        Assert.True(CpuLiveCurveCommand.TryCreate(contract, 3, 0, out var command));
        return new(new(), command, CpuLiveCurveCommandPurpose.Forward, before, new(platform.Pair, null));
    }

    private sealed class Proof(CpuLiveCurveNativeRequest request) : CpuLiveCurveWriteProof(request)
    {
        internal bool AllowPeer = true;
        internal override bool IsCurrent(CpuLiveCurveNativeRequest exactRequest) => true;
        internal override CpuLiveCurveTrustedRange? ReadFreshRange(CpuLiveCurveNativeRequest exactRequest) => new(0x5000A, 0, 30, 1, true);
        internal override bool AdmitUncappedPowerPeer(CpuLiveCurveNativeRequest exactRequest, uint capability) =>
            AllowPeer && exactRequest.PowerPeer is not null && (capability & 4) != 0;
    }

    private sealed class Platform : ICpuQueryWindowsPlatform, ICpuQueryDevice, ICpuQueryMutex
    {
        public CpuLiveCurveContract Contract { get; init; } = CpuLiveCurveContract.Rz090508;
        internal readonly short[] Offsets = Enumerable.Range(0, 8).Select(id => (short)(-id - 1)).ToArray();
        internal readonly List<uint> Commands = [], Selected = [];
        internal readonly CpuLivePowerPair Pair;
        internal string? Fault;
        internal bool Held, Pinned;
        internal int Setters, Copies, Opens;
        internal uint LastSetterArgument;
        private uint selected, argument, replyValue;
        internal Platform()
        {
            Assert.True(CpuLivePowerValue.TryFromMilliwatts(65000, out var fast));
            Assert.True(CpuLivePowerValue.TryFromMilliwatts(54000, out var slow));
            Assert.True(CpuLivePowerValue.TryFromMilliwatts(45000, out var stapm));
            Pair = new(fast, slow, stapm.RawBits);
        }
        public HostIdentityReadResult ReadHost() => new(new("Razer", Contract.ProductName, "variant", "1.06"), null);
        public CpuQueryProcessorIdentity ReadProcessor() => new("AuthenticAMD", 0x19, Contract == CpuLiveCurveContract.Rz090482 ? 0x74 : 0x75, 2);
        public bool ControllersAbsent() => true;
        public IDisposable PinBackendFiles() { Pinned = true; return new Cleanup(() => Pinned = false); }
        public InstalledCpuBackendSnapshot ReadBackend()
        {
            var profile = CpuInstalledBackendProfile.ForContract(Contract);
            return new(profile.ExpectedHashes.Select(pair => new CpuBackendFileMetadata(pair.Key, pair.Value, true, profile.SignerThumbprint)).ToArray(),
                true, @"\??\" + profile.DriverPath, 1, 3, true);
        }
        public ICpuQueryDevice OpenExistingDevice() { Opens++; return this; }
        public ICpuQueryMutex CreateSharedMutex() => this;
        public CpuQueryMutexWait Wait(TimeSpan timeout, CancellationToken token) { Assert.False(Held); Held = true; return CpuQueryMutexWait.Acquired; }
        public void Release() { Assert.True(Held); Held = false; }
        public void Dispose() { }
        public CpuQueryIoctlReply Invoke(uint code, byte[] input, int outputLength)
        {
            Assert.True(Held); Assert.True(Pinned);
            var output = new byte[outputLength];
            if (code == 0x81112F18)
            {
                var records = CpuQueryPciTestRecords.Create(null);
                for (int i = 28; i < records.Length; i += 52)
                { Put(records, i + 12, Word(records, i + 12) switch { 0x15071022 => 0x14E81022, 0x16F81022 => 0x14F01022, 0x16F91022 => 0x14F11022, var id => id }); }
                Put(output, 24, Word(records, 24));
                if (outputLength > 28) { records.CopyTo(output, 0); }
            }
            else if (code == 0x81112F24)
            {
                input.CopyTo(output, 0);
                uint offset = Word(input, 12);
                uint value = offset == 0 ? Word(input, 4) == 0 ? 0x14E81022u : Word(input, 8) == 0 ? 0x14F01022u : 0x14F11022u
                    : offset == 0x140 ? 0xABCD00FFu : selected == 0x005D528 ? 0u
                    : selected == 0x03B10A80 ? 1u : replyValue;
                Put(output, 20, value);
            }
            else if (code == 0x81112FF8)
            {
                uint value = Word(input, 4);
                if (Word(input, 0) == 0) { selected = value; Selected.Add(value); }
                else if (selected == 0x03B10A88) { argument = value; }
                else if (selected == 0x03B10A20)
                {
                    Commands.Add(value);
                    if (value == 0x53)
                    {
                        Setters++; LastSetterArgument = argument;
                        Offsets[(int)(argument >> 20)] = unchecked((short)argument);
                        if (Fault == "curve-drift") { Offsets[7] = -29; }
                        if (Fault == "native-false") { return new(false, []); }
                    }
                    replyValue = value switch
                    {
                        6 => 0x004C0008,
                        0x82 => Fault == "capability" || Fault == "capability-after" && Setters > 0 ? 0u : 4u,
                        0xE1 => unchecked((ushort)Offsets[(int)argument]),
                        0x65 => 0,
                        0x53 => 0,
                        _ => throw new InvalidOperationException("Unexpected CPU command."),
                    };
                }
            }
            else if (code == 0x81112F08)
            {
                Assert.Equal(0xAF0u, Word(input, 0)); Assert.Equal(0xAF4, outputLength);
                Copies++; Put(output, 0, 0xAF0);
                Put(output, 4, Pair.StapmRawBits); Put(output, 12, Pair.Fast.RawBits); Put(output, 20, Pair.Slow.RawBits);
                if (Fault == "before-peer" && Setters == 0 || Fault == "after-peer" && Setters > 0)
                { Put(output, 12, BitConverter.SingleToUInt32Bits(64)); }
            }
            else { throw new InvalidOperationException("Unexpected CPU IOCTL."); }
            return new(true, output);
        }
    }
    private sealed class Cleanup(Action action) : IDisposable { public void Dispose() => action(); }
    private static uint Word(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
    private static void Put(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
}
