using System.Text.RegularExpressions;

namespace OpenBlade.Contracts;

/// <summary>Redacts identifying account paths and credentials while retaining technical failure details.</summary>
public static partial class DiagnosticText
{
    public static string Redact(string value) => Redact(value, DefaultSensitiveTokens());

    public static string Redact(string value, IEnumerable<string> sensitiveTokens)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(sensitiveTokens);
        value = UrlCredentialPattern().Replace(value, "://[REDACTED]@");
        value = AuthorizationPattern().Replace(value, "Authorization=[REDACTED]");
        value = CredentialPattern().Replace(value, "$1=[REDACTED]");
        value = UserPathPattern().Replace(value, "$1[USER]");
        foreach (string token in sensitiveTokens.Where(token => !string.IsNullOrWhiteSpace(token))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(token => token.Length))
        {
            value = Regex.Replace(value,
                @"(?<![\p{L}\p{N}_])" + Regex.Escape(token) + @"(?![\p{L}\p{N}_])",
                "[REDACTED]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100));
        }
        return value;
    }

    private static IEnumerable<string> DefaultSensitiveTokens()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Environment.MachineName;
        yield return Environment.UserName;
    }

    [GeneratedRegex(@"(?i)\bauthorization[""']?\s*[:=]\s*(?:Bearer|Basic)\s+[^\s,;]+", RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationPattern();

    [GeneratedRegex(@"(?i)\b(password|passwd|pwd|(?:access[_-]?|refresh[_-]?|id[_-]?)?token|(?:client[_-]?)?secret|authorization|api[_-]?key)[""']?\s*[:=]\s*(""[^""]*""|'[^']*'|[^\s,;]+)", RegexOptions.CultureInvariant)]
    private static partial Regex CredentialPattern();

    [GeneratedRegex(@"://[^/\s:@]+:[^/\s@]+@", RegexOptions.CultureInvariant)]
    private static partial Regex UrlCredentialPattern();

    [GeneratedRegex(@"(?i)((?:[A-Z]:\\Users\\|/Users/|/home/))[^\\/\s""']+", RegexOptions.CultureInvariant)]
    private static partial Regex UserPathPattern();
}
