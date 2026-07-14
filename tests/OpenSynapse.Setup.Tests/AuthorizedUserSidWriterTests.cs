using OpenSynapse.Setup;

namespace OpenSynapse.Setup.Tests;

public sealed class AuthorizedUserSidWriterTests
{
    [Fact]
    public async Task WriteAsync_WritesAndHardensSiblingTemporaryFileBeforeAtomicReplacement()
    {
        RecordingFileOperations files = new();
        AtomicAuthorizedUserSidWriter writer = new(files);
        const string target = @"C:\ProgramData\OpenSynapse\authorized-user.sid";

        await writer.WriteAsync(
            new AuthorizedSidWriteRequest(target, "S-1-5-21-1-2-3-1001"),
            CancellationToken.None);

        string temporary = Assert.Single(files.WrittenPaths);
        Assert.Equal(Path.GetDirectoryName(target), Path.GetDirectoryName(temporary));
        Assert.EndsWith(".tmp", temporary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            [
                $"directory:{Path.GetDirectoryName(target)}",
                $"write:{temporary}:S-1-5-21-1-2-3-1001",
                $"acl:{temporary}",
                $"replace:{temporary}:{target}",
            ],
            files.Operations);
    }

    private sealed class RecordingFileOperations : IAuthorizedSidFileOperations
    {
        public List<string> Operations { get; } = [];
        public List<string> WrittenPaths { get; } = [];

        public void CreateDirectory(string path) => Operations.Add($"directory:{path}");

        public Task WriteAllTextAsync(string path, string content, CancellationToken cancellationToken)
        {
            WrittenPaths.Add(path);
            Operations.Add($"write:{path}:{content}");
            return Task.CompletedTask;
        }

        public void RestrictToSystemAndAdministrators(string path) => Operations.Add($"acl:{path}");

        public void Replace(string source, string destination) =>
            Operations.Add($"replace:{source}:{destination}");

        public void DeleteIfExists(string path) => Operations.Add($"delete:{path}");
    }
}
