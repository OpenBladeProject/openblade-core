namespace OpenBlade.Tray;

public sealed class WpfDiagnosticArchiveDestination : IDiagnosticArchiveDestination
{
    public Task<string?> ChooseDestinationAsync(
        string suggestedFileName,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            AddExtension = true,
            CheckPathExists = true,
            DefaultExt = ".zip",
            FileName = suggestedFileName,
            Filter = "ZIP archives (*.zip)|*.zip",
            OverwritePrompt = true,
            Title = "Export OpenBlade diagnostics",
        };
        return Task.FromResult(dialog.ShowDialog() == true ? dialog.FileName : null);
    }
}
