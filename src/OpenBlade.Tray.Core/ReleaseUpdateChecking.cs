using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace OpenBlade.Tray;

public enum UpdateCheckInterval
{
    ManualOnly,
    EveryStartup,
    Daily,
    Weekly,
    Monthly,
}

public sealed record UpdateCheckIntervalOption(
    UpdateCheckInterval Interval,
    string Label);

public sealed record ReleaseUpdateInfo(
    Version Version,
    string TagName,
    Uri ReleasePageUri);

public enum ReleaseUpdateDisposition
{
    PublishedRelease,
    NoEligibleRelease,
    NotModified,
}

public sealed record ReleaseUpdateResult(
    ReleaseUpdateDisposition Disposition,
    ReleaseUpdateInfo? Release,
    string? EntityTag);

public interface IReleaseUpdateClient
{
    Task<ReleaseUpdateResult> GetLatestReleaseAsync(
        string? entityTag,
        CancellationToken cancellationToken);
}

public class ReleaseUpdateException : Exception
{
    public ReleaseUpdateException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public class ReleaseUpdateRetryException : ReleaseUpdateException
{
    public ReleaseUpdateRetryException(string message, DateTimeOffset retryAfter)
        : base(message)
    {
        RetryAfter = retryAfter;
    }

    public DateTimeOffset RetryAfter { get; }
}

public sealed class ReleaseUpdateRateLimitException : ReleaseUpdateRetryException
{
    public ReleaseUpdateRateLimitException(DateTimeOffset retryAfter)
        : base(
            "GitHub temporarily rate-limited the OpenBlade release check.",
            retryAfter)
    {
    }
}

public static class ReleaseVersion
{
    public static bool TryParse(string? value, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        ReadOnlySpan<char> candidate = value.Trim();
        if (candidate.Length > 0 && (candidate[0] is 'v' or 'V'))
        {
            candidate = candidate[1..];
        }

        int suffix = candidate.IndexOfAny('-', '+');
        if (suffix >= 0)
        {
            candidate = candidate[..suffix];
        }

        if (!Version.TryParse(candidate, out Version? parsed) || parsed is null)
        {
            return false;
        }

        version = new Version(
            parsed.Major,
            parsed.Minor,
            Math.Max(parsed.Build, 0),
            Math.Max(parsed.Revision, 0));
        return true;
    }
}

public static class UpdateCheckSchedule
{
    public static bool IsDue(
        UpdateCheckInterval interval,
        DateTimeOffset? lastSuccessfulCheckAt,
        DateTimeOffset? retryAfter,
        DateTimeOffset now)
    {
        if (!Enum.IsDefined(interval))
        {
            interval = UpdateCheckInterval.Daily;
        }

        if (interval == UpdateCheckInterval.ManualOnly
            || retryAfter is DateTimeOffset retryNotBefore
                && retryNotBefore > now)
        {
            return false;
        }

        if (interval == UpdateCheckInterval.EveryStartup
            || lastSuccessfulCheckAt is null)
        {
            return true;
        }

        if (lastSuccessfulCheckAt > now)
        {
            return true;
        }

        TimeSpan elapsed = now - lastSuccessfulCheckAt.Value;
        return elapsed >= interval switch
        {
            UpdateCheckInterval.Daily => TimeSpan.FromDays(1),
            UpdateCheckInterval.Weekly => TimeSpan.FromDays(7),
            UpdateCheckInterval.Monthly => TimeSpan.FromDays(30),
            _ => throw new InvalidOperationException(
                "The update-check interval is invalid."),
        };
    }
}

public sealed class GitHubReleaseUpdateClient : IReleaseUpdateClient
{
    public const string ApiVersion = "2022-11-28";
    private const int MaximumResponseBytes = 256 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly Uri LatestReleaseEndpoint = new(
        "https://api.github.com/repos/OSSBlade/openblade-core/releases/latest");
    private static readonly HttpClient SharedHttpClient = CreateSharedHttpClient();
    private readonly HttpClient httpClient;

    public GitHubReleaseUpdateClient()
        : this(SharedHttpClient)
    {
    }

    public GitHubReleaseUpdateClient(HttpClient httpClient)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<ReleaseUpdateResult> GetLatestReleaseAsync(
        string? entityTag,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseEndpoint);
            request.Headers.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.UserAgent.ParseAdd("OpenBlade");
            request.Headers.Add("X-GitHub-Api-Version", ApiVersion);
            if (EntityTagHeaderValue.TryParse(entityTag, out EntityTagHeaderValue? parsedTag))
            {
                request.Headers.IfNoneMatch.Add(parsedTag);
            }

            using HttpResponseMessage response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            string? responseTag = response.Headers.ETag?.ToString() ?? entityTag;
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return new(
                    ReleaseUpdateDisposition.NotModified,
                    Release: null,
                    responseTag);
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new(
                    ReleaseUpdateDisposition.NoEligibleRelease,
                    Release: null,
                    responseTag);
            }

            if (response.StatusCode is HttpStatusCode.Forbidden
                or HttpStatusCode.TooManyRequests)
            {
                throw new ReleaseUpdateRateLimitException(
                    ResolveRetryAfter(
                        response,
                        DateTimeOffset.UtcNow,
                        TimeSpan.FromMinutes(1)));
            }

            if ((int)response.StatusCode >= 500)
            {
                throw new ReleaseUpdateRetryException(
                    "GitHub is temporarily unavailable.",
                    ResolveRetryAfter(
                        response,
                        DateTimeOffset.UtcNow,
                        TimeSpan.FromMinutes(5)));
            }

            response.EnsureSuccessStatusCode();
            await response.Content.LoadIntoBufferAsync(
                MaximumResponseBytes,
                timeout.Token).ConfigureAwait(false);
            await using Stream content = await response.Content.ReadAsStreamAsync(
                timeout.Token).ConfigureAwait(false);
            using JsonDocument document = await JsonDocument.ParseAsync(
                content,
                cancellationToken: timeout.Token).ConfigureAwait(false);

            JsonElement root = document.RootElement;
            string tagName = RequiredString(root, "tag_name");
            bool draft = RequiredBoolean(root, "draft");
            bool prerelease = RequiredBoolean(root, "prerelease");
            if (draft
                || prerelease
                || !ReleaseVersion.TryParse(tagName, out Version version))
            {
                return new(
                    ReleaseUpdateDisposition.NoEligibleRelease,
                    Release: null,
                    responseTag);
            }

            string releasePage = RequiredString(root, "html_url");
            if (!Uri.TryCreate(releasePage, UriKind.Absolute, out Uri? releasePageUri)
                || !string.Equals(
                    releasePageUri.Scheme,
                    Uri.UriSchemeHttps,
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    releasePageUri.Host,
                    "github.com",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "GitHub returned an invalid OpenBlade release page.");
            }

            return new(
                ReleaseUpdateDisposition.PublishedRelease,
                new ReleaseUpdateInfo(version, tagName, releasePageUri),
                responseTag);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception) when (timeout.IsCancellationRequested)
        {
            throw new ReleaseUpdateException(
                "The OpenBlade release check timed out.",
                exception);
        }
        catch (ReleaseUpdateException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is HttpRequestException
                or IOException
                or JsonException
                or InvalidDataException
                or NotSupportedException)
        {
            throw new ReleaseUpdateException(
                "The OpenBlade release feed could not be read.",
                exception);
        }
    }

    private static DateTimeOffset ResolveRetryAfter(
        HttpResponseMessage response,
        DateTimeOffset now,
        TimeSpan fallbackDelay)
    {
        if (response.Headers.RetryAfter?.Date is DateTimeOffset retryDate)
        {
            return retryDate > now ? retryDate : now + fallbackDelay;
        }

        if (response.Headers.RetryAfter?.Delta is TimeSpan retryDelta
            && retryDelta > TimeSpan.Zero)
        {
            return now + retryDelta;
        }

        if (response.Headers.TryGetValues(
                "x-ratelimit-reset",
                out IEnumerable<string>? resetValues)
            && long.TryParse(resetValues.FirstOrDefault(), out long resetSeconds))
        {
            try
            {
                DateTimeOffset reset =
                    DateTimeOffset.FromUnixTimeSeconds(resetSeconds);
                if (reset > now)
                {
                    return reset;
                }
            }
            catch (ArgumentOutOfRangeException)
            {
            }
        }

        return now + fallbackDelay;
    }

    private static string RequiredString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement property)
            || property.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new InvalidDataException(
                $"GitHub release metadata is missing '{propertyName}'.");
        }

        return property.GetString()!;
    }

    private static bool RequiredBoolean(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement property)
            || property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException(
                $"GitHub release metadata is missing '{propertyName}'.");
        }

        return property.GetBoolean();
    }

    private static HttpClient CreateSharedHttpClient() =>
        new(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
}
