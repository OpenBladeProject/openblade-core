using OpenBlade.Display;

namespace OpenBlade.Display.Tests;

public sealed class ColorProfileMutationTransactionTests
{
    [Fact]
    public void FirstAssociation_IsCreatedBeforePerUserScopeIsEnabled()
    {
        var state = new FakeColorProfileState(
            WindowsDisplayNative.SystemWideScope,
            defaultProfile: null,
            associations: []);

        ColorProfileMutationTransaction.Execute(
            "Native_CTR.icm",
            state.Baseline(),
            state.Operations(),
            () => true);

        Assert.Equal(
            ["add:Native_CTR.icm", "scope:True", "default:Native_CTR.icm"],
            state.Writes);
        Assert.Equal(WindowsDisplayNative.CurrentUserScope, state.Scope);
        Assert.Equal("Native_CTR.icm", state.DefaultProfile);
        Assert.Equal(["Native_CTR.icm"], state.Associations);
    }

    [Fact]
    public void AssociationThatActivatesPerUserScope_DoesNotRepeatScopeWrite()
    {
        var state = new FakeColorProfileState(
            WindowsDisplayNative.SystemWideScope,
            defaultProfile: null,
            associations: []);
        state.AddAssociationEffect = _ =>
            state.Scope = WindowsDisplayNative.CurrentUserScope;

        ColorProfileMutationTransaction.Execute(
            "Native_CTR.icm",
            state.Baseline(),
            state.Operations(),
            () => true);

        Assert.Equal(
            ["add:Native_CTR.icm", "default:Native_CTR.icm"],
            state.Writes);
        Assert.Equal(WindowsDisplayNative.CurrentUserScope, state.Scope);
    }

    [Fact]
    public void InactiveAssociationVisibleOnlyAsDefault_IsAcceptedBeforeScopeActivation()
    {
        var state = new FakeColorProfileState(
            WindowsDisplayNative.SystemWideScope,
            defaultProfile: null,
            associations: []);
        state.AddAssociationEffect = profile =>
        {
            state.RemoveAssociationFromReadback(profile);
            state.DefaultProfile = profile;
        };

        ColorProfileMutationTransaction.Execute(
            "Native_CTR.icm",
            state.Baseline(),
            state.Operations(),
            () => true);

        Assert.Equal(
            ["add:Native_CTR.icm", "scope:True", "default:Native_CTR.icm"],
            state.Writes);
        Assert.Equal(WindowsDisplayNative.CurrentUserScope, state.Scope);
        Assert.Equal("Native_CTR.icm", state.DefaultProfile);
        Assert.Empty(state.Associations);
    }

    [Fact]
    public void FailedScopeChange_RemovesNewAssociationWithoutRepeatingScopeWrite()
    {
        var state = new FakeColorProfileState(
            WindowsDisplayNative.SystemWideScope,
            defaultProfile: null,
            associations: []);
        state.SetScopeFailure = _ => throw new InvalidOperationException("scope failed");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            ColorProfileMutationTransaction.Execute(
                "Native_CTR.icm",
                state.Baseline(),
                state.Operations(),
                () => true));

        Assert.Contains("was restored", exception.Message);
        Assert.Equal(["add:Native_CTR.icm", "scope:True", "remove:Native_CTR.icm"], state.Writes);
        Assert.Equal(WindowsDisplayNative.SystemWideScope, state.Scope);
        Assert.Null(state.DefaultProfile);
        Assert.Empty(state.Associations);
    }

    [Fact]
    public void FailedScopeChangeThatMutatedScope_RestoresOriginalScope()
    {
        var state = new FakeColorProfileState(
            WindowsDisplayNative.SystemWideScope,
            defaultProfile: null,
            associations: []);
        state.SetScopeFailure = enabled =>
        {
            state.Scope = enabled
                ? WindowsDisplayNative.CurrentUserScope
                : WindowsDisplayNative.SystemWideScope;
            throw new InvalidOperationException("scope result was ambiguous");
        };

        _ = Assert.Throws<InvalidOperationException>(() =>
            ColorProfileMutationTransaction.Execute(
                "Native_CTR.icm",
                state.Baseline(),
                state.Operations(),
                () => true));

        Assert.Equal(
            [
                "add:Native_CTR.icm",
                "scope:True",
                "remove:Native_CTR.icm",
                "scope:False",
            ],
            state.Writes);
        Assert.Equal(WindowsDisplayNative.SystemWideScope, state.Scope);
        Assert.Empty(state.Associations);
    }

    [Fact]
    public void InactiveAssociationVisibleOnlyAsDefault_IsRemovedDuringRollback()
    {
        var state = new FakeColorProfileState(
            WindowsDisplayNative.SystemWideScope,
            defaultProfile: null,
            associations: []);
        state.AddAssociationEffect = profile =>
        {
            state.RemoveAssociationFromReadback(profile);
            state.DefaultProfile = profile;
        };
        state.SetScopeFailure = _ => throw new InvalidOperationException("scope failed");

        _ = Assert.Throws<InvalidOperationException>(() =>
            ColorProfileMutationTransaction.Execute(
                "Native_CTR.icm",
                state.Baseline(),
                state.Operations(),
                () => true));

        Assert.Equal(
            ["add:Native_CTR.icm", "scope:True", "remove:Native_CTR.icm"],
            state.Writes);
        Assert.Null(state.DefaultProfile);
        Assert.Empty(state.Associations);
        Assert.Equal(WindowsDisplayNative.SystemWideScope, state.Scope);
    }

    [Fact]
    public void FailedDefaultChange_PreservesOriginalCurrentUserAssociationsAndDefault()
    {
        var state = new FakeColorProfileState(
            WindowsDisplayNative.CurrentUserScope,
            "Blade.icm",
            ["Blade.icm", "sRGB_CTR.icm"]);
        state.SetDefaultFailure = profile =>
        {
            state.DefaultProfile = profile;
            throw new InvalidOperationException("default result was ambiguous");
        };

        _ = Assert.Throws<InvalidOperationException>(() =>
            ColorProfileMutationTransaction.Execute(
                "Native_CTR.icm",
                state.Baseline(),
                state.Operations(),
                () => true));

        Assert.Equal(
            [
                "add:Native_CTR.icm",
                "default:Native_CTR.icm",
                "default:Blade.icm",
                "remove:Native_CTR.icm",
            ],
            state.Writes);
        Assert.Equal("Blade.icm", state.DefaultProfile);
        Assert.Equal(["Blade.icm", "sRGB_CTR.icm"], state.Associations);
        Assert.Equal(WindowsDisplayNative.CurrentUserScope, state.Scope);
    }

    [Fact]
    public void FailedFinalReadback_RestoresExactEmptyBaseline()
    {
        var state = new FakeColorProfileState(
            WindowsDisplayNative.SystemWideScope,
            defaultProfile: null,
            associations: []);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            ColorProfileMutationTransaction.Execute(
                "Native_CTR.icm",
                state.Baseline(),
                state.Operations(),
                () => false));

        Assert.Contains("was restored", exception.Message);
        Assert.Equal(
            [
                "add:Native_CTR.icm",
                "scope:True",
                "default:Native_CTR.icm",
                "remove:Native_CTR.icm",
                "scope:False",
            ],
            state.Writes);
        Assert.Equal(WindowsDisplayNative.SystemWideScope, state.Scope);
        Assert.Null(state.DefaultProfile);
        Assert.Empty(state.Associations);
    }

    [Fact]
    public void FailedReconciliationRead_StopsWithoutRestorationWrites()
    {
        var state = new FakeColorProfileState(WindowsDisplayNative.CurrentUserScope, "Blade.icm", ["Blade.icm"]);
        bool reconcile = false;
        ColorProfileMutationOperations operations = state.Operations();
        operations = operations with
        {
            GetAssociations = () => reconcile ? throw new IOException("read unavailable") : state.Associations,
        };
        Assert.Throws<InvalidOperationException>(() => ColorProfileMutationTransaction.Execute(
            "Native_CTR.icm", state.Baseline(), operations, () => { reconcile = true; return false; }));
        Assert.Equal(["add:Native_CTR.icm", "default:Native_CTR.icm"], state.Writes);
    }

    private sealed class FakeColorProfileState(
        uint scope,
        string? defaultProfile,
        IReadOnlyList<string> associations)
    {
        private readonly List<string> associations = [.. associations];

        internal uint Scope { get; set; } = scope;

        internal string? DefaultProfile { get; set; } = defaultProfile;

        internal IReadOnlyList<string> Associations => associations;

        internal List<string> Writes { get; } = [];

        internal Action<bool>? SetScopeFailure { get; set; }

        internal Action<string>? SetDefaultFailure { get; set; }

        internal Action<string>? AddAssociationEffect { get; set; }

        internal ColorProfileMutationBaseline Baseline() => new(
            Scope,
            DefaultProfile,
            [.. associations]);

        internal ColorProfileMutationOperations Operations() => new(
            profile =>
            {
                Writes.Add($"add:{profile}");
                if (!associations.Contains(profile, StringComparer.OrdinalIgnoreCase))
                {
                    associations.Add(profile);
                }

                AddAssociationEffect?.Invoke(profile);
            },
            profile =>
            {
                Writes.Add($"remove:{profile}");
                associations.RemoveAll(candidate => string.Equals(
                    candidate,
                    profile,
                    StringComparison.OrdinalIgnoreCase));
                if (string.Equals(DefaultProfile, profile, StringComparison.OrdinalIgnoreCase))
                {
                    DefaultProfile = null;
                }
            },
            profile =>
            {
                Writes.Add($"default:{profile}");
                if (SetDefaultFailure is not null)
                {
                    Action<string> failure = SetDefaultFailure;
                    SetDefaultFailure = null;
                    failure(profile);
                    return;
                }

                DefaultProfile = profile;
            },
            enabled =>
            {
                Writes.Add($"scope:{enabled}");
                if (SetScopeFailure is not null)
                {
                    Action<bool> failure = SetScopeFailure;
                    SetScopeFailure = null;
                    failure(enabled);
                    return;
                }

                Scope = enabled
                    ? WindowsDisplayNative.CurrentUserScope
                    : WindowsDisplayNative.SystemWideScope;
            },
            () => Scope,
            () => DefaultProfile,
            () => [.. associations]);

        internal void RemoveAssociationFromReadback(string profile) =>
            associations.RemoveAll(candidate => string.Equals(
                candidate,
                profile,
                StringComparison.OrdinalIgnoreCase));
    }
}
