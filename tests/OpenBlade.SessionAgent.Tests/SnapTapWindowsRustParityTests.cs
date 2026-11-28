using OpenBlade.SessionAgent;

namespace OpenBlade.SessionAgent.Tests;

public sealed class SnapTapWindowsRustParityTests
{
    [Fact]
    public void SharedTraceMatchesManagedWindowsInputState()
    {
        var batches = new List<string>();
        int? accepted = null;
        SnapTapWindowsHookLoop.InputState CreateState() => new(batch =>
        {
            batches.Add(batch.Count == 1 ? Stroke(batch.First) : $"{Stroke(batch.First)},{Stroke(batch.Second)}");
            return accepted == -1 ? throw new InvalidOperationException("Scripted uncertain output")
                : accepted ?? batch.Count;
        });

        var state = CreateState();
        string scenario = "";
        bool cleanStop = false;
        int index = 0;
        foreach (string line in File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "SnapTapWindowsHook.txt")))
        {
            index++;
            if (line.Length == 0 || line.StartsWith('#')) { continue; }
            string[] cols = line.Split('|');
            if (cols[0] == "case")
            {
                scenario = cols[1];
                state = CreateState();
                cleanStop = false;
                continue;
            }

            int[] args = Array.ConvertAll(cols[1].Split(','), int.Parse);
            string decision = "-";
            accepted = null;
            switch (cols[0])
            {
                case "arm":
                    state.Reconcile(args[0], args[1], args[2] != 0 && args[3] != 0);
                    break;
                case "key":
                    accepted = args.Length > 5 ? args[5] : null;
                    var result = state.Handle((uint)args[0], (uint)args[1], (nuint)args[2], args[3], args[4]);
                    decision = (result.Handled, result.Suppress) switch
                    {
                        (false, false) => "pass",
                        (true, false) => "forward",
                        (true, true) => "suppress",
                        (false, true) => "block",
                    };
                    break;
                case "stop":
                    accepted = args.Length > 3 ? args[3] : null;
                    state.Stop(args[0], args[1], args[2] != 0);
                    // InputLoop publishes Off after a clean InputState.Stop.
                    cleanStop = !state.Faulted;
                    break;
                default:
                    throw new InvalidDataException($"Unknown operation {cols[0]}");
            }

            var actual = new[]
            {
                decision,
                Key(state.Winner),
                cleanStop ? "Off" : state.Status.ToString(),
                state.Failure is { } f ? f.Reason + (f.MayHaveSubmitted ? "!" : "") : "-",
                batches.Count == 0 ? "-" : string.Join(';', batches),
            };
            Assert.True(cols.Skip(2).SequenceEqual(actual),
                $"{scenario}:{index}: expected {string.Join('|', cols.Skip(2))}, got {string.Join('|', actual)}");
            batches.Clear();
        }
    }

    private static string Key(ushort scan) => scan switch { 0x1E => "A", 0x20 => "D", 0 => "-", _ => throw new InvalidDataException() };

    private static string Stroke(SnapTapKeyStroke stroke) => Key(stroke.ScanCode) + (stroke.IsDown ? "+" : "-");
}
