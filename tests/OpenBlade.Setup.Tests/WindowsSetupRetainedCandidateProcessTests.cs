using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using OpenBlade.Setup;

namespace OpenBlade.Setup.Tests;

public sealed class WindowsSetupRetainedCandidateProcessTests
{
    [Fact]
    public void RetainsOriginalGenerationAfterCallerClosesHandle()
    {
        using Process process = Process.GetCurrentProcess();
        SetupCandidateProcessIdentity expected = IdentityOf(process);
        using WindowsSetupRetainedCandidateProcess retained = Assert.IsType<WindowsSetupRetainedCandidateProcess>(
            WindowsSetupRetainedCandidateProcess.TryCapture(process.SafeHandle, expected, CancellationToken.None));

        Assert.False(process.SafeHandle.IsClosed);
        process.Dispose();
        AssertObservedIdentity(expected, retained);
        AssertObservedIdentity(expected, retained);

        retained.Dispose();
        Assert.Null(retained.ReadLive(CancellationToken.None));
    }

    [Fact]
    public void RejectsMismatchedGenerationAndClosedOriginal()
    {
        using Process process = Process.GetCurrentProcess();
        SetupCandidateProcessIdentity expected = IdentityOf(process);
        SafeProcessHandle original = process.SafeHandle;

        Assert.Null(WindowsSetupRetainedCandidateProcess.TryCapture(
            original, expected with { ProcessId = expected.ProcessId + 1 }, CancellationToken.None));
        Assert.Null(WindowsSetupRetainedCandidateProcess.TryCapture(
            original, expected with { CreationFileTimeUtc = expected.CreationFileTimeUtc + 1 }, CancellationToken.None));
        Assert.Null(WindowsSetupRetainedCandidateProcess.TryCapture(
            original, expected with { ExecutablePath = expected.ExecutablePath + ".other" }, CancellationToken.None));

        process.Dispose();
        Assert.Null(WindowsSetupRetainedCandidateProcess.TryCapture(original, expected, CancellationToken.None));
    }

    [Fact]
    public void RejectsExitEvenWhenExitCodeEqualsStillActive()
    {
        string image = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var start = new ProcessStartInfo(image)
        {
            Arguments = "/d /q /c \"set /p hold= & exit /b 259\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
        };
        using Process process = Process.Start(start)!;
        try
        {
            SetupCandidateProcessIdentity expected = new((uint)process.Id, process.StartTime.ToFileTimeUtc(), image);
            using WindowsSetupRetainedCandidateProcess retained = Assert.IsType<WindowsSetupRetainedCandidateProcess>(
                WindowsSetupRetainedCandidateProcess.TryCapture(process.SafeHandle, expected, CancellationToken.None));
            AssertObservedIdentity(expected, retained);

            process.StandardInput.Close();
            Assert.True(process.WaitForExit(5_000));
            Assert.Equal(259, process.ExitCode);
            Assert.Null(retained.ReadLive(CancellationToken.None));
            Assert.Null(WindowsSetupRetainedCandidateProcess.TryCapture(
                process.SafeHandle, expected, CancellationToken.None));
        }
        finally
        {
            process.StandardInput.Close();
        }
    }

    [Fact]
    public void HonorsCancellationAtHandoffAndOnRead()
    {
        using Process process = Process.GetCurrentProcess();
        SetupCandidateProcessIdentity expected = IdentityOf(process);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            WindowsSetupRetainedCandidateProcess.TryCapture(process.SafeHandle, expected, cancellation.Token));
        using WindowsSetupRetainedCandidateProcess retained = Assert.IsType<WindowsSetupRetainedCandidateProcess>(
            WindowsSetupRetainedCandidateProcess.TryCapture(process.SafeHandle, expected, CancellationToken.None));
        Assert.Throws<OperationCanceledException>(() => retained.ReadLive(cancellation.Token));
        AssertObservedIdentity(expected, retained);
    }

    private static void AssertObservedIdentity(
        SetupCandidateProcessIdentity expected, WindowsSetupRetainedCandidateProcess retained)
    {
        var observed = Assert.IsType<SetupCandidateProcessIdentity>(retained.ReadLive(CancellationToken.None));
        Assert.Equal(expected.ProcessId, observed.ProcessId);
        Assert.Equal(expected.CreationFileTimeUtc, observed.CreationFileTimeUtc);
        Assert.Equal(expected.ExecutablePath, observed.ExecutablePath, ignoreCase: true);
    }

    private static SetupCandidateProcessIdentity IdentityOf(Process process) =>
        new((uint)process.Id, process.StartTime.ToFileTimeUtc(), process.MainModule!.FileName);
}
