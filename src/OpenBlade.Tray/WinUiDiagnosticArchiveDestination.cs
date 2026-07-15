using Microsoft.UI;
using Microsoft.Windows.Storage.Pickers;

namespace OpenBlade.Tray;

public sealed class WinUiDiagnosticArchiveDestination(WindowId windowId)
    : IDiagnosticArchiveDestination
{
    private readonly WindowId windowId = windowId;

    public async Task<string?> ChooseDestinationAsync(
        string suggestedFileName,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string suggestedBaseName = Path.GetFileNameWithoutExtension(suggestedFileName);
        if (string.IsNullOrWhiteSpace(suggestedBaseName))
        {
            throw new ArgumentException(
                "A diagnostic archive name is required.",
                nameof(suggestedFileName));
        }

        var picker = new FileSavePicker(windowId)
        {
            SuggestedFileName = suggestedBaseName,
            DefaultFileExtension = ".zip",
            ShowOverwritePrompt = true,
            Title = "Export OpenBlade diagnostics",
        };
        picker.FileTypeChoices.Add("ZIP archives", [".zip"]);

        PickFileResult? result = await picker.PickSaveFileAsync().AsTask(token);
        token.ThrowIfCancellationRequested();
        return result?.Path;
    }
}
