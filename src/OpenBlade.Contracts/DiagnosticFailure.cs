using System.ComponentModel;

namespace OpenBlade.Contracts;

/// <summary>
/// A bounded failure that survives process, status and export boundaries without
/// copying exception messages, paths, device identifiers or input contents.
/// Diagnostic information never grants operation or recovery authority.
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
    private const int MaximumDepth = 4;
    private const int MaximumTokenLength = 96;

    public static DiagnosticFailure FromException(
        string component,
        string operation,
        string stage,
        string reason,
        Exception exception,
        bool mayHaveSubmitted = false)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return Capture(exception, 0);

        DiagnosticFailure Capture(Exception current, int depth) => new(
            component, operation, stage, reason, mayHaveSubmitted,
            IsToken(current.GetType().Name) ? current.GetType().Name : nameof(Exception),
            current.HResult,
            current is Win32Exception native ? native.NativeErrorCode : null,
            depth + 1 < MaximumDepth && current.InnerException is { } inner
                ? Capture(inner, depth + 1) : null);
    }

    public bool IsValid() => IsValid(0);

    private bool IsValid(int depth) => depth < MaximumDepth
        && IsToken(Component) && IsToken(Operation) && IsToken(Stage) && IsToken(Reason)
        && (ExceptionType is null || IsToken(ExceptionType))
        && (Cause is null || Cause.IsValid(depth + 1));

    private static bool IsToken(string? value) => value is { Length: > 0 and <= MaximumTokenLength }
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');
}
