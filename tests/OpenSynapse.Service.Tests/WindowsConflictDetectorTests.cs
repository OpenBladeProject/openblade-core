using OpenSynapse.Service;

namespace OpenSynapse.Service.Tests;

public sealed class WindowsConflictDetectorTests
{
    [Fact]
    public void GetRunningConflicts_ReturnsOnlyKnownRazerControllers()
    {
        WindowsConflictDetector detector = new(
            new FakeProcessSource(["notepad", "RazerAppEngine", "razerwdl"]));

        IReadOnlyList<string> conflicts = detector.GetRunningConflicts();

        Assert.Equal(["RazerAppEngine", "razerwdl"], conflicts);
    }

    private sealed class FakeProcessSource(IReadOnlyList<string> names) : IRunningProcessSource
    {
        public IReadOnlyList<string> GetProcessNames() => names;
    }
}
