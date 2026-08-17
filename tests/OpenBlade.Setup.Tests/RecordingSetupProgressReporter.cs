using OpenBlade.Setup;

namespace OpenBlade.Setup.Tests;

internal sealed class RecordingSetupProgressReporter : ISetupProgressReporter
{
    public List<SetupProgressEvent> Events { get; } = [];

    public void Report(SetupProgressEvent progress) => Events.Add(progress);

    public void Dispose()
    {
    }
}
