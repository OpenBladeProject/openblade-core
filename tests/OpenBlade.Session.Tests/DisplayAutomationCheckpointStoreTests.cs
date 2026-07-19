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
}
