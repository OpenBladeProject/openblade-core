using System.ComponentModel;
using System.Text.Json;

namespace OpenBlade.Contracts.Tests;

public sealed class DiagnosticFailureTests
{
    [Fact]
    public void CapturePreservesNativeCauseAndRedactedActionableText()
    {
        var native = new Win32Exception(5, @"C:\Users\private-owner\keyboard.sys password=secret-value");
        var exception = new InvalidOperationException("The keyboard package is invalid", native);
        DiagnosticFailure failure = DiagnosticFailure.FromException(
            "SnapTap", "Driver", "HookSetup", "HookWriteFailed", exception, mayHaveSubmitted: true);

        Assert.True(failure.IsValid());
        Assert.True(failure.MayHaveSubmitted);
        Assert.Equal(nameof(InvalidOperationException), failure.ExceptionType);
        Assert.Equal(exception.HResult, failure.HResult);
        Assert.Equal(5, failure.Cause?.NativeErrorCode);
        Assert.Equal(native.HResult, failure.Cause?.HResult);
        string json = JsonSerializer.Serialize(failure);
        Assert.Contains("The keyboard package is invalid", json, StringComparison.Ordinal);
        Assert.DoesNotContain("private-owner", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-value", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(failure, JsonSerializer.Deserialize<DiagnosticFailure>(json));
    }

    [Fact]
    public void CaptureBoundsDeepCauseChainsAndRetainsProcessExit()
    {
        Exception exception = new IOException("private");
        for (int index = 0; index < 30; index++)
        {
            exception = new InvalidOperationException("private", exception);
        }
        DiagnosticFailure failure = DiagnosticFailure.FromException(
            "Battery", "Report", "Generate", "ProcessFailed", exception) with
        {
            ProcessExitCode = 17,
        };
        Assert.True(failure.IsValid());
        Assert.NotNull(failure.Cause?.Cause?.Cause);
        Assert.True(failure.DetailsTruncated);
        DiagnosticFailure? current = failure;
        int depth = 0;
        while (current is not null)
        {
            depth++;
            current = current.Cause;
        }
        Assert.Equal(DiagnosticFailure.MaximumDepth, depth);
        Assert.Equal(17, failure.ProcessExitCode);
        Assert.False((failure with { Cause = failure }).IsValid());
    }

    [Fact]
    public void CaptureRetainsThrowLocationAndEveryAggregateBranch()
    {
        Exception first;
        try
        {
            throw new InvalidDataException("Missing endpoint driver binding.");
        }
        catch (InvalidDataException exception)
        {
            first = exception;
        }
        var aggregate = new AggregateException("Discovery failed", first,
            new Win32Exception(5, "Catalog access denied"), new IOException("Driver store unavailable"));
        DiagnosticFailure detail = DiagnosticFailure.FromException(
            "DriverPackage", "RzCommon", "Capture", "Rejected", aggregate);

        Assert.True(detail.IsValid());
        Assert.Contains(nameof(CaptureRetainsThrowLocationAndEveryAggregateBranch), detail.Cause?.StackTrace);
        Assert.Equal("Missing endpoint driver binding.", detail.Cause?.Message);
        Assert.Equal(2, detail.Related?.Count);
        Assert.Equal(5, detail.Related?[0].NativeErrorCode);
        Assert.Equal("Driver store unavailable", detail.Related?[1].Message);
        Assert.False(detail.DetailsTruncated);
        DiagnosticFailure roundTrip = JsonSerializer.Deserialize<DiagnosticFailure>(JsonSerializer.Serialize(detail))!;
        Assert.True(roundTrip.IsValid());
        Assert.Equal(detail.Related?[1].Message, roundTrip.Related?[1].Message);
    }

    [Fact]
    public void OversizedDetailReportsTruncationAndRemainsAValidStatusRecord()
    {
        DiagnosticFailure detail = DiagnosticFailure.FromException(
            "Test", "Read", "Capture", "Failed", new IOException(new string('x', 100_000)));

        Assert.True(detail.DetailsTruncated);
        Assert.True(detail.IsValid());
        Assert.Equal(8192, detail.Message?.Length);
        Assert.False((detail with { Message = new string('x', 8193) }).IsValid());
    }

    [Fact]
    public void ExceptionFormatterPreservesContextAndRedactsCredentials()
    {
        string text = DiagnosticTrace.FormatException("Test", "Read",
            new IOException("Binding failed; password=super-secret"),
            @"service=RzCommon; path=C:\Users\owner-value\driver.sys");

        Assert.Contains("Binding failed", text);
        Assert.Contains("service=RzCommon", text);
        Assert.DoesNotContain("super-secret", text);
        Assert.DoesNotContain("owner-value", text);
    }

    [Fact]
    public void CombinedFailureTreesAreRebudgetedWithExplicitTruncation()
    {
        DiagnosticFailure child = new("Test", "Read", "Capture", "Failed")
        {
            Message = new string('m', 8192),
            StackTrace = new string('s', 32768),
        };
        DiagnosticFailure combined = new("Test", "Cleanup", "Stop", "Failed")
        {
            Related = [child, child with { }, child with { }, child with { }, child with { }],
        };
        Assert.False(combined.IsValid());
        DiagnosticFailure bounded = combined.LimitDetails();
        Assert.True(bounded.IsValid());
        Assert.True(bounded.DetailsTruncated);
        Assert.Equal(5, bounded.Related?.Count);
        Assert.Equal(string.Empty, bounded.Related?[4].Message);
    }

    [Theory]
    [InlineData("Authorization: Bearer bearer-secret")]
    [InlineData("authorization=Basic basic-secret")]
    [InlineData("api_key=api-secret")]
    [InlineData("access_token=access-secret")]
    [InlineData("refresh_token=refresh-secret")]
    [InlineData("client_secret=client-secret")]
    [InlineData("{\"access_token\":\"json-secret\"}")]
    [InlineData("https://user-secret:password-secret@example.test/component")]
    public void RedactionCoversCredentialForms(string value)
    {
        string sanitized = DiagnosticText.Redact(value);
        Assert.Contains("[REDACTED]", sanitized);
        Assert.DoesNotContain("-secret", sanitized);
    }

    [Fact]
    public void CapturingAndFormattingDoNotChangeNativeLastError()
    {
        System.Runtime.InteropServices.Marshal.SetLastPInvokeError(1234);
        var failure = DiagnosticFailure.FromException("Test", "Read", "Capture", "Failed",
            new IOException("Technical error"));
        Assert.Equal(1234, System.Runtime.InteropServices.Marshal.GetLastPInvokeError());
        _ = failure.LimitDetails();
        Assert.Equal(1234, System.Runtime.InteropServices.Marshal.GetLastPInvokeError());
        _ = DiagnosticTrace.FormatException("Test", "Read", new IOException("Technical error"));
        Assert.Equal(1234, System.Runtime.InteropServices.Marshal.GetLastPInvokeError());
    }

    [Fact]
    public void ShortIdentityTokensDoNotDamageTechnicalWords()
    {
        string value = DiagnosticText.Redact(
            "Windows Win32Exception device channel dev ann win; host=win; user=ann",
            ["dev", "ann", "win"]);
        Assert.Contains("Windows Win32Exception device channel", value);
        Assert.DoesNotContain("user=ann", value);
        Assert.DoesNotContain("host=win", value);
        Assert.Contains("user=[REDACTED]", value);
        Assert.Contains("host=[REDACTED]", value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("C:\\private\\file")]
    [InlineData("line\nbreak")]
    [InlineData("free text")]
    public void RejectsFreeTextDiagnosticIdentifiers(string value)
    {
        Assert.False(new DiagnosticFailure(value, "Read", "Open", "Failed").IsValid());
        Assert.False(new DiagnosticFailure("Battery", "Read", "Open", value).IsValid());
    }
}
