using System.Diagnostics;
using System.Text.Json;

namespace OpenBlade.Tray.Tests;

public sealed class SnapTapRuntimeStatusTests : IDisposable
{
    [Theory]
    [InlineData(SnapTapRuntimeState.Unavailable)]
    [InlineData(SnapTapRuntimeState.RecoveryRequired)]
    [InlineData(SnapTapRuntimeState.DriverMissing)]
    [InlineData(SnapTapRuntimeState.UnsupportedModel)]
    [InlineData(SnapTapRuntimeState.ManualDriverSetupRequired)]
    public void DriverTerminalConditionSurvivesHelperExit(SnapTapRuntimeState state)
    {
        var status = new SnapTapRuntimeStatus(state, DateTimeOffset.UtcNow);
        SnapTapStatusStore.Write(status, StatusPath);
        Assert.Equal(status, SnapTapStatusStore.Read(StatusPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DriverProvenanceSurvivesStatusRoundTrip(bool experimental)
    {
        var status = new SnapTapRuntimeStatus(SnapTapRuntimeState.Unavailable, DateTimeOffset.UtcNow,
            DriverModel: "Blade 16 - RZ09-0528", ExperimentalDriver: experimental);
        SnapTapStatusStore.Write(status, StatusPath);
        Assert.Equal(status, SnapTapStatusStore.Read(StatusPath));
    }
    private readonly string directory = Path.Combine(
        Path.GetTempPath(), "OpenBlade.Tray.Tests", Guid.NewGuid().ToString("N"));

    private string StatusPath => Path.Combine(directory, "status.json");

    [Fact]
    public async Task Preferences_OldFileDefaultsOffAndOptInSurvivesSave()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "preferences.json");
        await File.WriteAllTextAsync(path, "{\"theme\":\"Dark\"}");
        var store = new UiPreferenceStore(path);
        UiPreferences old = await store.LoadAsync();
        Assert.False(old.SnapTapEnabled);
        Assert.False(UiPreferences.Default.SnapTapEnabled);
        await store.SaveAsync(old with { SnapTapEnabled = true });
        UiPreferences saved = await store.LoadAsync();
        Assert.True(saved.SnapTapEnabled);
        Assert.Equal(ThemePreference.Dark, saved.Theme);
    }

    [Theory]
    [InlineData(SnapTapRuntimeState.Active)]
    [InlineData(SnapTapRuntimeState.Armed)]
    public void Status_RoundTripAndReplacementLeaveNoTemporaryFiles(SnapTapRuntimeState state)
    {
        var active = new SnapTapRuntimeStatus(
            state, DateTimeOffset.UtcNow, Environment.ProcessId);
        SnapTapStatusStore.Write(active, StatusPath);
        Assert.Equal(active, SnapTapStatusStore.Read(StatusPath));
        var off = active with { State = SnapTapRuntimeState.Off, ProcessId = 0 };
        SnapTapStatusStore.Write(off, StatusPath);
        Assert.Equal(off, SnapTapStatusStore.Read(StatusPath));
        Assert.Single(Directory.GetFiles(directory));
    }

    [Theory]
    [InlineData(SnapTapRuntimeState.Active)]
    [InlineData(SnapTapRuntimeState.Starting)]
    [InlineData(SnapTapRuntimeState.WaitingForRelease)]
    [InlineData(SnapTapRuntimeState.Armed)]
    public void Status_MissingProcessCannotRemainActive(SnapTapRuntimeState state)
    {
        SnapTapStatusStore.Write(new(state, DateTimeOffset.UtcNow), StatusPath);
        Assert.Equal(SnapTapRuntimeState.Paused, SnapTapStatusStore.Read(StatusPath)?.State);
    }

    [Fact]
    public void Status_ReusedPidCannotRemainActive()
    {
        using Process current = Process.GetCurrentProcess();
        SnapTapStatusStore.Write(new(
            SnapTapRuntimeState.Active,
            new DateTimeOffset(current.StartTime.ToUniversalTime()).AddSeconds(-1),
            Environment.ProcessId), StatusPath);
        Assert.Equal(SnapTapRuntimeState.Paused, SnapTapStatusStore.Read(StatusPath)?.State);
    }

    [Theory]
    [InlineData("{invalid")]
    [InlineData("{\"version\":2,\"status\":{\"state\":0,\"updatedAt\":\"2026-01-01T00:00:00Z\"}}")]
    [InlineData("{\"version\":1,\"status\":{\"state\":999,\"updatedAt\":\"2026-01-01T00:00:00Z\"}}")]
    [InlineData("{\"version\":1,\"status\":{\"state\":0,\"updatedAt\":\"2099-01-01T00:00:00Z\"}}")]
    [InlineData("{\"version\":1,\"status\":{\"state\":0}}")]
    public void Status_InvalidDataIsUnavailable(string json)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(StatusPath, json);
        Assert.Null(SnapTapStatusStore.Read(StatusPath));
    }

    [Fact]
    public void Status_MissingLockedAndOversizedFilesAreUnavailable()
    {
        Assert.Null(SnapTapStatusStore.Read(StatusPath));
        Directory.CreateDirectory(directory);
        File.WriteAllText(StatusPath, new string(' ', 8193));
        Assert.Null(SnapTapStatusStore.Read(StatusPath));
        using FileStream locked = new(StatusPath, FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.Null(SnapTapStatusStore.Read(StatusPath));
    }

    [Fact]
    public void Status_WritesVersionedEnvelope()
    {
        SnapTapStatusStore.Write(new(SnapTapRuntimeState.Off, DateTimeOffset.UtcNow), StatusPath);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(StatusPath));
        Assert.Equal(1, document.RootElement.GetProperty("version").GetInt32());
        Assert.True(document.RootElement.TryGetProperty("status", out _));
    }

    [Fact]
    public void Status_ReplacementPreservesOpenReaderSnapshot()
    {
        var off = new SnapTapRuntimeStatus(SnapTapRuntimeState.Off, DateTimeOffset.UtcNow);
        SnapTapStatusStore.Write(off, StatusPath);
        using FileStream reader = new(StatusPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var armed = new SnapTapRuntimeStatus(
            SnapTapRuntimeState.Armed, DateTimeOffset.UtcNow, Environment.ProcessId);

        SnapTapStatusStore.Write(armed, StatusPath);

        Assert.Equal(armed, SnapTapStatusStore.Read(StatusPath));
        using JsonDocument original = JsonDocument.Parse(reader);
        Assert.Equal((int)SnapTapRuntimeState.Off,
            original.RootElement.GetProperty("status").GetProperty("state").GetInt32());
        Assert.Single(Directory.GetFiles(directory));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
