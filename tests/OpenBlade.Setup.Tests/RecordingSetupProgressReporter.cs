using OpenBlade.Setup;

namespace OpenBlade.Setup.Tests;

internal sealed class RecordingSetupProgressReporter : ISetupProgressReporter
{
    public List<string> Messages { get; } = [];

    public void Report(string message) => Messages.Add(message);

    public void Dispose()
    {
    }
}
