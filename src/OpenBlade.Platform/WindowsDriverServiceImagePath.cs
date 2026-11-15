namespace OpenBlade.Platform;

// Resolves Windows driver-path spellings only. Callers retain their separate
// canonicalization, protected-directory, filename, package and trust admission.
internal static class WindowsDriverServiceImagePath
{
    internal static string Resolve(string value, string windows, bool allowSystem32Relative)
    {
        string path = value;
        if (path.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
        { path = ResolveSystemRoot(path[12..], windows); }
        else if (path.StartsWith(@"%SystemRoot%\", StringComparison.OrdinalIgnoreCase))
        { path = ResolveSystemRoot(path[13..], windows); }
        else if (path.StartsWith(@"\??\", StringComparison.Ordinal))
        { path = path[4..]; }
        else if (path.StartsWith(@"\DosDevices\", StringComparison.OrdinalIgnoreCase))
        { path = path[12..]; }
        else if (allowSystem32Relative && path.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase))
        { path = Path.Combine(windows, path); }

        // GetFullPath alone would resolve arbitrary relative paths using the
        // current process directory, which is not the driver's Windows root.
        if (!Path.IsPathFullyQualified(path))
        { throw new InvalidDataException("The driver image path is not absolute or a supported Windows alias."); }
        return path;
    }

    private static string ResolveSystemRoot(string suffix, string windows)
    {
        if (Path.IsPathRooted(suffix))
        { throw new InvalidDataException("The driver image has an ambiguous SystemRoot path."); }
        return Path.Combine(windows, suffix);
    }
}
