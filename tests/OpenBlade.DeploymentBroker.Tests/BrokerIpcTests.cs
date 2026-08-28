using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using OpenBlade.DeploymentBroker;

namespace OpenBlade.DeploymentBroker.Tests;

public sealed class BrokerIpcTests
{
    private static readonly Guid RequestId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly SecurityIdentifier EnrolledSid = new("S-1-5-21-1-2-3-1001");

    [Fact]
    public void RequestCodec_RoundTripsOnlyFixedContract()
    {
        BrokerRequest expected = new(
            BrokerRequestCodec.ProtocolVersion,
            RequestId,
            BrokerAction.ApplyFixedInbox);

        byte[] bytes = BrokerRequestCodec.SerializeCanonical(expected);

        Assert.Equal(expected, BrokerRequestCodec.ParseCanonical(bytes));
        Assert.Equal(
            "{\"protocolVersion\":1,\"requestId\":\"22222222-2222-2222-2222-222222222222\",\"action\":\"ApplyFixedInbox\"}",
            Encoding.UTF8.GetString(bytes));
    }

    public static TheoryData<byte[]> InvalidRequests => new()
    {
        Encoding.UTF8.GetBytes("{\"protocolVersion\":1,\"requestId\":\"22222222-2222-2222-2222-222222222222\",\"action\":\"ApplyFixedInbox\",\"path\":\"C:/evil.exe\"}"),
        Encoding.UTF8.GetBytes("{\"protocolVersion\":1,\"protocolVersion\":1,\"requestId\":\"22222222-2222-2222-2222-222222222222\",\"action\":\"ApplyFixedInbox\"}"),
        Encoding.UTF8.GetBytes("{ \"protocolVersion\":1,\"requestId\":\"22222222-2222-2222-2222-222222222222\",\"action\":\"ApplyFixedInbox\"}"),
        Encoding.UTF8.GetBytes("{\"protocolVersion\":1,\"requestId\":\"22222222-2222-2222-2222-222222222222\",\"action\":\"ApplyFixedInbox\"}x"),
        new byte[] { 0xEF, 0xBB, 0xBF, (byte)'{', (byte)'}' },
        new byte[BrokerRequestCodec.MaximumRequestBytes + 1],
    };

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public void RequestCodec_RejectsUnknownDuplicateBomTrailingOrNoncanonical(byte[] bytes)
    {
        Assert.Throws<InvalidDataException>(() => BrokerRequestCodec.ParseCanonical(bytes));
    }

    [Fact]
    public void Authorization_RequiresExactSidActiveLocalInteractiveSession()
    {
        BrokerEnrollment enrollment = BrokerEnrollmentTests.CreateEnrollment();
        FixedActiveConsole active = new(7);

        Assert.True(BrokerAuthorizationPolicy.IsAuthorized(
            enrollment,
            new BrokerClientContext(EnrolledSid, 7, BrokerClientLogonKind.Interactive),
            active));
        Assert.False(BrokerAuthorizationPolicy.IsAuthorized(
            enrollment,
            new BrokerClientContext(EnrolledSid, 8, BrokerClientLogonKind.Interactive),
            active));
        Assert.False(BrokerAuthorizationPolicy.IsAuthorized(
            enrollment,
            new BrokerClientContext(
                new SecurityIdentifier("S-1-5-21-1-2-3-1002"),
                7,
                BrokerClientLogonKind.Interactive),
            active));
    }

    [Theory]
    [InlineData(BrokerClientLogonKind.RemoteInteractive)]
    [InlineData(BrokerClientLogonKind.Network)]
    [InlineData(BrokerClientLogonKind.Service)]
    [InlineData(BrokerClientLogonKind.Batch)]
    [InlineData(BrokerClientLogonKind.Other)]
    public void Authorization_RejectsEveryNonLocalInteractiveKind(BrokerClientLogonKind kind)
    {
        Assert.False(BrokerAuthorizationPolicy.IsAuthorized(
            BrokerEnrollmentTests.CreateEnrollment(),
            new BrokerClientContext(EnrolledSid, 7, kind),
            new FixedActiveConsole(7)));
    }

    [Fact]
    public void PipeAcl_DeniesNetworkAndAllowsOnlySystemAndEnrolledSid()
    {
        PipeSecurity security = BrokerPipeSecurityFactory.Create(EnrolledSid);
        AuthorizationRuleCollection rules = security.GetAccessRules(
            includeExplicit: true,
            includeInherited: false,
            typeof(SecurityIdentifier));
        PipeAccessRule[] typed = rules.Cast<PipeAccessRule>().ToArray();

        Assert.Contains(typed, rule =>
            rule.AccessControlType == AccessControlType.Deny
            && rule.IdentityReference == new SecurityIdentifier(WellKnownSidType.NetworkSid, null)
            && rule.PipeAccessRights == PipeAccessRights.FullControl);
        Assert.Contains(typed, rule =>
            rule.AccessControlType == AccessControlType.Allow
            && rule.IdentityReference == new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)
            && rule.PipeAccessRights == PipeAccessRights.FullControl);
        Assert.Contains(typed, rule =>
            rule.AccessControlType == AccessControlType.Allow
            && rule.IdentityReference == EnrolledSid
            && (rule.PipeAccessRights & PipeAccessRights.ReadWrite) == PipeAccessRights.ReadWrite);
        Assert.Equal(3, typed.Length);
    }

    [Fact]
    public async Task WindowsClientContextResolver_CapturesConnectedClientToken()
    {
        (NamedPipeServerStream server, NamedPipeClientStream client) =
            await CreateConnectedPipeAsync();
        using (server)
        using (client)
        {
            using WindowsIdentity current = WindowsIdentity.GetCurrent();
            BrokerClientContext context = new WindowsBrokerClientContextResolver().Resolve(server);

            Assert.Equal(current.User, context.UserSid);
            Assert.Equal(System.Diagnostics.Process.GetCurrentProcess().SessionId, context.SessionId);
        }
    }

    [Fact]
    public async Task WindowsClientContextResolver_DecodesAfterRevertAndClosesOnFailure()
    {
        (NamedPipeServerStream server, NamedPipeClientStream client) =
            await CreateConnectedPipeAsync();
        using (server)
        using (client)
        {
            TokenImpersonationLevel? captureLevel = null;
            TokenImpersonationLevel? decodeLevel = null;
            IntPtr closedToken = IntPtr.Zero;
            Func<BrokerClientContext> resolve = () => WindowsBrokerClientContextResolver.Resolve(
                server,
                () =>
                {
                    using WindowsIdentity identity = WindowsIdentity.GetCurrent();
                    captureLevel = identity.ImpersonationLevel;
                    return (new IntPtr(42), 0);
                },
                _ =>
                {
                    using WindowsIdentity identity = WindowsIdentity.GetCurrent();
                    decodeLevel = identity.ImpersonationLevel;
                    throw new InvalidDataException("simulated managed identity failure");
                },
                token => closedToken = token);

            Assert.Throws<InvalidDataException>(resolve);

            Assert.NotEqual(TokenImpersonationLevel.None, captureLevel);
            Assert.Equal(TokenImpersonationLevel.None, decodeLevel);
            Assert.Equal(new IntPtr(42), closedToken);
        }
    }

    [Fact]
    public async Task WindowsClientContextResolver_NormalizesCaptureFailureAfterRevert()
    {
        (NamedPipeServerStream server, NamedPipeClientStream client) =
            await CreateConnectedPipeAsync();
        using (server)
        using (client)
        {
            TokenImpersonationLevel? captureLevel = null;
            bool decoded = false;
            bool closed = false;
            Func<BrokerClientContext> resolve = () => WindowsBrokerClientContextResolver.Resolve(
                server,
                () =>
                {
                    using WindowsIdentity identity = WindowsIdentity.GetCurrent();
                    captureLevel = identity.ImpersonationLevel;
                    return (IntPtr.Zero, 5);
                },
                _ =>
                {
                    decoded = true;
                    throw new InvalidOperationException("The zero token must not be decoded.");
                },
                _ => closed = true);

            UnauthorizedAccessException exception = Assert.Throws<UnauthorizedAccessException>(
                resolve);

            Assert.NotEqual(TokenImpersonationLevel.None, captureLevel);
            System.ComponentModel.Win32Exception inner =
                Assert.IsType<System.ComponentModel.Win32Exception>(exception.InnerException);
            Assert.Equal(5, inner.NativeErrorCode);
            Assert.False(decoded);
            Assert.False(closed);
        }
    }

    [Fact]
    public void SingleFlight_AllowsExactlyOneLease()
    {
        BrokerSingleFlightGate gate = new();
        using BrokerSingleFlightGate.Lease first = Assert.IsType<BrokerSingleFlightGate.Lease>(
            gate.TryAcquire());
        Assert.Null(gate.TryAcquire());
        first.Dispose();
        Assert.NotNull(gate.TryAcquire());
    }

    [Fact]
    public async Task PipeServer_EveryCreationAttemptRequiresFirstPipeInstance()
    {
        List<PipeOptions> attempts = [];
        BrokerPaths paths = BrokerPaths.CreateForTests(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        BrokerPipeServer server = new(
            BrokerEnrollmentTests.CreateEnrollment(),
            new UnusedClientContextResolver(),
            new FixedActiveConsole(7),
            new BrokerReplayStore(paths),
            new BrokerSingleFlightGate(),
            new DisabledBrokerRequestHandler(),
            options =>
            {
                attempts.Add(options);
                throw new IOException("simulated pipe creation failure");
            });

        await Assert.ThrowsAsync<IOException>(() => server.ServeOnceAsync(CancellationToken.None));
        await Assert.ThrowsAsync<IOException>(() => server.ServeOnceAsync(CancellationToken.None));

        Assert.Equal(2, attempts.Count);
        Assert.All(attempts, options =>
        {
            Assert.True(options.HasFlag(PipeOptions.Asynchronous));
            Assert.True(options.HasFlag(PipeOptions.FirstPipeInstance));
        });
    }

    private sealed class FixedActiveConsole(int? sessionId) : IActiveConsoleSessionSource
    {
        public int? GetActiveConsoleSessionId() => sessionId;
    }

    private sealed class UnusedClientContextResolver : IBrokerClientContextResolver
    {
        public BrokerClientContext Resolve(NamedPipeServerStream pipe) =>
            throw new NotSupportedException();
    }

    private static async Task<(NamedPipeServerStream Server, NamedPipeClientStream Client)>
        CreateConnectedPipeAsync()
    {
        string pipeName = $"OpenBlade.DeploymentBroker.Tests.{Guid.NewGuid():N}";
        NamedPipeServerStream server = new(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        NamedPipeClientStream client = new(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Impersonation);
        try
        {
            Task connect = client.ConnectAsync(5000);
            await server.WaitForConnectionAsync();
            await connect;
            return (server, client);
        }
        catch
        {
            client.Dispose();
            server.Dispose();
            throw;
        }
    }
}
