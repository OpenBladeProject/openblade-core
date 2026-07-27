using OpenBlade.Testing;

namespace OpenBlade.Contracts.Tests;

public sealed class PublicApiBaselineTests
{
    [Fact]
    public void ExportedApiMatchesReviewedBaseline()
    {
        string[] actual = PublicApiSurface.Capture(typeof(DeviceCapabilities).Assembly);
        if (PublicApiSurface.UpdateBaselineIfRequested(
            "tests/OpenBlade.Contracts.Tests/PublicApiBaseline.txt",
            actual))
        {
            return;
        }

        string[] expected = PublicApiSurface.ReadBaseline(
            Path.Combine(AppContext.BaseDirectory, "PublicApiBaseline.txt"));
        Assert.Equal(expected, actual);
    }
}
