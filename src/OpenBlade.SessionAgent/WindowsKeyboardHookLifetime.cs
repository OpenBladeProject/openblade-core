namespace OpenBlade.SessionAgent;

// Used only by the hook's message thread. A failed retirement stops replacement,
// bounding ownership to the current hook and one hook still awaiting cleanup.
internal sealed class WindowsKeyboardHookLifetime(Func<nint> create, Func<nint, bool> release)
{
    private nint current;
    private nint retiring;
    private bool retirementFailed;

    internal bool InstallOrReplace()
    {
        if (retirementFailed || retiring != 0)
        {
            return false;
        }

        nint replacement = create();
        if (replacement == 0)
        {
            throw new InvalidOperationException("Keyboard hook creation returned no handle.");
        }
        retiring = current;
        current = replacement;
        if (retiring != 0)
        {
            if (!release(retiring))
            {
                retirementFailed = true;
                return false;
            }
            retiring = 0;
        }
        return true;
    }

    internal bool ReleaseAll()
    {
        if (current != 0 && release(current))
        {
            current = 0;
        }
        if (retiring != 0 && release(retiring))
        {
            retiring = 0;
        }
        return current == 0 && retiring == 0;
    }
}
