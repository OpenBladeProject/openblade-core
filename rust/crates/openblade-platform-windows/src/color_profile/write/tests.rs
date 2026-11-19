//! The production color-profile writer against an in-memory WCS boundary and
//! fence. No Windows reader, setter, monitor enumeration or fence runs. The
//! first nine cases port the managed `ColorProfileMutationTransactionTests`.
use super::*;
use crate::display::{AdvancedColorState, EndpointId};
use std::{
    collections::VecDeque,
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, Ordering},
    },
};

use super::super::ProfileRead;

const NATIVE: &str = "Native_CTR.icm";
const SRGB: &str = "sRGB_CTR.icm";
const FACTORY: &str = "Blade.icm";
const MONITOR: &str = r"\\?\DISPLAY#RAZER#INTERNAL";
const KEY: &str = r"\Registry\Machine\System\Monitor\0001";
const ACCESS_DENIED: i32 = 0x8007_0005u32 as i32;

/// Writes, fence checks and monitor enumerations in call order.
#[derive(Clone, Default)]
struct Log(Arc<Mutex<Vec<String>>>);

impl Log {
    fn push(&self, entry: impl Into<String>) {
        self.0.lock().unwrap().push(entry.into());
    }

    fn entries(&self) -> Vec<String> {
        self.0.lock().unwrap().clone()
    }

    fn writes(&self) -> Vec<String> {
        self.entries()
            .into_iter()
            .filter(|entry| entry != "fence" && entry != "monitors")
            .collect()
    }

    fn fences(&self) -> usize {
        self.entries().iter().filter(|e| *e == "fence").count()
    }
}

fn panel(hdr: Option<bool>) -> ActiveInternalDisplay {
    let mut display = ActiveInternalDisplay::brightness_fixture(MONITOR, 32);
    display.source = EndpointId {
        adapter_low: 11,
        adapter_high: -1,
        id: 12,
    };
    display.advanced_color = hdr
        .map(|enabled| AdvancedColorState {
            hdr_supported: true,
            hdr_user_enabled: enabled,
            bits_per_color_channel: None,
        })
        .ok_or(crate::display::AdvancedColorReadError::Native { status: 50 });
    display
}

fn sdr() -> Result<InternalDisplayTopology, DisplayReadError> {
    Ok(InternalDisplayTopology::Single(panel(Some(false))))
}

fn names(list: &[&str]) -> Vec<OsString> {
    list.iter().map(OsString::from).collect()
}

type Hook = fn(&mut Wcs, &OsStr) -> i32;
type ScopeHook = fn(&mut Wcs, bool) -> Result<(), u32>;

/// Managed `FakeColorProfileState`, plus the observation and monitor reads.
struct Wcs {
    log: Log,
    scope: ProfileScope,
    user: Vec<OsString>,
    user_default: Option<OsString>,
    system: Vec<OsString>,
    system_default: Option<OsString>,
    presets: Vec<OsString>,
    monitors: Vec<(&'static str, &'static str, bool)>,
    /// Topology reads consumed before the SDR panel.
    topologies: VecDeque<Result<InternalDisplayTopology, DisplayReadError>>,
    topology_reads: usize,
    /// Runs after the managed fake's own add effect.
    add_effect: Option<fn(&mut Wcs, &OsStr)>,
    add_status: i32,
    /// One-shot replacements for the default and scope setters.
    default_failure: Option<Hook>,
    scope_failure: Option<ScopeHook>,
    remove_has_effect: bool,
    /// Fail this read whenever at least this many writes were made.
    fail_read: Option<(ProfileRead, usize)>,
    /// Report an unknown scope on this (one-based) scope read.
    unknown_scope_at: Option<usize>,
    scope_reads: usize,
    cancel_on_write: Option<Arc<AtomicBool>>,
}

impl Wcs {
    fn new(scope: ProfileScope, default: Option<&str>, user: &[&str]) -> Self {
        Self {
            log: Log::default(),
            scope,
            user: names(user),
            user_default: default.map(OsString::from),
            system: names(&[FACTORY]),
            system_default: Some(FACTORY.into()),
            presets: names(&[NATIVE, SRGB]),
            monitors: vec![(MONITOR, KEY, true)],
            topologies: VecDeque::new(),
            topology_reads: 0,
            add_effect: None,
            add_status: 0,
            default_failure: None,
            scope_failure: None,
            remove_has_effect: true,
            fail_read: None,
            unknown_scope_at: None,
            scope_reads: 0,
            cancel_on_write: None,
        }
    }

    /// The managed fakes' empty system-wide baseline.
    fn empty() -> Self {
        Self::new(ProfileScope::SystemWide, None, &[])
    }

    fn write(&mut self, entry: String) {
        self.log.push(entry);
        if let Some(cancel) = &self.cancel_on_write {
            cancel.store(true, Ordering::SeqCst);
        }
    }

    fn read<T>(&self, operation: ProfileRead, value: T) -> Result<T, ProfileReadError> {
        match self.fail_read {
            Some((failed, writes)) if failed == operation && self.log.writes().len() >= writes => {
                Err(ProfileReadError::Native {
                    operation,
                    hresult: ACCESS_DENIED,
                })
            }
            _ => Ok(value),
        }
    }
}

impl ProfileReads for Wcs {
    fn scope(&mut self, display: &ActiveInternalDisplay) -> Result<ProfileScope, ProfileReadError> {
        assert_eq!(display.target, panel(None).target);
        self.scope_reads += 1;
        if self.unknown_scope_at == Some(self.scope_reads) {
            return Err(ProfileReadError::UnknownScope(9));
        }
        self.read(ProfileRead::Scope, self.scope)
    }

    fn associations(
        &mut self,
        _: &ActiveInternalDisplay,
        scope: ProfileScope,
    ) -> Result<Vec<OsString>, ProfileReadError> {
        let list = match scope {
            ProfileScope::CurrentUser => self.user.clone(),
            ProfileScope::SystemWide => self.system.clone(),
        };
        self.read(ProfileRead::Associations, list)
    }

    fn default_profile(
        &mut self,
        _: &ActiveInternalDisplay,
        scope: ProfileScope,
    ) -> Result<Option<OsString>, ProfileReadError> {
        let default = match scope {
            ProfileScope::CurrentUser => self.user_default.clone(),
            ProfileScope::SystemWide => self.system_default.clone(),
        };
        self.read(ProfileRead::Default, default)
    }

    fn presets(&mut self) -> Vec<OsString> {
        self.presets.clone()
    }
}

impl ColorProfileNative for Wcs {
    fn topology(&mut self) -> Result<InternalDisplayTopology, DisplayReadError> {
        self.topology_reads += 1;
        self.topologies.pop_front().unwrap_or_else(sdr)
    }

    fn monitors(&mut self, display: &ActiveInternalDisplay) -> Vec<Monitor> {
        assert_eq!(display.source_name, r"\\.\DISPLAY1");
        self.log.push("monitors");
        self.monitors
            .iter()
            .map(|&(interface, key, active)| Monitor {
                interface: interface.into(),
                key: key.into(),
                active,
            })
            .collect()
    }

    fn add_association(&mut self, display: &ActiveInternalDisplay, profile: &OsStr) -> i32 {
        assert_eq!(*display, panel(Some(false)));
        self.write(format!("add:{}", profile.to_string_lossy()));
        if !self.user.iter().any(|id| profiles_match(id, profile)) {
            self.user.push(profile.into());
        }
        if let Some(effect) = self.add_effect {
            effect(self, profile);
        }
        self.add_status
    }

    fn remove_association(&mut self, _: &ActiveInternalDisplay, profile: &OsStr) -> i32 {
        self.write(format!("remove:{}", profile.to_string_lossy()));
        if self.remove_has_effect {
            self.user.retain(|id| !profiles_match(id, profile));
            if self
                .user_default
                .as_deref()
                .is_some_and(|id| profiles_match(id, profile))
            {
                self.user_default = None;
            }
        }
        0
    }

    fn set_default(&mut self, _: &ActiveInternalDisplay, profile: &OsStr) -> i32 {
        self.write(format!("default:{}", profile.to_string_lossy()));
        if let Some(failure) = self.default_failure.take() {
            return failure(self, profile);
        }
        self.user_default = Some(profile.into());
        0
    }

    fn use_per_user_profiles(&mut self, key: &OsStr, enabled: bool) -> Result<(), u32> {
        assert_eq!(key, KEY);
        self.write(format!("scope:{enabled}"));
        if let Some(failure) = self.scope_failure.take() {
            return failure(self, enabled);
        }
        self.scope = if enabled {
            ProfileScope::CurrentUser
        } else {
            ProfileScope::SystemWide
        };
        Ok(())
    }
}

struct Fence {
    log: Log,
    topology: fn() -> Result<InternalDisplayTopology, DisplayReadError>,
    admit: bool,
}

impl SubmissionFence for Fence {
    fn topology(&self) -> Result<InternalDisplayTopology, DisplayReadError> {
        self.log.push("fence");
        (self.topology)()
    }

    fn admit(&self) -> bool {
        self.admit
    }
}

fn fence(wcs: &Wcs) -> Fence {
    Fence {
        log: wcs.log.clone(),
        topology: sdr,
        admit: true,
    }
}

fn set(wcs: &mut Wcs, key: &str) -> Result<ColorOutcome, ColorFailure> {
    let fence = fence(wcs);
    set_internal_color_profile(wcs, key, &fence, &|| false)
}

fn uncertain(fault: Fault, restoration: Result<(), Fault>) -> Result<ColorOutcome, ColorFailure> {
    Err(ColorFailure::Uncertain { fault, restoration })
}

fn refused(reason: ColorRefusal) -> Result<ColorOutcome, ColorFailure> {
    Err(ColorFailure::Refused(reason))
}

fn rejected_scope(_: &mut Wcs, _: bool) -> Result<(), u32> {
    Err(5)
}

#[test]
fn first_association_is_created_before_per_user_scope_is_enabled() {
    let mut wcs = Wcs::empty();
    assert_eq!(set(&mut wcs, NATIVE), Ok(ColorOutcome::Confirmed));
    assert_eq!(
        wcs.log.writes(),
        ["add:Native_CTR.icm", "scope:true", "default:Native_CTR.icm"]
    );
    assert_eq!(wcs.scope, ProfileScope::CurrentUser);
    assert_eq!(wcs.user_default.as_deref(), Some(OsStr::new(NATIVE)));
    assert_eq!(wcs.user, names(&[NATIVE]));
    // Prepare and the independent readback; the fence once, before the first
    // setter and after the monitor enumeration.
    assert_eq!(wcs.topology_reads, 2);
    assert_eq!(
        wcs.log.entries()[..3],
        ["monitors", "fence", "add:Native_CTR.icm"]
    );
    assert_eq!(wcs.log.fences(), 1);
}

#[test]
fn association_that_activates_per_user_scope_does_not_repeat_the_scope_write() {
    let mut wcs = Wcs::empty();
    wcs.add_effect = Some(|wcs, _| wcs.scope = ProfileScope::CurrentUser);
    assert_eq!(set(&mut wcs, NATIVE), Ok(ColorOutcome::Confirmed));
    assert_eq!(
        wcs.log.writes(),
        ["add:Native_CTR.icm", "default:Native_CTR.icm"]
    );
}

#[test]
fn inactive_association_visible_only_as_default_is_accepted_before_scope_activation() {
    let mut wcs = Wcs::empty();
    wcs.add_effect = Some(|wcs, profile| {
        wcs.user.retain(|id| id != profile);
        wcs.user_default = Some(profile.into());
    });
    assert_eq!(set(&mut wcs, NATIVE), Ok(ColorOutcome::Confirmed));
    assert_eq!(
        wcs.log.writes(),
        ["add:Native_CTR.icm", "scope:true", "default:Native_CTR.icm"]
    );
    assert!(wcs.user.is_empty());
}

#[test]
fn failed_scope_change_removes_the_new_association_without_repeating_the_scope_write() {
    let mut wcs = Wcs::empty();
    wcs.scope_failure = Some(rejected_scope);
    assert_eq!(
        set(&mut wcs, NATIVE),
        uncertain(Fault::ScopeRejected(5), Ok(()))
    );
    assert_eq!(
        wcs.log.writes(),
        ["add:Native_CTR.icm", "scope:true", "remove:Native_CTR.icm"]
    );
    assert_eq!(wcs.scope, ProfileScope::SystemWide);
    assert_eq!(wcs.user_default, None);
    assert!(wcs.user.is_empty());
}

#[test]
fn failed_scope_change_that_mutated_the_scope_restores_the_original_scope() {
    let mut wcs = Wcs::empty();
    wcs.scope_failure = Some(|wcs, enabled| {
        wcs.scope = if enabled {
            ProfileScope::CurrentUser
        } else {
            ProfileScope::SystemWide
        };
        Err(31)
    });
    assert_eq!(
        set(&mut wcs, NATIVE),
        uncertain(Fault::ScopeRejected(31), Ok(()))
    );
    // Restoration order: default, then the added association, then scope.
    assert_eq!(
        wcs.log.writes(),
        [
            "add:Native_CTR.icm",
            "scope:true",
            "remove:Native_CTR.icm",
            "scope:false"
        ]
    );
    assert_eq!(wcs.scope, ProfileScope::SystemWide);
    assert_eq!(wcs.log.fences(), 1, "restoration is never re-fenced");
}

#[test]
fn inactive_association_visible_only_as_default_is_removed_during_rollback() {
    let mut wcs = Wcs::empty();
    wcs.add_effect = Some(|wcs, profile| {
        wcs.user.retain(|id| id != profile);
        wcs.user_default = Some(profile.into());
    });
    wcs.scope_failure = Some(rejected_scope);
    assert_eq!(
        set(&mut wcs, NATIVE),
        uncertain(Fault::ScopeRejected(5), Ok(()))
    );
    assert_eq!(
        wcs.log.writes(),
        ["add:Native_CTR.icm", "scope:true", "remove:Native_CTR.icm"]
    );
    assert_eq!(wcs.user_default, None);
    assert!(wcs.user.is_empty());
}

#[test]
fn failed_default_change_restores_the_default_before_removing_the_association() {
    let mut wcs = Wcs::new(ProfileScope::CurrentUser, Some(FACTORY), &[FACTORY, SRGB]);
    wcs.default_failure = Some(|wcs, profile| {
        wcs.user_default = Some(profile.into());
        ACCESS_DENIED
    });
    assert_eq!(
        set(&mut wcs, NATIVE),
        uncertain(Fault::DefaultRejected(ACCESS_DENIED), Ok(()))
    );
    assert_eq!(
        wcs.log.writes(),
        [
            "add:Native_CTR.icm",
            "default:Native_CTR.icm",
            "default:Blade.icm",
            "remove:Native_CTR.icm",
        ]
    );
    assert_eq!(wcs.user_default.as_deref(), Some(OsStr::new(FACTORY)));
    assert_eq!(wcs.user, names(&[FACTORY, SRGB]));
    assert_eq!(wcs.scope, ProfileScope::CurrentUser);
}

#[test]
fn unconfirmed_readback_restores_the_exact_empty_baseline() {
    let mut wcs = Wcs::empty();
    // The independent observation reads another panel.
    wcs.topologies.push_back(sdr());
    wcs.topologies.push_back(Ok(InternalDisplayTopology::Single(
        ActiveInternalDisplay::brightness_fixture(r"\\?\DISPLAY#OTHER", 32),
    )));
    assert_eq!(
        set(&mut wcs, NATIVE),
        uncertain(Fault::Readback(Readback::Mismatch), Ok(()))
    );
    assert_eq!(
        wcs.log.writes(),
        [
            "add:Native_CTR.icm",
            "scope:true",
            "default:Native_CTR.icm",
            "remove:Native_CTR.icm",
            "scope:false",
        ]
    );
    assert_eq!(wcs.scope, ProfileScope::SystemWide);
    assert_eq!(wcs.user_default, None);
    assert!(wcs.user.is_empty());
}

#[test]
fn failed_reconciliation_read_stops_without_restoration_writes() {
    let mut wcs = Wcs::new(ProfileScope::CurrentUser, Some(FACTORY), &[FACTORY]);
    wcs.fail_read = Some((ProfileRead::Associations, 2));
    let read = ProfileReadError::Native {
        operation: ProfileRead::Associations,
        hresult: ACCESS_DENIED,
    };
    assert_eq!(
        set(&mut wcs, NATIVE),
        uncertain(
            Fault::Readback(Readback::Read(read)),
            Err(Fault::Read(read))
        )
    );
    assert_eq!(
        wcs.log.writes(),
        ["add:Native_CTR.icm", "default:Native_CTR.icm"]
    );
}

#[test]
fn other_readback_failures_are_uncertain_and_restored() {
    let other = |topology: Result<InternalDisplayTopology, DisplayReadError>| {
        let mut wcs = Wcs::empty();
        wcs.topologies.push_back(sdr());
        wcs.topologies.push_back(topology);
        let result = set(&mut wcs, NATIVE);
        assert_eq!(wcs.log.writes().len(), 5);
        result
    };
    let hdr_on = Ok(InternalDisplayTopology::Single(panel(Some(true))));
    let hdr_unknown = Ok(InternalDisplayTopology::Single(panel(None)));
    for (topology, readback) in [
        (hdr_on, Readback::Mismatch),
        (hdr_unknown, Readback::Mismatch),
        (
            Ok(InternalDisplayTopology::MultipleInternalDisplays),
            Readback::MultipleInternalDisplays,
        ),
        (
            Ok(InternalDisplayTopology::NoInternalDisplay),
            Readback::NoInternalDisplay,
        ),
        (
            Err(DisplayReadError::TopologyChanging),
            Readback::Topology(DisplayReadError::TopologyChanging),
        ),
    ] {
        assert_eq!(
            other(topology),
            uncertain(Fault::Readback(readback), Ok(()))
        );
    }
}

#[test]
fn unlisted_hdr_and_unreadable_requests_submit_nothing() {
    let quiet = |wcs: &Wcs| {
        assert!(wcs.log.entries().is_empty(), "{:?}", wcs.log.entries());
    };
    let mut wcs = Wcs::empty();
    assert_eq!(
        set(&mut wcs, "Unlisted.icm"),
        refused(ColorRefusal::ProfileUnavailable)
    );
    assert_eq!(wcs.topology_reads, 1);
    quiet(&wcs);

    for (hdr, reason) in [
        (Some(true), ColorRefusal::HdrEnabled),
        (None, ColorRefusal::HdrUnknown),
    ] {
        let mut wcs = Wcs::empty();
        wcs.topologies
            .push_back(Ok(InternalDisplayTopology::Single(panel(hdr))));
        assert_eq!(set(&mut wcs, NATIVE), refused(reason));
        quiet(&wcs);
    }
    // Managed BuildSnapshot reads before PrepareColorProfile checks HDR.
    let mut wcs = Wcs::empty();
    wcs.topologies
        .push_back(Ok(InternalDisplayTopology::Single(panel(Some(true)))));
    wcs.fail_read = Some((ProfileRead::Scope, 0));
    assert!(matches!(
        set(&mut wcs, NATIVE),
        Err(ColorFailure::Refused(ColorRefusal::Read(_)))
    ));

    for (topology, reason) in [
        (
            Err(DisplayReadError::BufferLimit),
            ColorRefusal::Topology(DisplayReadError::BufferLimit),
        ),
        (
            Ok(InternalDisplayTopology::Empty),
            ColorRefusal::NoInternalDisplay,
        ),
        (
            Ok(InternalDisplayTopology::MultipleInternalDisplays),
            ColorRefusal::MultipleInternalDisplays,
        ),
    ] {
        let mut wcs = Wcs::empty();
        wcs.topologies.push_back(topology);
        assert_eq!(set(&mut wcs, NATIVE), refused(reason));
        quiet(&wcs);
    }
}

#[test]
fn active_profile_is_a_no_op_by_full_path_or_file_name() {
    // The selected (system) scope already shows the requested default.
    let mut wcs = Wcs::empty();
    wcs.system_default = Some(r"C:\Windows\System32\spool\drivers\color\Blade.icm".into());
    for key in [FACTORY, r"D:\elsewhere\BLADE.ICM"] {
        assert_eq!(set(&mut wcs, key), Ok(ColorOutcome::Unchanged));
    }
    assert!(wcs.log.entries().is_empty());
    assert_eq!(wcs.topology_reads, 2);
}

#[test]
fn the_listed_id_not_the_request_spelling_is_written() {
    let mut wcs = Wcs::empty();
    wcs.presets = names(&[r"C:\color\Native_CTR.icm"]);
    assert_eq!(set(&mut wcs, "native_ctr.ICM"), Ok(ColorOutcome::Confirmed));
    assert_eq!(wcs.log.writes()[0], r"add:C:\color\Native_CTR.icm");
}

#[test]
fn baseline_reads_and_monitor_key_failures_submit_nothing() {
    for operation in [
        ProfileRead::Scope,
        ProfileRead::Default,
        ProfileRead::Associations,
    ] {
        // The observation reads the system scope; the baseline reads current user.
        let mut wcs = Wcs::empty();
        wcs.fail_read = Some((operation, 0));
        let result = set(&mut wcs, NATIVE);
        assert!(
            matches!(result, Err(ColorFailure::Refused(ColorRefusal::Read(_)))),
            "{operation:?}"
        );
        assert!(wcs.log.entries().is_empty());
    }
    let key = |monitors: Vec<(&'static str, &'static str, bool)>| {
        let mut wcs = Wcs::empty();
        wcs.monitors = monitors;
        let result = set(&mut wcs, NATIVE);
        assert_eq!(wcs.log.entries(), ["monitors"]);
        result
    };
    let monitor = |error| refused(ColorRefusal::Monitor(error));
    assert_eq!(key(vec![]), monitor(MonitorKeyError::NoActiveMonitor));
    assert_eq!(
        key(vec![(MONITOR, KEY, false)]),
        monitor(MonitorKeyError::NoActiveMonitor)
    );
    assert_eq!(
        key(vec![(MONITOR, KEY, true), (MONITOR, "other", true)]),
        monitor(MonitorKeyError::MultipleActiveMonitors)
    );
    assert_eq!(
        key(vec![(MONITOR, "  ", true)]),
        monitor(MonitorKeyError::NoDeviceKey)
    );
    // Interface names compare ordinally ignoring case; inactive ones are ignored.
    let mut wcs = Wcs::empty();
    wcs.monitors = vec![
        (r"\\?\display#razer#internal", KEY, true),
        (MONITOR, "inactive", false),
    ];
    assert_eq!(set(&mut wcs, NATIVE), Ok(ColorOutcome::Confirmed));

    let mut wcs = Wcs::empty();
    let mut blank = panel(Some(false));
    blank.monitor_name = Err(31);
    wcs.topologies
        .push_back(Ok(InternalDisplayTopology::Single(blank)));
    assert_eq!(
        set(&mut wcs, NATIVE),
        monitor(MonitorKeyError::NoDevicePath)
    );
    assert!(wcs.log.entries().is_empty());
}

#[test]
fn fence_refusals_and_cancellation_before_the_first_setter_submit_nothing() {
    let mut wcs = Wcs::empty();
    let mut denied = fence(&wcs);
    denied.admit = false;
    assert_eq!(
        set_internal_color_profile(&mut wcs, NATIVE, &denied, &|| false),
        refused(ColorRefusal::AdmissionDenied)
    );
    assert_eq!(wcs.log.entries(), ["monitors", "fence"]);

    let mut wcs = Wcs::empty();
    let mut moved = fence(&wcs);
    moved.topology = || {
        Ok(InternalDisplayTopology::Single(
            ActiveInternalDisplay::brightness_fixture(MONITOR, 33),
        ))
    };
    assert_eq!(
        set_internal_color_profile(&mut wcs, NATIVE, &moved, &|| false),
        refused(ColorRefusal::PanelChanged)
    );
    assert!(wcs.log.writes().is_empty());

    let mut wcs = Wcs::empty();
    let fence = fence(&wcs);
    assert_eq!(
        set_internal_color_profile(&mut wcs, NATIVE, &fence, &|| true),
        refused(ColorRefusal::Cancelled)
    );
    assert_eq!(wcs.log.entries(), ["monitors"]);
}

#[test]
fn cancellation_after_the_first_setter_does_not_stop_the_transaction() {
    let cancel = Arc::new(AtomicBool::new(false));
    let mut wcs = Wcs::empty();
    wcs.cancel_on_write = Some(cancel.clone());
    let fence = fence(&wcs);
    assert_eq!(
        set_internal_color_profile(&mut wcs, NATIVE, &fence, &|| cancel.load(Ordering::SeqCst)),
        Ok(ColorOutcome::Confirmed)
    );
    assert_eq!(wcs.log.writes().len(), 3);
}

#[test]
fn a_rejected_first_setter_is_uncertain_and_reconciled_without_writes() {
    // Managed reports every failure inside Execute as possibly submitted.
    let mut wcs = Wcs::empty();
    wcs.add_status = ACCESS_DENIED;
    wcs.add_effect = Some(|wcs, _| wcs.user.clear());
    assert_eq!(
        set(&mut wcs, NATIVE),
        uncertain(Fault::AssociateRejected(ACCESS_DENIED), Ok(()))
    );
    assert_eq!(wcs.log.writes(), ["add:Native_CTR.icm"]);

    // A rejected add that still took effect is not owned (requestedWasAdded
    // stays false): restoration stops without altering the associations.
    let mut wcs = Wcs::empty();
    wcs.add_status = ACCESS_DENIED;
    assert_eq!(
        set(&mut wcs, NATIVE),
        uncertain(
            Fault::AssociateRejected(ACCESS_DENIED),
            Err(Fault::AssociationsChanged)
        )
    );
    assert_eq!(wcs.log.writes(), ["add:Native_CTR.icm"]);
}

#[test]
fn a_read_failure_before_any_setter_is_a_refusal_without_restoration() {
    // Already associated, so Execute reads the scope before its first setter:
    // the observation, the baseline and Execute each read it once.
    let mut wcs = Wcs::new(ProfileScope::SystemWide, None, &[NATIVE]);
    wcs.unknown_scope_at = Some(3);
    assert_eq!(
        set(&mut wcs, NATIVE),
        refused(ColorRefusal::Read(ProfileReadError::UnknownScope(9)))
    );
    assert_eq!(
        wcs.log.entries(),
        ["monitors"],
        "neither fenced nor restored"
    );
}

#[test]
fn a_read_failure_after_the_first_setter_is_uncertain_and_restored() {
    // Not associated: Execute's scope read follows the association, so the
    // same failure is now possibly submitted and the managed Restore runs.
    let mut wcs = Wcs::empty();
    wcs.unknown_scope_at = Some(3);
    assert_eq!(
        set(&mut wcs, NATIVE),
        uncertain(Fault::Read(ProfileReadError::UnknownScope(9)), Ok(()))
    );
    assert_eq!(
        wcs.log.writes(),
        ["add:Native_CTR.icm", "remove:Native_CTR.icm"]
    );
    assert!(wcs.user.is_empty());
}

#[test]
fn the_fence_precedes_whichever_setter_comes_first() {
    // Each baseline starts the transaction with a different setter.
    for (scope, user) in [
        (ProfileScope::SystemWide, &[][..]),
        (ProfileScope::SystemWide, &[NATIVE][..]),
        (ProfileScope::CurrentUser, &[NATIVE][..]),
    ] {
        let mut wcs = Wcs::new(scope, None, user);
        let mut denied = fence(&wcs);
        denied.admit = false;
        assert_eq!(
            set_internal_color_profile(&mut wcs, NATIVE, &denied, &|| false),
            refused(ColorRefusal::AdmissionDenied),
            "{scope:?} {user:?}"
        );
        assert_eq!(
            wcs.log.entries(),
            ["monitors", "fence"],
            "{scope:?} {user:?}"
        );
    }
}

#[test]
fn a_default_only_preexisting_association_cannot_restore_a_null_default() {
    // Associated before the transaction, so it is never added or removed.
    let mut wcs = Wcs::new(ProfileScope::SystemWide, None, &[NATIVE]);
    wcs.default_failure = Some(|wcs, profile| {
        wcs.user_default = Some(profile.into());
        ACCESS_DENIED
    });
    assert_eq!(
        set(&mut wcs, NATIVE),
        uncertain(
            Fault::DefaultRejected(ACCESS_DENIED),
            Err(Fault::NullDefault)
        )
    );
    assert_eq!(wcs.log.writes(), ["scope:true", "default:Native_CTR.icm"]);
}

#[test]
fn restoration_stops_at_an_unconfirmed_removal() {
    let mut wcs = Wcs::empty();
    wcs.scope_failure = Some(rejected_scope);
    wcs.remove_has_effect = false;
    assert_eq!(
        set(&mut wcs, NATIVE),
        uncertain(Fault::ScopeRejected(5), Err(Fault::RemovalUnconfirmed))
    );
    assert_eq!(
        wcs.log.writes(),
        ["add:Native_CTR.icm", "scope:true", "remove:Native_CTR.icm"]
    );
}

#[test]
fn step_readbacks_that_miss_the_effect_are_uncertain() {
    // The add reports success but nothing is associated.
    let mut wcs = Wcs::empty();
    wcs.add_effect = Some(|wcs, _| wcs.user.clear());
    assert_eq!(
        set(&mut wcs, NATIVE),
        uncertain(Fault::AssociationUnconfirmed, Ok(()))
    );
    // The scope call succeeds without selecting the per-user scope.
    let mut wcs = Wcs::empty();
    wcs.scope_failure = Some(|_, _| Ok(()));
    assert_eq!(
        set(&mut wcs, NATIVE),
        uncertain(Fault::ScopeUnconfirmed, Ok(()))
    );
    // The default call succeeds without changing the default.
    let mut wcs = Wcs::empty();
    wcs.default_failure = Some(|_, _| 0);
    assert_eq!(
        set(&mut wcs, NATIVE),
        uncertain(Fault::DefaultUnconfirmed, Ok(()))
    );
    assert_eq!(
        wcs.log.writes(),
        [
            "add:Native_CTR.icm",
            "scope:true",
            "default:Native_CTR.icm",
            "remove:Native_CTR.icm",
            "scope:false",
        ]
    );
}

#[test]
fn a_positive_hresult_is_success_like_managed() {
    let mut wcs = Wcs::empty();
    wcs.add_status = 1;
    assert_eq!(set(&mut wcs, NATIVE), Ok(ColorOutcome::Confirmed));
}
