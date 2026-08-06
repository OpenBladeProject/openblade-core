using System.IO.Pipes;
using System.Text;

namespace OpenBlade.Setup;

public interface ISetupProgressReporter : IDisposable
{
    void Report(string message);
}

internal sealed class NullSetupProgressReporter : ISetupProgressReporter
{
    public static NullSetupProgressReporter Instance { get; } = new();

    public void Report(string message)
    {
    }

    public void Dispose()
    {
    }
}

internal sealed class TextWriterSetupProgressReporter(
    TextWriter writer,
    IDisposable? ownedResource = null) : ISetupProgressReporter
{
    private const int MaximumMessageLength = 512;
    private readonly Lock sync = new();
    private bool available = true;

    public void Report(string message)
    {
        if (!available || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        string normalized = message
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        if (normalized.Length > MaximumMessageLength)
        {
            normalized = normalized[..MaximumMessageLength];
        }

        lock (sync)
        {
            if (!available)
            {
                return;
            }

            try
            {
                writer.WriteLine(normalized);
                writer.Flush();
            }
            catch (IOException)
            {
                available = false;
            }
            catch (ObjectDisposedException)
            {
                available = false;
            }
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            available = false;
            TryDispose(writer);
            TryDispose(ownedResource);
        }
    }

    private static void TryDispose(IDisposable? resource)
    {
        try
        {
            resource?.Dispose();
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }
}

internal static class SetupProgressReporter
{
    private const string PipePrefix = "OpenBlade.Setup.Progress.";

    public static ISetupProgressReporter Create(string? pipeName)
    {
        if (!IsValidPipeName(pipeName))
        {
            return NullSetupProgressReporter.Instance;
        }

        NamedPipeClientStream? pipe = null;
        try
        {
            pipe = new NamedPipeClientStream(
                ".",
                pipeName!,
                PipeDirection.Out,
                PipeOptions.Asynchronous);
            pipe.Connect(2000);
            StreamWriter writer = new(
                pipe,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 1024,
                leaveOpen: true);
            return new TextWriterSetupProgressReporter(writer, pipe);
        }
        catch (IOException)
        {
            pipe?.Dispose();
            return NullSetupProgressReporter.Instance;
        }
        catch (TimeoutException)
        {
            pipe?.Dispose();
            return NullSetupProgressReporter.Instance;
        }
        catch (UnauthorizedAccessException)
        {
            pipe?.Dispose();
            return NullSetupProgressReporter.Instance;
        }
        catch (InvalidOperationException)
        {
            pipe?.Dispose();
            return NullSetupProgressReporter.Instance;
        }
    }

    internal static bool IsValidPipeName(string? pipeName)
    {
        if (pipeName is null
            || !pipeName.StartsWith(PipePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        string suffix = pipeName[PipePrefix.Length..];
        return Guid.TryParseExact(suffix, "N", out _);
    }
}
