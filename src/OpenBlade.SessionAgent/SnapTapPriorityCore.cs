namespace OpenBlade.SessionAgent;

public readonly record struct SnapTapKeyStroke(ushort ScanCode, bool IsDown);

public readonly record struct SnapTapOutputBatch(SnapTapKeyStroke First, SnapTapKeyStroke Second, int Count);

public readonly record struct SnapTapInputResult(bool Handled, bool Suppress);

/// <summary>Single-threaded last-pressed priority for the fixed A/D scan-code pair.</summary>
public sealed class SnapTapPriorityCore(Func<SnapTapOutputBatch, int> send)
{
    private ushort first;
    private ushort second;

    public bool Enabled { get; private set; }
    public bool Faulted { get; private set; }
    public bool ArePhysicallyReleased => first == 0 && second == 0;
    public ushort Winner { get; private set; }

    public bool Enable()
    {
        if (Faulted || !ArePhysicallyReleased || Winner != 0)
        {
            return false;
        }

        Enabled = true;
        return true;
    }

    public void Poison()
    {
        Enabled = false;
        Faulted = true;
        Winner = 0;
    }

    public void Disable()
    {
        bool releaseWinner = Enabled && !Faulted && Winner != 0;
        Enabled = false;
        if (releaseWinner)
        {
            Emit(new(new(Winner, false), default, 1), 0);
        }
    }

    public SnapTapInputResult Handle(SnapTapKeyStroke key)
    {
        if (key.ScanCode is not (0x1E or 0x20))
        {
            return default;
        }

        bool held = first == key.ScanCode || second == key.ScanCode;
        if (key.IsDown && !held)
        {
            if (first == 0)
            {
                first = key.ScanCode;
            }
            else
            {
                second = key.ScanCode;
            }
        }
        else if (!key.IsDown && held)
        {
            if (first == key.ScanCode)
            {
                first = second;
                second = 0;
            }
            else
            {
                second = 0;
            }
        }

        if (!Enabled)
        {
            return default;
        }

        ushort desired = second != 0 ? second : first;
        if (desired == Winner)
        {
            // Preserve physical winner repeats and releases. Only a losing repeat
            // must be suppressed; it belongs to an older physical press.
            return new(true, key.IsDown && held && desired != key.ScanCode);
        }

        if (key.IsDown)
        {
            // The original press carries its Raw Input device identity. Release
            // the old winner before forwarding it. Native delivery ordering is
            // verified separately from this single-threaded output plan.
            if (Winner != 0 && !Emit(new(new(Winner, false), default, 1), desired))
            {
                // Do not add a new down after an uncertain release.
                return new(false, true);
            }
            Winner = desired;
            return new(true, false);
        }

        if (desired == 0)
        {
            Winner = 0;
            return new(true, false);
        }

        // Restoring a still-held key has no new physical press. Keep the release
        // and restoration together, suppressing the original release on success.
        bool restored = Emit(new(new(Winner, false), new(desired, true), 2), desired);
        return new(restored, restored);
    }

    private bool Emit(SnapTapOutputBatch batch, ushort desired)
    {
        if (batch.Count != 0)
        {
            int accepted;
            try
            {
                accepted = send(batch);
            }
            catch
            {
                Poison();
                return false;
            }

            // Submission may have happened even on failure. Never retry or clean up
            // an uncertain output state, including poison raised inside the sender.
            if (Faulted || accepted != batch.Count)
            {
                Poison();
                return false;
            }
        }

        Winner = desired;
        return true;
    }
}
