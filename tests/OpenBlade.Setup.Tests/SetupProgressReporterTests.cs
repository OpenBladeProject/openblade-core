using System.Text;

using OpenBlade.Setup;

namespace OpenBlade.Setup.Tests;

public sealed class SetupProgressReporterTests
{
    [Theory]
    [InlineData("OpenBlade.Setup.Progress.0123456789abcdef0123456789abcdef", true)]
    [InlineData("OpenBlade.Setup.Progress.01234567-89ab-cdef-0123-456789abcdef", false)]
    [InlineData("Other.Progress.0123456789abcdef0123456789abcdef", false)]
    [InlineData("OpenBlade.Setup.Progress.not-a-guid", false)]
    [InlineData("", false)]
    public void IsValidPipeName_AcceptsOnlyPrivateGuidNames(string name, bool expected)
    {
        Assert.Equal(expected, SetupProgressReporter.IsValidPipeName(name));
    }

    [Fact]
    public void TextWriterReporter_EmitsVersionedCodeAndBase64Argument()
    {
        StringWriter writer = new();
        using TextWriterSetupProgressReporter reporter = new(writer);

        reporter.Report(SetupProgressCode.InspectPayload);
        reporter.Report(SetupProgressCode.CopyFile, "payload\r\nwith forged line");

        string[] lines = writer.ToString().Split(
            Environment.NewLine,
            StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal("v1|InspectPayload", lines[0]);
        Assert.Equal(
            "v1|CopyFile|cGF5bG9hZCAgd2l0aCBmb3JnZWQgbGluZQ==",
            lines[1]);
    }

    [Fact]
    public void ProgressEvent_BoundsTheOnlyDynamicArgument()
    {
        SetupProgressEvent progress = SetupProgressEvent.Create(
            SetupProgressCode.CopyFile,
            new string('x', 600));

        Assert.Equal(512, Assert.Single(progress.Arguments).Length);
        Assert.Equal($"Copying {new string('x', 512)}", progress.ToInvariantDiagnostic());
    }

    [Theory]
    [InlineData(SetupProgressCode.CopyFile)]
    [InlineData(SetupProgressCode.InstallService, "unexpected")]
    [InlineData(SetupProgressCode.Failure, "InstallService", "", "error")]
    public void ProgressEvent_RejectsTheWrongArgumentCount(
        SetupProgressCode code,
        params string[] arguments)
    {
        Assert.Throws<ArgumentException>(() => SetupProgressEvent.Create(code, arguments));
    }

    [Fact]
    public void InvariantDiagnostic_DoesNotDependOnCurrentCulture()
    {
        using CultureScope culture = new("fr-FR");

        SetupProgressEvent progress = SetupProgressEvent.Create(
            SetupProgressCode.CopyFile,
            "OpenBlade.Tray.exe");

        Assert.Equal("Copying OpenBlade.Tray.exe", progress.ToInvariantDiagnostic());
    }

    [Fact]
    public void TextWriterReporter_DisconnectDuringDisposeDoesNotFailSetup()
    {
        TrackingDisposable ownedResource = new();
        TextWriterSetupProgressReporter reporter = new(
            new ThrowingDisposeWriter(),
            ownedResource);

        reporter.Dispose();

        Assert.True(ownedResource.WasDisposed);
    }

    [Fact]
    public void Failure_SerializesFourBoundedBase64Arguments()
    {
        string longMessage = new string('\u4e2d', 600);
        SetupProgressEvent progress = SetupProgressEvent.Create(
            SetupProgressCode.Failure, "CopyFile", "payload.dll", longMessage, "rollback failed");

        string serialized = SetupProgressWireProtocol.Serialize(progress);
        string[] fields = serialized.Split('|');

        Assert.Equal(6, fields.Length);
        Assert.Equal("v1", fields[0]);
        Assert.Equal("Failure", fields[1]);
        Assert.Equal(progress.Arguments, fields.Skip(2).Select(field => Encoding.UTF8.GetString(Convert.FromBase64String(field))));
        Assert.InRange(serialized.Length, 1, 8192);
        Assert.Equal(512, progress.Arguments[2].Length);
    }

    [Theory]
    [InlineData("Failure", "")]
    [InlineData("unknown", "")]
    [InlineData("1", "")]
    [InlineData("InstallService", "unexpected")]
    public void Failure_RejectsInvalidOriginalStep(string step, string argument)
    {
        Assert.Throws<ArgumentException>(() => SetupProgressEvent.Create(
            SetupProgressCode.Failure, step, argument, "error", ""));
    }

    private sealed class ThrowingDisposeWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        protected override void Dispose(bool disposing)
        {
            throw new IOException("The installer UI disconnected.");
        }
    }

    private sealed class TrackingDisposable : IDisposable
    {
        public bool WasDisposed { get; private set; }

        public void Dispose()
        {
            WasDisposed = true;
        }
    }
}
