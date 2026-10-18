using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using OpenBlade.Testing;

namespace OpenBlade.Tray.Tests;

public sealed class WindowsVisualTestDesktopTests
{
    private const int UserObjectName = 2;
    private static readonly string PowerShellPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell",
        "v1.0",
        "powershell.exe");

    [Fact]
    public void ConstructorDoesNotMoveTheCallingThreadToTheIsolatedDesktop()
    {
        IntPtr beforeHandle = GetThreadDesktop(GetCurrentThreadId());
        string beforeName = GetDesktopName(beforeHandle);

        using var desktop = new WindowsVisualTestDesktop();

        Assert.Equal(beforeHandle, GetThreadDesktop(GetCurrentThreadId()));
        Assert.Equal(beforeName, GetDesktopName(GetThreadDesktop(GetCurrentThreadId())));
        Assert.NotEqual(beforeName, desktop.Name);
    }

    [Fact]
    public void StartPreservesDesktopEnvironmentWorkingDirectoryAndQuotedArguments()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string scriptPath = WriteDesktopProbeScript(directory);
            string reportPath = Path.Combine(directory, "desktop report 雪.txt");
            const string environmentValue = "overridden environment with spaces 雪";
            const string argumentValue = "argument with spaces & punctuation ' 雪";
            string parentDesktop = GetDesktopName(GetThreadDesktop(GetCurrentThreadId()));
            using var desktop = new WindowsVisualTestDesktop();
            ProcessStartInfo startInfo = CreateProbeStartInfo(
                directory,
                scriptPath,
                reportPath,
                argumentValue,
                environmentValue,
                parentDesktop);

            using Process process = desktop.Start(startInfo);
            WaitForOwnedProcess(process);

            string[] report = File.ReadAllLines(reportPath, Encoding.UTF8);
            Assert.Equal(5, report.Length);
            Assert.Equal(desktop.Name, report[0]);
            Assert.Equal(parentDesktop, report[1]);
            Assert.NotEqual(report[1], report[0]);
            Assert.Equal(environmentValue, report[2]);
            Assert.Equal(directory, report[3]);
            Assert.Equal(argumentValue, report[4]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void FailedLaunchDoesNotPreventAReplacementLaunchOnTheSameDesktop()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            using var desktop = new WindowsVisualTestDesktop();
            var missing = new ProcessStartInfo(Path.Combine(directory, "missing renderer.exe"))
            {
                UseShellExecute = false,
                WorkingDirectory = directory,
            };

            Assert.Throws<Win32Exception>(() => desktop.Start(missing));

            string scriptPath = WriteDesktopProbeScript(directory);
            string reportPath = Path.Combine(directory, "replacement report.txt");
            ProcessStartInfo replacement = CreateProbeStartInfo(
                directory,
                scriptPath,
                reportPath,
                "replacement argument",
                "replacement environment",
                GetDesktopName(GetThreadDesktop(GetCurrentThreadId())));
            using Process process = desktop.Start(replacement);
            WaitForOwnedProcess(process);

            Assert.Equal(desktop.Name, File.ReadAllLines(reportPath, Encoding.UTF8)[0]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void OperationsThrowAfterTheDesktopIsDisposed()
    {
        var desktop = new WindowsVisualTestDesktop();
        desktop.Dispose();
        var startInfo = new ProcessStartInfo(PowerShellPath)
        {
            UseShellExecute = false,
        };

        Assert.Throws<ObjectDisposedException>(() => desktop.AttachCurrentThread());
        Assert.Throws<ObjectDisposedException>(() => desktop.Start(startInfo));
    }

    private static ProcessStartInfo CreateProbeStartInfo(
        string workingDirectory,
        string scriptPath,
        string reportPath,
        string argumentValue,
        string environmentValue,
        string parentDesktop)
    {
        var startInfo = new ProcessStartInfo(PowerShellPath)
        {
            Arguments = string.Join(
                " ",
                "-NoLogo",
                "-NoProfile",
                "-NonInteractive",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                QuoteArgument(scriptPath),
                QuoteArgument(reportPath),
                QuoteArgument(argumentValue)),
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
        };
        startInfo.EnvironmentVariables["OPENBLADE_VISUAL_TEST_VALUE"] = environmentValue;
        startInfo.EnvironmentVariables["OPENBLADE_VISUAL_TEST_PARENT_DESKTOP"] = parentDesktop;
        return startInfo;
    }

    private static string WriteDesktopProbeScript(string directory)
    {
        string path = Path.Combine(directory, "desktop probe 雪.ps1");
        File.WriteAllText(
            path,
            """
            param(
                [Parameter(Mandatory = $true)][string]$ReportPath,
                [Parameter(Mandatory = $true)][string]$ObservedArgument)

            $native = @'
            using System;
            using System.Runtime.InteropServices;
            using System.Text;

            public static class OpenBladeDesktopProbe
            {
                [DllImport("kernel32.dll")]
                public static extern uint GetCurrentThreadId();

                [DllImport("user32.dll")]
                public static extern IntPtr GetThreadDesktop(uint threadId);

                [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
                public static extern bool GetUserObjectInformationW(
                    IntPtr handle,
                    int index,
                    StringBuilder information,
                    int length,
                    out int needed);

            }
            '@
            Add-Type -TypeDefinition $native -Language CSharp

            function Get-DesktopName([IntPtr]$Handle) {
                $buffer = New-Object System.Text.StringBuilder 256
                $needed = 0
                if (-not [OpenBladeDesktopProbe]::GetUserObjectInformationW(
                        $Handle, 2, $buffer, $buffer.Capacity * 2, [ref]$needed)) {
                    $errorCode = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
                    throw (New-Object ComponentModel.Win32Exception -ArgumentList $errorCode)
                }
                return $buffer.ToString()
            }

            $threadDesktop = [OpenBladeDesktopProbe]::GetThreadDesktop(
                [OpenBladeDesktopProbe]::GetCurrentThreadId())
            $lines = @(
                (Get-DesktopName $threadDesktop),
                $env:OPENBLADE_VISUAL_TEST_PARENT_DESKTOP,
                $env:OPENBLADE_VISUAL_TEST_VALUE,
                (Get-Location).Path,
                $ObservedArgument)
            [IO.File]::WriteAllLines(
                $ReportPath,
                $lines,
                (New-Object Text.UTF8Encoding($false)))
            """,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static void WaitForOwnedProcess(Process process)
    {
        if (!process.WaitForExit(milliseconds: 20_000))
        {
            process.Kill(entireProcessTree: true);
            Assert.True(
                process.WaitForExit(milliseconds: 5_000),
                $"Owned process {process.Id} did not exit after termination.");
            throw new TimeoutException($"Owned process {process.Id} exceeded the 20-second test timeout.");
        }

        Assert.Equal(0, process.ExitCode);
    }

    private static string QuoteArgument(string value) => '"' + value + '"';

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "OpenBlade visual desktop tests 雪 " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string GetDesktopName(IntPtr desktop)
    {
        var name = new StringBuilder(256);
        Assert.True(
            GetUserObjectInformationW(
                desktop,
                UserObjectName,
                name,
                name.Capacity * sizeof(char),
                out _),
            new Win32Exception(Marshal.GetLastWin32Error()).Message);
        return name.ToString();
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDesktop(uint threadId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformationW(
        IntPtr handle,
        int index,
        StringBuilder information,
        int length,
        out int needed);
}
