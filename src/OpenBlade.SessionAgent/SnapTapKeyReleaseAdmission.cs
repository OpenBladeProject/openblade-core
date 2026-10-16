namespace OpenBlade.SessionAgent;

/// <summary>Resolves the same physical pair as the hook before accepting held-key state.</summary>
internal static class SnapTapKeyReleaseAdmission
{
    internal static bool AreReleased(Func<uint, uint> mapScanCode, Func<int, short> readKeyState)
    {
        uint first = mapScanCode(0x1E);
        uint second = mapScanCode(0x20);
        if (first is 0 or > 255 || second is 0 or > 255 || first == second)
        {
            return false;
        }
        return (readKeyState((int)first) & 0x8000) == 0
            && (readKeyState((int)second) & 0x8000) == 0;
    }
}
