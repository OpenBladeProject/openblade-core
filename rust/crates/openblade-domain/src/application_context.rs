//! Managed `ProfileAutomationState`'s active application profile: which saved
//! rule a reported executable selects, when a profile update retires that
//! selection, and the save path's active-intent predicate.
//!
//! Rules are borrowed views, so the typed `ProfileSet` and the host's replay
//! share this one implementation. Path equality is the caller's: managed
//! compares with `StringComparison.OrdinalIgnoreCase`, which
//! `profile_set::text::same_ordinal_ignoring_case` reproduces from managed's
//! recorded table. Allocation-free; the caller picks the owned string type.

/// Managed `StringComparison.OrdinalIgnoreCase` equality, supplied by the caller
/// (`StringComparer.OrdinalIgnoreCase` in the profile validator).
pub type SamePath = fn(&str, &str) -> bool;

/// One saved `ApplicationProfileDefinition`, as selection reads it.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct Rule<'a> {
    /// The GUID in one fixed spelling; ids compare exactly, as `Guid`s do.
    pub id: &'a str,
    /// Stored in `NormalizeExecutablePath` form.
    pub executable_path: &'a str,
    pub enabled: bool,
}

impl Rule<'_> {
    /// `SetApplicationContext` (`ProfileAutomationState.cs:223-226`).
    fn selects(&self, path: &str, same_path: SamePath) -> bool {
        self.enabled && same_path(self.executable_path, path)
    }

    /// `UpdateProfiles` (`ProfileAutomationState.cs:134-140`).
    fn retains(&self, active: Active<'_>, same_path: SamePath) -> bool {
        self.enabled
            && self.id == active.id
            && same_path(self.executable_path, active.executable_path)
    }

    /// Whether this is the rule whose overrides apply while `id` is active:
    /// `GetApplicationOverride` (`ProfileAutomationState.cs:632-639`) takes
    /// the first enabled rule with that id and does not compare the path.
    pub fn supplies(&self, id: &str) -> bool {
        self.enabled && self.id == id
    }
}

/// Managed `activeApplicationProfileId` with `activeApplicationExecutablePath`,
/// the normalized path that was reported, not the rule's spelling of it.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct Active<'a> {
    pub id: &'a str,
    pub executable_path: &'a str,
}

/// The active pair `ProfileAutomationState` keeps between reports.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ApplicationContext<S> {
    active: Option<(S, S)>,
}

impl<S> Default for ApplicationContext<S> {
    fn default() -> Self {
        Self { active: None }
    }
}

impl<S: AsRef<str> + for<'a> From<&'a str>> ApplicationContext<S> {
    pub fn active(&self) -> Option<Active<'_>> {
        self.active.as_ref().map(|(id, path)| Active {
            id: id.as_ref(),
            executable_path: path.as_ref(),
        })
    }

    /// `SetApplicationContext` (`ProfileAutomationState.cs:216-236`): the
    /// first enabled rule for the reported path becomes active. Returns
    /// whether the active pair changed. `reported` is the path in
    /// `NormalizeExecutablePath` form, which managed computes here and the
    /// native wire admits as the only spelling; a blank path selects nothing.
    pub fn report<'r>(
        &mut self,
        rules: impl IntoIterator<Item = Rule<'r>>,
        reported: Option<&str>,
        same_path: SamePath,
    ) -> bool {
        let reported = reported.filter(|path| !path.chars().all(char::is_whitespace));
        let next = reported.and_then(|path| {
            rules
                .into_iter()
                .find(|rule| rule.selects(path, same_path))
                .map(|rule| Active {
                    id: rule.id,
                    executable_path: path,
                })
        });
        let unchanged = match (self.active(), next) {
            (None, None) => true,
            (Some(active), Some(next)) => {
                active.id == next.id && same_path(next.executable_path, active.executable_path)
            }
            _ => false,
        };
        if unchanged {
            return false;
        }
        self.active = next.map(|next| (S::from(next.id), S::from(next.executable_path)));
        true
    }

    /// `SetApplicationContext(null)`, which managed calls when the lease
    /// expires. Returns whether a profile was active.
    pub fn clear(&mut self) -> bool {
        self.active.take().is_some()
    }

    /// The active pair while `rules` still retain it, as `UpdateProfiles`
    /// would leave it. A reader that cannot show every rule change passed
    /// through [`Self::update`] finds no profile here, rather than the
    /// overrides of another rule that now holds the active id.
    pub fn retained<'r>(
        &self,
        rules: impl IntoIterator<Item = Rule<'r>>,
        same_path: SamePath,
    ) -> Option<Active<'_>> {
        let active = self.active()?;
        rules
            .into_iter()
            .any(|rule| rule.retains(active, same_path))
            .then_some(active)
    }

    /// `UpdateProfiles` (`ProfileAutomationState.cs:130-143`): the active
    /// pair survives only while an enabled rule with the same id still holds
    /// the reported path. It never selects a rule; the next report does.
    /// Returns whether the active pair was dropped.
    pub fn update<'r>(
        &mut self,
        rules: impl IntoIterator<Item = Rule<'r>>,
        same_path: SamePath,
    ) -> bool {
        if self.active.is_none() || self.retained(rules, same_path).is_some() {
            return false;
        }
        self.clear()
    }
}

/// What the save path reads before and after it stores profiles.
#[derive(Clone, Copy, Debug)]
pub struct Intent<'a, D, B> {
    /// `ActiveApplicationProfileId`.
    pub application_profile_id: Option<&'a str>,
    /// `ResolveAutomationDesired(confirmed)`. While no manual override,
    /// recovered HyperBoost intent or temporary battery override is active,
    /// that is `profile_set::application::desired_intent`; otherwise the host
    /// composes it over that profile layer.
    pub desired: D,
    /// `EffectiveLaptopBehavior`.
    pub laptop_behavior: B,
}

/// `activeApplicationIntentChanged` in `ServiceControlRuntime.UpdateProfilesAsync`
/// (`ServiceControlRuntime.cs:2116-2119`): a save changes the active intent
/// only while an application profile was active before it, and then when the
/// active id, the resolved desired state or the effective laptop behaviour
/// differs afterwards. Managed evaluates the later terms lazily; they have no
/// side effects, so the caller may resolve both states first.
pub fn intent_changed<D: PartialEq, B: PartialEq>(
    prior: &Intent<'_, D, B>,
    next: &Intent<'_, D, B>,
) -> bool {
    prior.application_profile_id.is_some()
        && (next.application_profile_id != prior.application_profile_id
            || next.desired != prior.desired
            || next.laptop_behavior != prior.laptop_behavior)
}

#[cfg(test)]
mod tests {
    extern crate std;

    use super::*;
    use std::string::String;

    fn ascii(left: &str, right: &str) -> bool {
        left.eq_ignore_ascii_case(right)
    }

    const fn rule(id: &'static str, enabled: bool) -> Rule<'static> {
        Rule {
            id,
            executable_path: r"C:\Game.exe",
            enabled,
        }
    }

    #[test]
    fn the_first_enabled_rule_for_the_path_is_selected_and_the_reported_path_kept() {
        let mut context = ApplicationContext::<String>::default();
        // The saved validator forbids a duplicate path, so only this test
        // pins the order among rules that share one.
        assert!(context.report(
            [rule("a", false), rule("b", true), rule("c", true)],
            Some(r"c:\game.exe"),
            ascii
        ));
        assert_eq!(
            context.active(),
            Some(Active {
                id: "b",
                executable_path: r"c:\game.exe"
            })
        );
        // The same rule under another spelling is a renewal, not a change.
        assert!(!context.report([rule("b", true)], Some(r"C:\GAME.EXE"), ascii));
        assert_eq!(context.active().unwrap().executable_path, r"c:\game.exe");
    }

    #[test]
    fn a_blank_or_unmatched_report_clears_only_an_active_pair() {
        let mut context = ApplicationContext::<String>::default();
        assert!(!context.report([rule("a", true)], Some(" \t"), ascii));
        assert!(!context.report([rule("a", true)], None, ascii));
        assert!(context.report([rule("a", true)], Some(r"C:\Game.exe"), ascii));
        assert!(context.report([rule("a", true)], Some(" "), ascii));
        assert_eq!(context.active(), None);
        assert!(!context.clear());
    }

    #[test]
    fn an_update_keeps_only_an_enabled_rule_with_the_same_id_and_path() {
        let mut context = ApplicationContext::<String>::default();
        assert!(!context.update([rule("a", true)], ascii));
        context.report([rule("a", true)], Some(r"C:\Game.exe"), ascii);
        assert!(!context.update([rule("b", true), rule("a", true)], ascii));
        // A reader that skipped the update sees no rule under a reused id.
        let replaced = Rule {
            executable_path: r"C:\Other.exe",
            ..rule("a", true)
        };
        assert_eq!(context.retained([replaced], ascii), None);
        assert_eq!(
            context
                .retained([rule("a", true)], ascii)
                .map(|active| active.id),
            Some("a")
        );
        let moved = Rule {
            executable_path: r"C:\Other.exe",
            ..rule("a", true)
        };
        assert!(context.update([moved, rule("b", true)], ascii));
        // A matching rule appearing in the update does not select it.
        assert!(!context.update([rule("a", true)], ascii));
        assert_eq!(context.active(), None);
        context.report([rule("a", true)], Some(r"C:\Game.exe"), ascii);
        assert!(context.update([rule("a", false)], ascii));
    }

    #[test]
    fn overrides_come_from_the_first_enabled_rule_with_the_id_whatever_its_path() {
        let moved = Rule {
            executable_path: r"C:\Other.exe",
            ..rule("a", true)
        };
        assert!(moved.supplies("a"));
        assert!(!rule("a", false).supplies("a"));
        assert!(!rule("b", true).supplies("a"));
    }

    #[test]
    fn the_intent_changes_only_from_an_active_profile() {
        let intent = |id, desired, laptop_behavior| Intent {
            application_profile_id: id,
            desired,
            laptop_behavior,
        };
        let prior = intent(Some("a"), 1, 1);
        assert!(!intent_changed(
            &intent(None, 1, 1),
            &intent(Some("a"), 2, 2)
        ));
        assert!(intent_changed(&prior, &intent(None, 1, 1)));
        assert!(intent_changed(&prior, &intent(Some("b"), 1, 1)));
        assert!(intent_changed(&prior, &intent(Some("a"), 2, 1)));
        assert!(intent_changed(&prior, &intent(Some("a"), 1, 2)));
        assert!(!intent_changed(&prior, &intent(Some("a"), 1, 1)));
    }
}
