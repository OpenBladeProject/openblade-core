using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace OpenBlade.Tray.Tests;

public sealed class ThxNativeComponentTrustTests
{
    private const string WindowsPublisher = "CN=Microsoft Windows, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";

    [Fact]
    public void PublisherPolicyAllowsCertificateRotationButRequiresExactVerifiedSubject()
    {
        Assert.True(ThxNativeComponentTrust.MatchesSigner(WindowsPublisher, "OLD-CERTIFICATE", WindowsPublisher, null));
        Assert.True(ThxNativeComponentTrust.MatchesSigner(WindowsPublisher, "NEW-CERTIFICATE", WindowsPublisher, null));
        Assert.False(ThxNativeComponentTrust.MatchesSigner("CN=Another Publisher", "NEW-CERTIFICATE", WindowsPublisher, null));
        Assert.False(ThxNativeComponentTrust.MatchesSigner(WindowsPublisher + ", OU=Other", "NEW-CERTIFICATE", WindowsPublisher, null));
        Assert.False(ThxNativeComponentTrust.MatchesSigner("", "NEW-CERTIFICATE", "", null));
    }

    [Fact]
    public void NativeClientPolicyStillRequiresBothReviewedPins()
    {
        string subject = ThxNativeComponentPackage.SignerSubject;
        string thumbprint = ThxNativeComponentPackage.SignerThumbprint;
        Assert.True(ThxNativeComponentTrust.MatchesSigner(subject, thumbprint, subject, thumbprint));
        Assert.False(ThxNativeComponentTrust.MatchesSigner(subject, "DIFFERENT-CERTIFICATE", subject, thumbprint));
        Assert.False(ThxNativeComponentTrust.MatchesSigner(WindowsPublisher, thumbprint, subject, thumbprint));
        Assert.False(ThxNativeComponentTrust.MatchesSigner(subject, "", subject, thumbprint));
        Assert.False(ThxNativeComponentTrust.MatchesSigner(subject, thumbprint, subject, ""));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ExistingPinnedEntryCannotSelectPublisherOnlyPolicyByOmittingItsPin(string? pin)
    {
        // An invalid handle proves the missing pin is rejected before native trust,
        // rather than being reinterpreted as the publisher-only entry point.
        using var handle = new SafeFileHandle(nint.Zero, ownsHandle: false);
        var error = Assert.Throws<CryptographicException>(() => ThxNativeComponentTrust.Verify(
            "unused", handle, WindowsPublisher, pin!, CancellationToken.None));
        Assert.Contains("requires its reviewed signer thumbprint", error.Message);
    }

    [Fact]
    public void PublisherEntryRequiresASubjectAndPreservesEarlyCancellation()
    {
        using var handle = new SafeFileHandle(nint.Zero, ownsHandle: false);
        Assert.Throws<CryptographicException>(() => ThxNativeComponentTrust.VerifyPublisher(
            "unused", handle, "", CancellationToken.None));
        var canceled = new CancellationToken(canceled: true);
        Assert.Throws<OperationCanceledException>(() => ThxNativeComponentTrust.VerifyPublisher(
            "unused", handle, WindowsPublisher, canceled));
        Assert.Throws<OperationCanceledException>(() => ThxNativeComponentTrust.Verify(
            "unused", handle, WindowsPublisher, "", canceled));
    }

    [Fact]
    public void PublisherEntryRejectsUnsignedFileBeforeAcceptingAnySignerIdentity()
    {
        string path = Path.Combine(Path.GetTempPath(), "OpenBlade.Trust." + Guid.NewGuid().ToString("N") + ".cat");
        try
        {
            File.WriteAllText(path, "unsigned catalog fixture");
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Assert.Throws<CryptographicException>(() => ThxNativeComponentTrust.VerifyPublisher(
                path, handle, WindowsPublisher, CancellationToken.None));
            Assert.False(handle.IsClosed);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
