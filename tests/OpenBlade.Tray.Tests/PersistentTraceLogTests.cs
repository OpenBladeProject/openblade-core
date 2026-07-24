using System.Diagnostics;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class PersistentTraceLogTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        $"OpenBlade.PersistentTraceLog.Tests-{Guid.NewGuid():N}");

    [Fact]
    public void TraceEvent_PersistsBoundedRedactedLine()
    {
        const string secret = "private-user-value";
        using var listener = new PersistentTraceLog(
            "OpenBlade.Tray",
            "1.2.3+test",
            directory,
            [secret]);

        listener.TraceEvent(
            null,
            "test",
            TraceEventType.Warning,
            0,
            "Could not open {0}",
            secret);

        string content = File.ReadAllText(listener.LogPath);
        Assert.Contains("[OpenBlade.Tray] [1.2.3+test] [Warning]", content);
        Assert.Contains("Could not open [REDACTED]", content);
        Assert.DoesNotContain(secret, content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TraceEvent_RotatesAtBoundedSize()
    {
        using var listener = new PersistentTraceLog(
            "OpenBlade.SessionAgent",
            "1.2.3+test",
            directory,
            []);
        string message = new('x', 16 * 1024);

        for (int index = 0; index < 12; index++)
        {
            listener.TraceEvent(
                null,
                "test",
                TraceEventType.Error,
                index,
                message);
        }

        string previousPath = Path.Combine(
            directory,
            "OpenBlade.SessionAgent.previous.log");
        Assert.True(File.Exists(previousPath));
        Assert.InRange(new FileInfo(listener.LogPath).Length, 1, 128 * 1024);
        Assert.InRange(new FileInfo(previousPath).Length, 1, 128 * 1024);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
