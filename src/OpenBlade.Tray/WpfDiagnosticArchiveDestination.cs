namespace OpenBlade.Tray;

public sealed class WpfDiagnosticArchiveDestination : IDiagnosticArchiveDestination
{
    public string? ChooseDestination(string suggestedFileName)
    {
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
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
