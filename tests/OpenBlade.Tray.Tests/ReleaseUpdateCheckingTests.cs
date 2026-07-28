using System.Net;
using System.Text;

namespace OpenBlade.Tray.Tests;

public sealed class ReleaseUpdateCheckingTests
{
    [Theory]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("V2.0", 2, 0, -1)]
    [InlineData("0.1.0+abcdef", 0, 1, 0)]
    [InlineData("3.4.5-preview.1", 3, 4, 5)]
    public void ReleaseVersionParsesComparableTags(
        string tag,
        int major,
        int minor,
        int build)
    {
        Assert.True(ReleaseVersion.TryParse(tag, out Version version));
        Assert.Equal(major, version.Major);
        Assert.Equal(minor, version.Minor);
        Assert.Equal(build, version.Build);
    }

    [Theory]
    [InlineData("")]
    [InlineData("v")]
    [InlineData("not-a-version")]
    public void ReleaseVersionRejectsInvalidTags(string tag) =>
        Assert.False(ReleaseVersion.TryParse(tag, out _));

    [Fact]
    public void ScheduleHonorsEverySupportedInterval()
    {
        var now = new DateTimeOffset(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);

        Assert.False(UpdateCheckSchedule.IsDue(
            UpdateCheckInterval.ManualOnly,
            null,
            now));
        Assert.True(UpdateCheckSchedule.IsDue(
            UpdateCheckInterval.EveryStartup,
            now,
            now));
        Assert.True(UpdateCheckSchedule.IsDue(
            UpdateCheckInterval.Daily,
            null,
            now));
        Assert.False(UpdateCheckSchedule.IsDue(
            UpdateCheckInterval.Daily,
            now.AddHours(-23),
            now));
        Assert.True(UpdateCheckSchedule.IsDue(
            UpdateCheckInterval.Daily,
            now.AddDays(-1),
            now));
        Assert.False(UpdateCheckSchedule.IsDue(
            UpdateCheckInterval.Weekly,
            now.AddDays(-6),
            now));
        Assert.True(UpdateCheckSchedule.IsDue(
            UpdateCheckInterval.Weekly,
            now.AddDays(-7),
            now));
        Assert.False(UpdateCheckSchedule.IsDue(
            UpdateCheckInterval.Monthly,
            now.AddDays(-29),
            now));
        Assert.True(UpdateCheckSchedule.IsDue(
            UpdateCheckInterval.Monthly,
            now.AddDays(-30),
            now));
        Assert.True(UpdateCheckSchedule.IsDue(
            UpdateCheckInterval.Daily,
            now.AddMinutes(1),
            now));
    }

    [Fact]
    public async Task GitHubClientReadsPublishedReleaseFromFixedFeed()
    {
        var handler = new RecordingHandler(
            HttpStatusCode.OK,
            """
            {
              "tag_name": "v1.4.0",
              "html_url": "https://github.com/OSSBlade/openblade-core/releases/tag/v1.4.0",
              "draft": false,
              "prerelease": false
            }
            """);
        using var httpClient = new HttpClient(handler);
        var client = new GitHubReleaseUpdateClient(httpClient);

        ReleaseUpdateInfo release = await client.GetLatestReleaseAsync(
            CancellationToken.None);

        Assert.Equal(new Version(1, 4, 0), release.Version);
        Assert.Equal("v1.4.0", release.TagName);
        Assert.Equal(
            "https://github.com/OSSBlade/openblade-core/releases/tag/v1.4.0",
            release.ReleasePageUri.AbsoluteUri);
        Assert.Equal(
            "https://api.github.com/repos/OSSBlade/openblade-core/releases/latest",
            handler.RequestUri?.AbsoluteUri);
        Assert.Contains(
            handler.Accept,
            value => value == "application/vnd.github+json");
        Assert.Contains("OpenBlade", handler.UserAgent, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        """{"tag_name":"v1.0.0","html_url":"http://github.com/OSSBlade/openblade-core/releases/tag/v1.0.0","draft":false,"prerelease":false}""")]
    [InlineData(
        """{"tag_name":"v1.0.0","html_url":"https://example.com/release","draft":false,"prerelease":false}""")]
    [InlineData(
        """{"tag_name":"bad","html_url":"https://github.com/OSSBlade/openblade-core/releases","draft":false,"prerelease":false}""")]
    [InlineData(
        """{"tag_name":"v1.0.0","html_url":"https://github.com/OSSBlade/openblade-core/releases","draft":false,"prerelease":true}""")]
    public async Task GitHubClientRejectsUntrustedMetadata(string json)
    {
        using var httpClient = new HttpClient(
            new RecordingHandler(HttpStatusCode.OK, json));
        var client = new GitHubReleaseUpdateClient(httpClient);

        await Assert.ThrowsAsync<ReleaseUpdateException>(
            () => client.GetLatestReleaseAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GitHubClientReportsMissingPublishedRelease()
    {
        using var httpClient = new HttpClient(
            new RecordingHandler(HttpStatusCode.NotFound, "{}"));
        var client = new GitHubReleaseUpdateClient(httpClient);

        await Assert.ThrowsAsync<ReleaseUpdateException>(
            () => client.GetLatestReleaseAsync(CancellationToken.None));
    }

    private sealed class RecordingHandler(
        HttpStatusCode statusCode,
        string responseBody) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        public string[] Accept { get; private set; } = [];

        public string UserAgent { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Accept = request.Headers.Accept
                .Select(value => value.MediaType ?? string.Empty)
                .ToArray();
            UserAgent = request.Headers.UserAgent.ToString();
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(
                    responseBody,
                    Encoding.UTF8,
                    "application/json"),
            });
        }
    }
}
