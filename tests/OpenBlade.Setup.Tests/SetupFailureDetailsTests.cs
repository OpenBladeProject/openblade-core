using System.ComponentModel;
using System.Text;
using OpenBlade.Setup;

namespace OpenBlade.Setup.Tests;

public sealed class SetupFailureDetailsTests
{
    [Fact]
    public void RecoverySteps_PreserveOriginalCopyFileAndItsArgument()
    {
        var inner = new RecordingSetupProgressReporter();
        using var reporter = new LoggingSetupProgressReporter(inner, new SetupLog(null));
        reporter.Report(SetupProgressCode.CopyFile, "OpenBlade.Tray.dll");
        reporter.Report(SetupProgressCode.RestoreFiles);
        reporter.Report(SetupProgressCode.RestorePreviousState);
        reporter.Report(SetupProgressCode.InstallService);

        reporter.ReportFailure(new IOException("File locked"));

        SetupProgressEvent failure = inner.Events.Last();
        Assert.Equal(SetupProgressCode.Failure, failure.Code);
        Assert.Equal(["CopyFile", "OpenBlade.Tray.dll", "File locked", ""], failure.Arguments);
    }

    [Fact]
    public void BeforeFirstStep_DefaultsToNeutralSetupPreparation()
    {
        var inner = new RecordingSetupProgressReporter();
        using var reporter = new LoggingSetupProgressReporter(inner, new SetupLog(null));

        reporter.ReportFailure(new InvalidOperationException("Cannot start"));

        Assert.Equal(["PrepareSetup", "", "Cannot start", ""], Assert.Single(inner.Events).Arguments);
    }

    [Fact]
    public void NestedTransactionAggregates_SeparateOriginalCauseAndEveryRecoveryFailure()
    {
        Exception error = new AggregateException("outer transaction",
            new AggregateException("inner transaction",
                new InvalidOperationException("Cannot copy", new IOException("File locked")),
                new IOException("Staging cleanup failed")),
            new UnauthorizedAccessException("Service rollback denied"));

        SetupProgressEvent failure = SetupFailureDetails.Create(
            SetupProgressEvent.Create(SetupProgressCode.CopyFile, "payload.dll"), error);

        Assert.Equal("Cannot copy; File locked", failure.Arguments[2]);
        Assert.Equal("inner transaction; Staging cleanup failed; outer transaction; Service rollback denied", failure.Arguments[3]);
        Assert.DoesNotContain("rollback", failure.Arguments[2], StringComparison.Ordinal);
    }

    [Fact]
    public void RecoverySummary_RetainsBackupGuidanceWithoutConflatingPrimaryAndSecondaryFailures()
    {
        const string backup = @"C:\Program Files\OpenBlade.rollback.0123456789abcdef0123456789abcdef";
        string context = $"Payload activation and restoration failed. The prior installation is retained at '{backup}'.";
        var error = new AggregateException(context,
            new IOException("Activation failed"), new UnauthorizedAccessException("Restore denied"));

        SetupProgressEvent failure = SetupFailureDetails.Create(
            SetupProgressEvent.Create(SetupProgressCode.ActivateFiles), error);

        Assert.Equal("Activation failed", failure.Arguments[2]);
        Assert.Equal($"{context}; Restore denied", failure.Arguments[3]);
        Assert.DoesNotContain("Activation failed", failure.Arguments[3], StringComparison.Ordinal);
        Assert.InRange(failure.Arguments[3].Length, 1, 512);
    }

    [Fact]
    public void DefaultAggregateText_DoesNotHideActualAdditionalFailure()
    {
        SetupProgressEvent failure = SetupFailureDetails.Create(
            SetupProgressEvent.Create(SetupProgressCode.VerifyPayload),
            new AggregateException(new IOException("Manifest mismatch"), new IOException("Staging cleanup failed")));

        Assert.Equal("Manifest mismatch", failure.Arguments[2]);
        Assert.Equal("Staging cleanup failed", failure.Arguments[3]);
    }

    [Fact]
    public void CauseMessages_AreDistinctAndRetainNativeErrorCode()
    {
        SetupProgressEvent failure = SetupFailureDetails.Create(
            SetupProgressEvent.Create(SetupProgressCode.InstallService),
            new InvalidOperationException("Service failed",
                new InvalidOperationException("Service failed", new Win32Exception(5, "Access denied"))));

        Assert.Equal("Service failed; Access denied (Win32 5, HRESULT 0x80004005)", failure.Arguments[2]);
        Assert.Empty(failure.Arguments[3]);
    }

    [Theory]
    [InlineData("\r\n\t\0message\u2028", "    message ")]
    public void Arguments_AreSingleLineValidUnicode(string message, string expected)
    {
        SetupProgressEvent failure = SetupFailureDetails.Create(
            SetupProgressEvent.Create(SetupProgressCode.InstallService), new Exception(message));

        Assert.Equal(expected, failure.Arguments[2]);
        Assert.Equal(failure.Arguments[2], new UTF8Encoding(false, true).GetString(
            new UTF8Encoding(false, true).GetBytes(failure.Arguments[2])));
    }

    [Fact]
    public void InvalidUnicode_IsReplacedBeforeEncoding()
    {
        // Build at runtime; custom-attribute metadata cannot preserve an unpaired surrogate.
        string message = "broken" + (char)0xd800 + "text";
        SetupProgressEvent failure = SetupFailureDetails.Create(
            SetupProgressEvent.Create(SetupProgressCode.InstallService), new Exception(message));

        Assert.Equal("broken\ufffdtext", failure.Arguments[2]);
    }

    [Fact]
    public void ArgumentLimit_DoesNotSplitASurrogatePair()
    {
        string message = new string('a', 511) + "\U0001f600";
        SetupProgressEvent failure = SetupProgressEvent.Create(
            SetupProgressCode.Failure, "InstallService", "", message, "");

        Assert.Equal(new string('a', 511), failure.Arguments[2]);
    }

    [Fact]
    public void LongWrapperMessage_RetainsUsefulInnerCause()
    {
        SetupProgressEvent failure = SetupFailureDetails.Create(
            SetupProgressEvent.Create(SetupProgressCode.InstallService),
            new InvalidOperationException(new string('a', 600), new IOException("File locked")));

        Assert.EndsWith("; File locked", failure.Arguments[2], StringComparison.Ordinal);
        Assert.InRange(failure.Arguments[2].Length, 1, 512);
    }

    [Fact]
    public void Cancellation_IsReportedWithoutChangingTheExceptionOrToken()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        var error = new OperationCanceledException("User canceled", source.Token);
        var inner = new RecordingSetupProgressReporter();
        using var reporter = new LoggingSetupProgressReporter(inner, new SetupLog(null));

        reporter.ReportFailure(error);

        Assert.Equal(source.Token, error.CancellationToken);
        Assert.True(error.CancellationToken.IsCancellationRequested);
        Assert.Equal("User canceled", Assert.Single(inner.Events).Arguments[2]);
    }

    [Fact]
    public void BrokenPipe_DisablesProgressWithoutChangingFailureHandling()
    {
        var writer = new BrokenPipeWriter();
        using var reporter = new LoggingSetupProgressReporter(
            new TextWriterSetupProgressReporter(writer), new SetupLog(null));

        reporter.ReportFailure(new IOException("Original error"));
        reporter.ReportFailure(new IOException("Original error"));

        Assert.Equal(1, writer.WriteAttempts);
    }

    [Fact]
    public void UnexpectedProgressSinkFailure_IsBestEffort()
    {
        using var reporter = new LoggingSetupProgressReporter(new ThrowingReporter(), new SetupLog(null));

        reporter.ReportFailure(new IOException("Original error"));
    }

    private sealed class BrokenPipeWriter : StringWriter
    {
        public int WriteAttempts { get; private set; }
        public override void WriteLine(string? value)
        {
            WriteAttempts++;
            throw new IOException("UI disconnected");
        }
    }

    private sealed class ThrowingReporter : ISetupProgressReporter
    {
        public void Report(SetupProgressEvent progress) => throw new InvalidOperationException("Sink unavailable");
        public void Dispose() { }
    }
}
