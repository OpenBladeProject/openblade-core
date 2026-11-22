using OpenBlade.Tray;

namespace OpenBlade.SessionAgent.Tests;

public sealed class KeyboardDriverStatusStoreTests
{
    [Fact]
    public void FunctionRowOnlyDriverNeverOverwritesWindowsSnapTapStatus()
    {
        WithPaths((joint, snap, fn) =>
        {
            var windows = new SnapTapRuntimeStatus(SnapTapRuntimeState.Off, DateTimeOffset.UtcNow)
            { Backend = KeyboardInputBackend.WindowsHook };
            SnapTapStatusStore.Write(windows, snap);
            var driver = new SnapTapRuntimeStatus(SnapTapRuntimeState.Off, DateTimeOffset.UtcNow);
            KeyboardDriverStatusStore.Write(driver, KeyboardDriverFeatures.FunctionRow, joint, snap, fn);
            Assert.Equal(windows, SnapTapStatusStore.Read(snap));
            Assert.Equal(driver, KeyboardDriverStatusStore.Read(joint));
            Assert.Equal(driver with { ExperimentalDriver = true }, FunctionRowBackendStatusStore.Read(fn));
        });
    }

    [Fact]
    public void CombinedScopeProjectsBothFeaturesAndKeepsFunctionRowExperimental()
    {
        WithPaths((joint, snap, fn) =>
        {
            var driver = new SnapTapRuntimeStatus(SnapTapRuntimeState.Off, DateTimeOffset.UtcNow);
            KeyboardDriverStatusStore.Write(driver, KeyboardDriverFeatures.SnapTap | KeyboardDriverFeatures.FunctionRow, joint, snap, fn);
            Assert.Equal(driver, SnapTapStatusStore.Read(snap));
            Assert.True(FunctionRowBackendStatusStore.Read(fn)?.ExperimentalDriver);
        });
    }

    [Theory]
    [InlineData(KeyboardDriverFeatures.FunctionRow, false)]
    [InlineData(KeyboardDriverFeatures.SnapTap, false)]
    [InlineData(KeyboardDriverFeatures.SnapTap | KeyboardDriverFeatures.FunctionRow, false)]
    [InlineData(KeyboardDriverFeatures.FunctionRow, true)]
    [InlineData(KeyboardDriverFeatures.SnapTap, true)]
    [InlineData(KeyboardDriverFeatures.SnapTap | KeyboardDriverFeatures.FunctionRow, true)]
    public void WorkerCleanPauseSurvivesStoreForSupervisorRearm(KeyboardDriverFeatures features, bool deferred)
    {
        // The worker publishes this exact status before exit 8. The supervisor reads it
        // back from disk, so an in-memory status cannot prove the rearm path.
        WithPaths((joint, snap, fn) =>
        {
            DateTimeOffset startedAt = DateTimeOffset.UtcNow;
            var paused = new SnapTapRuntimeStatus(SnapTapRuntimeState.Paused, DateTimeOffset.UtcNow, 123,
                Failure: deferred ? SnapTapDriverLoop.SubscriptionDeferredFailure()
                    : SnapTapDriverLoop.ForegroundUnavailableFailure(false, false, true));
            KeyboardDriverStatusStore.Write(paused, features, joint, snap, fn);
            SnapTapRuntimeStatus? read = KeyboardDriverStatusStore.Read(joint);
            Assert.Equal(paused.Failure, read?.Failure);
            Assert.True(SnapTapDriverSupervisor.IsCleanPause(
                SnapTapDriverLoop.CleanPauseExitCode, read, 123, startedAt));
            if (features.HasFlag(KeyboardDriverFeatures.FunctionRow))
            {
                Assert.Equal(SnapTapRuntimeState.Paused, FunctionRowBackendStatusStore.Read(fn)?.State);
            }
        });
    }

    [Fact]
    public void ActiveStatusStillRejectsFailure()
    {
        WithPaths((joint, _, _) =>
        {
            var active = new SnapTapRuntimeStatus(SnapTapRuntimeState.Active, DateTimeOffset.UtcNow, 123,
                Failure: SnapTapDriverLoop.ForegroundUnavailableFailure(true, true, true));
            Assert.Throws<ArgumentException>(() => SnapTapStatusStore.Write(active, joint));
        });
    }

    [Theory]
    [InlineData(SnapTapRuntimeState.Armed, SnapTapRuntimeState.WaitingForFunctionKey)]
    [InlineData(SnapTapRuntimeState.Active, SnapTapRuntimeState.WaitingForFunctionKey)]
    [InlineData(SnapTapRuntimeState.WaitingForRelease, SnapTapRuntimeState.WaitingForRelease)]
    [InlineData(SnapTapRuntimeState.Failed, SnapTapRuntimeState.Failed)]
    [InlineData(SnapTapRuntimeState.RecoveryRequired, SnapTapRuntimeState.RecoveryRequired)]
    public void UnknownFunctionKeyProjectsOnlyFunctionRowNormalStates(SnapTapRuntimeState actual, SnapTapRuntimeState expected)
    {
        var status = new SnapTapRuntimeStatus(actual, DateTimeOffset.UtcNow);
        Assert.Equal(expected, KeyboardDriverStatusStore.ProjectFunctionRow(status, false).State);
        Assert.Equal(actual, KeyboardDriverStatusStore.ProjectFunctionRow(status, true).State);
        Assert.Equal(actual, status.State); // joint/SnapTap state remains unchanged
    }

    private static void WithPaths(Action<string, string, string> action)
    {
        string directory = Path.Combine(Path.GetTempPath(), "OpenBlade-status-scope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string[] paths = [Path.Combine(directory, "joint.json"), Path.Combine(directory, "snap.json"), Path.Combine(directory, "fn.json")];
        try { action(paths[0], paths[1], paths[2]); }
        finally
        {
            foreach (string path in paths) { File.Delete(path); }
            Directory.Delete(directory);
        }
    }
}
