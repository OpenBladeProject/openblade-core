namespace OpenBlade.Tray;

/// <summary>Invariant row identities within one explicitly registered settings group.</summary>
public sealed record CardOrderPreference(string GroupId, IReadOnlyList<string> CardIds);

public static class CardOrderPreferencePolicy
{
    public static IReadOnlyList<string> Normalize(
        IEnumerable<string>? saved,
        IReadOnlyList<string> known)
    {
        var allowed = known.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return (saved ?? []).Concat(known)
            .Where(id => id is not null && allowed.Contains(id) && seen.Add(id))
            .ToArray();
    }
}
