using OpenBlade.Testing;

namespace OpenBlade.Session.Tests;

public sealed class PublicApiBaselineTests
{
    [Fact]
    public void ExportedApiMatchesReviewedBaseline()
    {
        string[] actual = PublicApiSurface.Capture(typeof(SessionDisplayIpc).Assembly);
        if (PublicApiSurface.UpdateBaselineIfRequested(
            "tests/OpenBlade.Session.Tests/PublicApiBaseline.txt",
            actual))
        {
            return;
        }

        string[] expected = PublicApiSurface.ReadBaseline(
            Path.Combine(AppContext.BaseDirectory, "PublicApiBaseline.txt"));
        Assert.Equal(expected, actual);
    }
}
