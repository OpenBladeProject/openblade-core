using System;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace OpenBlade.Installer.UI.Tests;

public sealed class InstallerFailureTests
{
    internal static string Wire(string step, string argument, string error, string recovery = "") =>
        "v1|Failure|" + string.Join("|", new[] { step, argument, error, recovery }
            .Select(value => Convert.ToBase64String(Encoding.UTF8.GetBytes(value))));

    [Fact]
    public void FailureShowsOriginalStepAndCauseWithRollbackFailureSeparate()
    {
        Assert.True(InstallerSetupProgress.TryParse(
            Wire("VerifyServiceHealth", "", "The service pipe closed.", "Previous service could not start."),
            out InstallerSetupProgress? progress));

        string message = OpenBladeBootstrapperApplication.DescribeFailure(
            unchecked((int)0x80070001), progress!.Failure, "OpenBlade", "Incorrect function.", false);

        Assert.Contains("Checking the OpenBlade service", message);
        Assert.Contains("The service pipe closed.", message);
        Assert.Contains("Additional cleanup or recovery error: Previous service could not start.", message);
        Assert.Contains("0x80070001", message);
        Assert.DoesNotContain("Incorrect function.", message);
    }

    [Fact]
    public void CopyFailureKeepsTheOriginalRelativeFileName()
    {
        Assert.True(InstallerSetupProgress.TryParse(
            Wire("CopyFile", @"payload\OpenBlade.Tray.exe", "Access denied."),
            out InstallerSetupProgress? progress));
        Assert.Equal(@"payload\OpenBlade.Tray.exe", progress!.Failure!.Step.Argument);
        Assert.Contains(@"payload\OpenBlade.Tray.exe", OpenBladeBootstrapperApplication.DescribeFailure(
            -1, progress.Failure, null, null, false));
    }

    [Fact]
    public void CancellationDoesNotHideARecoveryFailureOrClaimRestoration()
    {
        Assert.True(InstallerSetupProgress.TryParse(
            Wire("InstallService", "", "The operation was cancelled.", "Access denied."),
            out InstallerSetupProgress? progress));
        string message = OpenBladeBootstrapperApplication.DescribeFailure(
            -1, progress!.Failure, null, null, true);
        Assert.Contains("Access denied.", message);
        Assert.DoesNotContain("was restored", message);
        Assert.DoesNotContain("was restored", OpenBladeBootstrapperApplication.DescribeFailure(
            -1, null, null, null, true));
    }

    [Fact]
    public void CancellationTakesPrecedenceOverAnExpectedPackageExit()
    {
        string message = OpenBladeBootstrapperApplication.DescribeFailure(
            unchecked((int)0x80070642), null, "Microsoft .NET 10 Desktop Runtime", "User exit", true);
        Assert.Contains("Setup was cancelled.", message);
        Assert.DoesNotContain("Setup failed while running", message);
        Assert.Contains("0x80070642", message);
    }

    [Fact]
    public async Task CompletionWithoutASetupWriterDoesNotWaitForTheDrainDeadline()
    {
        using InstallerProgressPipe pipe = InstallerProgressPipe.Start(_ => { });
        Task completion = pipe.CompleteAsync();
        Assert.True(completion.IsCompleted);
        await completion;
    }

    [Fact]
    public void PrerequisiteFailureNamesThePackageWhenSetupHasNotStarted()
    {
        string message = OpenBladeBootstrapperApplication.DescribeFailure(
            unchecked((int)0x80070643), null, "Microsoft .NET 10 Desktop Runtime", "Installation failed.", false);
        Assert.Contains("Microsoft .NET 10 Desktop Runtime", message);
        Assert.Contains("Installation failed.", message);
        Assert.Contains("0x80070643", message);
    }

    [Theory]
    [InlineData("Failure", "", "error")]
    [InlineData("999", "", "error")]
    [InlineData("0", "", "error")]
    [InlineData("Unknown", "", "error")]
    [InlineData("InspectPayload", "unexpected", "error")]
    [InlineData("VerifyServiceHealth", "", "")]
    [InlineData("VerifyServiceHealth", "", "forged\nline")]
    [InlineData("VerifyServiceHealth", "", "forged\tline")]
    public void RejectsInvalidFailureFields(string step, string argument, string error)
    {
        Assert.False(InstallerSetupProgress.TryParse(Wire(step, argument, error), out _));
    }

    [Fact]
    public void RejectsInvalidUtf8OversizedFieldsAndMalformedEnvelope()
    {
        Assert.False(InstallerSetupProgress.TryParse(Wire("CopyFile", new string('x', 513), "error"), out _));
        Assert.False(InstallerSetupProgress.TryParse(Wire("InstallService", "", new string('x', 513)), out _));
        Assert.False(InstallerSetupProgress.TryParse("v1|Failure|/w==|||", out _));
        Assert.False(InstallerSetupProgress.TryParse("v1|Failure|not-base64|||", out _));
        Assert.False(InstallerSetupProgress.TryParse(Wire("InstallService", "", "error") + "|extra", out _));
    }

    [Fact]
    public async Task ReaderAcceptsMaximumUnicodeFailureAndRecoversAfterOversizedLine()
    {
        string text = new('界', 512);
        string wire = Wire("CopyFile", text, text, text);
        using StringReader reader = new(new string('x', 9000) + "\n" + wire + "\n");
        var messages = new System.Collections.Generic.List<InstallerSetupProgress>();
        await InstallerProgressPipe.ReadMessagesAsync(reader, messages.Add);
        Assert.Equal(text, Assert.Single(messages).Failure!.Error);
        Assert.Equal(text, messages[0].Failure!.RecoveryError);
    }

    [Fact]
    public async Task CompletionDrainsTheTerminalEventBeforeClosingThePipe()
    {
        InstallerSetupFailure? failure = null;
        using InstallerProgressPipe pipe = InstallerProgressPipe.Start(progress => failure = progress.Failure);
        using (NamedPipeClientStream client = new(".", pipe.Name, PipeDirection.Out))
        {
            await client.ConnectAsync(2000);
            using StreamWriter writer = new(client, new UTF8Encoding(false));
            await writer.WriteLineAsync(Wire("InstallService", "", "Access denied."));
        }
        await pipe.CompleteAsync(setupPackageExecuted: true);
        Assert.Equal("Access denied.", failure?.Error);
    }

    [Theory]
    [InlineData("zh-CN", "失败步骤：")]
    [InlineData("ru-RU", "Сбой на этапе:")]
    public void FailureSummaryUsesTheSupportedLanguage(string culture, string expected)
    {
        CultureInfo previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            Assert.True(InstallerSetupProgress.TryParse(Wire("VerifyServiceHealth", "", "Access denied."),
                out InstallerSetupProgress? progress));
            string message = OpenBladeBootstrapperApplication.DescribeFailure(
                -1, progress!.Failure, null, null, false);
            Assert.Contains(expected, message);
            Assert.Contains("Access denied.", message);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }
}
