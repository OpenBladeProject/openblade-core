namespace OpenBlade.Display;

internal sealed record ColorProfileMutationBaseline(
    uint Scope,
    string? DefaultProfile,
    IReadOnlyList<string> Associations);

internal sealed record ColorProfileMutationOperations(
    Action<string> AddAssociation,
    Action<string> RemoveAssociation,
    Action<string> SetDefault,
    Action<bool> SetPerUserScope,
    Func<uint> GetScope,
    Func<string?> GetDefault,
    Func<IReadOnlyList<string>> GetAssociations);

internal static class ColorProfileMutationTransaction
{
    internal static void Execute(
        string requestedProfile,
        ColorProfileMutationBaseline baseline,
        ColorProfileMutationOperations operations,
        Func<bool> verifyApplied)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedProfile);
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(verifyApplied);

        bool requestedWasAdded = false;
        bool requestedAlreadyPresent = Contains(baseline.Associations, requestedProfile)
            || ProfilesMatch(baseline.DefaultProfile, requestedProfile);

        try
        {
            if (!requestedAlreadyPresent)
            {
                operations.AddAssociation(requestedProfile);
                requestedWasAdded = true;
                RequireAssociationOrDefault(operations, requestedProfile);
            }

            if (operations.GetScope() != WindowsDisplayNative.CurrentUserScope)
            {
                operations.SetPerUserScope(true);
                RequireScope(operations, WindowsDisplayNative.CurrentUserScope);
            }

            operations.SetDefault(requestedProfile);
            RequireDefault(operations, requestedProfile);
            if (!verifyApplied())
            {
                throw new InvalidOperationException(
                    "Windows did not confirm the requested color profile.");
            }
        }
        catch (Exception mutationFailure)
        {
            try
            {
                Restore(
                    requestedProfile,
                    requestedWasAdded,
                    baseline,
                    operations);
            }
            catch (Exception restorationFailure)
            {
                throw new InvalidOperationException(
                    $"{mutationFailure.Message} The previous color-profile configuration could not be restored: {restorationFailure.Message}",
                    new AggregateException(mutationFailure, restorationFailure));
            }

            throw new InvalidOperationException(
                $"{mutationFailure.Message} The previous color-profile configuration was restored.",
                mutationFailure);
        }
    }

    private static void Restore(
        string requestedProfile,
        bool requestedWasAdded,
        ColorProfileMutationBaseline baseline,
        ColorProfileMutationOperations operations)
    {
        // Reconcile before writing. A failed native call may still have changed
        // state; do not replay it or guess which part was submitted.
        IReadOnlyList<string> currentAssociations = operations.GetAssociations();
        string? currentDefault = operations.GetDefault();
        string[] unexpectedAssociations = currentAssociations.Where(
                profile => !Contains(baseline.Associations, profile)
                    && (!requestedWasAdded
                        || !ProfilesMatch(profile, requestedProfile)))
            .ToArray();
        bool missingOriginalAssociation = baseline.Associations.Any(
            profile => !Contains(currentAssociations, profile));
        if (unexpectedAssociations.Length > 0 || missingOriginalAssociation)
        {
            throw new InvalidOperationException(
                "The current-user color-profile associations changed unexpectedly; restoration stopped without altering them.");
        }

        if (!ProfilesMatch(currentDefault, baseline.DefaultProfile))
        {
            if (baseline.DefaultProfile is not null)
            {
                operations.SetDefault(baseline.DefaultProfile);
                RequireDefault(operations, baseline.DefaultProfile);
            }
            else if (currentDefault is not null)
            {
                if (!requestedWasAdded
                    || !ProfilesMatch(currentDefault, requestedProfile))
                {
                    throw new InvalidOperationException(
                        "The previous null default cannot be restored without changing a pre-existing association.");
                }
            }
        }

        if (requestedWasAdded
            && (Contains(operations.GetAssociations(), requestedProfile)
                || ProfilesMatch(operations.GetDefault(), requestedProfile)))
        {
            operations.RemoveAssociation(requestedProfile);
            RequireAssociationAndDefaultAbsent(operations, requestedProfile);
        }

        uint currentScope = operations.GetScope();
        if (currentScope != baseline.Scope)
        {
            operations.SetPerUserScope(baseline.Scope == WindowsDisplayNative.CurrentUserScope);
            RequireScope(operations, baseline.Scope);
        }

        IReadOnlyList<string> restoredAssociations = operations.GetAssociations();
        if (!SetEquals(restoredAssociations, baseline.Associations)
            || !ProfilesMatch(operations.GetDefault(), baseline.DefaultProfile)
            || operations.GetScope() != baseline.Scope)
        {
            throw new InvalidOperationException(
                "Windows did not confirm restoration of the previous color-profile configuration.");
        }
    }

    private static void RequireAssociationOrDefault(
        ColorProfileMutationOperations operations,
        string profile)
    {
        if (!Contains(operations.GetAssociations(), profile)
            && !ProfilesMatch(operations.GetDefault(), profile))
        {
            throw new InvalidOperationException(
                "Windows did not confirm the color-profile association.");
        }
    }

    private static void RequireAssociationAndDefaultAbsent(
        ColorProfileMutationOperations operations,
        string profile)
    {
        if (Contains(operations.GetAssociations(), profile)
            || ProfilesMatch(operations.GetDefault(), profile))
        {
            throw new InvalidOperationException(
                "Windows did not confirm removal of the color-profile association and default.");
        }
    }

    private static void RequireDefault(
        ColorProfileMutationOperations operations,
        string expected)
    {
        if (!ProfilesMatch(operations.GetDefault(), expected))
        {
            throw new InvalidOperationException(
                "Windows did not confirm the requested default color profile.");
        }
    }

    private static void RequireScope(ColorProfileMutationOperations operations, uint expected)
    {
        if (operations.GetScope() != expected)
        {
            throw new InvalidOperationException(
                "Windows did not confirm the requested color-profile scope.");
        }
    }

    private static bool SetEquals(
        IReadOnlyList<string> left,
        IReadOnlyList<string> right) =>
        left.Count == right.Count
        && left.All(profile => Contains(right, profile));

    private static bool Contains(IReadOnlyList<string> profiles, string profile) =>
        profiles.Any(candidate => ProfilesMatch(candidate, profile));

    private static bool ProfilesMatch(string? profile, string? other)
    {
        if (profile is null || other is null)
        {
            return profile is null && other is null;
        }

        return string.Equals(profile, other, StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                Path.GetFileName(profile),
                Path.GetFileName(other),
                StringComparison.OrdinalIgnoreCase);
    }
}
