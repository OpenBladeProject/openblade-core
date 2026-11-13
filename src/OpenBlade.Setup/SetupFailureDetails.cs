using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenBlade.Setup;

internal static class SetupFailureDetails
{
    internal const int MaximumArgumentLength = 512;

    internal static SetupProgressEvent Create(SetupProgressEvent step, Exception exception)
    {
        List<string> recovery = [];
        Exception primary = exception;
        // Setup transactions put the original exception first, then recovery failures.
        // Unwrap that first branch only; flattening would mix recovery into the cause.
        for (int depth = 0; depth < 16 && primary is AggregateException aggregate
            && aggregate.InnerExceptions.Count > 0; depth++)
        {
            List<string> details = [];
            string context = AggregateContext(aggregate);
            if (context.Length != 0)
            {
                details.Add(context);
            }
            details.AddRange(aggregate.InnerExceptions.Skip(1).Select(Summarize));
            recovery.InsertRange(0, details);
            primary = aggregate.InnerExceptions[0];
        }
        return SetupProgressEvent.Create(
            SetupProgressCode.Failure,
            step.Code.ToString(),
            step.Code == SetupProgressCode.CopyFile ? step.Arguments[0] : string.Empty,
            Summarize(primary),
            string.Join("; ", recovery));
    }

    private static string AggregateContext(AggregateException aggregate)
    {
        // Message includes each inner message. Remove the runtime-generated suffix
        // so secondary failures stay separate from the original cause, retaining
        // custom transaction guidance such as the directory holding prior files.
        string suffix = new AggregateException(string.Empty, aggregate.InnerExceptions).Message;
        string context = aggregate.Message;
        if (suffix.Length > 0 && context.EndsWith(suffix, StringComparison.Ordinal))
        {
            context = context[..^suffix.Length];
        }
        return string.Equals(context, new AggregateException().Message, StringComparison.Ordinal)
            ? string.Empty : Sanitize(context, maximumLength: 240);
    }

    private static string Summarize(Exception exception)
    {
        List<string> messages = [];
        // Bound traversal as well as the wire representation for malformed/deep chains.
        for (int depth = 0; exception is not null && depth < 16; depth++)
        {
            // Reserve space for an inner cause even when the wrapper message is long.
            string message = Sanitize(exception.Message, maximumLength: 240);
            if (message.Length == 0)
            {
                message = exception.GetType().Name;
            }
            if (exception is Win32Exception win32)
            {
                message += $" (Win32 {win32.NativeErrorCode}, HRESULT 0x{exception.HResult:X8})";
            }
            else if (exception is ExternalException)
            {
                message += $" (HRESULT 0x{exception.HResult:X8})";
            }
            if (!messages.Contains(message, StringComparer.Ordinal))
            {
                messages.Add(message);
            }
            exception = exception.InnerException!;
        }
        return Sanitize(string.Join("; ", messages));
    }

    internal static string Sanitize(string value, int maximumLength = MaximumArgumentLength)
    {
        StringBuilder result = new(Math.Min(value.Length, maximumLength));
        foreach (Rune rune in value.EnumerateRunes())
        {
            Rune sanitized = Rune.IsControl(rune) || rune.Value is 0x2028 or 0x2029
                ? new Rune(' ') : rune;
            if (result.Length + sanitized.Utf16SequenceLength > maximumLength)
            {
                break;
            }
            result.Append(sanitized.ToString());
        }
        return result.ToString();
    }
}
