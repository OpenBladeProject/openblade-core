using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

// The native power-setting notices (rust/crates/openblade-domain/src/power_setting.rs)
// are pinned against managed NativeWindowsLifecycleNotificationBackend: the
// console display classifier, the POWERBROADCAST_SETTING decode
// (TryReadPowerSettingDword, read by reflection over an unmanaged copy of each
// row's bytes) and the notice-to-signal mapping, driven through the real
// NativeSubscription callbacks. The Rust domain and host tests read the same
// file.
public sealed class PowerSettingNativeVectorsTests
{
    private const string NativePowerSettingVectors = "power-setting-vectors.json";
    private const string RegenerateNativePowerSettingVectors =
        "OPENBLADE_REGENERATE_NATIVE_POWER_SETTING_VECTORS";

    private const string NativePowerSettingComment =
        "Recorded by PowerSettingNativeVectorsTests; "
        + "regenerate with OPENBLADE_REGENERATE_NATIVE_POWER_SETTING_VECTORS set to this directory. "
        + "display rows are ClassifyConsoleDisplayTransition(previous, current). "
        + "decode rows hold one POWERBROADCAST_SETTING (GUID, little-endian DataLength, payload) as hex; "
        + "expect holds TryReadPowerSettingDword's value for each registered GUID, or null when it refuses. "
        + "A mapping step is one power event; a PBT_POWERSETTINGCHANGE (32787) step may carry a setting. "
        + "Each step's signal is the gate trigger managed's NativeSubscription callback publishes for it "
        + "(OnSuspendResumeNotification, or for a setting change the callback Windows routes its GUID to), "
        + "filtered as Publish filters gate triggers (Suspending, Resumed, DisplayOn).";

    private const uint PowerSettingChange = 0x8013;

    private static readonly Guid AcDcPowerSource = new("5D3E9A59-E9D5-4B00-A6BD-FF34FF516548");
    private static readonly Guid ConsoleDisplayState = new("6FE69556-704A-47A0-8F24-C28D936FDA47");
    private static readonly Guid MonitorPowerOn = new("02731015-4510-4526-99E6-E5A17EBD1AEA");

    private delegate bool TryRead(IntPtr setting, Guid expected, out uint value);

    [Fact]
    public void NativePowerSettingVectors_MatchManagedNotifications()
    {
        string? output = Environment.GetEnvironmentVariable(RegenerateNativePowerSettingVectors);
        string path = string.IsNullOrEmpty(output)
            ? Path.Combine(AppContext.BaseDirectory, NativePowerSettingVectors)
            : Path.Combine(output, NativePowerSettingVectors);
        JsonObject expected = Record(NativePowerSettingRows());
        if (!string.IsNullOrEmpty(output))
        {
            // One row per line keeps the shared file reviewable in a diff.
            var text = new StringBuilder("{\n\"comment\":")
                .Append(expected["comment"]!.ToJsonString());
            foreach (string section in Sections)
            {
                text.Append(",\n\"").Append(section).Append("\":[\n")
                    .AppendJoin(",\n", expected[section]!.AsArray().Select(row => row!.ToJsonString()))
                    .Append("\n]");
            }

            File.WriteAllText(
                path,
                text.Append("\n}\n").ToString(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        // The file holds exactly the reviewed rows and managed's results for
        // them: a dropped or edited row would otherwise weaken the Rust side.
        JsonNode root = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.True(
            JsonNode.DeepEquals(root, expected),
            $"{NativePowerSettingVectors} differs from managed; regenerate it.");
    }

    private static readonly string[] Sections = ["display", "decode", "mapping"];

    private static JsonObject NativePowerSettingRows()
    {
        uint?[] previous = [null, 0, 1, 2, 3, uint.MaxValue];
        uint[] current = [0, 1, 2, 3, uint.MaxValue];
        var display = new JsonArray(previous
            .SelectMany(prior => current.Select(now => (JsonNode)new JsonObject
            {
                ["previous"] = prior,
                ["current"] = now,
            }))
            .ToArray());

        JsonObject[] decode =
        [
            Decode("display/off", Display(0)),
            Decode("display/on", Display(1)),
            Decode("display/dimmed", Display(2)),
            Decode("display/value-3", Display(3)),
            Decode("display/value-max", Display(uint.MaxValue)),
            Decode("display/value-256", Display(256)),
            Decode("display/value-high-byte", Display(0x0100_0001)),
            Decode("display/length-top-byte", Setting(ConsoleDisplayState, 0x0100_0000, 1, 0, 0, 0)),
            Decode("display/length-0", Setting(ConsoleDisplayState, 0)),
            Decode("display/length-3", Setting(ConsoleDisplayState, 3, 1, 0, 0)),
            Decode("display/length-3-with-a-whole-dword", Setting(ConsoleDisplayState, 3, 1, 0, 0, 0)),
            Decode("display/length-0-with-a-whole-dword", Setting(ConsoleDisplayState, 0, 1, 0, 0, 0)),
            Decode("display/length-8", Setting(ConsoleDisplayState, 8, 1, 0, 0, 0, 9, 9, 9, 9)),
            Decode("display/length-max", Setting(ConsoleDisplayState, uint.MaxValue, 2, 0, 0, 0)),
            Decode("source/ac", Source(0)),
            Decode("source/dc", Source(1)),
            Decode("source/hot", Source(2)),
            Decode("source/value-3", Source(3)),
            Decode("source/length-3", Setting(AcDcPowerSource, 3, 1, 0, 0)),
            Decode("guid/unregistered", Setting(MonitorPowerOn, 4, 1, 0, 0, 0)),
            Decode("guid/last-byte", Raw(ConsoleDisplayState, 15, 0x48, 4, 1, 0, 0, 0)),
            Decode("guid/first-byte", Raw(AcDcPowerSource, 0, 0x5A, 4, 1, 0, 0, 0)),
            Decode("guid/text-order", TextOrder(ConsoleDisplayState, 4, 1, 0, 0, 0)),
        ];

        JsonObject[] mapping =
        [
            Map("modern-standby/display-off-without-suspend",
                Notice(Display(1)), Notice(Display(0)), Notice(Display(1))),
            Map("modern-standby/dimmed-is-display-on",
                Notice(Display(1)), Notice(Display(2)), Notice(Display(0)), Notice(Display(2)),
                Notice(Display(1))),
            Map("s3/suspend-display-off-resume-display-on",
                Event(4), Notice(Display(0)), Event(7), Notice(Display(1))),
            Map("s3/automatic-resume", Event(4), Event(0x12), Event(7)),
            Map("display/first-read-only-records",
                Notice(Display(0)), Notice(Display(0)), Notice(Display(2))),
            Map("display/refused-reads-keep-the-state",
                Notice(Display(1)), Notice(Display(0)), Notice(Display(3)),
                Notice(Setting(ConsoleDisplayState, 3, 1, 0, 0)), Notice(Display(1))),
            Map("display/source-and-unregistered-settings-never-read-the-display",
                Notice(Display(0)), Notice(Source(1)), Notice(Setting(MonitorPowerOn, 4, 1, 0, 0, 0)),
                Notice(Display(1))),
            Map("events/other-power-events-carry-no-signal",
                Event(10), Event(6), Event(PowerSettingChange), Event(0x8013 + 1), Event(0x12)),
        ];

        return new JsonObject
        {
            ["comment"] = NativePowerSettingComment,
            ["display"] = display,
            ["decode"] = new JsonArray(decode),
            ["mapping"] = new JsonArray(mapping),
        };

        static JsonObject Decode(string name, string bytes) => new() { ["name"] = name, ["bytes"] = bytes };

        static JsonObject Map(string name, params JsonObject[] steps) =>
            new() { ["name"] = name, ["steps"] = new JsonArray(steps) };

        static JsonObject Event(uint value) => new() { ["event"] = value };

        static JsonObject Notice(string bytes) => new() { ["event"] = PowerSettingChange, ["bytes"] = bytes };

        static string Display(uint value) => Setting(ConsoleDisplayState, 4, BitConverter.GetBytes(value));

        static string Source(uint value) => Setting(AcDcPowerSource, 4, BitConverter.GetBytes(value));

        static string Setting(Guid guid, uint length, params byte[] payload) =>
            Hex([.. guid.ToByteArray(), .. BitConverter.GetBytes(length), .. payload]);

        static string Raw(Guid guid, int index, byte replacement, uint length, params byte[] payload)
        {
            byte[] bytes = guid.ToByteArray();
            bytes[index] = replacement;
            return Hex([.. bytes, .. BitConverter.GetBytes(length), .. payload]);
        }

        // The GUID in its textual byte order rather than Windows memory order.
        static string TextOrder(Guid guid, uint length, params byte[] payload) =>
            Hex([.. Convert.FromHexString(guid.ToString("N")), .. BitConverter.GetBytes(length), .. payload]);

        static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static JsonObject Record(JsonObject rows)
    {
        TryRead tryRead = TryReadPowerSettingDword();
        foreach (JsonNode? row in rows["display"]!.AsArray())
        {
            uint? previous = row!["previous"]?.GetValue<uint>();
            uint current = row["current"]!.GetValue<uint>();
            row["expect"] = NativeWindowsLifecycleNotificationBackend
                .ClassifyConsoleDisplayTransition(previous, current) switch
            {
                ServiceLifecycleTrigger.DisplayOn => "on",
                ServiceLifecycleTrigger.DisplayOff => "off",
                null => null,
                var other => throw new InvalidDataException($"unexpected display trigger {other}"),
            };
        }

        foreach (JsonNode? row in rows["decode"]!.AsArray())
        {
            string bytes = row!["bytes"]!.GetValue<string>();
            row["expect"] = new JsonObject
            {
                ["powerSource"] = Read(tryRead, bytes, AcDcPowerSource),
                ["consoleDisplay"] = Read(tryRead, bytes, ConsoleDisplayState),
            };
        }

        Callbacks callbacks = Callbacks.Find();
        foreach (JsonNode? row in rows["mapping"]!.AsArray())
        {
            var published = new List<ServiceLifecycleTrigger>();
            object subscription = callbacks.Subscription(published.Add);
            var signals = new JsonArray();
            foreach (JsonNode? step in row!["steps"]!.AsArray())
            {
                published.Clear();
                uint type = step!["event"]!.GetValue<uint>();
                callbacks.Deliver(subscription, type, step["bytes"]?.GetValue<string>());
                string[] gate = published
                    .Select(trigger => trigger switch
                    {
                        ServiceLifecycleTrigger.Suspending => "suspend",
                        ServiceLifecycleTrigger.Resumed => "resume",
                        ServiceLifecycleTrigger.DisplayOn => "displayOn",
                        _ => null,
                    })
                    .OfType<string>()
                    .ToArray();
                Assert.True(gate.Length <= 1, $"{row["name"]}: one notice published {gate.Length} gate triggers");
                signals.Add(gate.Length == 0 ? null : JsonValue.Create(gate[0]));
            }

            row["expect"] = signals;
        }

        return rows;
    }

    private static uint? Read(TryRead tryRead, string hex, Guid expected) =>
        WithSetting(hex, buffer => tryRead(buffer, expected, out uint value) ? value : (uint?)null);

    private static T WithSetting<T>(string hex, Func<IntPtr, T> use)
    {
        byte[] bytes = Convert.FromHexString(hex);
        // Managed reads the header and, for a DataLength of at least four, one
        // DWORD after it. Pad so a short row is never read past.
        int size = Math.Max(bytes.Length, 24);
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[size], 0, buffer, size);
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            return use(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static Type NativeSubscription() =>
        typeof(NativeWindowsLifecycleNotificationBackend)
            .GetNestedType("NativeSubscription", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("NativeSubscription was not found.");

    // The registered NativeSubscription callbacks on an uninitialized instance,
    // so nothing registers with Windows. Windows routes each setting change to
    // the callback registered for its GUID; an unregistered GUID is never
    // delivered, and a setting change without data reaches the display reader.
    private sealed class Callbacks(
        Type subscription,
        MethodInfo onSuspendResume,
        MethodInfo onPowerSource,
        MethodInfo onConsoleDisplay,
        FieldInfo publish,
        FieldInfo consoleDisplayState)
    {
        public static Callbacks Find()
        {
            Type subscription = NativeSubscription();
            const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
            return new Callbacks(
                subscription,
                Method("OnSuspendResumeNotification"),
                Method("OnPowerSourceNotification"),
                Method("OnConsoleDisplayNotification"),
                Field("publish"),
                Field("consoleDisplayState"));

            MethodInfo Method(string name) => subscription.GetMethod(name, Private)
                ?? throw new InvalidOperationException($"{name} was not found.");

            FieldInfo Field(string name) => subscription.GetField(name, Private)
                ?? throw new InvalidOperationException($"{name} was not found.");
        }

        public object Subscription(Action<ServiceLifecycleTrigger> tap)
        {
            object instance = RuntimeHelpers.GetUninitializedObject(subscription);
            publish.SetValue(instance, tap);
            // The constructor's initial value: no display state read yet.
            consoleDisplayState.SetValue(instance, -1);
            return instance;
        }

        public void Deliver(object instance, uint type, string? hex)
        {
            if (type != PowerSettingChange)
            {
                onSuspendResume.Invoke(instance, [IntPtr.Zero, type, IntPtr.Zero]);
                return;
            }

            if (hex is null)
            {
                onConsoleDisplay.Invoke(instance, [IntPtr.Zero, type, IntPtr.Zero]);
                return;
            }

            Guid setting = new(Convert.FromHexString(hex).AsSpan(0, 16));
            MethodInfo? callback = setting == ConsoleDisplayState ? onConsoleDisplay
                : setting == AcDcPowerSource ? onPowerSource
                : null;
            if (callback is not null)
            {
                WithSetting(hex, buffer => callback.Invoke(instance, [IntPtr.Zero, type, buffer]));
            }
        }
    }

    private static TryRead TryReadPowerSettingDword()
    {
        Type subscription = NativeSubscription();
        MethodInfo method = subscription.GetMethod(
                "TryReadPowerSettingDword",
                BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("TryReadPowerSettingDword was not found.");
        return method.CreateDelegate<TryRead>();
    }
}
