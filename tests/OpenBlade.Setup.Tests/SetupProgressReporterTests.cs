using OpenBlade.Setup;
using System.Text;

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
    public void TextWriterReporter_EmitsOneBoundedLinePerAction()
    {
        StringWriter writer = new();
        using TextWriterSetupProgressReporter reporter = new(writer);

        reporter.Report("Copying payload\r\nwith forged line");
        reporter.Report(new string('x', 600));

        string[] lines = writer.ToString().Split(
            Environment.NewLine,
            StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal("Copying payload  with forged line", lines[0]);
        Assert.Equal(512, lines[1].Length);
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
