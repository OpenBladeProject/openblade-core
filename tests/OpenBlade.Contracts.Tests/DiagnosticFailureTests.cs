using System.ComponentModel;
using System.Text.Json;

namespace OpenBlade.Contracts.Tests;

public sealed class DiagnosticFailureTests
{
    [Fact]
    public void CapturePreservesNativeCauseWithoutPrivateExceptionText()
    {
        var native = new Win32Exception(5, @"private-user C:\private\keyboard.sys");
        var exception = new InvalidOperationException("private wrapper text", native);
        DiagnosticFailure failure = DiagnosticFailure.FromException(
            "SnapTap", "Driver", "HookSetup", "HookWriteFailed", exception, mayHaveSubmitted: true);

        Assert.True(failure.IsValid());
        Assert.True(failure.MayHaveSubmitted);
        Assert.Equal(nameof(InvalidOperationException), failure.ExceptionType);
        Assert.Equal(exception.HResult, failure.HResult);
        Assert.Equal(5, failure.Cause?.NativeErrorCode);
        Assert.Equal(native.HResult, failure.Cause?.HResult);
        string json = JsonSerializer.Serialize(failure);
        Assert.DoesNotContain("private", json, StringComparison.OrdinalIgnoreCase);
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
        Assert.Null(failure.Cause?.Cause?.Cause?.Cause);
        Assert.Equal(17, failure.ProcessExitCode);
        Assert.False((failure with { Cause = failure }).IsValid());
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
