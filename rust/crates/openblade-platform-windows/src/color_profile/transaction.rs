//! Scripted transaction for the private display replay only. Its ownership
//! rules are stricter than managed; the production writer (`write`) mirrors
//! managed `ColorProfileMutationTransaction` instead.

use std::{
    cmp::Ordering,
    ffi::{OsStr, OsString},
};

use crate::display::{
    ActiveInternalDisplay, DisplayReadError, EndpointId, InternalDisplayTopology,
};

use super::{
    ColorProfileSnapshot, HdrState, Monitor, ProfileReadError, ProfileReads, ProfileScope,
    file_name, monitor_path, native::ordinal_ignore_case, present, profiles_match,
    resolve_monitor_key, snapshot,
};

pub(super) struct SetColorProfile<'a>(pub &'a OsStr);

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(super) enum CurrentUserChange<'a> {
    Associate(&'a OsStr),
    Remove(&'a OsStr),
    SetDefault(&'a OsStr),
    SelectScope(ProfileScope),
}

pub(super) trait ScriptedOperations: ProfileReads {
    fn monitors(&mut self, display: &ActiveInternalDisplay) -> Result<Vec<Monitor>, Failure>;
    // Association/default operations always use CurrentUser and standard ICC.
    // SelectScope toggles the current user's override on the exact monitor key.
    fn submit(
        &mut self,
        display: &ActiveInternalDisplay,
        monitor_key: &OsStr,
        change: CurrentUserChange<'_>,
    ) -> Result<(), Failure>;
}

#[derive(Debug, PartialEq, Eq)]
pub enum Failure {
    Topology(DisplayReadError),
    Target,
    Hdr(HdrState),
    ProfileUnavailable,
    Read(ProfileReadError),
    Submit(i32),
    StateChanged,
    Context,
    Readback,
    NullDefault,
    #[cfg_attr(test, allow(dead_code))] // Raised by the Session Agent's display path.
    IntentUnavailable,
}

impl From<ProfileReadError> for Failure {
    fn from(error: ProfileReadError) -> Self {
        Self::Read(error)
    }
}

#[derive(Debug, PartialEq, Eq)]
pub enum Disposition {
    NoSubmission,
    BaselineConfirmed,
    Restored,
    Unresolved(Failure),
}

#[derive(Debug, PartialEq, Eq)]
pub struct Rejected {
    pub cause: Failure,
    pub disposition: Disposition,
}

#[derive(Debug, PartialEq, Eq)]
pub enum Outcome {
    AlreadySelected(ColorProfileSnapshot),
    Applied(ColorProfileSnapshot),
}

pub(super) fn set_color_profile<Api: ScriptedOperations>(
    request: SetColorProfile<'_>,
    api: &mut Api,
    mut topology: impl FnMut() -> Result<InternalDisplayTopology, DisplayReadError>,
    mut hdr: impl FnMut(&ActiveInternalDisplay) -> HdrState,
    admitted: &dyn Fn() -> bool,
    prepare: impl FnOnce(&mut Api, &ActiveInternalDisplay, &OsStr) -> Result<(), Failure>,
) -> Result<Outcome, Rejected> {
    let before_submission = |cause| Rejected {
        cause,
        disposition: Disposition::NoSubmission,
    };
    let display = admitted_display(&mut topology, &mut hdr).map_err(before_submission)?;
    let before =
        snapshot(api, &display, HdrState::Off).map_err(|error| before_submission(error.into()))?;
    let Some(profile) = before
        .profiles
        .iter()
        .find(|profile| present(request.0) && profiles_match(&profile.id, request.0))
    else {
        return Err(before_submission(Failure::ProfileUnavailable));
    };
    let key = monitor_key(api, &display).map_err(before_submission)?;
    if profile.active {
        let fresh = admitted_display(&mut topology, &mut hdr).map_err(before_submission)?;
        if !same_display(&display, &fresh)
            || !same_name(&key, &monitor_key(api, &fresh).map_err(before_submission)?)
        {
            return Err(before_submission(Failure::Target));
        }
        let verified = snapshot(api, &fresh, HdrState::Off)
            .map_err(|error| before_submission(error.into()))?;
        let scope = api
            .scope(&fresh)
            .map_err(|error| before_submission(error.into()))?;
        if !admitted() {
            return Err(before_submission(Failure::Context));
        }
        return if scope == before.scope
            && verified.scope == scope
            && selected(&verified, &profile.id)
        {
            Ok(Outcome::AlreadySelected(verified))
        } else {
            Err(before_submission(Failure::StateChanged))
        };
    }

    let baseline = State::read(api, &display).map_err(before_submission)?;
    let refreshed =
        snapshot(api, &display, HdrState::Off).map_err(|error| before_submission(error.into()))?;
    if baseline.scope != before.scope || refreshed != before {
        return Err(before_submission(Failure::StateChanged));
    }
    let mut transaction = Transaction {
        api,
        topology: &mut topology,
        hdr: &mut hdr,
        admitted,
        display: &display,
        key,
        state: baseline.clone(),
        submitted: false,
        changed: false,
        added: false,
        ownership_lost: false,
    };
    // Publish intent only for a real transaction and after fresh admission.
    // advance rechecks the exact state and context before calling the setter.
    transaction
        .require_known_state()
        .map_err(before_submission)?;
    prepare(transaction.api, &display, &profile.id).map_err(before_submission)?;
    let result = transaction.apply(&profile.id);
    match result {
        Ok(verified) => Ok(Outcome::Applied(verified)),
        Err(cause) => {
            let disposition = if !transaction.submitted {
                Disposition::NoSubmission
            } else {
                match transaction.restore(&baseline, &profile.id) {
                    Ok(()) if transaction.changed => Disposition::Restored,
                    Ok(()) => Disposition::BaselineConfirmed,
                    Err(error) => Disposition::Unresolved(error),
                }
            };
            Err(Rejected { cause, disposition })
        }
    }
}

fn admitted_display(
    topology: &mut impl FnMut() -> Result<InternalDisplayTopology, DisplayReadError>,
    hdr: &mut impl FnMut(&ActiveInternalDisplay) -> HdrState,
) -> Result<ActiveInternalDisplay, Failure> {
    let InternalDisplayTopology::Single(display) = topology().map_err(Failure::Topology)? else {
        return Err(Failure::Target);
    };
    match hdr(&display) {
        HdrState::Off => Ok(display),
        state => Err(Failure::Hdr(state)),
    }
}

fn same_display(before: &ActiveInternalDisplay, after: &ActiveInternalDisplay) -> bool {
    let endpoints = |display: &ActiveInternalDisplay| -> (EndpointId, EndpointId) {
        (display.source, display.target)
    };
    endpoints(before) == endpoints(after)
        && same_name(&before.source_name, &after.source_name)
        && same_default(monitor_path(before), monitor_path(after))
}

pub(super) fn monitor_key(
    api: &mut impl ScriptedOperations,
    display: &ActiveInternalDisplay,
) -> Result<OsString, Failure> {
    let path = monitor_path(display)
        .filter(|path| present(path))
        .ok_or(Failure::Target)?;
    let monitors = api.monitors(display)?;
    resolve_monitor_key(path, monitors).map_err(|_| Failure::Target)
}

fn selected(snapshot: &ColorProfileSnapshot, profile: &OsStr) -> bool {
    snapshot
        .selected
        .as_deref()
        .is_some_and(|id| profiles_match(id, profile))
}

fn same_name(left: &OsStr, right: &OsStr) -> bool {
    ordinal_ignore_case(left, right) == Ordering::Equal
}

// Transaction state retains full returned paths. Filename equivalence identifies
// the requested profile but cannot prove an unrelated full path was preserved.
pub(super) fn same_effect_name(expected: &OsStr, observed: &OsStr) -> bool {
    if same_name(expected, observed) {
        return true;
    }
    let expected_leaf = file_name(expected);
    let observed_leaf = file_name(observed);
    // WCS can report a bare name in place of a path. Two different full
    // paths do not establish ownership merely because their names match.
    (expected == expected_leaf || observed == observed_leaf)
        && same_name(&expected_leaf, &observed_leaf)
}

fn same_default(left: Option<&OsStr>, right: Option<&OsStr>) -> bool {
    match (left, right) {
        (Some(left), Some(right)) => same_name(left, right),
        (None, None) => true,
        _ => false,
    }
}

#[derive(Clone)]
struct State {
    scope: ProfileScope,
    default: Option<OsString>,
    associations: Vec<OsString>,
}

impl State {
    fn read(api: &mut impl ProfileReads, display: &ActiveInternalDisplay) -> Result<Self, Failure> {
        let scope = api.scope(display)?;
        let mut associations = api.associations(display, ProfileScope::CurrentUser)?;
        associations.sort_by(|a, b| ordinal_ignore_case(a, b));
        let default = api.default_profile(display, ProfileScope::CurrentUser)?;
        if api.scope(display)? != scope {
            return Err(Failure::StateChanged);
        }
        Ok(Self {
            scope,
            default,
            associations,
        })
    }

    fn matches(&self, other: &Self) -> bool {
        self.scope == other.scope
            && self.same_default(other)
            && same_associations(self.associations.iter(), other.associations.iter())
    }

    fn same_default(&self, other: &Self) -> bool {
        same_default(self.default.as_deref(), other.default.as_deref())
    }

    fn default_is(&self, profile: &OsStr) -> bool {
        self.default
            .as_deref()
            .is_some_and(|default| same_effect_name(profile, default))
    }

    fn contains(&self, profile: &OsStr) -> bool {
        self.associations
            .iter()
            .any(|id| profiles_match(id, profile))
    }

    fn confirms(&self, change: CurrentUserChange<'_>, after: &Self) -> bool {
        let same_list = || same_associations(self.associations.iter(), after.associations.iter());
        let same_scope = self.scope == after.scope;
        match change {
            CurrentUserChange::Associate(profile) => {
                let added = after
                    .associations
                    .iter()
                    .filter(|id| same_effect_name(profile, id))
                    .count();
                // Windows may expose the first association only through the default
                // and may activate the per-user scope as part of the add.
                (same_scope || after.scope == ProfileScope::CurrentUser)
                    && (self.same_default(after) || after.default_is(profile))
                    && (added == 1 || (added == 0 && after.default_is(profile)))
                    && same_associations(
                        self.associations.iter(),
                        after
                            .associations
                            .iter()
                            .filter(|id| !same_effect_name(profile, id)),
                    )
            }
            CurrentUserChange::Remove(profile) => {
                same_scope
                    && !after.contains(profile)
                    && (if self.default_is(profile) {
                        after.default.is_none()
                    } else {
                        self.same_default(after)
                    })
                    && same_associations(
                        self.associations
                            .iter()
                            .filter(|id| !same_effect_name(profile, id)),
                        after.associations.iter(),
                    )
            }
            CurrentUserChange::SetDefault(profile) => {
                same_scope && same_list() && after.default_is(profile)
            }
            CurrentUserChange::SelectScope(scope) => {
                after.scope == scope && same_list() && self.same_default(after)
            }
        }
    }
}

fn same_associations<'a>(
    mut left: impl Iterator<Item = &'a OsString>,
    mut right: impl Iterator<Item = &'a OsString>,
) -> bool {
    loop {
        match (left.next(), right.next()) {
            (None, None) => return true,
            (Some(a), Some(b)) if same_name(a, b) => {}
            _ => return false,
        }
    }
}

struct Transaction<'a, Api, Topology, Hdr> {
    api: &'a mut Api,
    topology: &'a mut Topology,
    hdr: &'a mut Hdr,
    admitted: &'a dyn Fn() -> bool,
    display: &'a ActiveInternalDisplay,
    key: OsString,
    state: State,
    submitted: bool,
    changed: bool,
    added: bool,
    ownership_lost: bool,
}

impl<Api, Topology, Hdr> Transaction<'_, Api, Topology, Hdr>
where
    Api: ScriptedOperations,
    Topology: FnMut() -> Result<InternalDisplayTopology, DisplayReadError>,
    Hdr: FnMut(&ActiveInternalDisplay) -> HdrState,
{
    fn observe(&mut self) -> Result<State, Failure> {
        let fresh = admitted_display(self.topology, self.hdr)?;
        if !same_display(self.display, &fresh)
            || !same_name(&self.key, &monitor_key(self.api, &fresh)?)
        {
            return Err(Failure::Target);
        }
        let observed = State::read(self.api, &fresh)?;
        if !(self.admitted)() {
            return Err(Failure::Context);
        }
        Ok(observed)
    }

    fn require_known_state(&mut self) -> Result<(), Failure> {
        let after = self.observe_for_write()?;
        if self.state.matches(&after) {
            Ok(())
        } else {
            self.ownership_lost = true;
            Err(Failure::StateChanged)
        }
    }

    fn observe_for_write(&mut self) -> Result<State, Failure> {
        let observed = self.observe();
        self.ownership_lost |= matches!(
            observed,
            Err(Failure::StateChanged | Failure::Target | Failure::Hdr(_) | Failure::Context)
        );
        observed
    }

    fn advance(&mut self, change: CurrentUserChange<'_>) -> Result<(), Failure> {
        self.require_known_state()?;
        self.submitted = true;
        self.api.submit(self.display, &self.key, change)?;
        let after = self.observe_for_write()?;
        if !self.state.confirms(change, &after) {
            self.ownership_lost |= !self.state.matches(&after);
            return Err(Failure::Readback);
        }
        self.changed |= !self.state.matches(&after);
        self.state = after;
        Ok(())
    }

    fn apply(&mut self, profile: &OsStr) -> Result<ColorProfileSnapshot, Failure> {
        if !self.state.contains(profile) && !self.state.default_is(profile) {
            self.advance(CurrentUserChange::Associate(profile))?;
            self.added = true;
        }
        if self.state.scope != ProfileScope::CurrentUser {
            self.advance(CurrentUserChange::SelectScope(ProfileScope::CurrentUser))?;
        }
        self.advance(CurrentUserChange::SetDefault(profile))?;
        let verified = snapshot(self.api, self.display, HdrState::Off)?;
        self.require_known_state()?;
        if verified.scope != ProfileScope::CurrentUser || !selected(&verified, profile) {
            return Err(Failure::Readback);
        }
        Ok(verified)
    }

    fn restore(&mut self, baseline: &State, requested: &OsStr) -> Result<(), Failure> {
        if self.ownership_lost {
            return Err(Failure::StateChanged);
        }
        // Failed submissions never update our last verified state. If a failed
        // call took effect or another actor intervened, do not claim ownership.
        self.require_known_state()?;
        if !self.state.same_default(baseline) {
            if let Some(default) = baseline.default.as_deref() {
                self.advance(CurrentUserChange::SetDefault(default))?;
                if !self.state.same_default(baseline) {
                    return Err(Failure::Readback);
                }
            } else if !self.added || !self.state.default_is(requested) {
                // There is no standalone clear-default operation. Do not
                // remove an association that existed before this transaction.
                return Err(Failure::NullDefault);
            }
        }
        if self.added && (self.state.contains(requested) || self.state.default_is(requested)) {
            self.advance(CurrentUserChange::Remove(requested))?;
        }
        if self.state.scope != baseline.scope {
            self.advance(CurrentUserChange::SelectScope(baseline.scope))?;
        }
        if baseline.matches(&self.observe()?) {
            Ok(())
        } else {
            Err(Failure::Readback)
        }
    }
}
