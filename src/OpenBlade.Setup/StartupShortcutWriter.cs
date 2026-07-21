using System.Runtime.InteropServices;

namespace OpenBlade.Setup;

public sealed record ShortcutRequest(
    string ShortcutPath,
    string TargetPath,
    string WorkingDirectory,
    string? Arguments = null,
    string Description = "OpenBlade",
    int WindowStyle = 1);

public interface IStartupShortcutWriter
{
    Task CreateAsync(ShortcutRequest request, CancellationToken cancellationToken);
}

internal sealed class WindowsStartupShortcutWriter : IStartupShortcutWriter
{
    public Task CreateAsync(ShortcutRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        Directory.CreateDirectory(Path.GetDirectoryName(request.ShortcutPath)!);
        Type shellType = Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!;
        object shell = Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException("Could not create the Windows shortcut service.");
        object? shortcut = null;
        try
        {
            dynamic dynamicShell = shell;
            shortcut = dynamicShell.CreateShortcut(request.ShortcutPath);
            dynamic dynamicShortcut = shortcut;
            dynamicShortcut.TargetPath = request.TargetPath;
            dynamicShortcut.WorkingDirectory = request.WorkingDirectory;
            dynamicShortcut.Arguments = request.Arguments ?? string.Empty;
            dynamicShortcut.WindowStyle = request.WindowStyle;
            dynamicShortcut.Description = request.Description;
            dynamicShortcut.Save();
        }
        finally
        {
            ReleaseComObject(shortcut);
            ReleaseComObject(shell);
        }

        return Task.CompletedTask;
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }
}
