//! Manual SDR color profile for the single active internal display.
//!
//! Mirrors managed `WindowsDisplaySettingsController.PrepareColorProfile`
//! and `ColorProfileMutationTransaction`. One fresh observation selects the
//! panel and its listed profiles; HDR on, an unlisted request and an already
//! active profile submit nothing. The baseline is the selected scope with the
//! current-user default and associations, and the WCS device key is that of
//! the panel's one active monitor. The transaction associates the requested
//! profile unless it is already associated or the default, selects the
//! per-user scope unless it is selected, makes the profile the default,
//! checks each step by readback and confirms the selection with an
//! independent observation. Any failure after the first setter runs managed
//! `Restore` exactly, unless the observation did not re-identify the panel
//! (see the deviations below): reconcile first and stop if the associations
//! changed unexpectedly; otherwise restore the default, remove an association
//! this transaction added, restore the scope and confirm the baseline. No
//! failed call is retried; like managed `Restore`, restoration calls the
//! default and scope setters again, with their baseline values.
//!
//! The native layer adds the host fence immediately before the first setter.
//! From that setter on Windows may have changed state, so every later failure
//! is uncertain whatever restoration achieves, and caller cancellation no
//! longer applies (managed `RunMutationAsync` keeps ownership through
//! restoration). A failure before it (a read, the fence or cancellation)
//! submitted nothing and runs no restoration. The fence also requires the
//! fresh panel to be in confirmed SDR.
//!
//! Deliberate deviations from managed:
//! - A read failure inside `Execute` before its first setter is a refusal.
//!   Managed reports it as possibly submitted and its `Restore` may then
//!   write, although nothing was changed.
//! - HDR on or unreadable at the fence is a refusal. Managed checks HDR only
//!   in its observation.
//! - Once every setter succeeded, restoration runs only after a readback that
//!   re-identified the same single panel with a readable advanced-color
//!   state. Another panel, no or several internal panels, a failed topology
//!   read or an unreadable advanced-color state report
//!   [`ColorFailure::Unverified`] without restoration writes: restoring would
//!   write to a stale target or under an unknown state.
//!   Managed reads a failed advanced-color read as SDR, so it may confirm
//!   there, and restores after every `verifyApplied` failure.

use std::ffi::{OsStr, OsString};

use crate::display::{
    ActiveInternalDisplay, DisplayReadError, FenceRefusal, InternalDisplayTopology, PanelIdentity,
    SubmissionFence, admit_submission,
};

use super::{
    ColorProfileSnapshot, HdrState, Monitor, MonitorKeyError, ProfileReadError, ProfileReads,
    ProfileScope, monitor_path, present, profiles_match, resolve_monitor_key, snapshot,
};

/// Current-user Windows boundary for one color-profile request: the WCS
/// reads, a fresh CCD read, the monitor enumeration and the four setters.
pub trait ColorProfileNative: ProfileReads {
    /// A fresh CCD read, including the target's advanced-color state.
    fn topology(&mut self) -> Result<InternalDisplayTopology, DisplayReadError>;

    /// `EnumDisplayDevicesW(EDD_GET_DEVICE_INTERFACE_NAME)` for the panel's
    /// GDI source.
    fn monitors(&mut self, display: &ActiveInternalDisplay) -> Vec<Monitor>;

    /// `ColorProfileAddDisplayAssociation` (current user, neither default nor
    /// advanced color). Returns the HRESULT.
    fn add_association(&mut self, display: &ActiveInternalDisplay, profile: &OsStr) -> i32;

    /// `ColorProfileRemoveDisplayAssociation` (current user). Returns the HRESULT.
    fn remove_association(&mut self, display: &ActiveInternalDisplay, profile: &OsStr) -> i32;

    /// `ColorProfileSetDisplayDefaultAssociation` (current user, standard
    /// ICC). Returns the HRESULT.
    fn set_default(&mut self, display: &ActiveInternalDisplay, profile: &OsStr) -> i32;

    /// `WcsSetUsePerUserProfiles(CLASS_MONITOR)` on the monitor device key.
    /// A failure carries `GetLastError`.
    fn use_per_user_profiles(&mut self, monitor_key: &OsStr, enabled: bool) -> Result<(), u32>;
}

#[derive(Debug, PartialEq, Eq)]
pub enum ColorOutcome {
    /// The fresh observation already showed the requested profile active;
    /// nothing was submitted.
    Unchanged,
    /// Every step and the independent observation confirmed the request.
    Confirmed,
}

#[derive(Debug, PartialEq, Eq)]
pub enum ColorFailure {
    /// Nothing was submitted.
    Refused(ColorRefusal),
    /// A setter was called, so Windows may have changed state. `fault` is the
    /// managed mutation failure and `restoration` the managed `Restore` result.
    Uncertain {
        fault: Fault,
        restoration: Result<(), Fault>,
    },
    /// Every setter succeeded and was read back, but the independent
    /// observation did not re-identify the same single panel under a known
    /// advanced-color state, so the managed `Restore` was not attempted: it
    /// would write to a stale target or under an unknown state. Deliberate
    /// deviation: managed restores after every `verifyApplied` failure.
    Unverified(Unverified),
}

#[derive(Debug, PartialEq, Eq)]
pub enum ColorRefusal {
    /// The fresh topology read failed.
    Topology(DisplayReadError),
    NoInternalDisplay,
    MultipleInternalDisplays,
    /// A WCS read before the first setter failed.
    Read(ProfileReadError),
    /// Managed `ColorProfilesEnabled` is false, at the observation or at the
    /// fence.
    HdrEnabled,
    /// The advanced-color read failed at the observation or at the fence, so
    /// SDR is unconfirmed.
    HdrUnknown,
    /// The request matches no listed profile.
    ProfileUnavailable,
    Monitor(MonitorKeyError),
    Cancelled,
    /// The fenced topology no longer identifies the observed panel.
    PanelChanged,
    /// The host's lifetime, session or power admission no longer holds.
    AdmissionDenied,
}

impl From<FenceRefusal> for ColorRefusal {
    fn from(refusal: FenceRefusal) -> Self {
        match refusal {
            FenceRefusal::Cancelled => Self::Cancelled,
            FenceRefusal::PanelChanged => Self::PanelChanged,
            FenceRefusal::AdmissionDenied => Self::AdmissionDenied,
        }
    }
}

/// The managed exception that ended `Execute` or `Restore`.
#[derive(Debug, PartialEq, Eq)]
pub enum Fault {
    /// A WCS read failed.
    Read(ProfileReadError),
    /// A setter returned this HRESULT (or, for the scope, Win32 error).
    AssociateRejected(i32),
    RemoveRejected(i32),
    DefaultRejected(i32),
    ScopeRejected(u32),
    /// The readback after a setter did not show its effect.
    AssociationUnconfirmed,
    RemovalUnconfirmed,
    DefaultUnconfirmed,
    ScopeUnconfirmed,
    /// The independent observation did not show the requested profile active.
    Readback(Readback),
    /// Restoration only: the current-user associations differ from the
    /// baseline other than by the association this transaction added.
    AssociationsChanged,
    /// Restoration only: no setter clears a default that a pre-existing
    /// association provides.
    NullDefault,
    /// Restoration only: the final readback does not show the baseline.
    RestorationUnconfirmed,
}

/// Why the independent observation (managed `verifyApplied`) did not confirm
/// although it re-identified the same single panel under a known
/// advanced-color state. Only these failures run managed `Restore`.
#[derive(Debug, PartialEq, Eq)]
pub enum Readback {
    Read(ProfileReadError),
    /// HDR on, or a different or no active profile.
    Mismatch,
}

/// Why the independent observation could not re-identify the same single
/// panel under a known advanced-color state. None of these runs `Restore`;
/// a new reason belongs here unless restoring against it is safe.
#[derive(Debug, PartialEq, Eq)]
pub enum Unverified {
    Topology(DisplayReadError),
    NoInternalDisplay,
    MultipleInternalDisplays,
    /// Another panel (endpoints or monitor path) was read back.
    PanelChanged,
    /// The panel's advanced-color read failed.
    HdrUnknown,
}

/// Select the listed profile matching `key` (managed `ProfilesMatch`) as the
/// internal display's SDR default. Every `Refused` failure submitted nothing.
pub fn set_internal_color_profile<N: ColorProfileNative + ?Sized>(
    native: &mut N,
    key: &str,
    fence: &dyn SubmissionFence,
    cancelled: &dyn Fn() -> bool,
) -> Result<ColorOutcome, ColorFailure> {
    let refused = ColorFailure::Refused;
    let read = |error| refused(ColorRefusal::Read(error));
    let display = match native.topology() {
        Ok(InternalDisplayTopology::Single(display)) => display,
        Ok(InternalDisplayTopology::MultipleInternalDisplays) => {
            return Err(refused(ColorRefusal::MultipleInternalDisplays));
        }
        Ok(InternalDisplayTopology::Empty | InternalDisplayTopology::NoInternalDisplay) => {
            return Err(refused(ColorRefusal::NoInternalDisplay));
        }
        Err(error) => return Err(refused(ColorRefusal::Topology(error))),
    };
    // Managed BuildSnapshot reads before PrepareColorProfile checks HDR.
    let hdr = HdrState::of(&display);
    let state = snapshot(native, &display, hdr).map_err(read)?;
    match hdr {
        HdrState::Off => {}
        HdrState::On => return Err(refused(ColorRefusal::HdrEnabled)),
        HdrState::Unknown => return Err(refused(ColorRefusal::HdrUnknown)),
    }
    let key = OsStr::new(key);
    let Some(requested) = state
        .profiles
        .into_iter()
        .find(|profile| profiles_match(&profile.id, key))
    else {
        return Err(refused(ColorRefusal::ProfileUnavailable));
    };
    if state
        .selected
        .as_deref()
        .is_some_and(|active| profiles_match(&requested.id, active))
    {
        return Ok(ColorOutcome::Unchanged);
    }
    let baseline = Baseline {
        scope: native.scope(&display).map_err(read)?,
        default: native
            .default_profile(&display, ProfileScope::CurrentUser)
            .map_err(read)?,
        associations: native
            .associations(&display, ProfileScope::CurrentUser)
            .map_err(read)?,
    };
    let monitor = |error| refused(ColorRefusal::Monitor(error));
    let path = monitor_path(&display)
        .filter(|path| present(path))
        .ok_or(monitor(MonitorKeyError::NoDevicePath))?;
    let monitor_key = resolve_monitor_key(path, native.monitors(&display)).map_err(monitor)?;

    let mut transaction = Transaction {
        native,
        display: &display,
        panel: PanelIdentity::of(&display),
        monitor_key: &monitor_key,
        fence,
        cancelled,
        submitted: false,
        added: false,
    };
    match transaction.execute(&requested.id, &baseline) {
        Ok(()) => Ok(ColorOutcome::Confirmed),
        Err(Stop::Refused(reason)) => Err(refused(reason)),
        Err(Stop::Unverified(reason)) => Err(ColorFailure::Unverified(reason)),
        Err(Stop::Fault(fault)) => Err(ColorFailure::Uncertain {
            fault,
            restoration: transaction.restore(&requested.id, &baseline),
        }),
    }
}

/// Managed `ColorProfileMutationBaseline`.
struct Baseline {
    scope: ProfileScope,
    default: Option<OsString>,
    associations: Vec<OsString>,
}

/// Why the forward transaction stopped.
enum Stop {
    /// Before the first setter: nothing was submitted.
    Refused(ColorRefusal),
    /// After a possible submission.
    Fault(Fault),
    /// After every setter: the observation did not re-identify the panel.
    Unverified(Unverified),
}

impl From<Fault> for Stop {
    fn from(fault: Fault) -> Self {
        Self::Fault(fault)
    }
}

struct Transaction<'a, N: ?Sized> {
    native: &'a mut N,
    /// Managed operations keep the prepared target for every call.
    display: &'a ActiveInternalDisplay,
    panel: PanelIdentity,
    monitor_key: &'a OsStr,
    fence: &'a dyn SubmissionFence,
    cancelled: &'a dyn Fn() -> bool,
    /// A setter has been called.
    submitted: bool,
    /// Managed `requestedWasAdded`: the association call returned success.
    added: bool,
}

impl<N: ColorProfileNative + ?Sized> Transaction<'_, N> {
    /// Managed `Execute`'s try block.
    fn execute(&mut self, requested: &OsStr, baseline: &Baseline) -> Result<(), Stop> {
        let present = contains(&baseline.associations, requested)
            || same(baseline.default.as_deref(), Some(requested));
        if !present {
            self.admit()?;
            self.associate(requested)?;
            self.added = true;
            self.require_association_or_default(requested)?;
        }
        // Without an association, this read precedes every setter.
        let scope = self.scope().map_err(|fault| self.stop(fault))?;
        if scope != ProfileScope::CurrentUser {
            self.admit()?;
            self.select_scope(true)?;
            self.require_scope(ProfileScope::CurrentUser)?;
        }
        self.admit()?;
        self.make_default(requested)?;
        self.require_default(requested)?;
        self.verify(requested)?;
        Ok(())
    }

    /// Managed `Restore`, which runs only after a possible submission here.
    /// It writes without a new fence: like managed, the transaction keeps
    /// ownership through restoration once Windows may have changed state.
    fn restore(&mut self, requested: &OsStr, baseline: &Baseline) -> Result<(), Fault> {
        // Reconcile before writing. A failed call may still have changed
        // state; do not replay it or guess which part was submitted.
        let current = self.associations()?;
        let default = self.default()?;
        let unexpected = current.iter().any(|profile| {
            !contains(&baseline.associations, profile)
                && (!self.added || !profiles_match(profile, requested))
        });
        let missing = baseline
            .associations
            .iter()
            .any(|profile| !contains(&current, profile));
        if unexpected || missing {
            return Err(Fault::AssociationsChanged);
        }
        if !same(default.as_deref(), baseline.default.as_deref()) {
            if let Some(prior) = baseline.default.as_deref() {
                self.make_default(prior)?;
                self.require_default(prior)?;
            } else if let Some(current) = default.as_deref() {
                // Removing the added association below clears its default.
                if !self.added || !profiles_match(current, requested) {
                    return Err(Fault::NullDefault);
                }
            }
        }
        if self.added
            && (contains(&self.associations()?, requested)
                || same(self.default()?.as_deref(), Some(requested)))
        {
            self.dissociate(requested)?;
            self.require_absent(requested)?;
        }
        if self.scope()? != baseline.scope {
            self.select_scope(baseline.scope == ProfileScope::CurrentUser)?;
            self.require_scope(baseline.scope)?;
        }
        let restored = self.associations()?;
        if !set_equals(&restored, &baseline.associations)
            || !same(self.default()?.as_deref(), baseline.default.as_deref())
            || self.scope()? != baseline.scope
        {
            return Err(Fault::RestorationUnconfirmed);
        }
        Ok(())
    }

    /// The host fence, immediately before the first setter only.
    fn admit(&mut self) -> Result<(), Stop> {
        if self.submitted {
            return Ok(());
        }
        let panel = &self.panel;
        admit_submission(
            self.fence,
            self.cancelled,
            // The observation's HDR refusal also holds at the fence: SDR must
            // still be confirmed immediately before the first setter.
            |fresh| {
                if !panel.same_as(fresh) {
                    return Err(ColorRefusal::PanelChanged);
                }
                match HdrState::of(fresh) {
                    HdrState::Off => Ok(()),
                    HdrState::On => Err(ColorRefusal::HdrEnabled),
                    HdrState::Unknown => Err(ColorRefusal::HdrUnknown),
                }
            },
        )
        .map_err(Stop::Refused)?;
        // From here Windows may change state.
        self.submitted = true;
        Ok(())
    }

    /// A read failure is a refusal only while nothing was submitted.
    fn stop(&self, fault: Fault) -> Stop {
        match fault {
            Fault::Read(error) if !self.submitted => Stop::Refused(ColorRefusal::Read(error)),
            fault => Stop::Fault(fault),
        }
    }

    fn scope(&mut self) -> Result<ProfileScope, Fault> {
        self.native.scope(self.display).map_err(Fault::Read)
    }

    fn associations(&mut self) -> Result<Vec<OsString>, Fault> {
        self.native
            .associations(self.display, ProfileScope::CurrentUser)
            .map_err(Fault::Read)
    }

    fn default(&mut self) -> Result<Option<OsString>, Fault> {
        self.native
            .default_profile(self.display, ProfileScope::CurrentUser)
            .map_err(Fault::Read)
    }

    // Managed ThrowIfHResultFailed: only a negative HRESULT is a failure.
    fn associate(&mut self, profile: &OsStr) -> Result<(), Fault> {
        match self.native.add_association(self.display, profile) {
            status if status < 0 => Err(Fault::AssociateRejected(status)),
            _ => Ok(()),
        }
    }

    fn dissociate(&mut self, profile: &OsStr) -> Result<(), Fault> {
        match self.native.remove_association(self.display, profile) {
            status if status < 0 => Err(Fault::RemoveRejected(status)),
            _ => Ok(()),
        }
    }

    fn make_default(&mut self, profile: &OsStr) -> Result<(), Fault> {
        match self.native.set_default(self.display, profile) {
            status if status < 0 => Err(Fault::DefaultRejected(status)),
            _ => Ok(()),
        }
    }

    fn select_scope(&mut self, per_user: bool) -> Result<(), Fault> {
        self.native
            .use_per_user_profiles(self.monitor_key, per_user)
            .map_err(Fault::ScopeRejected)
    }

    fn require_association_or_default(&mut self, profile: &OsStr) -> Result<(), Fault> {
        if !contains(&self.associations()?, profile)
            && !same(self.default()?.as_deref(), Some(profile))
        {
            return Err(Fault::AssociationUnconfirmed);
        }
        Ok(())
    }

    fn require_absent(&mut self, profile: &OsStr) -> Result<(), Fault> {
        if contains(&self.associations()?, profile)
            || same(self.default()?.as_deref(), Some(profile))
        {
            return Err(Fault::RemovalUnconfirmed);
        }
        Ok(())
    }

    fn require_default(&mut self, profile: &OsStr) -> Result<(), Fault> {
        if !same(self.default()?.as_deref(), Some(profile)) {
            return Err(Fault::DefaultUnconfirmed);
        }
        Ok(())
    }

    fn require_scope(&mut self, scope: ProfileScope) -> Result<(), Fault> {
        if self.scope()? != scope {
            return Err(Fault::ScopeUnconfirmed);
        }
        Ok(())
    }

    /// Managed `verifyApplied`: a fresh observation of the same panel shows
    /// the requested profile active. Not re-identifying the panel under a
    /// known advanced-color state stops as [`Unverified`]; a failed profile
    /// read or a mismatch is a [`Readback`] fault, which `Restore` follows.
    fn verify(&mut self, requested: &OsStr) -> Result<(), Stop> {
        let (after, hdr) = self.reidentify().map_err(Stop::Unverified)?;
        // With HDR on no default is read, so nothing is selected.
        let observed: ColorProfileSnapshot = snapshot(self.native, &after, hdr)
            .map_err(|error| Fault::Readback(Readback::Read(error)))?;
        match observed.selected.as_deref() {
            Some(active) if profiles_match(active, requested) => Ok(()),
            _ => Err(Fault::Readback(Readback::Mismatch).into()),
        }
    }

    /// The observation's single panel, the same as the prepared one, and its
    /// known HDR state. Nothing about profiles is read before this holds.
    fn reidentify(&mut self) -> Result<(ActiveInternalDisplay, HdrState), Unverified> {
        let after = match self.native.topology().map_err(Unverified::Topology)? {
            InternalDisplayTopology::Single(after) => after,
            InternalDisplayTopology::MultipleInternalDisplays => {
                return Err(Unverified::MultipleInternalDisplays);
            }
            InternalDisplayTopology::Empty | InternalDisplayTopology::NoInternalDisplay => {
                return Err(Unverified::NoInternalDisplay);
            }
        };
        if !self.panel.same_as(&after) {
            return Err(Unverified::PanelChanged);
        }
        match HdrState::of(&after) {
            HdrState::Unknown => Err(Unverified::HdrUnknown),
            hdr => Ok((after, hdr)),
        }
    }
}

fn contains(profiles: &[OsString], profile: &OsStr) -> bool {
    profiles
        .iter()
        .any(|candidate| profiles_match(candidate, profile))
}

/// Managed `ProfilesMatch` over nullable IDs: two nulls match.
fn same(left: Option<&OsStr>, right: Option<&OsStr>) -> bool {
    match (left, right) {
        (Some(left), Some(right)) => profiles_match(left, right),
        (None, None) => true,
        _ => false,
    }
}

/// Managed `SetEquals`: equal counts and every left ID listed on the right.
fn set_equals(left: &[OsString], right: &[OsString]) -> bool {
    left.len() == right.len() && left.iter().all(|profile| contains(right, profile))
}

/// Production native boundary: the WCS reader, the CCD reader, the monitor
/// enumeration and the four real WCS setter call sites. Unit-test builds of
/// this crate cannot construct it; dependents inject `ColorProfileNative`
/// doubles.
#[cfg(not(test))]
pub struct WindowsColorProfileNative {
    _private: (),
}

#[cfg(not(test))]
impl WindowsColorProfileNative {
    /// Call from the current-user desktop that owns the display settings.
    pub fn current_user() -> Self {
        Self { _private: () }
    }
}

#[cfg(not(test))]
impl ProfileReads for WindowsColorProfileNative {
    fn scope(&mut self, display: &ActiveInternalDisplay) -> Result<ProfileScope, ProfileReadError> {
        super::native::WindowsProfiles.scope(display)
    }

    fn associations(
        &mut self,
        display: &ActiveInternalDisplay,
        scope: ProfileScope,
    ) -> Result<Vec<OsString>, ProfileReadError> {
        super::native::WindowsProfiles.associations(display, scope)
    }

    fn default_profile(
        &mut self,
        display: &ActiveInternalDisplay,
        scope: ProfileScope,
    ) -> Result<Option<OsString>, ProfileReadError> {
        super::native::WindowsProfiles.default_profile(display, scope)
    }

    fn presets(&mut self) -> Vec<OsString> {
        super::native::WindowsProfiles.presets()
    }
}

#[cfg(not(test))]
impl ColorProfileNative for WindowsColorProfileNative {
    fn topology(&mut self) -> Result<InternalDisplayTopology, DisplayReadError> {
        crate::display::read_internal_display()
    }

    fn monitors(&mut self, display: &ActiveInternalDisplay) -> Vec<Monitor> {
        super::native::monitors(&display.source_name)
    }

    fn add_association(&mut self, display: &ActiveInternalDisplay, profile: &OsStr) -> i32 {
        super::native::add_display_association(display, profile)
    }

    fn remove_association(&mut self, display: &ActiveInternalDisplay, profile: &OsStr) -> i32 {
        super::native::remove_display_association(display, profile)
    }

    fn set_default(&mut self, display: &ActiveInternalDisplay, profile: &OsStr) -> i32 {
        super::native::set_display_default(display, profile)
    }

    fn use_per_user_profiles(&mut self, monitor_key: &OsStr, enabled: bool) -> Result<(), u32> {
        super::native::use_per_user_profiles(monitor_key, enabled)
    }
}

#[cfg(test)]
mod tests;
