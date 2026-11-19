using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Xunit;

namespace OpenBlade.Installer.UI.Tests;

public sealed class InstallerPackageLogTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(),
        "OpenBlade.Installer.PackageLog.Tests", Guid.NewGuid().ToString("N"));

    public InstallerPackageLogTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, recursive: true);
    private string PackageLog => Path.Combine(directory, "setup.log");

    [Fact]
    public void PipeFailureIsReadableEvenWhenThePackageLogIsUnavailable()
    {
        Assert.True(InstallerSetupProgress.TryParse(InstallerFailureTests.Wire(
            "CopyFile", "payload.dll", "Access denied", "Staging cleanup failed"),
            out InstallerSetupProgress? progress));

        string line = OpenBladeBootstrapperApplication.FormatPipeDiagnostic(progress!);

        Assert.Contains("failure at CopyFile: Access denied", line);
        Assert.Contains("file: payload.dll", line);
        Assert.Contains("cleanup or recovery: Staging cleanup failed", line);
        Assert.DoesNotContain("v1|", line);
    }

    [Fact]
    public void MainLogContainsStepCauseAndStackWithoutProgressPipeOrUi()
    {
        const string diagnostic = "{\"exceptionType\":\"InvalidOperationException\","
            + "\"message\":\"Driver setup failed\",\"stackTrace\":\"at EnsureAsync()\","
            + "\"cause\":{\"exceptionType\":\"Win32Exception\",\"message\":\"Access denied\"},"
            + "\"related\":[{\"message\":\"Cleanup failed\"}]}";
        File.WriteAllText(PackageLog, "Started: Install\nStep: EnsureCpuTemperaturePrerequisite\n" + diagnostic);
        List<string> main = new();

        InstallerPackageLog.AppendToBundleLog("OpenBladeSetup", unchecked((int)0x80070001), PackageLog, main.Add);

        string combined = string.Join("\n", main);
        Assert.Contains("EnsureCpuTemperaturePrerequisite", combined);
        Assert.Contains(diagnostic, combined);
        Assert.Contains("result: 0x80070001", combined);
        Assert.EndsWith("diagnostics end.", main.Last());
    }

    [Fact]
    public void RollbackSuccessDoesNotReplaceTheOriginalFailure()
    {
        List<string> main = new();
        File.WriteAllText(PackageLog, "Started: Install\nOriginal installation exception\n");
        InstallerPackageLog.AppendToBundleLog("OpenBladeSetup", -1, PackageLog, main.Add);
        File.AppendAllText(PackageLog, "Started: Uninstall\nFinished with exit code 0.\n");
        InstallerPackageLog.AppendToBundleLog("OpenBladeSetup", 0, PackageLog, main.Add);

        string combined = string.Join("\n", main);
        Assert.Contains("Original installation exception", combined);
        Assert.Contains("Finished with exit code 0.", combined);
        Assert.True(combined.IndexOf("Original installation exception", StringComparison.Ordinal)
            < combined.IndexOf("Started: Uninstall", StringComparison.Ordinal));
        Assert.Equal(2, main.Count(line => line.Contains("diagnostics begin")));
    }

    [Fact]
    public void SnapshotSurvivesPackageLogCleanupDuringBundleLogging()
    {
        File.WriteAllText(PackageLog, "Step: VerifyPayload\nThe manifest did not match.\n");
        List<string> main = new();
        InstallerPackageLog.AppendToBundleLog("OpenBladeSetup", -1, PackageLog, message =>
        {
            if (main.Count == 0) { File.Delete(PackageLog); }
            main.Add(message);
        });

        Assert.False(File.Exists(PackageLog));
        Assert.Contains(main, line => line.Contains("The manifest did not match."));
    }

    [Fact]
    public void SharedWriterCanKeepTheLogOpenAndAppendAfterTheSnapshot()
    {
        File.WriteAllText(PackageLog, "Original failure\n");
        using FileStream writer = new(PackageLog, FileMode.Append, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        List<string> main = new();

        InstallerPackageLog.AppendToBundleLog("OpenBladeSetup", -1, PackageLog, message =>
        {
            if (main.Count == 0)
            {
                byte[] addition = Encoding.UTF8.GetBytes("Newer record after snapshot\n");
                writer.Write(addition, 0, addition.Length);
                writer.Flush();
            }
            main.Add(message);
        });

        Assert.Contains(main, line => line.Contains("Original failure"));
        Assert.DoesNotContain(main, line => line.Contains("Newer record after snapshot"));
    }

    [Fact]
    public void ParentDirectoryJunctionIsRejectedBeforeItsTargetContentIsRead()
    {
        string target = Path.Combine(directory, "target");
        string junction = Path.Combine(directory, "junction");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "setup.log"), "Must not import redirected content");
        using (Process process = Process.Start(new ProcessStartInfo("cmd.exe",
            "/d /c mklink /J \"" + junction + "\" \"" + target + "\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!)
        {
            Assert.True(process.WaitForExit(10000));
            Assert.Equal(0, process.ExitCode);
        }
        try
        {
            List<string> main = new();
            InstallerPackageLog.AppendToBundleLog("OpenBladeSetup", -1,
                Path.Combine(junction, "setup.log"), main.Add);

            Assert.Contains("diagnostics unavailable", Assert.Single(main));
            Assert.DoesNotContain(main, line => line.Contains("Must not import redirected content"));
        }
        finally
        {
            // Remove only the known junction itself, never recursively traverse its target.
            Directory.Delete(junction);
        }
    }

    [Fact]
    public void DirectoryCannotBeImportedAsAPackageLog()
    {
        List<string> main = new();
        InstallerPackageLog.AppendToBundleLog("OpenBladeSetup", -1, directory, main.Add);
        Assert.Contains("diagnostics unavailable", Assert.Single(main));
    }

    [Fact]
    public void HardLinkedContentCannotBeImportedAsAPackageLog()
    {
        string target = Path.Combine(directory, "original.log");
        File.WriteAllText(target, "Must not import hard-linked content");
        Assert.True(CreateHardLinkW(PackageLog, target, IntPtr.Zero));
        List<string> main = new();

        InstallerPackageLog.AppendToBundleLog("OpenBladeSetup", -1, PackageLog, main.Add);

        Assert.Contains("diagnostics unavailable", Assert.Single(main));
        Assert.DoesNotContain(main, line => line.Contains("Must not import hard-linked content"));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string fileName, string existingFileName, IntPtr securityAttributes);

    [Fact]
    public void Utf8BomIsRemovedFromTheFirstRecord()
    {
        File.WriteAllText(PackageLog, "Original exception", new UTF8Encoding(true));
        List<string> main = new();

        InstallerPackageLog.AppendToBundleLog("OpenBladeSetup", -1, PackageLog, main.Add);

        Assert.Contains("OpenBlade.Setup package log: Original exception", main);
        Assert.DoesNotContain(main, line => line.Contains("\uFEFF"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void TailStartingInsideUtf8CharacterKeepsThePartialExceptionWithoutReplacement(int skippedPrefixBytes)
    {
        string retained = new string('x', InstallerPackageLog.MaximumBytes - (4 - skippedPrefixBytes) - 11)
            + "inner cause";
        File.WriteAllText(PackageLog, "\U0001f600" + retained, new UTF8Encoding(false));
        List<string> main = new();

        InstallerPackageLog.AppendToBundleLog("OpenBladeSetup", -1, PackageLog, main.Add);

        const string prefix = "OpenBlade.Setup package log: ";
        string imported = string.Concat(main.Where(line => line.StartsWith(prefix, StringComparison.Ordinal))
            .Select(line => line.Substring(prefix.Length)));
        Assert.Equal(retained, imported);
        Assert.DoesNotContain("\uFFFD", imported, StringComparison.Ordinal);
        Assert.Contains(main, line => line.Contains("diagnostics truncated"));
    }

    [Fact]
    public void RecordSanitizerRemovesControlsAndUnicodeLineSeparators()
    {
        Assert.Equal("cause      detail\U0001f600", InstallerPackageLog.SanitizeRecord(
            "cause\r\n\t\0\u2028\u2029detail\U0001f600"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("missing.log")]
    [InlineData("invalid\0path")]
    public void MissingOrInvalidLogIsExplicitAndDoesNotThrow(string? path)
    {
        List<string> main = new();
        InstallerPackageLog.AppendToBundleLog("OpenBladeSetup", -1,
            path == "missing.log" ? PackageLog : path, main.Add);

        Assert.Contains("diagnostics unavailable", Assert.Single(main));
    }

    [Fact]
    public void OtherPackagesDoNotReadOrImportTheSetupLog()
    {
        List<string> main = new();
        InstallerPackageLog.AppendToBundleLog("OpenBladeDotNetRuntime", -1, "invalid\0path", main.Add);
        Assert.Empty(main);
    }

    [Fact]
    public void ByteBudgetKeepsTheRecentFailureAndMarksTruncation()
    {
        File.WriteAllText(PackageLog, "old marker\n" + new string('x', InstallerPackageLog.MaximumBytes)
            + "\nRecent exception and recovery details\n");
        List<string> main = new();
        InstallerPackageLog.AppendToBundleLog("OpenBladeSetup", -1, PackageLog, main.Add);

        Assert.DoesNotContain(main, line => line.Contains("old marker"));
        Assert.Contains(main, line => line.Contains("Recent exception and recovery details"));
        Assert.Contains(main, line => line.Contains("diagnostics truncated"));
    }

    [Fact]
    public void RecordBudgetRetainsTheLastFailure()
    {
        File.WriteAllLines(PackageLog, Enumerable.Range(0, InstallerPackageLog.MaximumRecords + 20)
            .Select(index => "record " + index).Concat(new[] { "final failure" }));
        List<string> main = new();
        InstallerPackageLog.AppendToBundleLog("OpenBladeSetup", -1, PackageLog, main.Add);

        Assert.Equal(InstallerPackageLog.MaximumRecords,
            main.Count(line => line.StartsWith("OpenBlade.Setup package log: ", StringComparison.Ordinal)));
        Assert.Contains(main, line => line.Contains("final failure"));
        Assert.Contains(main, line => line.Contains("diagnostics truncated"));
    }

    [Fact]
    public void LongUnicodeExceptionIsSplitWithoutLosingItsInnerCause()
    {
        string detail = new string('x', 4095) + "\U0001f600" + new string('界', 5000) + "inner cause\u001b\u2028end";
        File.WriteAllText(PackageLog, detail, new UTF8Encoding(false));
        List<string> main = new();
        InstallerPackageLog.AppendToBundleLog("OpenBladeSetup", -1, PackageLog, main.Add);

        const string prefix = "OpenBlade.Setup package log: ";
        string[] records = main.Where(line => line.StartsWith(prefix, StringComparison.Ordinal))
            .Select(line => line.Substring(prefix.Length)).ToArray();
        Assert.Equal(detail.Replace('\u001b', ' ').Replace('\u2028', ' '), string.Concat(records));
        Assert.All(records, record =>
        {
            Assert.True(record.Length <= InstallerPackageLog.MaximumRecordCharacters);
            _ = new UTF8Encoding(false, true).GetBytes(record);
        });
    }

    [Fact]
    public void BundleLogSinkFailureDoesNotChangeInstallerExecution()
    {
        File.WriteAllText(PackageLog, "Original exception");
        Exception? failure = Record.Exception(() => InstallerPackageLog.AppendToBundleLog(
            "OpenBladeSetup", -1, PackageLog, _ => throw new IOException("Log unavailable")));
        Assert.Null(failure);
    }
}
