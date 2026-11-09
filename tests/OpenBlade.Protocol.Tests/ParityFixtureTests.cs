using System.Text.Json;
using System.Text.Json.Nodes;
using OpenBlade.Testing;
using Xunit.Sdk;

namespace OpenBlade.Protocol.Tests;

public sealed class ParityFixtureTests
{
    [Theory]
    [InlineData("future-schema")]
    [InlineData("wrong-suite")]
    [InlineData("missing-revision")]
    [InlineData("different-revision")]
    [InlineData("unknown-root-field")]
    [InlineData("unknown-row-field")]
    [InlineData("missing-row-field")]
    [InlineData("blank-id")]
    [InlineData("duplicate-id")]
    public void ReaderRejectsMalformedContract(string fault)
    {
        JsonObject root = new()
        {
            ["schemaVersion"] = 1,
            ["suite"] = "application-profile-merge",
            ["sourceRevision"] = ParityFixture.SourceRevision,
            ["cases"] = new JsonArray(new JsonObject
            {
                ["id"] = "case",
                ["baseline"] = null,
                ["override"] = null,
                ["expected"] = null,
                ["expectedError"] = "baseline-null",
            }),
        };
        JsonObject row = root["cases"]![0]!.AsObject();
        switch (fault)
        {
            case "future-schema": root["schemaVersion"] = 2; break;
            case "wrong-suite": root["suite"] = "other"; break;
            case "missing-revision": root.Remove("sourceRevision"); break;
            case "different-revision": root["sourceRevision"] = new string('0', 40); break;
            case "unknown-root-field": root["extra"] = true; break;
            case "unknown-row-field": row["extra"] = true; break;
            case "missing-row-field": row.Remove("expectedError"); break;
            case "blank-id": row["id"] = " "; break;
            case "duplicate-id": root["cases"]!.AsArray().Add(row.DeepClone()); break;
            default: throw new ArgumentOutOfRangeException(nameof(fault));
        }

        Assert.ThrowsAny<XunitException>(() =>
            ParityFixture.Parse(root.ToJsonString(), "application-profile-merge", "cases"));
    }

    [Theory]
    [InlineData("aa")]
    [InlineData("0")]
    [InlineData("GG")]
    public void ReaderRejectsNonCanonicalHex(string hex)
    {
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(hex));
        Assert.ThrowsAny<XunitException>(() => ParityFixture.Hex(document.RootElement));
    }

    [Fact]
    public void ReaderRejectsDuplicateFields()
    {
        Assert.Throws<JsonException>(() => ParityFixture.Parse(
            "{\"schemaVersion\":1,\"schemaVersion\":1}", "application-profile-merge", "cases"));
    }
}
