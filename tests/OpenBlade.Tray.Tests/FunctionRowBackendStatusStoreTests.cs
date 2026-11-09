namespace OpenBlade.Tray.Tests;

public sealed class FunctionRowBackendStatusStoreTests
{
    [Fact]
    public void IndependentStatusUsesSameBoundedSanitizedContract()
    {
        Assert.NotEqual(SnapTapStatusStore.GetDefaultPath(), FunctionRowBackendStatusStore.GetDefaultPath());
        string directory = Path.Combine(Path.GetTempPath(), "OpenBlade.FunctionRow.Tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "function-row.json");
        try
        {
            var status = new SnapTapRuntimeStatus(SnapTapRuntimeState.Paused, DateTimeOffset.UtcNow)
            { Backend = KeyboardInputBackend.WindowsHook };
            FunctionRowBackendStatusStore.Write(status, path);
            Assert.Equal(status, FunctionRowBackendStatusStore.Read(path));
            Assert.Equal(status, FunctionRowBackendStatusStore.CaptureDiagnostics(path).Status);
            Assert.Null(FunctionRowBackendStatusStore.CaptureDiagnostics(path).CaptureFailure);
            File.WriteAllText(path, "{invalid");
            Assert.Null(FunctionRowBackendStatusStore.Read(path));
            Assert.NotNull(FunctionRowBackendStatusStore.CaptureDiagnostics(path).CaptureFailure);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
