using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;

namespace OpenBlade.Capture;

// The native callback cannot block. Overflow poisons this probe rather than
// dropping a response and treating a later message as a successful exchange.
internal sealed class CpuPowerProbeResponses
{
    internal const int MaximumMessages = 64;
    internal const int MaximumMessageBytes = 16 * 1024;
    private Query? activeQuery;
    private int querying;
    private int overflowed;

    internal void AddUtf8(nint pointer)
    {
        Query? query = Volatile.Read(ref activeQuery);
        if (query is null || pointer == 0 || Volatile.Read(ref overflowed) != 0)
        {
            return;
        }
        int length = 0;
        while (length <= MaximumMessageBytes && Marshal.ReadByte(pointer, length) != 0)
        {
            length++;
        }
        if (length > MaximumMessageBytes)
        {
            Overflow(query);
            return;
        }
        if (length > 0)
        {
            // Never allocate an unbounded string before enforcing the byte limit.
            // The original native byte length is already bounded. UTF-8
            // replacement characters must not be re-encoded into a false overflow.
            Enqueue(query, Marshal.PtrToStringUTF8(pointer, length)!);
        }
    }

    internal async Task<int> QueryValueAsync(
        Func<bool> getter,
        string expectedFunction,
        string valueName,
        TimeSpan responseTimeout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfOverflowed();
        if (Interlocked.CompareExchange(ref querying, 1, 0) != 0)
        {
            throw new InvalidOperationException("CPU power probe queries cannot overlap.");
        }

        // A fresh queue is armed before invoking the getter, including synchronous
        // callbacks. Registration/handshake messages and earlier queues cannot
        // answer this request. The native API supplies no correlation ID, so a
        // delayed same-function response arriving during this query is indistinguishable.
        var query = new Query();
        Volatile.Write(ref activeQuery, query);
        try
        {
            if (!getter())
            {
                throw new InvalidOperationException($"{expectedFunction} did not accept the read-only request.");
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(responseTimeout);
            try
            {
                while (await query.Messages.Reader.WaitToReadAsync(timeout.Token).ConfigureAwait(false))
                {
                    while (query.Messages.Reader.TryRead(out string? message))
                    {
                        // A busy stream must not postpone cancellation until empty.
                        timeout.Token.ThrowIfCancellationRequested();
                        ThrowIfOverflowed();
                        if (WindowsRzAmdOverclockProbe.TryParseValue(
                            message, expectedFunction, valueName, out int value))
                        {
                            ThrowIfOverflowed();
                            return value;
                        }
                        string? nativeFailure = ReadMatchingNativeFailure(message, expectedFunction);
                        if (nativeFailure is not null)
                        {
                            throw new InvalidDataException(nativeFailure);
                        }
                    }
                    ThrowIfOverflowed();
                }
                ThrowIfOverflowed();
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out waiting for {expectedFunction}.");
            }

            throw new InvalidDataException($"{expectedFunction} ended without a response.");
        }
        finally
        {
            Volatile.Write(ref activeQuery, null);
            Volatile.Write(ref querying, 0);
        }
    }

    private static string? ReadMatchingNativeFailure(string message, string expectedFunction)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(message);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || ReadString(root, "dllName") != "RzAMDOverClockDLL"
                || ReadString(root, "dllFunc") != expectedFunction
                || ReadString(root, "status") is not { } status
                || ReadString(root, "error") is not { } error
                || (status == "SUCCESS" && error.Length == 0))
            {
                return null;
            }

            // The pinned RzDLLService envelope uses SUCCESS/FAIL and an error
            // string. A matching negative reply ends this getter; it is not a
            // missing response or permission to accept a later callback.
            return $"{DiagnosticField(expectedFunction, 64)} native failure "
                + $"(status={DiagnosticField(status, 64)}; error={DiagnosticField(error, 256)}).";
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string DiagnosticField(string value, int maximumCharacters)
    {
        int length = Math.Min(value.Length, maximumCharacters);
        string result = string.Create(length, value, static (characters, source) =>
        {
            for (int index = 0; index < characters.Length; index++)
            {
                characters[index] = char.IsControl(source[index]) ? ' ' : source[index];
            }
        });
        return value.Length > length ? result + "..." : result;
    }

    private void Enqueue(Query query, string message)
    {
        if (ReferenceEquals(query, Volatile.Read(ref activeQuery))
            && !string.IsNullOrWhiteSpace(message)
            && !query.Messages.Writer.TryWrite(message))
        {
            Overflow(query);
        }
    }

    private void Overflow(Query query)
    {
        if (ReferenceEquals(query, Volatile.Read(ref activeQuery)))
        {
            Interlocked.Exchange(ref overflowed, 1);
            query.Messages.Writer.TryComplete();
        }
    }

    private void ThrowIfOverflowed()
    {
        if (Volatile.Read(ref overflowed) != 0)
        {
            throw new InvalidDataException("The CPU power probe callback message limit was exceeded.");
        }
    }

    private sealed class Query
    {
        internal Channel<string> Messages { get; } = Channel.CreateBounded<string>(
            new BoundedChannelOptions(MaximumMessages)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false,
            });
    }
}
