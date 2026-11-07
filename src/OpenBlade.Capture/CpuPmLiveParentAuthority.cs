using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace OpenBlade.Capture;

internal sealed record CpuPmAuthorityPeer(int ProcessId, long CreationFileTimeUtc, string ExecutablePath, string ExecutableSha256)
{
    internal bool IsValid => ProcessId > 0 && CreationFileTimeUtc > 0 && !string.IsNullOrEmpty(ExecutablePath) && Path.IsPathFullyQualified(ExecutablePath)
        && ExecutableSha256 is { Length: 64 } && ExecutableSha256.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
}

internal interface ICpuPmAuthorityPeerReader { CpuPmAuthorityPeer? ReadFreshPeer(); }
internal interface ICpuPmAuthorityChannel : ICpuPmAuthorityPeerReader, IDisposable
{
    Task<byte[]> ExchangeAsync(byte[] request, CancellationToken token);
}
internal interface ICpuPmAuthorityClock { TimeSpan Now { get; } }
internal sealed class CpuPmAuthorityClock : ICpuPmAuthorityClock
{
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    public TimeSpan Now => elapsed.Elapsed;
}

// This callback must belong to the actual still-live lifecycle owner. There is
// deliberately no native implementation or metadata-to-positive conversion here.
// Fresh SCM/tag/pin checks and a retained create handle are required at every call.
internal interface ICpuPmOwnedDriverProofSource
{
    CpuPmOwnedDriverProof? ReadFresh(Guid runId, Guid lifetimeId);
}
internal sealed record CpuPmOwnedDriverProof(Guid RunId, Guid LifetimeId, bool AbsenceObservedBeforeCreate,
    bool RetainedCreatedHandle, int OwnSuccessfulStarts, bool Running, bool DemandStartKernelDriver,
    bool OriginalConfiguration, bool OwnTagMatches, bool VendorAndPayloadPinsHeld, bool OwnerContinuity,
    bool TrustedOperatorPremiseAccepted)
{
    internal bool Admits(Guid run, Guid lifetime) => RunId == run && LifetimeId == lifetime
        && AbsenceObservedBeforeCreate && RetainedCreatedHandle && OwnSuccessfulStarts == 1 && Running
        && DemandStartKernelDriver && OriginalConfiguration && OwnTagMatches && VendorAndPayloadPinsHeld
        && OwnerContinuity && TrustedOperatorPremiseAccepted;
}

internal sealed record CpuPmAuthorityMessage(Guid RunId, Guid LifetimeId, int Counter, string Nonce, bool? Active);
internal static class CpuPmAuthorityProtocol
{
    internal const int MaximumFrameBytes = 4096;
    // Nine mailboxes: 18,101 maximum exchanges * four lifetime reads, plus
    // 17,982 unsuccessful polls * two reads = 108,368; setup/checkpoints < 512.
    // This ceiling does not extend the fixed engine's 90-second budget.
    internal const int MaximumRequests = 131072;
    internal static readonly TimeSpan RequestBudget = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan SessionBudget = TimeSpan.FromSeconds(90);
    internal static string PipeName(Guid runId) => runId != Guid.Empty
        ? "OpenBlade.Private.PmAuthority." + runId.ToString("N") : throw new ArgumentException("Run ID required.");
    internal static byte[] Encode(CpuPmAuthorityMessage message)
    {
        byte[] bytes = message.Active.HasValue
            ? JsonSerializer.SerializeToUtf8Bytes(new { version = 1, run = message.RunId.ToString("N"), lifetime = message.LifetimeId.ToString("N"), counter = message.Counter, nonce = message.Nonce, active = message.Active.Value })
            : JsonSerializer.SerializeToUtf8Bytes(new { version = 1, run = message.RunId.ToString("N"), lifetime = message.LifetimeId.ToString("N"), counter = message.Counter, nonce = message.Nonce });
        if (Decode(bytes, message.Active.HasValue) is null) { throw new ArgumentException("Invalid authority message."); }
        return bytes;
    }
    internal static CpuPmAuthorityMessage? Decode(byte[]? bytes, bool response)
    {
        if (bytes is null || bytes.Length is 0 or > MaximumFrameBytes) { return null; }
        try
        {
            using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 2 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { return null; }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!seen.Add(property.Name) || property.Name is not ("version" or "run" or "lifetime" or "counter" or "nonce" or "active")) { return null; }
            }
            if (seen.Count != (response ? 6 : 5) || seen.Contains("active") != response
                || !root.GetProperty("version").TryGetInt32(out int version) || version != 1
                || !root.GetProperty("counter").TryGetInt32(out int counter) || counter is < 1 or > MaximumRequests) { return null; }
            string? run = root.GetProperty("run").GetString(), lifetime = root.GetProperty("lifetime").GetString(), nonce = root.GetProperty("nonce").GetString();
            if (!Guid.TryParseExact(run, "N", out var runId) || runId == Guid.Empty || runId.ToString("N") != run
                || !Guid.TryParseExact(lifetime, "N", out var lifetimeId) || lifetimeId == Guid.Empty || lifetimeId.ToString("N") != lifetime
                || nonce is not { Length: 64 } || nonce.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))) { return null; }
            return new(runId, lifetimeId, counter, nonce, response ? root.GetProperty("active").GetBoolean() : null);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException) { return null; }
    }
}

// Userspace trusted-operator evidence only: this cannot prove loaded kernel bytes
// or absence of a privileged outsider's unobserved stop/start. No production grant.
internal sealed class CpuPmLiveParentAuthority : IDisposable
{
    private readonly Guid run, lifetime;
    private readonly CpuPmAuthorityPeer parent;
    private readonly ICpuPmAuthorityChannel channel;
    private readonly ICpuPmAuthorityClock clock;
    private readonly TimeSpan started;
    private readonly HashSet<string> nonces = new(StringComparer.Ordinal);
    private TimeSpan previous;
    private int counter, busy, revoked;
    internal CpuPmLiveParentAuthority(Guid run, Guid lifetime, CpuPmAuthorityPeer parent, ICpuPmAuthorityChannel channel, ICpuPmAuthorityClock clock)
    {
        if (run == Guid.Empty || lifetime == Guid.Empty || !parent.IsValid) { throw new ArgumentException("Bound authority identities required."); }
        this.run = run; this.lifetime = lifetime; this.parent = parent; this.channel = channel; this.clock = clock;
        started = previous = clock.Now;
    }
    internal bool IsRevoked => Volatile.Read(ref revoked) != 0;
    internal async Task<bool> ReadFreshAsync(CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) { Revoke(); return false; }
        try
        {
            TimeSpan requestStarted = clock.Now;
            if (!Check(token) || channel.ReadFreshPeer() != parent || !Check(token)
                || clock.Now - requestStarted >= CpuPmAuthorityProtocol.RequestBudget
                || counter >= CpuPmAuthorityProtocol.MaximumRequests) { return Revoke(); }
            string nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            if (!nonces.Add(nonce)) { return Revoke(); }
            var request = new CpuPmAuthorityMessage(run, lifetime, ++counter, nonce, null);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(CpuPmAuthorityProtocol.RequestBudget);
            byte[] bytes = await channel.ExchangeAsync(CpuPmAuthorityProtocol.Encode(request), deadline.Token)
                .WaitAsync(CpuPmAuthorityProtocol.RequestBudget, token).ConfigureAwait(false);
            if (!Check(token) || deadline.IsCancellationRequested || clock.Now - requestStarted >= CpuPmAuthorityProtocol.RequestBudget
                || channel.ReadFreshPeer() != parent) { return Revoke(); }
            var response = CpuPmAuthorityProtocol.Decode(bytes, true);
            if (response != (request with { Active = true })) { return Revoke(); }
            return Check(token) && clock.Now - requestStarted < CpuPmAuthorityProtocol.RequestBudget || Revoke();
        }
        // Even an unexpected injected peer/transport failure permanently revokes
        // this attempt. No exception detail is sent over the authority channel.
        catch (Exception)
        { return Revoke(); }
        finally { Volatile.Write(ref busy, 0); }
    }
    private bool Check(CancellationToken token)
    {
        TimeSpan now = clock.Now;
        if (IsRevoked || token.IsCancellationRequested || now < previous || now - started >= CpuPmAuthorityProtocol.SessionBudget) { return false; }
        previous = now; return true;
    }
    private bool Revoke() { Interlocked.Exchange(ref revoked, 1); return false; }
    public void Dispose() { Revoke(); channel.Dispose(); }
}

// Protocol handler only. No listener, CreateService/StartService provider or
// supervisor exists in this slice. A null provider always denies.
internal sealed class CpuPmParentAuthoritySession
{
    private readonly Guid run, lifetime;
    private readonly CpuPmAuthorityPeer child;
    private readonly ICpuPmAuthorityPeerReader peers;
    private readonly ICpuPmOwnedDriverProofSource? owner;
    private readonly ICpuPmAuthorityClock clock;
    private readonly TimeSpan started;
    private readonly HashSet<string> nonces = new(StringComparer.Ordinal);
    private TimeSpan previous;
    private int counter, busy, revoked;
    internal CpuPmParentAuthoritySession(Guid run, Guid lifetime, CpuPmAuthorityPeer child, ICpuPmAuthorityPeerReader peers,
        ICpuPmOwnedDriverProofSource? owner, ICpuPmAuthorityClock clock)
    {
        if (run == Guid.Empty || lifetime == Guid.Empty || !child.IsValid) { throw new ArgumentException("Bound authority identities required."); }
        this.run = run; this.lifetime = lifetime; this.child = child; this.peers = peers; this.owner = owner; this.clock = clock;
        started = previous = clock.Now;
    }
    internal byte[]? Handle(byte[] frame, CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) { Interlocked.Exchange(ref revoked, 1); return null; }
        try
        {
            TimeSpan began = clock.Now;
            if (!Check(token) || peers.ReadFreshPeer() != child || !Check(token)
                || clock.Now - began >= CpuPmAuthorityProtocol.RequestBudget) { return Deny(); }
            var request = CpuPmAuthorityProtocol.Decode(frame, false);
            if (request is null || request.RunId != run || request.LifetimeId != lifetime || request.Counter != counter + 1 || !nonces.Add(request.Nonce)) { return Deny(); }
            counter = request.Counter;
            if (owner?.ReadFresh(run, lifetime)?.Admits(run, lifetime) != true || !Check(token)
                || clock.Now - began >= CpuPmAuthorityProtocol.RequestBudget || peers.ReadFreshPeer() != child || !Check(token)
                || clock.Now - began >= CpuPmAuthorityProtocol.RequestBudget) { return Deny(); }
            return CpuPmAuthorityProtocol.Encode(request with { Active = true });
        }
        // A failed owner callback must never leave a resumable counter/state.
        catch (Exception)
        { return Deny(); }
        finally { Volatile.Write(ref busy, 0); }
    }
    private bool Check(CancellationToken token)
    {
        TimeSpan now = clock.Now;
        if (Volatile.Read(ref revoked) != 0 || token.IsCancellationRequested || now < previous || now - started >= CpuPmAuthorityProtocol.SessionBudget) { return false; }
        previous = now; return true;
    }
    internal void Revoke() => Interlocked.Exchange(ref revoked, 1);
    private byte[]? Deny() { Interlocked.Exchange(ref revoked, 1); return null; }
}
