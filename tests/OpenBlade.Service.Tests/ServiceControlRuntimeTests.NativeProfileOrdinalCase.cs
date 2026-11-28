using System.Text;
using System.Text.Json.Nodes;

namespace OpenBlade.Service.Tests;

// The native duplicate-path comparison is pinned against the comparer the IPC
// codec and the profile validator use. The vector is every set of characters
// StringComparer.OrdinalIgnoreCase holds equal, found without any case table:
// equal strings hash alike, so each character only meets its hash bucket.
public sealed partial class ServiceControlRuntimeTests
{
    private const string NativeProfileOrdinalCase = "profile-ordinal-ignore-case.json";

    [Fact]
    public void NativeProfileOrdinalCase_MatchesManagedOrdinalIgnoreCase()
    {
        string? output = Environment.GetEnvironmentVariable(RegenerateNativeProfileVectors);
        string source = string.IsNullOrEmpty(output)
            ? Path.Combine(AppContext.BaseDirectory, NativeProfileOrdinalCase)
            : Path.Combine(output, NativeProfileOrdinalCase);
        JsonObject root = JsonNode.Parse(File.ReadAllText(source))!.AsObject();
        string[] observed = ObserveOrdinalIgnoreCaseClasses();

        if (!string.IsNullOrEmpty(output))
        {
            var text = new StringBuilder("{\n\"comment\":")
                .Append(root["comment"]!.ToJsonString())
                .Append(",\n\"classes\":[\n")
                .AppendJoin(",\n", observed.Select(entry => $"\"{entry}\""))
                .Append("\n]\n}\n");
            File.WriteAllText(source, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return;
        }

        string[] recorded = [.. root["classes"]!.AsArray().Select(entry => entry!.GetValue<string>())];
        Assert.Equal(recorded, observed);
    }

    // Each class as ascending hexadecimal code points; single characters are omitted.
    private static string[] ObserveOrdinalIgnoreCaseClasses()
    {
        StringComparer comparer = StringComparer.OrdinalIgnoreCase;
        var buckets = new Dictionary<int, List<int>>();
        for (int scalar = 0; scalar <= 0x10FFFF; scalar++)
        {
            if (scalar is >= 0xD800 and <= 0xDFFF)
            {
                continue;
            }

            int hash = comparer.GetHashCode(char.ConvertFromUtf32(scalar));
            if (!buckets.TryGetValue(hash, out List<int>? bucket))
            {
                buckets[hash] = bucket = [];
            }

            bucket.Add(scalar);
        }

        var classes = new List<List<int>>();
        foreach (List<int> bucket in buckets.Values.Where(bucket => bucket.Count > 1))
        {
            // A bucket may also hold characters that merely collide.
            var split = new List<List<int>>();
            foreach (int scalar in bucket)
            {
                string text = char.ConvertFromUtf32(scalar);
                List<int>? joined = split.Find(
                    members => comparer.Equals(char.ConvertFromUtf32(members[0]), text));
                if (joined is null)
                {
                    split.Add([scalar]);
                }
                else
                {
                    Assert.All(joined, member => Assert.True(comparer.Equals(char.ConvertFromUtf32(member), text)));
                    joined.Add(scalar);
                }
            }

            classes.AddRange(split.Where(members => members.Count > 1));
        }

        return [.. classes
            .OrderBy(members => members[0])
            .Select(members => string.Join(' ', members.Select(scalar => scalar.ToString("X"))))];
    }
}
