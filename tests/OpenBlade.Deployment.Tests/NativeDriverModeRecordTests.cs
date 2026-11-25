using System.Text;
using System.Text.Json;

namespace OpenBlade.Deployment.Tests;

// Setup's parser replays the Rust codec's shared vectors (driver_mode_history.rs):
// the same accepted records with the same fields, the same rejections, and the
// canonical re-encoding. Setup never writes a record, so follows is not replayed;
// every record a transition names must still parse.
public sealed class NativeDriverModeRecordTests
{
    private static JsonElement Vector() =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "driver-mode-record-v1.json"))).RootElement;
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void AcceptedRecordsParseToTheirFieldsAndReencodeExactly()
    {
        var v = Vector();
        Assert.Equal(NativeDriverModeRecord.Header, v.GetProperty("header").GetString());
        Assert.Equal(NativeDriverModeRecord.Contract, v.GetProperty("contract").GetString());
        Assert.Equal(NativeDriverModeRecord.MaxBytes, v.GetProperty("maxBytes").GetInt32());
        int count = 0;
        foreach (var entry in v.GetProperty("records").EnumerateObject())
        {
            var e = entry.Value;
            byte[] bytes = Bytes(e.GetProperty("bytes").GetString()!);
            var record = NativeDriverModeRecord.Parse(bytes);
            Assert.Equal(e.GetProperty("sequence").GetString(), record.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(e.GetProperty("owner").GetString(), record.Owner);
            Assert.Equal(e.GetProperty("state").GetString(), record.State.ToString());
            Assert.Equal(e.GetProperty("prior").GetString(), record.Prior.ToString());
            Assert.Equal(e.GetProperty("ids").EnumerateArray().Select(id => id.GetByte()), record.Ids);
            Assert.Equal(bytes, record.Encode());
            count++;
        }
        Assert.Equal(13, count);
    }

    [Fact]
    public void RejectedInputsAreRefused()
    {
        int count = 0;
        foreach (var e in Vector().GetProperty("rejected").EnumerateArray())
        {
            byte[] bytes = Bytes(e.GetProperty("bytes").GetString()!);
            Assert.Throws<InvalidDataException>(() => NativeDriverModeRecord.Parse(bytes));
            count++;
        }
        Assert.Equal(40, count);
    }

    [Fact]
    public void EveryRecordATransitionNamesParsesCanonically()
    {
        int count = 0;
        foreach (var e in Vector().GetProperty("transitions").EnumerateArray())
        {
            foreach (string key in new[] { "prior", "next" })
            {
                if (e.GetProperty(key).GetString() is not { } text) { continue; }
                byte[] bytes = Bytes(text);
                Assert.Equal(bytes, NativeDriverModeRecord.Parse(bytes).Encode());
            }
            count++;
        }
        Assert.Equal(38, count);
    }
}
