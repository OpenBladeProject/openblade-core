using System.Text;
using System.Text.Json;
using OpenBlade.Contracts;

namespace OpenBlade.Contracts.Tests;

public sealed class RustStatusCliContractTests
{
    [Fact]
    public void FixedRequestFixture_MatchesRealReadOnlyContract()
    {
        string repository = FindRepository();
        string fixture = File.ReadAllText(Path.Combine(
            repository, "rust", "crates", "openblade-status-cli", "fixtures", "request.json")).TrimEnd();
        var request = new GetStatusRequest(
            OpenBladeIpc.ReadOnlyCompatibilityProtocolVersion,
            Guid.Parse("11111111-2222-4333-8444-555555555555"));
        string actual = JsonSerializer.Serialize<ServiceRequest>(
            request, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(78, OpenBladeIpc.ProtocolVersion);
        Assert.Equal(51, OpenBladeIpc.ReadOnlyCompatibilityProtocolVersion);
        Assert.Equal("OpenBlade.Service.v2", OpenBladeIpc.PipeName);
        Assert.Equal(65536, OpenBladeIpc.MaximumMessageBytes);
        Assert.Equal(fixture, actual);
        Assert.Equal(89, Encoding.UTF8.GetByteCount(actual));
    }

    private static string FindRepository()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "rust", "Cargo.toml")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository fixture root is unavailable.");
    }
}
