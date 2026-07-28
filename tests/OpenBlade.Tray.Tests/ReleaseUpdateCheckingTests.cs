using System.Net;
using System.Text;

namespace OpenBlade.Tray.Tests;

public sealed class ReleaseUpdateCheckingTests
{
    [Theory]
    [InlineData("v1.2.3", 1, 2, 3, 0)]
    [InlineData("V2.0", 2, 0, 0, 0)]
    [InlineData("0.1.0+abcdef", 0, 1, 0, 0)]
    [InlineData("3.4.5.6-preview.1", 3, 4, 5, 6)]
    public void ReleaseVersionParsesAndNormalizesComparableTags(
        string tag,
        int major,
        int minor,
        int build,
        int revision)
    {
        Assert.True(ReleaseVersion.TryParse(tag, out Version version));
        Assert.Equal(new Version(major, minor, build, revision), version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("v")]
    [InlineData("not-a-version")]
    public void ReleaseVersionRejectsInvalidTagsWithZeroFallback(string tag)
    {
        Assert.False(ReleaseVersion.TryParse(tag, out Version version));
        Assert.Equal(new Version(0, 0, 0, 0), version);
    }

    [Fact]
    public void ScheduleHonorsIntervalsAndRateLimitBackoff()
    {
        var now = new DateTimeOffset(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);

        Assert.False(IsDue(UpdateCheckInterval.ManualOnly, null, null, now));
        Assert.True(IsDue(UpdateCheckInterval.EveryStartup, now, null, now));
        Assert.True(IsDue(UpdateCheckInterval.Daily, null, null, now));
        Assert.False(IsDue(
            UpdateCheckInterval.Daily,
            now.AddHours(-23),
            null,
            now));
        Assert.True(IsDue(
            UpdateCheckInterval.Daily,
            now.AddDays(-1),
            null,
            now));
        Assert.False(IsDue(
            UpdateCheckInterval.Weekly,
            now.AddDays(-6),
            null,
            now));
        Assert.True(IsDue(
            UpdateCheckInterval.Weekly,
            now.AddDays(-7),
            null,
            now));
        Assert.False(IsDue(
            UpdateCheckInterval.Monthly,
            now.AddDays(-29),
            null,
            now));
        Assert.True(IsDue(
            UpdateCheckInterval.Monthly,
            now.AddDays(-30),
            null,
            now));
        Assert.True(IsDue(
            UpdateCheckInterval.Daily,
            now.AddMinutes(1),
            null,
            now));
        Assert.False(IsDue(
            UpdateCheckInterval.EveryStartup,
            null,
            now.AddMinutes(1),
            now));
    }

    [Fact]
    public async Task GitHubClientReadsPublishedReleaseWithConditionalHeaders()
    {
        var handler = new RecordingHandler(
            HttpStatusCode.OK,
            ValidReleaseJson,
            response =>
            {
                response.Headers.ETag =
                    new System.Net.Http.Headers.EntityTagHeaderValue("\"new-tag\"");
            });
        using var httpClient = new HttpClient(handler);
        var client = new GitHubReleaseUpdateClient(httpClient);

        ReleaseUpdateResult result = await client.GetLatestReleaseAsync(
            "\"old-tag\"",
            CancellationToken.None);

        Assert.Equal(ReleaseUpdateDisposition.PublishedRelease, result.Disposition);
        Assert.Equal(new Version(1, 4, 0, 0), result.Release?.Version);
        Assert.Equal("v1.4.0", result.Release?.TagName);
        Assert.Equal("\"new-tag\"", result.EntityTag);
        Assert.Equal(
            "https://api.github.com/repos/OSSBlade/openblade-core/releases/latest",
            handler.RequestUri?.AbsoluteUri);
        Assert.Contains(
            handler.Accept,
            value => value == "application/vnd.github+json");
        Assert.Equal("\"old-tag\"", handler.IfNoneMatch);
        Assert.Equal(GitHubReleaseUpdateClient.ApiVersion, handler.ApiVersion);
        Assert.Equal("2022-11-28", handler.ApiVersion);
        Assert.Contains("OpenBlade", handler.UserAgent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GitHubClientTreatsNotModifiedAsSuccessfulConditionalCheck()
    {
        using var httpClient = new HttpClient(
            new RecordingHandler(HttpStatusCode.NotModified, string.Empty));
        var client = new GitHubReleaseUpdateClient(httpClient);

        ReleaseUpdateResult result = await client.GetLatestReleaseAsync(
            "\"cached\"",
            CancellationToken.None);

        Assert.Equal(ReleaseUpdateDisposition.NotModified, result.Disposition);
        Assert.Null(result.Release);
        Assert.Equal("\"cached\"", result.EntityTag);
    }

    [Theory]
    [InlineData(
        """{"tag_name":"v1.0.0","html_url":"https://github.com/OSSBlade/openblade-core/releases","draft":true,"prerelease":false}""")]
    [InlineData(
        """{"tag_name":"v1.0.0","html_url":"https://github.com/OSSBlade/openblade-core/releases","draft":false,"prerelease":true}""")]
    [InlineData(
        """{"tag_name":"bad","html_url":"https://github.com/OSSBlade/openblade-core/releases","draft":false,"prerelease":false}""")]
    public async Task GitHubClientTreatsIneligibleMetadataAsSuccessfulCheck(string json)
    {
        using var httpClient = new HttpClient(
            new RecordingHandler(HttpStatusCode.OK, json));
        var client = new GitHubReleaseUpdateClient(httpClient);

        ReleaseUpdateResult result = await client.GetLatestReleaseAsync(
            null,
            CancellationToken.None);

        Assert.Equal(ReleaseUpdateDisposition.NoEligibleRelease, result.Disposition);
        Assert.Null(result.Release);
    }

    [Fact]
    public async Task GitHubClientTreatsMissingPublishedReleaseAsSuccessfulCheck()
    {
        using var httpClient = new HttpClient(
            new RecordingHandler(HttpStatusCode.NotFound, "{}"));
        var client = new GitHubReleaseUpdateClient(httpClient);

        ReleaseUpdateResult result = await client.GetLatestReleaseAsync(
            null,
            CancellationToken.None);

        Assert.Equal(ReleaseUpdateDisposition.NoEligibleRelease, result.Disposition);
    }

    [Theory]
    [InlineData(
        """{"tag_name":"v1.0.0","html_url":"http://github.com/OSSBlade/openblade-core/releases/tag/v1.0.0","draft":false,"prerelease":false}""")]
    [InlineData(
        """{"tag_name":"v1.0.0","html_url":"https://example.com/release","draft":false,"prerelease":false}""")]
    public async Task GitHubClientRejectsUntrustedReleaseUrl(string json)
    {
        using var httpClient = new HttpClient(
            new RecordingHandler(HttpStatusCode.OK, json));
        var client = new GitHubReleaseUpdateClient(httpClient);

        await Assert.ThrowsAsync<ReleaseUpdateException>(
            () => client.GetLatestReleaseAsync(null, CancellationToken.None));
    }

    [Theory]
    [InlineData("{not-json")]
    [InlineData("""{"tag_name":5,"html_url":"https://github.com/x","draft":false,"prerelease":false}""")]
    [InlineData("""{"tag_name":"v1.0.0","html_url":"https://github.com/x","draft":"no","prerelease":false}""")]
    public async Task GitHubClientWrapsMalformedMetadata(string json)
    {
        using var httpClient = new HttpClient(
            new RecordingHandler(HttpStatusCode.OK, json));
        var client = new GitHubReleaseUpdateClient(httpClient);

        await Assert.ThrowsAsync<ReleaseUpdateException>(
            () => client.GetLatestReleaseAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task GitHubClientRejectsOversizedResponse()
    {
        string oversized = "{\"tag_name\":\"v1.0.0\",\"html_url\":\"https://github.com/"
            + new string('x', 300_000)
            + "\",\"draft\":false,\"prerelease\":false}";
        using var httpClient = new HttpClient(
            new RecordingHandler(HttpStatusCode.OK, oversized));
        var client = new GitHubReleaseUpdateClient(httpClient);

        await Assert.ThrowsAsync<ReleaseUpdateException>(
            () => client.GetLatestReleaseAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task GitHubClientReportsServerFailure()
    {
        using var httpClient = new HttpClient(
            new RecordingHandler(HttpStatusCode.InternalServerError, "{}"));
        var client = new GitHubReleaseUpdateClient(httpClient);

        await Assert.ThrowsAsync<ReleaseUpdateRetryException>(
            () => client.GetLatestReleaseAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task GitHubClientHonorsServerRetryAfter()
    {
        using var httpClient = new HttpClient(
            new RecordingHandler(
                HttpStatusCode.ServiceUnavailable,
                "{}",
                response => response.Headers.RetryAfter =
                    new System.Net.Http.Headers.RetryConditionHeaderValue(
                        TimeSpan.FromMinutes(3))));
        var client = new GitHubReleaseUpdateClient(httpClient);
        DateTimeOffset before = DateTimeOffset.UtcNow;

        ReleaseUpdateRetryException exception =
            await Assert.ThrowsAsync<ReleaseUpdateRetryException>(
                () => client.GetLatestReleaseAsync(null, CancellationToken.None));

        Assert.InRange(
            exception.RetryAfter,
            before.AddMinutes(2),
            before.AddMinutes(4));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task GitHubClientReportsRateLimitWithRetryAfter(
        HttpStatusCode statusCode)
    {
        using var httpClient = new HttpClient(
            new RecordingHandler(
                statusCode,
                "{}",
                response => response.Headers.RetryAfter =
                    new System.Net.Http.Headers.RetryConditionHeaderValue(
                        TimeSpan.FromMinutes(5))));
        var client = new GitHubReleaseUpdateClient(httpClient);
        DateTimeOffset before = DateTimeOffset.UtcNow;

        ReleaseUpdateRateLimitException exception =
            await Assert.ThrowsAsync<ReleaseUpdateRateLimitException>(
                () => client.GetLatestReleaseAsync(null, CancellationToken.None));

        Assert.InRange(
            exception.RetryAfter,
            before.AddMinutes(4),
            before.AddMinutes(6));
    }

    [Fact]
    public async Task GitHubClientPreservesCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var httpClient = new HttpClient(
            new RecordingHandler(HttpStatusCode.OK, ValidReleaseJson));
        var client = new GitHubReleaseUpdateClient(httpClient);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetLatestReleaseAsync(null, cancellation.Token));
    }

    private static bool IsDue(
        UpdateCheckInterval interval,
        DateTimeOffset? lastSuccessfulCheckAt,
        DateTimeOffset? retryAfter,
        DateTimeOffset now) =>
        UpdateCheckSchedule.IsDue(
            interval,
            lastSuccessfulCheckAt,
            retryAfter,
            now);

    private const string ValidReleaseJson =
        """
        {
          "tag_name": "v1.4.0",
          "html_url": "https://github.com/OSSBlade/openblade-core/releases/tag/v1.4.0",
          "draft": false,
          "prerelease": false
        }
        """;

    private sealed class RecordingHandler(
        HttpStatusCode statusCode,
        string responseBody,
        Action<HttpResponseMessage>? configureResponse = null) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        public string[] Accept { get; private set; } = [];

        public string UserAgent { get; private set; } = string.Empty;

        public string? IfNoneMatch { get; private set; }

        public string? ApiVersion { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestUri = request.RequestUri;
            Accept = request.Headers.Accept
                .Select(value => value.MediaType ?? string.Empty)
                .ToArray();
            UserAgent = request.Headers.UserAgent.ToString();
            IfNoneMatch = request.Headers.IfNoneMatch.SingleOrDefault()?.ToString();
            ApiVersion = request.Headers.TryGetValues(
                "X-GitHub-Api-Version",
                out IEnumerable<string>? values)
                ? values.SingleOrDefault()
                : null;
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(
                    responseBody,
                    Encoding.UTF8,
                    "application/json"),
            };
            configureResponse?.Invoke(response);
            return Task.FromResult(response);
        }
    }
}
