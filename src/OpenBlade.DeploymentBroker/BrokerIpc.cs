using System.Buffers.Binary;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace OpenBlade.DeploymentBroker;

public enum BrokerAction
{
    ApplyFixedInbox,
}

public sealed record BrokerRequest(int ProtocolVersion, Guid RequestId, BrokerAction Action);

public enum BrokerResponseStatus
{
    Rejected,
    Succeeded,
    RolledBack,
    Indeterminate,
}

public sealed record BrokerResponse(
    int ProtocolVersion,
    Guid RequestId,
    BrokerResponseStatus Status,
    string Code);

public static class BrokerRequestCodec
{
    public const int ProtocolVersion = 1;
    public const int MaximumRequestBytes = 512;

    public static byte[] SerializeCanonical(BrokerRequest request)
    {
        Validate(request);
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("protocolVersion", request.ProtocolVersion);
            writer.WriteString("requestId", request.RequestId.ToString("D"));
            writer.WriteString("action", nameof(BrokerAction.ApplyFixedInbox));
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    public static BrokerRequest ParseCanonical(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.Length > MaximumRequestBytes)
        {
            throw new InvalidDataException("The broker request has an invalid size.");
        }

        if (bytes.Length >= 3 && bytes[..3].SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }))
        {
            throw new InvalidDataException("The broker request must not contain a BOM.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                bytes.ToArray(),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 2,
                });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("The broker request must be an object.");
            }

            Dictionary<string, JsonElement> values = ReadExactProperties(document.RootElement);
            BrokerRequest request = new(
                ReadInt32(values, "protocolVersion"),
                ReadGuid(values, "requestId"),
                ReadAction(values, "action"));
            Validate(request);
            if (!bytes.SequenceEqual(SerializeCanonical(request)))
            {
                throw new InvalidDataException("The broker request is not canonical JSON.");
            }

            return request;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException)
        {
            throw new InvalidDataException("The broker request is malformed.", exception);
        }
    }

    private static Dictionary<string, JsonElement> ReadExactProperties(JsonElement root)
    {
        HashSet<string> allowed = new(
            ["protocolVersion", "requestId", "action"],
            StringComparer.Ordinal);
        HashSet<string> seen = new(StringComparer.Ordinal);
        HashSet<string> seenIgnoreCase = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, JsonElement> values = new(StringComparer.Ordinal);
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!allowed.Contains(property.Name)
                || !seen.Add(property.Name)
                || !seenIgnoreCase.Add(property.Name))
            {
                throw new InvalidDataException("The broker request has an unknown or duplicate property.");
            }

            values.Add(property.Name, property.Value);
        }

        if (values.Count != allowed.Count || allowed.Any(name => !values.ContainsKey(name)))
        {
            throw new InvalidDataException("The broker request is missing a required property.");
        }

        return values;
    }

    private static int ReadInt32(IReadOnlyDictionary<string, JsonElement> values, string name) =>
        values[name].TryGetInt32(out int result)
            ? result
            : throw new InvalidDataException($"Request property '{name}' must be an integer.");

    private static string ReadString(IReadOnlyDictionary<string, JsonElement> values, string name) =>
        values[name].ValueKind == JsonValueKind.String
            ? values[name].GetString()!
            : throw new InvalidDataException($"Request property '{name}' must be a string.");

    private static Guid ReadGuid(IReadOnlyDictionary<string, JsonElement> values, string name)
    {
        string text = ReadString(values, name);
        return Guid.TryParseExact(text, "D", out Guid result)
            ? result
            : throw new InvalidDataException("The request ID must be a canonical GUID.");
    }

    private static BrokerAction ReadAction(
        IReadOnlyDictionary<string, JsonElement> values,
        string name) => string.Equals(
            ReadString(values, name),
            nameof(BrokerAction.ApplyFixedInbox),
            StringComparison.Ordinal)
        ? BrokerAction.ApplyFixedInbox
        : throw new InvalidDataException("The broker action is not recognized.");

    private static void Validate(BrokerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ProtocolVersion != ProtocolVersion
            || request.RequestId == Guid.Empty
            || request.Action != BrokerAction.ApplyFixedInbox)
        {
            throw new InvalidDataException("The broker request contract is invalid.");
        }
    }
}

internal static class BrokerResponseCodec
{
    public static byte[] SerializeCanonical(BrokerResponse response)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("protocolVersion", response.ProtocolVersion);
            writer.WriteString("requestId", response.RequestId.ToString("D"));
            writer.WriteString("status", response.Status.ToString());
            writer.WriteString("code", response.Code);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }
}

public enum BrokerClientLogonKind
{
    Interactive,
    RemoteInteractive,
    Network,
    Service,
    Batch,
    Other,
}

public sealed record BrokerClientContext(
    SecurityIdentifier UserSid,
    int SessionId,
    BrokerClientLogonKind LogonKind);

public interface IBrokerClientContextResolver
{
    BrokerClientContext Resolve(NamedPipeServerStream pipe);
}

public sealed class WindowsBrokerClientContextResolver : IBrokerClientContextResolver
{
    private const uint TokenQuery = 0x0008;

    public BrokerClientContext Resolve(NamedPipeServerStream pipe) => Resolve(
        pipe,
        CaptureImpersonatedToken,
        ReadClientContext,
        token => CloseHandle(token));

    internal static BrokerClientContext Resolve(
        NamedPipeServerStream pipe,
        Func<(IntPtr Token, int Error)> captureImpersonatedToken,
        Func<IntPtr, BrokerClientContext> readClientContext,
        Action<IntPtr> closeToken)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        ArgumentNullException.ThrowIfNull(captureImpersonatedToken);
        ArgumentNullException.ThrowIfNull(readClientContext);
        ArgumentNullException.ThrowIfNull(closeToken);
        if (!pipe.IsConnected)
        {
            throw new InvalidOperationException("The broker client pipe is not connected.");
        }

        (IntPtr Token, int Error) capture = default;
        try
        {
            // Do only the native token capture while impersonating. Managed identity
            // construction can lazy-load framework assemblies; the enrolled user is
            // intentionally unable to read the protected broker directory.
            pipe.RunAsClient(() => capture = captureImpersonatedToken());
            if (capture.Token == IntPtr.Zero)
            {
                throw new UnauthorizedAccessException(
                    "The broker client token is unavailable.",
                    new System.ComponentModel.Win32Exception(capture.Error));
            }

            return readClientContext(capture.Token);
        }
        finally
        {
            if (capture.Token != IntPtr.Zero)
            {
                closeToken(capture.Token);
            }
        }
    }

    private static (IntPtr Token, int Error) CaptureImpersonatedToken()
    {
        bool captured = OpenThreadToken(
            GetCurrentThread(),
            TokenQuery,
            openAsSelf: true,
            out IntPtr token);
        return captured ? (token, 0) : (IntPtr.Zero, Marshal.GetLastWin32Error());
    }

    private static BrokerClientContext ReadClientContext(IntPtr token)
    {
        if (token == IntPtr.Zero)
        {
            throw new UnauthorizedAccessException("The broker client token is unavailable.");
        }

        using WindowsIdentity identity = new(token);
        SecurityIdentifier sid = identity.User
            ?? throw new UnauthorizedAccessException("The broker client SID is unavailable.");
        BrokerClientLogonKind kind = Classify(identity);
        return new BrokerClientContext(sid, ReadSessionId(identity), kind);
    }

    private static BrokerClientLogonKind Classify(WindowsIdentity identity)
    {
        if (IsInGroup(identity, new SecurityIdentifier("S-1-5-14")))
        {
            return BrokerClientLogonKind.RemoteInteractive;
        }

        if (IsInGroup(identity, WellKnownSidType.NetworkSid))
        {
            return BrokerClientLogonKind.Network;
        }

        if (IsInGroup(identity, WellKnownSidType.ServiceSid))
        {
            return BrokerClientLogonKind.Service;
        }

        if (IsInGroup(identity, WellKnownSidType.BatchSid))
        {
            return BrokerClientLogonKind.Batch;
        }

        return IsInGroup(identity, WellKnownSidType.InteractiveSid)
            ? BrokerClientLogonKind.Interactive
            : BrokerClientLogonKind.Other;
    }

    private static bool IsInGroup(WindowsIdentity identity, WellKnownSidType type) =>
        IsInGroup(identity, new SecurityIdentifier(type, null));

    private static bool IsInGroup(WindowsIdentity identity, SecurityIdentifier sid) =>
        identity.Groups?.Contains(sid) == true;

    private static int ReadSessionId(WindowsIdentity identity)
    {
        int value = 0;
        if (!GetTokenInformation(
                identity.AccessToken.DangerousGetHandle(),
                TokenInformationClass.TokenSessionId,
                ref value,
                sizeof(int),
                out _))
        {
            throw new UnauthorizedAccessException(
                "The broker client session ID is unavailable.",
                new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        }

        return value;
    }

    private enum TokenInformationClass
    {
        TokenSessionId = 12,
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenThreadToken(
        IntPtr threadHandle,
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool openAsSelf,
        out IntPtr tokenHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle,
        TokenInformationClass tokenInformationClass,
        ref int tokenInformation,
        int tokenInformationLength,
        out int returnLength);
}

public interface IActiveConsoleSessionSource
{
    int? GetActiveConsoleSessionId();
}

public sealed class WindowsActiveConsoleSessionSource : IActiveConsoleSessionSource
{
    public int? GetActiveConsoleSessionId()
    {
        uint value = WTSGetActiveConsoleSessionId();
        return value == uint.MaxValue ? null : unchecked((int)value);
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();
}

public static class BrokerAuthorizationPolicy
{
    public static bool IsAuthorized(
        BrokerEnrollment enrollment,
        BrokerClientContext client,
        IActiveConsoleSessionSource activeConsole)
    {
        ArgumentNullException.ThrowIfNull(enrollment);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(activeConsole);
        SecurityIdentifier enrolledSid = BrokerEnrollmentCodec.ParseAccountSid(
            enrollment.EnrolledAccountSid);
        return client.UserSid == enrolledSid
            && client.LogonKind == BrokerClientLogonKind.Interactive
            && activeConsole.GetActiveConsoleSessionId() == client.SessionId;
    }
}

public static class BrokerPipeSecurityFactory
{
    public static PipeSecurity Create(SecurityIdentifier enrolledUser)
    {
        ArgumentNullException.ThrowIfNull(enrolledUser);
        PipeSecurity security = new();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            enrolledUser,
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        return security;
    }
}

public sealed class BrokerReplayStore(BrokerPaths paths)
{
    public bool TryMarkAccepted(Guid requestId)
    {
        paths.EnsureDirectory(paths.ReplayDirectory);
        string marker = Path.Combine(paths.ReplayDirectory, requestId.ToString("N") + ".accepted");
        try
        {
            using FileStream stream = new(
                marker,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1,
                FileOptions.WriteThrough);
            stream.WriteByte(1);
            stream.Flush(flushToDisk: true);
            paths.ProtectFile(marker);
            return true;
        }
        catch (IOException) when (File.Exists(marker))
        {
            return false;
        }
    }
}

public sealed class BrokerSingleFlightGate
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public Lease? TryAcquire() => gate.Wait(0) ? new Lease(gate) : null;

    public sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? ownedGate = gate;

        public void Dispose() => Interlocked.Exchange(ref ownedGate, null)?.Release();
    }
}

public interface IBrokerRequestHandler
{
    Task<BrokerResponse> HandleAsync(
        BrokerRequest request,
        BrokerEnrollment enrollment,
        CancellationToken cancellationToken);
}

public sealed class DisabledBrokerRequestHandler : IBrokerRequestHandler
{
    public Task<BrokerResponse> HandleAsync(
        BrokerRequest request,
        BrokerEnrollment enrollment,
        CancellationToken cancellationToken) => Task.FromResult(new BrokerResponse(
            BrokerRequestCodec.ProtocolVersion,
            request.RequestId,
            BrokerResponseStatus.Rejected,
            "execution-pipeline-disabled"));
}

public sealed class BrokerPipeServer
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(5);
    private readonly BrokerEnrollment enrollment;
    private readonly IBrokerClientContextResolver contextResolver;
    private readonly IActiveConsoleSessionSource activeConsole;
    private readonly BrokerReplayStore replayStore;
    private readonly BrokerSingleFlightGate singleFlight;
    private readonly IBrokerRequestHandler handler;
    private readonly Func<PipeOptions, NamedPipeServerStream> createPipe;

    public BrokerPipeServer(
        string pipeName,
        BrokerEnrollment enrollment,
        IBrokerClientContextResolver contextResolver,
        IActiveConsoleSessionSource activeConsole,
        BrokerReplayStore replayStore,
        BrokerSingleFlightGate singleFlight,
        IBrokerRequestHandler handler)
        : this(
            enrollment,
            contextResolver,
            activeConsole,
            replayStore,
            singleFlight,
            handler,
            options => NamedPipeServerStreamAcl.Create(
                pipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                options,
                inBufferSize: 0,
                outBufferSize: 0,
                BrokerPipeSecurityFactory.Create(
                    BrokerEnrollmentCodec.ParseAccountSid(enrollment.EnrolledAccountSid))))
    {
    }

    internal BrokerPipeServer(
        BrokerEnrollment enrollment,
        IBrokerClientContextResolver contextResolver,
        IActiveConsoleSessionSource activeConsole,
        BrokerReplayStore replayStore,
        BrokerSingleFlightGate singleFlight,
        IBrokerRequestHandler handler,
        Func<PipeOptions, NamedPipeServerStream> createPipe)
    {
        this.enrollment = enrollment;
        this.contextResolver = contextResolver;
        this.activeConsole = activeConsole;
        this.replayStore = replayStore;
        this.singleFlight = singleFlight;
        this.handler = handler;
        this.createPipe = createPipe;
    }

    public async Task ServeOnceAsync(CancellationToken cancellationToken)
    {
        await using NamedPipeServerStream pipe = createPipe(
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance);
        await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
        BrokerRequest request;
        try
        {
            using CancellationTokenSource readDeadline = CancellationTokenSource
                .CreateLinkedTokenSource(cancellationToken);
            readDeadline.CancelAfter(FrameTimeout);
            request = BrokerRequestCodec.ParseCanonical(
                await ReadFrameAsync(pipe, readDeadline.Token).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is InvalidDataException or OperationCanceledException)
        {
            await WriteResponseWithDeadlineAsync(
                pipe,
                new BrokerResponse(
                    BrokerRequestCodec.ProtocolVersion,
                    Guid.Empty,
                    BrokerResponseStatus.Rejected,
                    "malformed-request"),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        BrokerClientContext client = contextResolver.Resolve(pipe);
        BrokerResponse response;
        if (!BrokerAuthorizationPolicy.IsAuthorized(enrollment, client, activeConsole))
        {
            response = Reject(request, "unauthorized-client");
        }
        else
        {
            using BrokerSingleFlightGate.Lease? lease = singleFlight.TryAcquire();
            if (lease is null)
            {
                response = Reject(request, "deployment-in-progress");
            }
            else if (!replayStore.TryMarkAccepted(request.RequestId))
            {
                response = Reject(request, "replayed-request");
            }
            else
            {
                try
                {
                    response = await handler.HandleAsync(request, enrollment, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    // The replay marker already makes this accepted request
                    // non-retriable. Return an explicit terminal ambiguity
                    // instead of disconnecting and tempting a blind retry.
                    response = new BrokerResponse(
                        BrokerRequestCodec.ProtocolVersion,
                        request.RequestId,
                        BrokerResponseStatus.Indeterminate,
                        "broker-internal-failure");
                }
            }
        }

        await WriteResponseWithDeadlineAsync(
            pipe,
            response,
            cancellationToken).ConfigureAwait(false);
    }

    private static BrokerResponse Reject(BrokerRequest request, string code) => new(
        BrokerRequestCodec.ProtocolVersion,
        request.RequestId,
        BrokerResponseStatus.Rejected,
        code);

    private static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] lengthBytes = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(lengthBytes, cancellationToken).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
        if (length <= 0 || length > BrokerRequestCodec.MaximumRequestBytes)
        {
            throw new InvalidDataException("The broker request frame has an invalid size.");
        }

        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return payload;
    }

    private static async Task WriteResponseAsync(
        Stream stream,
        BrokerResponse response,
        CancellationToken cancellationToken)
    {
        byte[] payload = BrokerResponseCodec.SerializeCanonical(response);
        byte[] lengthBytes = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(lengthBytes, payload.Length);
        await stream.WriteAsync(lengthBytes, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteResponseWithDeadlineAsync(
        Stream stream,
        BrokerResponse response,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(FrameTimeout);
        await WriteResponseAsync(stream, response, deadline.Token).ConfigureAwait(false);
    }
}
