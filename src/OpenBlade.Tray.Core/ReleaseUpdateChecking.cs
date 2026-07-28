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

public interface IReleaseUpdateClient
{
    Task<ReleaseUpdateInfo> GetLatestReleaseAsync(CancellationToken cancellationToken);
}

public sealed class ReleaseUpdateException : Exception
{
    public ReleaseUpdateException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public static class ReleaseVersion
{
    public static bool TryParse(string? value, out Version version)
    {
        version = new Version();
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

        version = parsed;
        return true;
    }
}

public static class UpdateCheckSchedule
{
    public static bool IsDue(
        UpdateCheckInterval interval,
        DateTimeOffset? lastCheckAt,
        DateTimeOffset now)
    {
        if (!Enum.IsDefined(interval))
        {
            interval = UpdateCheckInterval.Daily;
        }

        if (interval == UpdateCheckInterval.ManualOnly)
        {
            return false;
        }

        if (interval == UpdateCheckInterval.EveryStartup || lastCheckAt is null)
        {
            return true;
        }

        if (lastCheckAt > now)
        {
            return true;
        }

        TimeSpan elapsed = now - lastCheckAt.Value;
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
    private const int MaximumResponseBytes = 256 * 1024;
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

    public async Task<ReleaseUpdateInfo> GetLatestReleaseAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseEndpoint);
            request.Headers.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.UserAgent.ParseAdd("OpenBlade");
            request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");

            using HttpResponseMessage response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new ReleaseUpdateException(
                    "The OpenBlade release feed does not contain a published release.");
            }

            response.EnsureSuccessStatusCode();
            await response.Content.LoadIntoBufferAsync(
                MaximumResponseBytes,
                cancellationToken).ConfigureAwait(false);
            await using Stream content = await response.Content.ReadAsStreamAsync(
                cancellationToken).ConfigureAwait(false);
            using JsonDocument document = await JsonDocument.ParseAsync(
                content,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            JsonElement root = document.RootElement;
            string tagName = RequiredString(root, "tag_name");
            if (!ReleaseVersion.TryParse(tagName, out Version version))
            {
                throw new InvalidDataException(
                    $"GitHub returned an invalid OpenBlade release tag '{tagName}'.");
            }

            if (RequiredBoolean(root, "draft") || RequiredBoolean(root, "prerelease"))
            {
                throw new InvalidDataException(
                    "GitHub returned a non-public OpenBlade release.");
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

            return new ReleaseUpdateInfo(version, tagName, releasePageUri);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ReleaseUpdateException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is HttpRequestException
                or JsonException
                or InvalidDataException
                or NotSupportedException
                or TaskCanceledException)
        {
            throw new ReleaseUpdateException(
                "The OpenBlade release feed could not be read.",
                exception);
        }
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
        new()
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
}
