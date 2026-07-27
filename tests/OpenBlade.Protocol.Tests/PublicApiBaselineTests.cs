using OpenBlade.Testing;

namespace OpenBlade.Protocol.Tests;

public sealed class PublicApiBaselineTests
{
    [Fact]
    public void ExportedApiMatchesReviewedBaseline()
    {
        string[] actual = PublicApiSurface.Capture(typeof(TargetBladeDevice).Assembly);
        if (PublicApiSurface.UpdateBaselineIfRequested(
            "tests/OpenBlade.Protocol.Tests/PublicApiBaseline.txt",
            actual))
        {
            return;
        }

        string[] expected = PublicApiSurface.ReadBaseline(
            Path.Combine(AppContext.BaseDirectory, "PublicApiBaseline.txt"));
        Assert.Equal(expected, actual);
    }
}
