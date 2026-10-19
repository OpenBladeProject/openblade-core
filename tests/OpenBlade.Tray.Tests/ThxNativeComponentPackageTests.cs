using System.Net;
using System.Security.Cryptography;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class ThxNativeComponentPackageTests : IDisposable
{
    private static readonly byte[] Content = "reviewed THX fixture"u8.ToArray();
    private static readonly string Hash = Convert.ToHexString(SHA256.HashData(Content));
    private readonly string directory = Path.Combine(Path.GetTempPath(), "OpenBlade.ThxPackage.Tests", Guid.NewGuid().ToString("N"));
    private string Destination => Path.Combine(directory, ThxNativeComponentPackage.FileName);

    [Fact]
    public void DescriptorPinsTheReviewedVendorArtifactAndStableUserPath()
    {
        Assert.Equal("https://app-assets.razer.com/files/synapse/devices/common/THX/ThxV4Native_v1.1.1.0.dll", ThxNativeComponentPackage.DownloadUri.AbsoluteUri);
        Assert.Equal(13684872, ThxNativeComponentPackage.Length);
        Assert.Equal("8E8F6923624762935D70B72F58BEC00808DB84DF740340047919B53331130A0A", ThxNativeComponentPackage.Sha256);
        Assert.Equal("3FDDBD6F820144422494342EF44898D732E068DC", ThxNativeComponentPackage.SignerThumbprint);
        Assert.Equal("CN=Razer USA Ltd., O=Razer USA Ltd., L=Irvine, S=California, C=US", ThxNativeComponentPackage.SignerSubject);
        Assert.EndsWith(Path.Combine("OpenBlade", "Components", "THX", "1.1.1.0", ThxNativeComponentPackage.FileName), ThxNativeComponentPackage.GetInstalledPath());
        Assert.EndsWith(Path.Combine("Razer", "RazerAppEngine", "User Data", "Apps", "Common", "thxv4common", ThxNativeComponentPackage.FileName), ThxNativeComponentPackage.GetSynapsePath());
    }

    [Theory]
    [InlineData(true, true, "owned")]
    [InlineData(true, false, "owned")]
    [InlineData(false, true, "synapse")]
    [InlineData(false, false, null)]
    public void ResolverUsesVerifiedOwnedComponentThenVerifiedSynapseFallback(bool owned, bool synapse, string? expected)
    {
        bool result = ThxNativeComponentPackage.TryResolve("owned", "synapse", path => path == "owned" ? owned : synapse, out string? resolved);
        Assert.Equal(expected is not null, result);
        Assert.Equal(expected, resolved);
    }

    [Fact]
    public void ResolverRejectsActualCorruptOrMissingComponentsWithoutThrowing()
    {
        WritePrior();
        Assert.False(ThxNativeComponentPackage.TryResolve(Destination, Path.Combine(directory, "missing.dll"), out string? path));
        Assert.Null(path);
        Assert.False(ThxNativeComponentPackage.TryResolve(Path.Combine(directory, "missing.dll"), Destination, out path));
        Assert.Null(path);
    }

    [Fact]
    public async Task VerifiedDownloadPublishesAfterTrustWhileStagingRemainsLocked()
    {
        using var client = Client(Content);
        bool trusted = false;
        string path = await ThxNativeComponentPackage.AcquireVerifiedAsync(Destination, client, (staging, handle, _) =>
        {
            Assert.False(handle.IsInvalid);
            Assert.False(File.Exists(Destination));
            Assert.Throws<IOException>(() => File.WriteAllBytes(staging, [0]));
            Assert.Throws<IOException>(() => File.Delete(staging));
            trusted = true;
        }, Content.Length, Hash, CancellationToken.None);
        Assert.True(trusted);
        Assert.Equal(Destination, path);
        Assert.True(File.Exists(path), string.Join(" | ", Directory.GetFiles(directory)));
        Assert.Equal(Content, File.ReadAllBytes(path));
        Assert.Single(Directory.GetFiles(directory));
    }

    [Fact]
    public async Task VerifiedPublicationReplacesOldFileAtomically()
    {
        WritePrior();
        using var client = Client(Content);
        await ThxNativeComponentPackage.AcquireVerifiedAsync(Destination, client, (_, _, _) =>
            Assert.Equal("prior", File.ReadAllText(Destination)), Content.Length, Hash, CancellationToken.None);
        Assert.Equal(Content, File.ReadAllBytes(Destination));
        Assert.Single(Directory.GetFiles(directory));
    }

    [Fact]
    public async Task CorruptionNeverReachesTrustAndPreservesPriorComponent()
    {
        WritePrior();
        using var client = Client(new byte[Content.Length]);
        await Assert.ThrowsAsync<InvalidDataException>(() => ThxNativeComponentPackage.AcquireVerifiedAsync(
            Destination, client, (_, _, _) => Assert.Fail("Corrupt bytes reached signature verification."),
            Content.Length, Hash, CancellationToken.None));
        AssertPriorOnly();
    }

    [Fact]
    public async Task FailedTrustPreservesPriorComponentAndRemovesOnlyOwnStaging()
    {
        WritePrior();
        using var client = Client(Content);
        await Assert.ThrowsAsync<CryptographicException>(() => ThxNativeComponentPackage.AcquireVerifiedAsync(
            Destination, client, (_, _, _) => throw new CryptographicException("untrusted"),
            Content.Length, Hash, CancellationToken.None));
        AssertPriorOnly();
    }

    [Fact]
    public async Task CancellationAfterTrustPreventsPublication()
    {
        WritePrior();
        using var cancel = new CancellationTokenSource();
        using var client = Client(Content);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ThxNativeComponentPackage.AcquireVerifiedAsync(
            Destination, client, (_, _, _) => cancel.Cancel(), Content.Length, Hash, cancel.Token));
        AssertPriorOnly();
    }

    [Fact]
    public async Task CancellationDuringResponsePreservesPriorComponent()
    {
        WritePrior();
        using var cancel = new CancellationTokenSource();
        using var client = new HttpClient(new Handler((_, token) =>
        {
            cancel.Cancel();
            token.ThrowIfCancellationRequested();
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ThxNativeComponentPackage.AcquireVerifiedAsync(
            Destination, client, (_, _, _) => Assert.Fail("Canceled download reached trust."), Content.Length, Hash, cancel.Token));
        AssertPriorOnly();
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(2, false)]
    [InlineData(200, false)]
    public async Task IncorrectLengthIsRejectedWithAndWithoutContentLength(int length, bool header)
    {
        WritePrior();
        using var client = new HttpClient(new Handler((_, _) =>
        {
            var content = new StreamContent(new NonSeekableStream(new byte[length]));
            if (header)
            {
                content.Headers.ContentLength = length;
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }));
        await Assert.ThrowsAsync<InvalidDataException>(() => ThxNativeComponentPackage.AcquireVerifiedAsync(
            Destination, client, (_, _, _) => Assert.Fail("Wrong length reached trust."), Content.Length, Hash, CancellationToken.None));
        AssertPriorOnly();
    }

    [Theory]
    [InlineData("http://app-assets.razer.com/asset")]
    [InlineData("https://app-assets.razer.com.evil.test/asset")]
    [InlineData("https://evil.test/asset")]
    [InlineData("https://user@app-assets.razer.com/asset")]
    [InlineData("https://app-assets.razer.com:444/asset")]
    [InlineData("https://app-assets.razer.com/asset#fragment")]
    public async Task UnsafeRedirectIsRejectedBeforeSecondRequest(string location)
    {
        int calls = 0;
        using var client = new HttpClient(new Handler((_, _) =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri(location) } };
        }));
        await Assert.ThrowsAsync<InvalidDataException>(() => ThxNativeComponentPackage.DownloadAsync(client, new MemoryStream(), Content.Length, CancellationToken.None));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RelativeVendorRedirectWorksButRedirectLoopsAreBounded()
    {
        int calls = 0;
        using var client = new HttpClient(new Handler((request, _) =>
        {
            calls++;
            if (calls == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.TemporaryRedirect) { Headers = { Location = new Uri("/reviewed.dll", UriKind.Relative) } };
            }
            Assert.Equal("https://app-assets.razer.com/reviewed.dll", request.RequestUri!.AbsoluteUri);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Content) };
        }));
        using var output = new MemoryStream();
        await ThxNativeComponentPackage.DownloadAsync(client, output, Content.Length, CancellationToken.None);
        Assert.Equal(Content, output.ToArray());
        int loopCalls = 0;
        using var loopClient = new HttpClient(new Handler((_, _) =>
        {
            loopCalls++;
            return new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = ThxNativeComponentPackage.DownloadUri } };
        }));
        await Assert.ThrowsAsync<InvalidDataException>(() => ThxNativeComponentPackage.DownloadAsync(loopClient, output, Content.Length, CancellationToken.None));
        Assert.Equal(4, loopCalls);
    }

    [Fact]
    public async Task RealWindowsTrustRejectsUnsignedFileWithoutPublishing()
    {
        WritePrior();
        using var client = Client(Content);
        await Assert.ThrowsAsync<CryptographicException>(() => ThxNativeComponentPackage.AcquireVerifiedAsync(
            Destination, client, ThxNativeComponentTrust.Verify, Content.Length, Hash, CancellationToken.None));
        AssertPriorOnly();
    }

    [Fact]
    public async Task LockedPriorComponentIsPreservedOnPublicationFailure()
    {
        WritePrior();
        using var prior = new FileStream(Destination, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var client = Client(Content);
        await Assert.ThrowsAsync<IOException>(() => ThxNativeComponentPackage.AcquireVerifiedAsync(
            Destination, client, (_, _, _) => { }, Content.Length, Hash, CancellationToken.None));
        AssertPriorOnly();
    }

    [Fact]
    public async Task AlreadyCanceledRequestCreatesNothingAndMakesNoRequest()
    {
        using var client = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("Unexpected request")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ThxNativeComponentPackage.AcquireVerifiedAsync(
            Destination, client, (_, _, _) => { }, Content.Length, Hash, new CancellationToken(true)));
        Assert.False(Directory.Exists(directory));
    }

    private sealed class NonSeekableStream(byte[] content) : MemoryStream(content)
    {
        public override bool CanSeek => false;
    }

    private static HttpClient Client(byte[] content) => new(new Handler((_, _) =>
        new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) }));

    private void WritePrior()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Destination, "prior");
    }

    private void AssertPriorOnly()
    {
        Assert.Equal("prior", File.ReadAllText(Destination));
        Assert.Single(Directory.GetFiles(directory));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request, cancellationToken));
    }
}
