using System.Text;
using OpenBlade.Capture;

namespace OpenBlade.Capture.Tests;

public sealed class CpuPmLiveParentAuthorityTests
{
    private static readonly Guid Run = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Lifetime = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly CpuPmAuthorityPeer Parent = new(31, 100, @"C:\pinned\parent.exe", new('A', 64));
    private static readonly CpuPmAuthorityPeer Child = new(32, 200, @"C:\pinned\child.exe", new('B', 64));
    private static CpuPmOwnedDriverProof Proof() => new(Run, Lifetime, true, true, 1, true, true, true, true, true, true, true);
    private static CpuPmAuthorityMessage Request(int counter = 1, char nonce = 'a') => new(Run, Lifetime, counter, new(nonce, 64), null);

    [Fact]
    public async Task EachPositiveRequiresNewPeerChecksNonceAndActualOwnerCallback()
    {
        var clock = new Clock(); var peer = new Peer(Child); var proof = new Source();
        var server = new CpuPmParentAuthoritySession(Run, Lifetime, Child, peer, proof, clock);
        var channel = new Channel(request => server.Handle(request, CancellationToken.None)!);
        using var reader = new CpuPmLiveParentAuthority(Run, Lifetime, Parent, channel, clock);
        Assert.True(await reader.ReadFreshAsync(CancellationToken.None));
        Assert.True(await reader.ReadFreshAsync(CancellationToken.None));
        Assert.Equal(2, proof.Reads); Assert.Equal(4, peer.Reads); Assert.Equal(4, channel.PeerReads);
        Assert.Equal([1, 2], channel.Requests.Select(r => r.Counter));
        Assert.NotEqual(channel.Requests[0].Nonce, channel.Requests[1].Nonce);
    }

    [Theory]
    [InlineData("pid")]
    [InlineData("start")]
    [InlineData("image")]
    [InlineData("hash")]
    [InlineData("lost")]
    public async Task WrongOrLostRetainedParentRefusesBeforeRequestAndNeverRecovers(string fault)
    {
        var channel = new Channel(Reply) { Peer = Changed(Parent, fault) };
        using var reader = new CpuPmLiveParentAuthority(Run, Lifetime, Parent, channel, new Clock());
        Assert.False(await reader.ReadFreshAsync(CancellationToken.None));
        channel.Peer = Parent;
        Assert.False(await reader.ReadFreshAsync(CancellationToken.None));
        Assert.Empty(channel.Requests); Assert.True(reader.IsRevoked);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("lifetime")]
    [InlineData("nonce")]
    [InlineData("counter")]
    [InlineData("inactive")]
    [InlineData("malformed")]
    [InlineData("oversized")]
    [InlineData("pipe-lost")]
    [InlineData("peer-after")]
    public async Task ForgedStaleAndLostRepliesPermanentlyRevoke(string fault)
    {
        var channel = new Channel(Reply);
        channel.Respond = bytes =>
        {
            var request = CpuPmAuthorityProtocol.Decode(bytes, false)!;
            if (fault == "pipe-lost") { throw new IOException(); }
            if (fault == "malformed") { return Encoding.UTF8.GetBytes("{}"); }
            if (fault == "oversized") { return new byte[4097]; }
            if (fault == "peer-after") { channel.Peer = null; }
            var response = request with { Active = fault != "inactive" };
            response = fault switch
            {
                "run" => response with { RunId = Lifetime },
                "lifetime" => response with { LifetimeId = Run },
                "nonce" => response with { Nonce = new('0', 64) },
                "counter" => response with { Counter = 2 },
                _ => response,
            };
            return CpuPmAuthorityProtocol.Encode(response);
        };
        using var reader = new CpuPmLiveParentAuthority(Run, Lifetime, Parent, channel, new Clock());
        Assert.False(await reader.ReadFreshAsync(CancellationToken.None));
        channel.Peer = Parent; channel.Respond = Reply;
        Assert.False(await reader.ReadFreshAsync(CancellationToken.None));
        Assert.Single(channel.Requests);
    }

    [Theory]
    [InlineData("request-expired")]
    [InlineData("session-expired")]
    [InlineData("regression")]
    [InlineData("canceled")]
    public async Task DeadlineIncludesCallbackAndPeerVerificationTime(string fault)
    {
        var clock = new Clock { Now = TimeSpan.FromSeconds(1) };
        using var cancel = new CancellationTokenSource();
        var channel = new Channel(request =>
        {
            if (fault == "canceled") { cancel.Cancel(); }
            clock.Now = fault switch { "request-expired" => TimeSpan.FromSeconds(3), "session-expired" => TimeSpan.FromSeconds(91), "regression" => TimeSpan.Zero, _ => clock.Now };
            return Reply(request);
        });
        using var reader = new CpuPmLiveParentAuthority(Run, Lifetime, Parent, channel, clock);
        Assert.False(await reader.ReadFreshAsync(cancel.Token)); Assert.True(reader.IsRevoked);
    }

    [Fact]
    public async Task ReplayedPreviousPositiveIsNotFresh()
    {
        byte[]? saved = null;
        var channel = new Channel(request => saved ??= Reply(request));
        using var reader = new CpuPmLiveParentAuthority(Run, Lifetime, Parent, channel, new Clock());
        Assert.True(await reader.ReadFreshAsync(CancellationToken.None));
        Assert.False(await reader.ReadFreshAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SlowPeerCheckCannotStartRequestOrAcceptReply(bool afterReply)
    {
        var clock = new Clock(); var channel = new Channel(Reply);
        channel.OnPeerRead = () => { if (channel.PeerReads == (afterReply ? 2 : 1)) { clock.Now += TimeSpan.FromSeconds(2); } };
        using var reader = new CpuPmLiveParentAuthority(Run, Lifetime, Parent, channel, clock);
        Assert.False(await reader.ReadFreshAsync(CancellationToken.None));
        Assert.Equal(afterReply ? 1 : 0, channel.Requests.Count);
    }

    [Fact]
    public async Task DisposeAndCanceledAttemptNeverSend()
    {
        var channel = new Channel(Reply);
        var reader = new CpuPmLiveParentAuthority(Run, Lifetime, Parent, channel, new Clock());
        reader.Dispose(); Assert.True(channel.Disposed);
        Assert.False(await reader.ReadFreshAsync(CancellationToken.None)); Assert.Empty(channel.Requests);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        using var second = new CpuPmLiveParentAuthority(Run, Lifetime, Parent, new Channel(Reply), new Clock());
        Assert.False(await second.ReadFreshAsync(canceled.Token));
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("create-handle")]
    [InlineData("start-count")]
    [InlineData("stopped")]
    [InlineData("type")]
    [InlineData("config")]
    [InlineData("tag")]
    [InlineData("pins")]
    [InlineData("continuity")]
    [InlineData("premise")]
    [InlineData("run")]
    [InlineData("lifetime")]
    [InlineData("provider-missing")]
    public void ParentCannotPromoteIncompleteOrDriftingOwnership(string fault)
    {
        var proof = Proof();
        proof = fault switch
        {
            "absent" => proof with { AbsenceObservedBeforeCreate = false },
            "create-handle" => proof with { RetainedCreatedHandle = false },
            "start-count" => proof with { OwnSuccessfulStarts = 2 },
            "stopped" => proof with { Running = false },
            "type" => proof with { DemandStartKernelDriver = false },
            "config" => proof with { OriginalConfiguration = false },
            "tag" => proof with { OwnTagMatches = false },
            "pins" => proof with { VendorAndPayloadPinsHeld = false },
            "continuity" => proof with { OwnerContinuity = false },
            "premise" => proof with { TrustedOperatorPremiseAccepted = false },
            "run" => proof with { RunId = Lifetime },
            "lifetime" => proof with { LifetimeId = Run },
            _ => proof,
        };
        var source = new Source { Current = proof };
        var server = new CpuPmParentAuthoritySession(Run, Lifetime, Child, new Peer(Child), fault == "provider-missing" ? null : source, new Clock());
        Assert.Null(server.Handle(CpuPmAuthorityProtocol.Encode(Request()), CancellationToken.None));
        source.Current = Proof();
        Assert.Null(server.Handle(CpuPmAuthorityProtocol.Encode(Request(2, 'b')), CancellationToken.None));
    }

    [Theory]
    [InlineData("pid")]
    [InlineData("start")]
    [InlineData("image")]
    [InlineData("hash")]
    [InlineData("lost")]
    public void ParentBindsRetainedChildBeforeReadingDriverProof(string fault)
    {
        var source = new Source();
        var server = new CpuPmParentAuthoritySession(Run, Lifetime, Child, new Peer(Changed(Child, fault)), source, new Clock());
        Assert.Null(server.Handle(CpuPmAuthorityProtocol.Encode(Request()), CancellationToken.None)); Assert.Equal(0, source.Reads);
    }

    [Theory]
    [InlineData("nonce")]
    [InlineData("counter")]
    [InlineData("tag-after-first")]
    [InlineData("slow-owner")]
    [InlineData("peer-after")]
    public void ParentRejectsReplayAndChecksProofAndPeerAgain(string fault)
    {
        var clock = new Clock(); var peer = new Peer(Child); var source = new Source();
        var server = new CpuPmParentAuthoritySession(Run, Lifetime, Child, peer, source, clock);
        Assert.NotNull(server.Handle(CpuPmAuthorityProtocol.Encode(Request()), CancellationToken.None));
        if (fault == "tag-after-first") { source.Current = Proof() with { OwnTagMatches = false }; }
        if (fault == "slow-owner") { source.OnRead = () => clock.Now += TimeSpan.FromSeconds(2); }
        if (fault == "peer-after") { source.OnRead = () => peer.Current = null; }
        var request = Request(fault == "counter" ? 1 : 2, fault == "nonce" ? 'a' : 'b');
        Assert.Null(server.Handle(CpuPmAuthorityProtocol.Encode(request), CancellationToken.None));
    }

    [Theory]
    [InlineData("run")]
    [InlineData("lifetime")]
    [InlineData("sequence")]
    [InlineData("revoked")]
    [InlineData("exception")]
    public void ParentRefusesWrongRequestAndCannotRecoverAfterTerminalFailure(string fault)
    {
        var source = new Source();
        var server = new CpuPmParentAuthoritySession(Run, Lifetime, Child, new Peer(Child), source, new Clock());
        if (fault == "revoked") { server.Revoke(); }
        if (fault == "exception") { source.OnRead = () => throw new IOException(); }
        var request = fault switch { "run" => Request() with { RunId = Lifetime }, "lifetime" => Request() with { LifetimeId = Run }, "sequence" => Request(2), _ => Request() };
        Assert.Null(server.Handle(CpuPmAuthorityProtocol.Encode(request), CancellationToken.None));
        source.OnRead = null;
        Assert.Null(server.Handle(CpuPmAuthorityProtocol.Encode(Request()), CancellationToken.None));
    }

    [Fact]
    public void UnexpectedOwnerExceptionCannotResumeAtNextCounter()
    {
        var source = new Source { OnRead = () => throw new FormatException("private") };
        var server = new CpuPmParentAuthoritySession(Run, Lifetime, Child, new Peer(Child), source, new Clock());
        Assert.Null(server.Handle(CpuPmAuthorityProtocol.Encode(Request()), CancellationToken.None));
        source.OnRead = null;
        Assert.Null(server.Handle(CpuPmAuthorityProtocol.Encode(Request(2, 'b')), CancellationToken.None));
        Assert.Equal(1, source.Reads);
    }

    [Fact]
    public async Task UnexpectedPeerOrTransportExceptionCannotResume()
    {
        var channel = new Channel(_ => throw new FormatException("private"));
        using var reader = new CpuPmLiveParentAuthority(Run, Lifetime, Parent, channel, new Clock());
        Assert.False(await reader.ReadFreshAsync(CancellationToken.None));
        channel.Respond = Reply;
        Assert.False(await reader.ReadFreshAsync(CancellationToken.None)); Assert.Single(channel.Requests);
        var peer = new Peer(Child) { OnRead = () => throw new FormatException("private") };
        var source = new Source();
        var server = new CpuPmParentAuthoritySession(Run, Lifetime, Child, peer, source, new Clock());
        Assert.Null(server.Handle(CpuPmAuthorityProtocol.Encode(Request()), CancellationToken.None));
        peer.OnRead = null;
        Assert.Null(server.Handle(CpuPmAuthorityProtocol.Encode(Request()), CancellationToken.None)); Assert.Equal(0, source.Reads);
    }

    [Fact]
    public void WireSchemaRejectsDuplicatesUnknownPropertiesAndNoncanonicalOrOverboundValues()
    {
        string valid = Encoding.UTF8.GetString(CpuPmAuthorityProtocol.Encode(Request()));
        string[] invalid = [valid.Replace("\"version\":1", "\"version\":1,\"version\":1"), valid.Replace("\"version\":1", "\"other\":1"),
            valid.Replace("\"version\":1", "\"version\":1.0"), valid.Replace("\"counter\":1", "\"counter\":131073"),
            valid.Replace(new string('a', 64), new string('A', 64)), valid.Replace("\"counter\":1", "\"counter\":0"),
            valid[..^1] + ",\"active\":true}", "[]", "null", valid + "{}"];
        foreach (string text in invalid) { Assert.Null(CpuPmAuthorityProtocol.Decode(Encoding.UTF8.GetBytes(text), false)); }
        Assert.Null(CpuPmAuthorityProtocol.Decode(new byte[4097], false));
        Assert.NotNull(CpuPmAuthorityProtocol.Decode(CpuPmAuthorityProtocol.Encode(Request(CpuPmAuthorityProtocol.MaximumRequests)), false));
        Assert.Throws<ArgumentException>(() => CpuPmAuthorityProtocol.PipeName(Guid.Empty));
    }

    private static byte[] Reply(byte[] bytes) => CpuPmAuthorityProtocol.Encode(CpuPmAuthorityProtocol.Decode(bytes, false)! with { Active = true });
    private static CpuPmAuthorityPeer? Changed(CpuPmAuthorityPeer peer, string fault) => fault switch
    { "pid" => peer with { ProcessId = peer.ProcessId + 1 }, "start" => peer with { CreationFileTimeUtc = peer.CreationFileTimeUtc + 1 }, "image" => peer with { ExecutablePath = @"C:\forged.exe" }, "hash" => peer with { ExecutableSha256 = new('C', 64) }, _ => null };
    private sealed class Clock : ICpuPmAuthorityClock { public TimeSpan Now { get; set; } }
    private sealed class Peer(CpuPmAuthorityPeer? current) : ICpuPmAuthorityPeerReader
    { internal CpuPmAuthorityPeer? Current = current; internal int Reads; internal Action? OnRead; public CpuPmAuthorityPeer? ReadFreshPeer() { Reads++; OnRead?.Invoke(); return Current; } }
    private sealed class Source : ICpuPmOwnedDriverProofSource
    { internal CpuPmOwnedDriverProof? Current = Proof(); internal int Reads; internal Action? OnRead; public CpuPmOwnedDriverProof? ReadFresh(Guid runId, Guid lifetimeId) { Reads++; OnRead?.Invoke(); return Current; } }
    private sealed class Channel(Func<byte[], byte[]> respond) : ICpuPmAuthorityChannel
    {
        internal Func<byte[], byte[]> Respond = respond; internal CpuPmAuthorityPeer? Peer = Parent;
        internal readonly List<CpuPmAuthorityMessage> Requests = []; internal int PeerReads; internal bool Disposed; internal Action? OnPeerRead;
        public CpuPmAuthorityPeer? ReadFreshPeer() { PeerReads++; OnPeerRead?.Invoke(); return Peer; }
        public Task<byte[]> ExchangeAsync(byte[] request, CancellationToken token)
        { Requests.Add(CpuPmAuthorityProtocol.Decode(request, false)!); return Task.FromResult(Respond(request)); }
        public void Dispose() => Disposed = true;
    }
}
