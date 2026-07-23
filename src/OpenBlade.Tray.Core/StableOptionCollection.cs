namespace OpenBlade.Tray;

/// <summary>
/// Retains an existing option-list instance while its ordered values are
/// unchanged. WinUI selection controls use item identity as part of their
/// selection lifecycle, so replacing an equivalent list is both unnecessary
/// and capable of temporarily clearing the displayed selection.
/// </summary>
internal static class StableOptionCollection
{
    public static IReadOnlyList<T> ReuseEquivalent<T>(
        IReadOnlyList<T> current,
        IReadOnlyList<T> incoming)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(incoming);

        if (current.Count != incoming.Count)
        {
            return incoming;
        }

        EqualityComparer<T> comparer = EqualityComparer<T>.Default;
        for (int index = 0; index < current.Count; index++)
        {
            if (!comparer.Equals(current[index], incoming[index]))
            {
                return incoming;
            }
        }

        return current;
    }
}
