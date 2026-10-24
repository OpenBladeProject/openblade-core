using System.ComponentModel;
using System.Runtime.InteropServices;

namespace OpenBlade.Contracts;

/// <summary>
/// Failure provenance across process, status and export boundaries. Detailed text is
/// redacted; limits are explicit and never grant operation or recovery authority.
/// </summary>
public sealed record DiagnosticFailure(
    string Component,
    string Operation,
    string Stage,
    string Reason,
    bool MayHaveSubmitted = false,
    string? ExceptionType = null,
    int? HResult = null,
    int? NativeErrorCode = null,
    DiagnosticFailure? Cause = null,
    int? ProcessExitCode = null)
{
    public const int MaximumDepth = 16;
    public const int MaximumNodes = 64;
    private const int MaximumTokenLength = 96;
    private const int MaximumMessageCharacters = 8192;
    private const int MaximumStackCharacters = 32768;
    private const int MaximumTextCharacters = 128 * 1024;

    public string? Message { get; init; }
    public string? StackTrace { get; init; }
    public string? Context { get; init; }
    public IReadOnlyList<DiagnosticFailure>? Related { get; init; }
    public bool DetailsTruncated { get; init; }

    public static DiagnosticFailure FromException(
        string component,
        string operation,
        string stage,
        string reason,
        Exception exception,
        bool mayHaveSubmitted = false)
    {
        ArgumentNullException.ThrowIfNull(exception);
        int nodes = 0;
        int characters = 0;
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        int nativeError = Marshal.GetLastPInvokeError();
        try { return Capture(exception, 0); }
        finally { Marshal.SetLastPInvokeError(nativeError); }

        DiagnosticFailure Capture(Exception current, int depth)
        {
            seen.Add(current);
            nodes++;
            bool truncated = false;
            string? message = Text(current.Message, MaximumMessageCharacters, ref truncated);
            string? stack = Text(current.StackTrace, MaximumStackCharacters, ref truncated);
            var children = current is AggregateException aggregate
                ? aggregate.InnerExceptions.ToArray()
                : current.InnerException is { } inner ? [inner] : Array.Empty<Exception>();
            var captured = new List<DiagnosticFailure>();
            foreach (Exception child in children)
            {
                if (depth + 1 >= MaximumDepth || nodes >= MaximumNodes || seen.Contains(child))
                {
                    truncated = true;
                    continue;
                }
                captured.Add(Capture(child, depth + 1));
            }
            return new(component, operation, stage, reason, mayHaveSubmitted,
                IsToken(current.GetType().Name) ? current.GetType().Name : nameof(Exception),
                current.HResult, current is Win32Exception native ? native.NativeErrorCode : null,
                captured.FirstOrDefault())
            {
                Message = message,
                StackTrace = stack,
                Related = captured.Count > 1 ? captured.Skip(1).ToArray() : null,
                DetailsTruncated = truncated || captured.Any(child => child.DetailsTruncated),
            };
        }

        string? Text(string? value, int maximum, ref bool truncated)
        {
            if (value is null)
            {
                return null;
            }
            string sanitized = DiagnosticText.Redact(value);
            int available = Math.Max(0, Math.Min(maximum, MaximumTextCharacters - characters));
            characters += Math.Min(available, sanitized.Length);
            if (sanitized.Length > available)
            {
                truncated = true;
                return sanitized[..available];
            }
            return sanitized;
        }
    }

    /// <summary>Rebudgets records combined during cleanup without losing silent branches.</summary>
    public DiagnosticFailure LimitDetails()
    {
        int nodes = 0;
        int characters = 0;
        var seen = new HashSet<DiagnosticFailure>(ReferenceEqualityComparer.Instance);
        int nativeError = Marshal.GetLastPInvokeError();
        try { return Limit(this, 0)!; }
        finally { Marshal.SetLastPInvokeError(nativeError); }

        DiagnosticFailure? Limit(DiagnosticFailure value, int depth)
        {
            if (depth >= MaximumDepth || nodes >= MaximumNodes || !seen.Add(value))
            {
                return null;
            }
            nodes++;
            bool truncated = value.DetailsTruncated;
            string? message = Text(value.Message, MaximumMessageCharacters, ref truncated);
            string? stack = Text(value.StackTrace, MaximumStackCharacters, ref truncated);
            string? context = Text(value.Context, MaximumMessageCharacters, ref truncated);
            DiagnosticFailure? cause = value.Cause is null ? null : Limit(value.Cause, depth + 1);
            truncated |= value.Cause is not null && cause is null || cause?.DetailsTruncated == true;
            var related = new List<DiagnosticFailure>();
            foreach (DiagnosticFailure child in value.Related ?? [])
            {
                DiagnosticFailure? retained = Limit(child, depth + 1);
                if (retained is null)
                {
                    truncated = true;
                }
                else
                {
                    related.Add(retained);
                    truncated |= retained.DetailsTruncated;
                }
            }
            return value with
            {
                Message = message,
                StackTrace = stack,
                Context = context,
                Cause = cause,
                Related = related.Count == 0 ? null : related,
                DetailsTruncated = truncated,
            };
        }

        string? Text(string? value, int maximum, ref bool truncated)
        {
            if (value is null)
            {
                return null;
            }
            string sanitized = DiagnosticText.Redact(value);
            int available = Math.Max(0, Math.Min(maximum, MaximumTextCharacters - characters));
            characters += Math.Min(available, sanitized.Length);
            truncated |= sanitized.Length > available;
            return sanitized.Length <= available ? sanitized : sanitized[..available];
        }
    }

    public bool IsValid()
    {
        int nodes = 0;
        int characters = 0;
        var seen = new HashSet<DiagnosticFailure>(ReferenceEqualityComparer.Instance);
        return Validate(this, 0);

        bool Validate(DiagnosticFailure failure, int depth)
        {
            if (depth >= MaximumDepth || ++nodes > MaximumNodes || !seen.Add(failure)
                || !IsToken(failure.Component) || !IsToken(failure.Operation)
                || !IsToken(failure.Stage) || !IsToken(failure.Reason)
                || (failure.ExceptionType is not null && !IsToken(failure.ExceptionType))
                || failure.Message?.Length > MaximumMessageCharacters
                || failure.StackTrace?.Length > MaximumStackCharacters
                || failure.Context?.Length > MaximumMessageCharacters)
            {
                return false;
            }
            characters += (failure.Message?.Length ?? 0) + (failure.StackTrace?.Length ?? 0)
                + (failure.Context?.Length ?? 0);
            return characters <= MaximumTextCharacters
                && (failure.Cause is null || Validate(failure.Cause, depth + 1))
                && (failure.Related is null || failure.Related.All(child => child is not null && Validate(child, depth + 1)));
        }
    }

    private static bool IsToken(string? value) => value is { Length: > 0 and <= MaximumTokenLength }
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');
}
