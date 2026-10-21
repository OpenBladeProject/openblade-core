using OpenBlade.Contracts;
using OpenBlade.Session;

namespace OpenBlade.Session.Tests;

public sealed class DisplayAutomationCheckpointStoreTests
{
    [Fact]
    public void SaveAndLoad_RoundTripsConfirmedSourceAtomically()
    {
        string root = Path.Combine(Path.GetTempPath(), $"openblade-display-{Guid.NewGuid():N}");
        string path = Path.Combine(root, "checkpoint.json");
        try
        {
            var store = new DisplayAutomationCheckpointStore(path);

            Assert.True(store.Save(PowerSource.UsbC));

            Assert.Equal(PowerSource.UsbC, store.Load());
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void InvalidOrUnconfirmedState_IsIgnored()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"openblade-display-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{not-json");
            var store = new DisplayAutomationCheckpointStore(path);

            Assert.Null(store.Load());
            Assert.False(store.Save(PowerSource.ExternalUnknown));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RecoveryCheckpointRoundTripsApplicationBaselineAndOwnedFields()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"openblade-display-{Guid.NewGuid():N}.json");
        try
        {
            var store = new DisplayAutomationCheckpointStore(path);
            Guid id = Guid.NewGuid();
            var expected = new DisplayAutomationRecoveryState(
                PowerSource.Battery,
                id,
                new DisplayProfileSettings("fixed-60", false, "native")
                {
                    BrightnessPercent = 40,
                },
                OwnedRefreshRate: false,
                OwnedHdr: true,
                OwnedColorProfile: false,
                OwnedBrightness: true,
                ManualHold: true,
                AutomaticWritesPaused: true,
                UncertainRefreshRate: false,
                UncertainHdr: true,
                UncertainColorProfile: false,
                UncertainBrightness: true);

            Assert.True(store.SaveRecovery(expected));

            Assert.Equal(expected, store.LoadRecovery());
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void OversizedRecoveryCheckpointIsRejectedBeforePublishing()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"openblade-display-{Guid.NewGuid():N}.json");
        try
        {
            var store = new DisplayAutomationCheckpointStore(path);
            var oversized = new DisplayAutomationRecoveryState(
                PowerSource.PluggedIn,
                null,
                new DisplayProfileSettings(new string('x', 5000), null, null),
                OwnedRefreshRate: true,
                OwnedHdr: false,
                OwnedColorProfile: false,
                OwnedBrightness: false,
                ManualHold: false,
                AutomaticWritesPaused: true,
                UncertainRefreshRate: true,
                UncertainHdr: false,
                UncertainColorProfile: false,
                UncertainBrightness: false);

            Assert.False(store.SaveRecovery(oversized));
            Assert.False(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".tmp");
        }
    }
}
