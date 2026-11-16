using OpenBlade.Contracts;

namespace OpenBlade.Tray;

/// <summary>
/// Editable keyboard colors for the reviewed RZ09-0529 key map.
/// This is presentation state only; applying it still uses the normal typed
/// lighting path and its existing admission and restoration checks.
/// </summary>
public sealed class KeyboardKeyColorEditor
{
    public const int DefaultFrameDurationMilliseconds = 100;

    private readonly RgbColor[] colors = new RgbColor[Rz090529KeyboardLightingLayout.SlotCount];
    private readonly LightingSettings original;
    private readonly KeyboardLightingSequenceFrame[] frames;
    private readonly bool loop = true;
    private readonly HashSet<int> editedSlots = [];

    public KeyboardKeyColorEditor(LightingSettings? initial = null)
    {
        original = initial ?? LightingSettingsFactory.Static(
            LightingSettingsFactory.DefaultVisibleColor, LightingSettingsFactory.DefaultVisibleBrightness);
        if (!LightingSettingsValidation.IsWellFormed(original))
        {
            throw new ArgumentException("The lighting draft is invalid.", nameof(initial));
        }

        if (original.Parameters is SequenceLightingParameters sequence)
        {
            if (sequence.LayoutId != KeyboardLightingLayoutIds.Rz09_0529)
            {
                throw new ArgumentException("The sequence must target the RZ09-0529 keyboard.", nameof(initial));
            }

            frames = KeyboardLightingSequenceCodec.Decode(sequence.TimelineBase64, sequence.LayoutId)
                .Frames.ToArray();
            loop = sequence.Loop;
            for (int slot = 0; slot < colors.Length; slot++)
            {
                colors[slot] = frames[0].Slots[slot];
            }
            return;
        }

        // Static intent is an exact seed. Other effects have no per-key draft;
        // start from their primary intent color, not a guessed firmware frame.
        RgbColor seed = original.Parameters is StaticLightingParameters single
            ? single.Color : original.Color;
        for (int slot = 0; slot < colors.Length; slot++)
        {
            if (Rz090529KeyboardLightingLayout.IsKeySlot(slot))
            {
                colors[slot] = seed;
            }
        }
        frames = [new(DefaultFrameDurationMilliseconds, colors.ToArray())];
    }

    public IReadOnlyList<RgbColor> Colors => Array.AsReadOnly(colors);

    public bool HasChanges => editedSlots.Count != 0;

    public static string GetKeyLabel(int slot)
    {
        if (!Rz090529KeyboardLightingLayout.IsKeySlot(slot))
        {
            throw new ArgumentOutOfRangeException(nameof(slot));
        }

        // Prefer the arrow identity over its navigation aliases. These are
        // aliases in this exact model's reviewed input map, not extra keys.
        foreach ((ushort ScanCode, string Label) arrow in new (ushort, string)[]
                 { (0x4B, "←"), (0x48, "↑"), (0x4D, "→"), (0x50, "↓") })
        {
            if (Rz090529KeyboardLightingLayout.TryMapScanCode(arrow.ScanCode, true, out byte mapped)
                && mapped == slot)
            {
                return arrow.Label;
            }
        }

        if (!Rz090529KeyboardLightingLayout.TryGetRepresentativeScanCode(
                slot, out ushort scanCode, out bool extended))
        {
            // A numeric address identifies an editable regional position without
            // inventing a key name absent from the common reviewed input map.
            return $"#{slot + 1}";
        }

        if (extended)
        {
            return scanCode switch
            {
                0x1C => "Num↵",
                0x1D => "RCtrl",
                0x35 => "Num/",
                0x38 => "RAlt",
                0x47 => "Home",
                0x49 => "PgUp",
                0x4F => "End",
                0x51 => "PgDn",
                0x52 => "Ins",
                0x53 => "Del",
                0x5B => "Win",
                _ => $"#{slot + 1}",
            };
        }

        if (scanCode is >= 0x02 and <= 0x0B)
        {
            return "1234567890"[scanCode - 0x02].ToString();
        }
        if (scanCode is >= 0x10 and <= 0x19)
        {
            return "QWERTYUIOP"[scanCode - 0x10].ToString();
        }
        if (scanCode is >= 0x1E and <= 0x26)
        {
            return "ASDFGHJKL"[scanCode - 0x1E].ToString();
        }
        if (scanCode is >= 0x2C and <= 0x32)
        {
            return "ZXCVBNM"[scanCode - 0x2C].ToString();
        }
        if (scanCode is >= 0x3B and <= 0x44)
        {
            return $"F{scanCode - 0x3A}";
        }

        return scanCode switch
        {
            0x01 => "Esc",
            0x0C => "-",
            0x0D => "=",
            0x0E => "Bksp",
            0x0F => "Tab",
            0x1A => "[",
            0x1B => "]",
            0x1D => "LCtrl",
            0x27 => ";",
            0x28 => "'",
            0x29 => "`",
            0x2A => "LShift",
            0x2B => "\\",
            0x33 => ",",
            0x34 => ".",
            0x35 => "/",
            0x36 => "RShift",
            0x37 => "Num*",
            0x38 => "LAlt",
            0x39 => "Space",
            0x3A => "Caps",
            0x45 => "Num",
            0x47 => "Num7",
            0x48 => "Num8",
            0x49 => "Num9",
            0x4A => "Num-",
            0x4B => "Num4",
            0x4C => "Num5",
            0x4D => "Num6",
            0x4E => "Num+",
            0x4F => "Num1",
            0x50 => "Num2",
            0x51 => "Num3",
            0x52 => "Num0",
            0x53 => "Num.",
            0x57 => "F11",
            0x58 => "F12",
            _ => $"#{slot + 1}",
        };
    }

    public void SetKeyColor(int matrixSlot, RgbColor color)
    {
        if (!Rz090529KeyboardLightingLayout.IsKeySlot(matrixSlot))
        {
            throw new ArgumentOutOfRangeException(nameof(matrixSlot),
                "Choose a mapped RZ09-0529 keyboard key.");
        }

        colors[matrixSlot] = color;
        if (frames.Any(frame => frame.Slots[matrixSlot] != color))
        {
            editedSlots.Add(matrixSlot);
        }
        else
        {
            editedSlots.Remove(matrixSlot);
        }
    }

    public LightingSettings BuildLightingSettings()
    {
        if (!HasChanges)
        {
            return original;
        }

        // Off has no visible brightness to retain. Applying a new key color
        // explicitly enables the keyboard at the normal visible default.
        return BuildLightingSettings(original.Brightness == 0
            ? LightingSettingsFactory.DefaultVisibleBrightness : original.Brightness);
    }

    public LightingSettings BuildLightingSettings(byte brightness)
    {
        if (brightness is 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(brightness));
        }

        if (!HasChanges)
        {
            return original.Effect == LightingEffect.Off ? original : original with { Brightness = brightness };
        }

        // Keep animation timing, loop intent, and every untouched key. A color
        // edit pins only the selected key across the existing sequence frames.
        var editedFrames = frames.Select(frame =>
        {
            RgbColor[] slots = frame.Slots.ToArray();
            foreach (int slot in editedSlots)
            {
                slots[slot] = colors[slot];
            }
            return new KeyboardLightingSequenceFrame(frame.DurationMilliseconds, slots);
        });
        string timeline = KeyboardLightingSequenceCodec.Encode(
            editedFrames, KeyboardLightingLayoutIds.Rz09_0529);
        return LightingSettingsFactory.Sequence(
            KeyboardLightingLayoutIds.Rz09_0529,
            timeline,
            loop: loop,
            brightness: brightness) with
        {
            IndependentControls = original.IndependentControls,
            ControlsKeyboardEffect = true,
        };
    }
}
