//! Selected manual `applyProfile` composed from the reviewed display writers.
//!
//! Order mirrors the managed dispatcher and `DisplayProfileApplier
//! .ApplyProfileAsync`/`ApplyProfileCoreAsync`. The native write gate refuses
//! a profile that touches any unresolved field, then one durable checkpoint
//! covers every configured field (`BeginManualOperation`) before any device
//! interaction. An unconfigured profile is answered by one query, without
//! power admission. Otherwise the trusted power source must match the
//! request, and the fields are taken in the managed order with the managed
//! conditions: refresh rate (the current key differs), HDR (supported and
//! different), color profile (SDR, and the active ID differs ignoring case)
//! and brightness (the nearest supported level differs). The selecting query
//! decides the first field; each later field is decided from the reply query
//! of the step before it, and is never decided from an unavailable snapshot.
//! Each step is the reviewed single-field attempt (`refresh_write`,
//! `hdr_write`, `color_write`, `brightness_write`), with its own fence
//! (topology, Origin, session, power generation and cancellation) immediately
//! before its one possible submission, followed by one reply query; a
//! successful step whose power context changed is stale. The first step that
//! does not succeed ends the operation, so no later field is written after a
//! refused or ambiguous step and nothing is replayed. Finally the reply must
//! show every requested setting (managed `ProfileMatches`). Every admitted
//! operation records its certainty before returning, even when the client,
//! deadline or Stop has already abandoned the reply.
//!
//! Certainty is recorded per field. The durable managed record keeps the
//! managed semantics: an Indeterminate profile leaves every configured field
//! uncertain until an explicit read confirms them all. The native write gate
//! gains exactly the fields whose submission may be unresolved: a step that
//! may have submitted without a confirmed result, a submitted step whose
//! power context went stale, and a confirmed step that the operation's final
//! reply does not show (an unavailable final reply shows none). A field
//! that submitted nothing is never gated.
//! A fatal reply query or brightness worker failure stops the feature after
//! the operation is recorded with the status it had reached: Succeeded for
//! an unconfigured profile, the Blocked refusal for a power source that does
//! not match, and Indeterminate once the selecting query is reached. Across
//! an Agent restart the gate reloads the durable uncertain fields, which for
//! an Indeterminate profile are all of its fields.
//!
//! Deliberate deviations from managed. None writes a field that managed would
//! not write on the same true display state. Deviations 1, 3, 5 and 6 report
//! more certainty than managed (Failed or Blocked where managed reports
//! Indeterminate), each only when nothing was submitted:
//! 1. Managed counts every successful setter, a controller no-op included, as
//!    a possible submission of the profile; native counts only steps that
//!    submitted or may have, so a later failure before any submission is
//!    Failed or Blocked rather than Indeterminate.
//! 2. Managed continues after a confirmed step that its reply does not show
//!    and reports the mismatch at the end; native stops at that step
//!    (Indeterminate), before writing any later field.
//! 3. A writer's fence refusal (power changed before submission, panel
//!    changed, cancellation) is Blocked while nothing was submitted; managed's
//!    stale check before a setter is reported Indeterminate by its dispatcher.
//! 4. The native gate refuses the whole profile while any configured field is
//!    unresolved, like each single-field write.
//! 5. A selecting read interrupted by cancellation is an unavailable snapshot,
//!    so the profile fails before any write; managed reports a canceled
//!    operation Indeterminate.
//! 6. A fatal query of an unconfigured or power-refused profile is recorded
//!    Succeeded or Blocked; managed records every dispatcher exception
//!    Indeterminate.
//! 7. State source: managed decides each later field from the setter's own
//!    result snapshot (`result.State ?? QueryAsync`); native decides it from
//!    its fresh reply query, which is also the snapshot that confirms the
//!    step. When the two differ, native follows the managed rule on the
//!    current display state, and a configured later field facing an
//!    unavailable reply fails (managed `Failure(state.Message ?? ...)`,
//!    Indeterminate after a submission) instead of being decided from it.
//! 8. Each writer's own deviations apply to its step.
use super::{
    brightness_write,
    color_write::{self, SetColorProfile},
    hdr_write::{self, SetHdr},
    manual_write::{Attempt, ManualRequest, ManualWriteOutcome, admit_power},
    observation::{Observed, SharedBrightness, Snapshot},
    recovery::{CheckpointFile, Fields, Recovery, Status},
    refresh_write::{self, SetRefreshRate},
    write::{STALE_AFTER, UNREFRESHED, WindowsHost, begin, lock, refreshed},
};
use crate::session_native::power_admission::PowerContext;
use openblade_platform_windows::color_profile::profile_ids_equal;
use std::{
    borrow::Cow,
    ffi::OsStr,
    io,
    sync::{Mutex, atomic::AtomicBool},
};

/// Native-only write gate (no managed equivalent).
pub(super) const PAUSED: &str =
    "Display profile changes are paused until a display read confirms the last change.";
/// Managed `ApplyProfileCoreAsync`: an unavailable snapshot without a message.
pub(super) const UNAVAILABLE: &str = "Display settings are unavailable.";
/// Managed `ApplyProfileCoreAsync`: the final reply does not match the profile.
pub(super) const UNCONFIRMED: &str = "Windows did not confirm every requested display setting.";

/// Managed `DisplayProfileSettings`: each `None` member is not configured.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub(super) struct Settings {
    pub(super) refresh_rate_key: Option<String>,
    pub(super) hdr_enabled: Option<bool>,
    pub(super) color_profile_key: Option<String>,
    pub(super) brightness_percent: Option<u8>,
}

impl Settings {
    /// Managed `DisplayOwnedSettings.From`.
    pub(super) fn fields(&self) -> Fields {
        Fields {
            refresh: self.refresh_rate_key.is_some(),
            hdr: self.hdr_enabled.is_some(),
            color: self.color_profile_key.is_some(),
            brightness: self.brightness_percent.is_some(),
        }
    }

    /// Managed `DisplayProfileSettings.IsConfigured`.
    pub(super) fn configured(&self) -> bool {
        self.fields() != Fields::default()
    }

    /// Managed `ProfileMatches`.
    fn shown_by(&self, state: &Observed<'_>) -> bool {
        state.available
            && self
                .refresh_rate_key
                .as_deref()
                .is_none_or(|key| state.refresh_key == Some(key))
            && self
                .hdr_enabled
                .is_none_or(|enabled| enabled == state.hdr_enabled)
            && self
                .color_profile_key
                .as_deref()
                .is_none_or(|key| state.hdr_enabled || same_id(state.color_profile, key))
            && self.brightness_percent.is_none_or(|percent| {
                nearest(percent, state.levels).is_some_and(|level| state.brightness == Some(level))
            })
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub(super) struct ApplyProfile {
    pub(super) expected_power_source: u8,
    pub(super) settings: Settings,
}

/// Managed `DisplayBrightnessLevels.SelectNearest`: the closest level, ties
/// to the lower one, or `None` without levels.
fn nearest(percent: u8, levels: &[u8]) -> Option<u8> {
    levels
        .iter()
        .copied()
        .filter(|level| *level <= 100)
        .min_by_key(|level| (level.abs_diff(percent), *level))
}

/// Managed `string.Equals(active, key, OrdinalIgnoreCase)`; a missing active
/// ID never equals a key.
fn same_id(active: Option<&str>, key: &str) -> bool {
    active.is_some_and(|active| profile_ids_equal(OsStr::new(active), OsStr::new(key)))
}

/// Injected OS boundary. Production reads the trusted power context and the
/// full display snapshot, and runs each reviewed writer's admitted attempt
/// against its own real native and the host fence.
pub(super) trait ProfileHost {
    fn power(&self) -> Option<PowerContext>;
    fn query(
        &mut self,
        brightness: &SharedBrightness,
        canceled: &AtomicBool,
    ) -> io::Result<Snapshot>;
    fn refresh(
        &mut self,
        request: &SetRefreshRate,
        admitted: PowerContext,
        canceled: &AtomicBool,
    ) -> Attempt;
    fn hdr(&mut self, request: &SetHdr, admitted: PowerContext, canceled: &AtomicBool) -> Attempt;
    fn color(
        &mut self,
        request: &SetColorProfile,
        admitted: PowerContext,
        canceled: &AtomicBool,
    ) -> Attempt;
    fn brightness(
        &mut self,
        brightness: &SharedBrightness,
        percent: u8,
        admitted: PowerContext,
        canceled: &AtomicBool,
    ) -> brightness_write::Attempt;
}

impl ProfileHost for WindowsHost {
    fn power(&self) -> Option<PowerContext> {
        WindowsHost::power(self)
    }

    fn query(
        &mut self,
        brightness: &SharedBrightness,
        canceled: &AtomicBool,
    ) -> io::Result<Snapshot> {
        WindowsHost::query(self, brightness, canceled)
    }

    fn refresh(
        &mut self,
        request: &SetRefreshRate,
        admitted: PowerContext,
        canceled: &AtomicBool,
    ) -> Attempt {
        request.attempt(&mut self.refresh_writer(), admitted, canceled)
    }

    fn hdr(&mut self, request: &SetHdr, admitted: PowerContext, canceled: &AtomicBool) -> Attempt {
        request.attempt(&mut self.hdr_writer(), admitted, canceled)
    }

    fn color(
        &mut self,
        request: &SetColorProfile,
        admitted: PowerContext,
        canceled: &AtomicBool,
    ) -> Attempt {
        request.attempt(&mut self.color_writer(), admitted, canceled)
    }

    fn brightness(
        &mut self,
        brightness: &SharedBrightness,
        percent: u8,
        admitted: PowerContext,
        canceled: &AtomicBool,
    ) -> brightness_write::Attempt {
        brightness_write::admitted(self, brightness, percent, admitted, canceled)
    }
}

/// The profile's fields in the managed `ApplyProfileCoreAsync` order.
#[derive(Clone, Copy)]
enum Stage {
    Refresh,
    Hdr,
    Color,
    Brightness,
}

impl Stage {
    const ORDER: [Self; 4] = [Self::Refresh, Self::Hdr, Self::Color, Self::Brightness];

    fn configured(self, settings: &Settings) -> bool {
        match self {
            Self::Refresh => settings.refresh_rate_key.is_some(),
            Self::Hdr => settings.hdr_enabled.is_some(),
            Self::Color => settings.color_profile_key.is_some(),
            Self::Brightness => settings.brightness_percent.is_some(),
        }
    }

    /// Managed `ApplyProfileCoreAsync`'s condition for this field against an
    /// available snapshot: the step to write, `None` when the field is not
    /// configured or the snapshot already satisfies it, or the managed
    /// failure when brightness is configured without a supported level.
    fn select(
        self,
        request: &ApplyProfile,
        state: &Observed<'_>,
    ) -> Result<Option<Step>, &'static str> {
        let settings = &request.settings;
        let expected_power_source = request.expected_power_source;
        Ok(match self {
            Self::Refresh => settings
                .refresh_rate_key
                .as_ref()
                .filter(|key| state.refresh_key != Some(key.as_str()))
                .map(|key| {
                    Step::Refresh(SetRefreshRate {
                        expected_power_source,
                        key: key.clone(),
                    })
                }),
            Self::Hdr => settings
                .hdr_enabled
                .filter(|enabled| state.hdr_supported && state.hdr_enabled != *enabled)
                .map(|enabled| {
                    Step::Hdr(SetHdr {
                        expected_power_source,
                        enabled,
                    })
                }),
            Self::Color => settings
                .color_profile_key
                .as_ref()
                .filter(|key| !state.hdr_enabled && !same_id(state.color_profile, key))
                .map(|key| {
                    Step::Color(SetColorProfile {
                        expected_power_source,
                        key: key.clone(),
                    })
                }),
            Self::Brightness => match settings.brightness_percent {
                None => None,
                Some(percent) => {
                    let level =
                        nearest(percent, state.levels).ok_or(brightness_write::UNAVAILABLE)?;
                    (state.brightness != Some(level)).then_some(Step::Brightness(level))
                }
            },
        })
    }
}

/// One selected write of the profile. A confirmed brightness step carries
/// the level the worker read back.
enum Step {
    Refresh(SetRefreshRate),
    Hdr(SetHdr),
    Color(SetColorProfile),
    Brightness(u8),
}

impl Step {
    fn field(&self) -> Fields {
        match self {
            Self::Refresh(_) => Fields::REFRESH,
            Self::Hdr(_) => Fields::HDR,
            Self::Color(_) => Fields::COLOR,
            Self::Brightness(_) => Fields::BRIGHTNESS,
        }
    }

    fn unconfirmed(&self) -> &'static str {
        match self {
            Self::Refresh(_) => refresh_write::UNCONFIRMED,
            Self::Hdr(_) => hdr_write::UNCONFIRMED,
            Self::Color(_) => color_write::UNCONFIRMED,
            Self::Brightness(_) => brightness_write::UNCONFIRMED,
        }
    }

    fn shown(&self, state: &Snapshot) -> bool {
        match self {
            Self::Refresh(request) => request.shown(state),
            Self::Hdr(request) => request.shown(state),
            Self::Color(request) => request.shown(state),
            Self::Brightness(level) => state.brightness() == Some(*level),
        }
    }

    /// The step's admitted attempt, or a fatal brightness worker error and
    /// whether it may have submitted.
    fn attempt(
        &mut self,
        host: &mut impl ProfileHost,
        brightness: &SharedBrightness,
        admitted: PowerContext,
        canceled: &AtomicBool,
    ) -> Result<Attempt, (bool, io::Error)> {
        Ok(match self {
            Self::Refresh(request) => host.refresh(request, admitted, canceled),
            Self::Hdr(request) => host.hdr(request, admitted, canceled),
            Self::Color(request) => host.color(request, admitted, canceled),
            Self::Brightness(level) => {
                match host.brightness(brightness, *level, admitted, canceled)? {
                    (Status::Succeeded, _, None) => Attempt::Unchanged,
                    (Status::Succeeded, _, Some(confirmed)) => {
                        *level = confirmed;
                        Attempt::Confirmed
                    }
                    (status, error, _) => Attempt::Done(
                        status,
                        Cow::Borrowed(error.unwrap_or(brightness_write::UNAVAILABLE)),
                    ),
                }
            }
        })
    }
}

/// Whether an attempt submitted, or may have.
fn submitted(attempt: &Attempt) -> bool {
    matches!(
        attempt,
        Attempt::Confirmed | Attempt::Done(Status::Indeterminate, _)
    )
}

/// The certainty of the operation so far.
#[derive(Default)]
struct Progress {
    /// Any step submitted or may have (managed `submitted`, without no-ops).
    submitted: bool,
    /// Submitted steps confirmed while the power context held.
    confirmed: Vec<Step>,
    /// Fields of submitted steps without a confirmed, current result.
    unresolved: Fields,
}

impl Progress {
    /// Record one step that submitted or may have.
    fn record(&mut self, step: Step, status: Status) {
        self.submitted = true;
        if status == Status::Succeeded {
            self.confirmed.push(step);
        } else {
            self.unresolved = self.unresolved.include(step.field());
        }
    }

    /// The operation's result. A failure after any submission is
    /// Indeterminate (managed `Failure(error, state, submitted)`).
    fn end(
        &self,
        status: Status,
        error: Option<Cow<'static, str>>,
        state: Snapshot,
        fatal: Option<io::Error>,
    ) -> Ending {
        let status = if self.submitted && status != Status::Succeeded {
            Status::Indeterminate
        } else {
            status
        };
        Ending {
            status,
            error,
            state,
            fatal,
        }
    }

    /// Every field whose submission may be unresolved once `state` is the
    /// final reply: a confirmed write it does not show is unresolved too.
    fn unresolved(&self, state: &Snapshot) -> Fields {
        self.confirmed
            .iter()
            .filter(|step| !step.shown(state))
            .fold(self.unresolved, |fields, step| fields.include(step.field()))
    }
}

/// The operation's result; `fatal` is returned after it is recorded.
struct Ending {
    status: Status,
    error: Option<Cow<'static, str>>,
    state: Snapshot,
    fatal: Option<io::Error>,
}

/// The complete admitted profile operation.
pub(super) fn run<F: CheckpointFile, H: ProfileHost>(
    host: &mut H,
    brightness: &SharedBrightness,
    recovery: &Mutex<Recovery<F>>,
    request: &ApplyProfile,
    canceled: &AtomicBool,
) -> io::Result<ManualWriteOutcome> {
    let power = host.power();
    if let Some(refused) = begin(recovery, request.settings.fields(), PAUSED, power, || {
        host.query(brightness, canceled)
    })? {
        return Ok(refused.into());
    }

    // Admitted manual operation: whatever happens next, record its certainty.
    let mut progress = Progress::default();
    let ending = apply(host, brightness, request, power, canceled, &mut progress);
    let unresolved = progress.unresolved(&ending.state);
    lock(recovery)?.complete_with(ending.status, &ending.state, unresolved);
    match ending.fatal {
        Some(error) => Err(error),
        None => Ok(ManualWriteOutcome {
            status: ending.status,
            error: ending.error,
            state: ending.state,
        }),
    }
}

/// One automatic transition's profile operation, which display automation
/// records itself (managed `ApplyProfileForTransitionAsync`).
#[cfg(feature = "agent-display-automation-v1")]
pub(super) struct Applied {
    pub(super) outcome: ManualWriteOutcome,
    /// The fields whose submission may be unresolved, as `run` computes them
    /// for the native write gate.
    pub(super) unresolved: Fields,
    /// A fatal reply query or brightness worker failure, returned after the
    /// transition is recorded.
    pub(super) fatal: Option<io::Error>,
}

/// Managed `ApplyProfileForTransitionAsync`: the transition's own admission
/// must still be the trusted context, unless the profile is unconfigured
/// (`ValidateExpectedAsync`, refused with one query), and then the same
/// composition as a manual profile, without the manual checkpoint or the
/// native write gate, which display automation applies itself.
#[cfg(feature = "agent-display-automation-v1")]
pub(super) fn transition<H: ProfileHost>(
    host: &mut H,
    brightness: &SharedBrightness,
    request: &ApplyProfile,
    admitted: PowerContext,
    canceled: &AtomicBool,
) -> Applied {
    let mut progress = Progress::default();
    let current = host.power();
    let ending = if request.settings.configured() && current != Some(admitted) {
        let (state, fatal) = read(host, brightness, canceled);
        let error = if current.is_none() {
            super::write::UNCONFIRMED_POWER
        } else {
            super::write::CHANGED_POWER
        };
        progress.end(Status::Blocked, Some(Cow::Borrowed(error)), state, fatal)
    } else {
        apply(
            host,
            brightness,
            request,
            Some(admitted),
            canceled,
            &mut progress,
        )
    };
    Applied {
        unresolved: progress.unresolved(&ending.state),
        outcome: ManualWriteOutcome {
            status: ending.status,
            error: ending.error,
            state: ending.state,
        },
        fatal: ending.fatal,
    }
}

/// One query: an interrupted read is an unavailable snapshot, and any other
/// failure is fatal after the operation is recorded.
fn read(
    host: &mut impl ProfileHost,
    brightness: &SharedBrightness,
    canceled: &AtomicBool,
) -> (Snapshot, Option<io::Error>) {
    match refreshed(host.query(brightness, canceled)) {
        Ok(state) => (state, None),
        Err(error) => (Snapshot::unavailable(UNREFRESHED), Some(error)),
    }
}

fn apply(
    host: &mut impl ProfileHost,
    brightness: &SharedBrightness,
    request: &ApplyProfile,
    power: Option<PowerContext>,
    canceled: &AtomicBool,
    progress: &mut Progress,
) -> Ending {
    // Managed applies no power check to an unconfigured profile.
    if !request.settings.configured() {
        let (state, fatal) = read(host, brightness, canceled);
        return progress.end(Status::Succeeded, None, state, fatal);
    }
    let admitted = match admit_power(power, request.expected_power_source) {
        Ok(admitted) => admitted,
        Err(refused) => {
            let (state, fatal) = read(host, brightness, canceled);
            let (status, error) = refused.resolve(false, UNCONFIRMED);
            return progress.end(status, error, state, fatal);
        }
    };
    let (mut state, fatal) = read(host, brightness, canceled);
    if fatal.is_some() {
        return progress.end(Status::Indeterminate, None, state, fatal);
    }
    for stage in Stage::ORDER {
        if !stage.configured(&request.settings) {
            continue;
        }
        // Managed checks availability once, on the selecting query, and then
        // decides from each setter's own snapshot. Native decides from each
        // reply query, so it repeats the check before every decision.
        let observed = state.observed();
        if !observed.available {
            let error = Cow::Owned(observed.message.unwrap_or(UNAVAILABLE).to_owned());
            return progress.end(Status::Failed, Some(error), state, None);
        }
        let mut step = match stage.select(request, &observed) {
            Ok(Some(step)) => step,
            Ok(None) => continue,
            Err(error) => {
                return progress.end(Status::Failed, Some(Cow::Borrowed(error)), state, None);
            }
        };
        let attempt = match step.attempt(host, brightness, admitted, canceled) {
            Ok(attempt) => attempt,
            Err((possible, fatal)) => {
                // The brightness worker failed: record it without a reply query.
                if possible {
                    progress.record(step, Status::Indeterminate);
                }
                let state = Snapshot::unavailable(UNREFRESHED);
                return progress.end(Status::Indeterminate, None, state, Some(fatal));
            }
        };
        let possible = submitted(&attempt);
        let (reply, fatal) = read(host, brightness, canceled);
        let (status, error) = attempt.resolve(step.shown(&reply), step.unconfirmed());
        // Managed EnsureCurrent after a successful setter, including a no-op.
        let (status, error) = if status == Status::Succeeded && host.power() != Some(admitted) {
            (Status::Indeterminate, Some(Cow::Borrowed(STALE_AFTER)))
        } else {
            (status, error)
        };
        state = reply;
        if possible {
            progress.record(step, status);
        }
        if fatal.is_some() {
            return progress.end(Status::Indeterminate, None, state, fatal);
        }
        if status != Status::Succeeded {
            return progress.end(status, error, state, None);
        }
    }
    if request.settings.shown_by(&state.observed()) {
        progress.end(Status::Succeeded, None, state, None)
    } else {
        let error = Cow::Borrowed(UNCONFIRMED);
        progress.end(Status::Failed, Some(error), state, None)
    }
}

#[cfg(test)]
#[path = "profile_apply_tests.rs"]
mod tests;
