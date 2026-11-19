//! Manual setColorProfile through the shared Rust WCS writer against a
//! scripted native boundary, with injected power, fence, reply query and
//! durable storage. No Windows supplier, endpoint, CCD, WCS, ICC, registry or
//! monitor enumeration call or hardware is used.
use super::*;
use crate::session_native::display_query::{
    manual_write::CHANGING,
    observation::fixture_query,
    test_support::{FencedTopology, Host, Memory, Rig, TestPanel, flag, result},
};
use openblade_platform_windows::{
    color_profile::{Monitor, ProfileReads, ProfileScope},
    display::{
        ActiveInternalDisplay, DisplayReadError, InternalDisplayTopology, Operation,
        scripted::{RefreshTiming, Scenario},
    },
};
use std::{
    collections::VecDeque,
    ffi::OsString,
    sync::{Arc, atomic::AtomicBool},
};

const NATIVE: &str = "Native_CTR.icm";
const SRGB: &str = "sRGB_CTR.icm";
const KEY: &str = r"\Registry\Machine\System\Monitor\0001";
const ACCESS_DENIED: i32 = 0x8007_0005u32 as i32;
const RESTORED: &str = "The previous color-profile configuration was restored.";

/// The scripted panel: the shared CCD fixture under SDR or HDR, and the
/// managed `FakeColorProfileState` WCS model behind it.
struct Panel {
    hdr: bool,
    replaced: bool,
    /// Topology reads consumed before the panel's own state.
    topology_script: VecDeque<Result<InternalDisplayTopology, DisplayReadError>>,
    topologies: usize,
    scope: ProfileScope,
    user: Vec<OsString>,
    user_default: Option<OsString>,
    system: Vec<OsString>,
    system_default: Option<OsString>,
    presets: Vec<OsString>,
    /// The (interface, key, active) monitors of the panel's source; `None`
    /// is its one active monitor.
    monitors: Option<Vec<(&'static str, &'static str, bool)>>,
    add_status: i32,
    /// Whether the association call changes state, whatever it returns.
    add_effect: bool,
    remove_effect: bool,
    scope_error: Option<u32>,
    /// This WCS read fails from the first call.
    failing_read: Option<ProfileRead>,
    /// Every setter call, in order.
    writes: Vec<String>,
    /// Set by every setter (cancellation racing a write).
    cancel_on_write: Option<Arc<AtomicBool>>,
    /// The reply query shows this active profile instead of the panel's.
    reply_active: Option<Option<&'static str>>,
    /// The fence reads this HDR state instead of the panel.s.
    fence_hdr: Option<bool>,
}

impl Panel {
    fn topology(&self) -> Result<InternalDisplayTopology, DisplayReadError> {
        Scenario::Available.topology_with_hdr(self.hdr, RefreshTiming::default(), self.replaced)
    }

    /// The default Windows shows for the selected scope under SDR.
    fn active(&self) -> Option<OsString> {
        if self.hdr {
            return None;
        }
        match self.scope {
            ProfileScope::CurrentUser => self.user_default.clone(),
            ProfileScope::SystemWide => self.system_default.clone(),
        }
    }

    fn read<T>(&self, operation: ProfileRead, value: T) -> Result<T, ProfileReadError> {
        match self.failing_read {
            Some(failing) if failing == operation => Err(ProfileReadError::Native {
                operation,
                hresult: ACCESS_DENIED,
            }),
            _ => Ok(value),
        }
    }

    fn write(&mut self, entry: String) {
        self.writes.push(entry);
        if let Some(cancel) = &self.cancel_on_write {
            cancel.store(true, Ordering::SeqCst);
        }
    }
}

impl ProfileReads for Panel {
    fn scope(&mut self, _: &ActiveInternalDisplay) -> Result<ProfileScope, ProfileReadError> {
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

impl ColorProfileNative for Panel {
    fn topology(&mut self) -> Result<InternalDisplayTopology, DisplayReadError> {
        self.topologies += 1;
        self.topology_script
            .pop_front()
            .unwrap_or_else(|| Panel::topology(self))
    }

    fn monitors(&mut self, display: &ActiveInternalDisplay) -> Vec<Monitor> {
        let Some(monitors) = &self.monitors else {
            let interface = display
                .monitor_name
                .as_ref()
                .ok()
                .and_then(|name| name.device_path.clone())
                .expect("the fixture reports a monitor path");
            return vec![Monitor {
                interface,
                key: KEY.into(),
                active: true,
            }];
        };
        monitors
            .iter()
            .map(|&(interface, key, active)| Monitor {
                interface: interface.into(),
                key: key.into(),
                active,
            })
            .collect()
    }

    fn add_association(&mut self, _: &ActiveInternalDisplay, profile: &OsStr) -> i32 {
        self.write(format!("add:{}", profile.to_string_lossy()));
        if self.add_effect && !self.user.iter().any(|id| profiles_match(id, profile)) {
            self.user.push(profile.into());
        }
        self.add_status
    }

    fn remove_association(&mut self, _: &ActiveInternalDisplay, profile: &OsStr) -> i32 {
        self.write(format!("remove:{}", profile.to_string_lossy()));
        if self.remove_effect {
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
        self.user_default = Some(profile.into());
        0
    }

    fn use_per_user_profiles(&mut self, key: &OsStr, enabled: bool) -> Result<(), u32> {
        assert_eq!(key, KEY, "the panel's own monitor device key");
        self.write(format!("scope:{enabled}"));
        if let Some(error) = self.scope_error {
            return Err(error);
        }
        self.scope = if enabled {
            ProfileScope::CurrentUser
        } else {
            ProfileScope::SystemWide
        };
        Ok(())
    }
}

impl TestPanel for Panel {
    type Value = &'static str;
    type Request = SetColorProfile;

    fn request(expected_power_source: u8, key: &'static str) -> SetColorProfile {
        SetColorProfile {
            expected_power_source,
            key: key.into(),
        }
    }

    fn fenced(&self) -> FencedTopology {
        let hdr = self.fence_hdr.unwrap_or(self.hdr);
        Box::new(move || {
            Scenario::Available.topology_with_hdr(hdr, RefreshTiming::default(), false)
        })
    }

    fn reply(&self) -> Snapshot {
        let active = match self.reply_active {
            Some(active) => active.map(String::from),
            None => self.active().map(|id| id.into_string().unwrap()),
        };
        snapshot(self.hdr, active.as_deref())
    }
}

/// A reply query under SDR or HDR with this active profile.
fn snapshot(hdr: bool, active: Option<&str>) -> Snapshot {
    let mut value = serde_json::to_value(fixture_query("available")).unwrap();
    value["hdrEnabled"] = hdr.into();
    value["colorProfilesEnabled"] = (!hdr).into();
    value["activeColorProfileId"] = active.into();
    serde_json::from_value(value).unwrap()
}

/// An admitted host over an SDR panel: the system-wide scope with no
/// current-user association and no default, so no profile is active.
fn admitted(source: u8) -> Host<Panel> {
    Host::admitted(
        source,
        Panel {
            hdr: false,
            replaced: false,
            topology_script: VecDeque::new(),
            topologies: 0,
            scope: ProfileScope::SystemWide,
            user: Vec::new(),
            user_default: None,
            system: Vec::new(),
            system_default: None,
            presets: [NATIVE, SRGB].map(OsString::from).to_vec(),
            monitors: None,
            add_status: 0,
            add_effect: true,
            remove_effect: true,
            scope_error: None,
            failing_read: None,
            writes: Vec::new(),
            cancel_on_write: None,
            reply_active: None,
            fence_hdr: None,
        },
    )
}

impl Host<Panel> {
    /// Fence checks and setter calls: any device interaction past the reads.
    fn interactions(&self) -> (usize, usize) {
        (
            self.fence_checks.load(Ordering::SeqCst),
            self.panel.writes.len(),
        )
    }
}

fn failed(error: &str) -> String {
    format!("The color-profile change failed: {error} {RESTORED}")
}

#[test]
fn requested_profile_is_fenced_written_confirmed_and_recorded() {
    let rig = Rig::new(Memory::default());
    let mut host = admitted(1);
    let outcome = rig.set(&mut host, 1, SRGB).unwrap();
    assert_eq!(result(&outcome), (Status::Succeeded, None));
    assert_eq!(outcome.state.color_profile(), Some(SRGB));
    // Managed order: associate, select the per-user scope, make it default.
    assert_eq!(
        host.panel.writes,
        ["add:sRGB_CTR.icm", "scope:true", "default:sRGB_CTR.icm"]
    );
    // One observation and one independent readback, one fence check, one reply.
    assert_eq!(host.panel.topologies, 2);
    assert_eq!(host.fence_checks.load(Ordering::SeqCst), 1);
    assert_eq!(host.queries, 1);
    // Begin and complete both reached durable storage before returning.
    assert_eq!(rig.file.writes.load(Ordering::SeqCst), 2);
    let record = rig.file.record();
    assert_eq!(record["LastReconciledPowerSource"], 1);
    assert!(flag(&record, "ManualHold"));
    assert!(!flag(&record, "AutomaticWritesPaused"));
    assert!(!flag(&record, "UncertainColorProfile"));
    assert!(!rig.unresolved().color);
}

#[test]
fn active_profile_submits_nothing_like_managed() {
    let rig = Rig::new(Memory::default());
    let mut host = admitted(1);
    host.panel.system_default = Some(SRGB.into());
    // A full path naming the same file is the same profile (ProfilesMatch).
    for key in [SRGB, r"D:\elsewhere\SRGB_ctr.ICM"] {
        let outcome = rig.set(&mut host, 1, key).unwrap();
        assert_eq!(result(&outcome), (Status::Succeeded, None));
    }
    // Managed PrepareColorProfile returns Success(state) without a baseline.
    assert_eq!(host.interactions(), (0, 0));
    assert_eq!(host.panel.topologies, 2);
    let record = rig.file.record();
    assert!(!flag(&record, "AutomaticWritesPaused") && !flag(&record, "UncertainColorProfile"));

    // An interrupted reply query cannot make an unsubmitted no-op uncertain.
    let outcome = rig
        .set_canceled(&mut host, 1, SRGB, &AtomicBool::new(true))
        .unwrap();
    assert_eq!(result(&outcome), (Status::Succeeded, None));
    assert_eq!(outcome.state.color_profile(), None);
    assert_eq!(host.interactions(), (0, 0));
    assert!(!rig.unresolved().color);
}

#[test]
fn unlisted_hdr_and_unconfirmed_sdr_requests_fail_without_submission() {
    let rig = Rig::new(Memory::default());
    for key in ["Missing.icm", "sRGB", "sRGB_CTR.icc"] {
        let mut host = admitted(1);
        let outcome = rig.set(&mut host, 1, key).unwrap();
        assert_eq!(
            result(&outcome),
            (Status::Failed, Some(UNAVAILABLE)),
            "{key}"
        );
        assert_eq!(host.interactions(), (0, 0));
    }

    // Managed: color profiles are unavailable while HDR is on.
    let mut host = admitted(1);
    host.panel.hdr = true;
    let outcome = rig.set(&mut host, 1, SRGB).unwrap();
    assert_eq!(result(&outcome), (Status::Failed, Some(HDR_ON)));
    assert_eq!(host.interactions(), (0, 0));

    // Deliberate deviation: an unreadable advanced-color state is not SDR.
    let mut host = admitted(1);
    host.panel
        .topology_script
        .push_back(Scenario::HdrError.topology(false));
    let outcome = rig.set(&mut host, 1, SRGB).unwrap();
    assert_eq!(result(&outcome), (Status::Failed, Some(HDR_UNKNOWN)));
    assert_eq!(host.interactions(), (0, 0));

    let record = rig.file.record();
    assert!(!flag(&record, "AutomaticWritesPaused") && !flag(&record, "UncertainColorProfile"));
    assert!(!rig.unresolved().color);
}

#[test]
fn observation_read_and_monitor_failures_fail_without_submission() {
    let topologies: [(Result<InternalDisplayTopology, DisplayReadError>, &str); 5] = [
        (Scenario::Unavailable.topology(false), NO_INTERNAL),
        (
            Scenario::NativeError.topology(false),
            "Windows could not read the active display configuration: Win32 error 5.",
        ),
        (
            Ok(InternalDisplayTopology::MultipleInternalDisplays),
            MULTIPLE,
        ),
        (Err(DisplayReadError::TopologyChanging), CHANGING),
        (
            Err(DisplayReadError::Native {
                operation: Operation::BufferSizes,
                status: 31,
            }),
            "Windows could not size the active display configuration: Win32 error 31.",
        ),
    ];
    for (topology, expected) in topologies {
        let rig = Rig::new(Memory::default());
        let mut host = admitted(1);
        host.panel.topology_script.push_back(topology);
        let outcome = rig.set(&mut host, 1, SRGB).unwrap();
        assert_eq!(result(&outcome), (Status::Failed, Some(expected)));
        assert_eq!(host.interactions(), (0, 0));
        assert!(!rig.unresolved().color);
    }
    for (read, expected) in [
        (
            ProfileRead::Scope,
            "Windows could not determine the active color-profile scope",
        ),
        (
            ProfileRead::Associations,
            "Windows could not enumerate display color profiles",
        ),
        (
            ProfileRead::Default,
            "Windows could not read the default display color profile",
        ),
    ] {
        let rig = Rig::new(Memory::default());
        let mut host = admitted(1);
        host.panel.failing_read = Some(read);
        let outcome = rig.set(&mut host, 1, SRGB).unwrap();
        assert_eq!(result(&outcome), (Status::Failed, Some(expected)));
        assert_eq!(host.interactions(), (0, 0));
        assert!(!rig.unresolved().color);
    }
    for (monitors, expected) in [
        (
            vec![],
            "Windows did not report one active monitor device for the internal display.",
        ),
        (
            vec![(r"\\?\DISPLAY#OTHER", KEY, true)],
            "Windows did not report one active monitor device for the internal display.",
        ),
    ] {
        let rig = Rig::new(Memory::default());
        let mut host = admitted(1);
        host.panel.monitors = Some(monitors);
        let outcome = rig.set(&mut host, 1, SRGB).unwrap();
        assert_eq!(result(&outcome), (Status::Failed, Some(expected)));
        assert_eq!(host.interactions(), (0, 0));
    }
}

#[test]
fn fence_refusals_never_submit() {
    // Power admission precedes the attempt: `manual_write` tests it once.
    let rig = Rig::new(Memory::legacy(0));

    // Origin, session, power generation or Stop changed after preparation.
    let mut host = admitted(1);
    host.admit = false;
    let outcome = rig.set(&mut host, 1, SRGB).unwrap();
    assert_eq!(result(&outcome), (Status::Blocked, Some(STALE)));
    assert_eq!(host.interactions(), (1, 0));

    // The fence's fresh topology no longer shows the observed panel.
    let mut host = admitted(1);
    host.changed = true;
    let outcome = rig.set(&mut host, 1, SRGB).unwrap();
    assert_eq!(result(&outcome), (Status::Blocked, Some(PANEL_CHANGED)));
    assert_eq!(host.interactions(), (1, 0));

    // The observed panel was replaced before the fence's fresh read.
    let mut host = admitted(1);
    host.panel.replaced = true;
    let outcome = rig.set(&mut host, 1, SRGB).unwrap();
    assert_eq!(result(&outcome), (Status::Blocked, Some(PANEL_CHANGED)));
    assert_eq!(host.interactions(), (1, 0));

    // HDR turned on after the observation: refused at the fence, certain.
    let mut host = admitted(1);
    host.panel.fence_hdr = Some(true);
    let outcome = rig.set(&mut host, 1, SRGB).unwrap();
    assert_eq!(result(&outcome), (Status::Failed, Some(HDR_ON)));
    assert_eq!(host.interactions(), (1, 0));

    // Canceled before submission (client loss, deadline or Stop).
    let mut host = admitted(1);
    let outcome = rig
        .set_canceled(&mut host, 1, SRGB, &AtomicBool::new(true))
        .unwrap();
    assert_eq!(result(&outcome), (Status::Blocked, Some(CANCELED)));
    assert_eq!(host.interactions(), (0, 0));

    // Blocked completions restore the inherited (unpaused) state.
    let record = rig.file.record();
    assert!(!flag(&record, "AutomaticWritesPaused") && !flag(&record, "UncertainColorProfile"));
    assert!(!rig.unresolved().color);
    let mut host = admitted(1);
    assert_eq!(
        rig.set(&mut host, 1, SRGB).unwrap().status,
        Status::Succeeded
    );
}

#[test]
fn rejected_first_setter_is_uncertain_and_pauses_across_restart_until_explicit_read() {
    let rig = Rig::new(Memory::default());
    let mut host = admitted(2);
    host.panel.add_status = ACCESS_DENIED;
    host.panel.add_effect = false;
    let outcome = rig.set(&mut host, 2, SRGB).unwrap();
    // Managed: every failure inside the transaction may have submitted, and
    // reconciliation found nothing to restore.
    let expected = failed("Windows could not associate the color profile");
    assert_eq!(
        result(&outcome),
        (Status::Indeterminate, Some(expected.as_str()))
    );
    assert_eq!(host.panel.writes, ["add:sRGB_CTR.icm"]);
    let record = rig.file.record();
    assert!(flag(&record, "AutomaticWritesPaused") && flag(&record, "UncertainColorProfile"));
    assert!(!flag(&record, "UncertainHdr") && !flag(&record, "UncertainBrightness"));
    assert!(!flag(&record, "UncertainRefreshRate"));
    assert_eq!(rig.unresolved(), Fields::COLOR);

    // Same process: refused before any device interaction, never replayed,
    // with the reply query's snapshot.
    host.panel.add_status = 0;
    host.panel.add_effect = true;
    let (topologies, queries) = (host.panel.topologies, host.queries);
    let (checkpoint, record) = (rig.file.writes.load(Ordering::SeqCst), rig.file.record());
    let blocked = rig.set(&mut host, 2, SRGB).unwrap();
    assert_eq!(result(&blocked), (Status::Blocked, Some(PAUSED)));
    assert_eq!(host.queries, queries + 1);
    assert_eq!(
        serde_json::to_value(&blocked.state).unwrap(),
        serde_json::to_value(host.panel.reply()).unwrap()
    );
    assert_eq!(
        (host.panel.writes.len(), host.panel.topologies),
        (1, topologies)
    );
    // The gate precedes Begin: the paused record is not rewritten.
    assert_eq!(rig.file.writes.load(Ordering::SeqCst), checkpoint);
    assert_eq!(rig.file.record(), record);

    // Restart: the durable checkpoint restores the gate.
    let restarted = Rig::new(rig.file.clone());
    assert!(restarted.unresolved().color);
    let blocked = restarted.set(&mut host, 2, SRGB).unwrap();
    assert_eq!(result(&blocked), (Status::Blocked, Some(PAUSED)));
    assert_eq!(host.panel.writes.len(), 1);

    // An unavailable read observed nothing: the gate stays closed.
    restarted.explicit_read(&Snapshot::unavailable("gone"));
    assert!(restarted.unresolved().color);
    // The panel's own SDR read shows no active profile, which only a color
    // write can change: it reopens the native gate, and the managed pause
    // stays until a complete readback.
    let read = host.panel.reply();
    assert_eq!(read.color_profile(), None);
    restarted.explicit_read(&read);
    assert!(!restarted.unresolved().color);
    let record = restarted.file.record();
    assert!(flag(&record, "AutomaticWritesPaused") && flag(&record, "UncertainColorProfile"));
    // The repairing request is admitted; its confirmed result resumes.
    let outcome = restarted.set(&mut host, 2, SRGB).unwrap();
    assert_eq!(result(&outcome), (Status::Succeeded, None));
    assert_eq!(host.panel.writes.len(), 4);
    let record = restarted.file.record();
    assert!(!flag(&record, "AutomaticWritesPaused") && !flag(&record, "UncertainColorProfile"));
}

#[test]
fn mid_transaction_failure_reports_managed_restoration_without_replay() {
    // The rejected scope change rolls back the association it followed.
    let rig = Rig::new(Memory::default());
    let mut host = admitted(1);
    host.panel.scope_error = Some(5);
    let outcome = rig.set(&mut host, 1, SRGB).unwrap();
    let expected = failed("Windows could not change the color-profile scope (Win32 error 5)");
    assert_eq!(
        result(&outcome),
        (Status::Indeterminate, Some(expected.as_str()))
    );
    assert_eq!(
        host.panel.writes,
        ["add:sRGB_CTR.icm", "scope:true", "remove:sRGB_CTR.icm"]
    );
    assert!(host.panel.user.is_empty());
    assert_eq!(host.panel.scope, ProfileScope::SystemWide);
    assert_eq!(rig.unresolved(), Fields::COLOR);
    assert!(flag(&rig.file.record(), "UncertainColorProfile"));

    // Restoration that Windows does not confirm stops and says so.
    let rig = Rig::new(Memory::default());
    let mut host = admitted(1);
    host.panel.scope_error = Some(5);
    host.panel.remove_effect = false;
    let outcome = rig.set(&mut host, 1, SRGB).unwrap();
    assert_eq!(
        result(&outcome),
        (
            Status::Indeterminate,
            Some(
                "The color-profile change failed: Windows could not change the color-profile \
                 scope (Win32 error 5) The previous color-profile configuration could not be \
                 restored: Windows did not confirm removal of the color-profile association \
                 and default."
            )
        )
    );
    assert_eq!(
        host.panel.writes,
        ["add:sRGB_CTR.icm", "scope:true", "remove:sRGB_CTR.icm"]
    );
    assert_eq!(rig.unresolved(), Fields::COLOR);
}

#[test]
fn readback_that_does_not_confirm_is_uncertain_and_restored() {
    // The independent observation reads the same panel with HDR on.
    let rig = Rig::new(Memory::default());
    let mut host = admitted(1);
    let observed = host.panel.topology();
    host.panel.topology_script.push_back(observed);
    host.panel
        .topology_script
        .push_back(Scenario::Available.topology_with_hdr(true, RefreshTiming::default(), false));
    let outcome = rig.set(&mut host, 1, SRGB).unwrap();
    let expected = failed(UNCONFIRMED);
    assert_eq!(
        result(&outcome),
        (Status::Indeterminate, Some(expected.as_str()))
    );
    assert_eq!(
        host.panel.writes,
        [
            "add:sRGB_CTR.icm",
            "scope:true",
            "default:sRGB_CTR.icm",
            "remove:sRGB_CTR.icm",
            "scope:false",
        ]
    );
    assert_eq!(host.panel.active(), None);
    assert_eq!(rig.unresolved(), Fields::COLOR);
}

#[test]
fn readback_that_does_not_reidentify_the_panel_is_indeterminate_without_restoration() {
    // Another panel, then no internal panel: neither is restored against.
    for (readback, reason) in [
        (
            Scenario::Available.topology_with_hdr(false, RefreshTiming::default(), true),
            PANEL_CHANGED_AFTER,
        ),
        (Scenario::Unavailable.topology(false), NO_INTERNAL),
    ] {
        let rig = Rig::new(Memory::default());
        let mut host = admitted(1);
        let observed = host.panel.topology();
        host.panel.topology_script.push_back(observed);
        host.panel.topology_script.push_back(readback);
        let outcome = rig.set(&mut host, 1, SRGB).unwrap();
        let expected = format!("{UNVERIFIED} {reason}");
        assert_eq!(
            result(&outcome),
            (Status::Indeterminate, Some(expected.as_str()))
        );
        assert_eq!(
            host.panel.writes,
            ["add:sRGB_CTR.icm", "scope:true", "default:sRGB_CTR.icm"]
        );
        assert_eq!(host.panel.active().as_deref(), Some(OsStr::new(SRGB)));
        assert_eq!(rig.unresolved(), Fields::COLOR);
    }
}

#[test]
fn reply_that_does_not_show_a_confirmed_change_is_indeterminate() {
    let rig = Rig::new(Memory::default());
    let mut host = admitted(1);
    host.panel.reply_active = Some(Some(NATIVE));
    let outcome = rig.set(&mut host, 1, SRGB).unwrap();
    assert_eq!(result(&outcome), (Status::Indeterminate, Some(UNCONFIRMED)));
    assert_eq!(host.panel.writes.len(), 3);
    assert!(rig.unresolved().color);
}

#[test]
fn cancellation_after_the_first_setter_completes_the_transaction_without_replay() {
    let rig = Rig::new(Memory::default());
    let mut host = admitted(1);
    let canceled = Arc::new(AtomicBool::new(false));
    host.panel.cancel_on_write = Some(canceled.clone());
    let outcome = rig.set_canceled(&mut host, 1, SRGB, &canceled).unwrap();
    // Managed keeps ownership through readback once a setter ran; the
    // interrupted reply query cannot show the change, so it is uncertain.
    assert_eq!(host.panel.writes.len(), 3);
    assert_eq!(host.panel.topologies, 2);
    assert_eq!(result(&outcome), (Status::Indeterminate, Some(UNCONFIRMED)));
    assert_eq!(outcome.state.color_profile(), None);
    assert!(rig.unresolved().color);
    host.panel.cancel_on_write = None;
    let blocked = rig.set(&mut host, 1, NATIVE).unwrap();
    assert_eq!(result(&blocked), (Status::Blocked, Some(PAUSED)));
    assert_eq!(host.panel.writes.len(), 3);
}

#[test]
fn color_uncertainty_gates_only_color_and_other_gates_never_block_it() {
    // A color Indeterminate leaves HDR, refresh and brightness writable.
    let rig = Rig::new(Memory::default());
    let mut host = admitted(1);
    host.panel.reply_active = Some(None);
    assert_eq!(
        rig.set(&mut host, 1, SRGB).unwrap().status,
        Status::Indeterminate
    );
    assert_eq!(rig.unresolved(), Fields::COLOR);
    let record = rig.file.record();
    assert!(!flag(&record, "UncertainHdr") && !flag(&record, "UncertainBrightness"));
    assert!(!flag(&record, "UncertainRefreshRate"));

    // A paused managed checkpoint with every other field uncertain does not
    // gate a color write; the managed pause keeps every affected field.
    let file = Memory::with(
        r#"{"SchemaVersion":4,"LastReconciledPowerSource":1,"ManualHold":true,"AutomaticWritesPaused":true,"UncertainRefreshRate":true,"UncertainHdr":true,"UncertainBrightness":true}"#,
    );
    let rig = Rig::new(file);
    let others = Fields {
        refresh: true,
        hdr: true,
        brightness: true,
        ..Fields::default()
    };
    assert_eq!(rig.unresolved(), others);
    let mut host = admitted(1);
    let outcome = rig.set(&mut host, 1, SRGB).unwrap();
    assert_eq!(result(&outcome), (Status::Succeeded, None));
    assert_eq!(host.panel.writes.len(), 3);
    assert_eq!(rig.unresolved(), others);
    let record = rig.file.record();
    assert!(flag(&record, "AutomaticWritesPaused"));
    assert!(flag(&record, "UncertainColorProfile") && flag(&record, "UncertainHdr"));

    // An available read without brightness clears only the color gate.
    let file = Memory::with(
        r#"{"SchemaVersion":4,"LastReconciledPowerSource":1,"ManualHold":true,"AutomaticWritesPaused":true,"UncertainColorProfile":true,"UncertainBrightness":true}"#,
    );
    let rig = Rig::new(file);
    assert_eq!(
        rig.set(&mut admitted(1), 1, SRGB).unwrap().error.as_deref(),
        Some(PAUSED)
    );
    rig.explicit_read(&snapshot(false, None));
    assert_eq!(rig.unresolved(), Fields::BRIGHTNESS);
    assert!(flag(&rig.file.record(), "AutomaticWritesPaused"));
}

#[test]
fn an_hdr_on_read_resumes_color_and_color_writes_then_fail_like_managed() {
    let rig = Rig::new(Memory::default());
    let mut host = admitted(1);
    host.panel.reply_active = Some(None);
    assert_eq!(
        rig.set(&mut host, 1, SRGB).unwrap().status,
        Status::Indeterminate
    );
    assert!(rig.unresolved().color);
    // HDR turned on elsewhere: managed HasCompleteReadback accepts the color
    // field of an HDR-on read, so both the gate and the pause resume.
    host.panel.hdr = true;
    host.panel.reply_active = None;
    let read = host.panel.reply();
    assert_eq!(read.color_profile(), None);
    rig.explicit_read(&read);
    assert!(!rig.unresolved().color);
    let record = rig.file.record();
    assert!(!flag(&record, "AutomaticWritesPaused") && !flag(&record, "UncertainColorProfile"));
    // Managed PrepareColorProfile refuses while HDR is on.
    let writes = host.panel.writes.len();
    let outcome = rig.set(&mut host, 1, NATIVE).unwrap();
    assert_eq!(result(&outcome), (Status::Failed, Some(HDR_ON)));
    assert_eq!(host.panel.writes.len(), writes);
    assert!(!flag(&rig.file.record(), "AutomaticWritesPaused"));
}

/// Every writer refusal before the first setter, with its managed status and text.
#[test]
fn refusals_map_to_managed_statuses_and_texts() {
    let attempt = |reason| match refused(reason) {
        Attempt::Failed {
            error,
            reply_resolves,
        } => {
            // Managed color profiles never reconcile a failure with the reply.
            assert!(!reply_resolves);
            (Status::Failed, error.into_owned())
        }
        Attempt::Done(status, error) => (status, error.into_owned()),
        Attempt::Unchanged | Attempt::Confirmed => panic!("a refusal is not a result"),
    };
    let read = |operation| {
        ColorRefusal::Read(ProfileReadError::Native {
            operation,
            hresult: ACCESS_DENIED,
        })
    };
    for (reason, status, text) in [
        (
            ColorRefusal::Topology(DisplayReadError::TopologyChanging),
            Status::Failed,
            CHANGING,
        ),
        (ColorRefusal::NoInternalDisplay, Status::Failed, NO_INTERNAL),
        (
            ColorRefusal::MultipleInternalDisplays,
            Status::Failed,
            MULTIPLE,
        ),
        (
            read(ProfileRead::Scope),
            Status::Failed,
            "Windows could not determine the active color-profile scope",
        ),
        (
            read(ProfileRead::Associations),
            Status::Failed,
            "Windows could not enumerate display color profiles",
        ),
        (
            read(ProfileRead::Default),
            Status::Failed,
            "Windows could not read the default display color profile",
        ),
        (
            ColorRefusal::Read(ProfileReadError::UnknownScope(7)),
            Status::Failed,
            "Windows could not determine the active color-profile scope",
        ),
        (
            ColorRefusal::Read(ProfileReadError::InvalidResponse(ProfileRead::Default)),
            Status::Failed,
            "Windows could not read the default display color profile",
        ),
        (ColorRefusal::HdrEnabled, Status::Failed, HDR_ON),
        (ColorRefusal::HdrUnknown, Status::Failed, HDR_UNKNOWN),
        (
            ColorRefusal::ProfileUnavailable,
            Status::Failed,
            UNAVAILABLE,
        ),
        (
            ColorRefusal::Monitor(MonitorKeyError::NoDevicePath),
            Status::Failed,
            "Windows did not report a monitor device path for the internal display.",
        ),
        (
            ColorRefusal::Monitor(MonitorKeyError::NoActiveMonitor),
            Status::Failed,
            "Windows did not report one active monitor device for the internal display.",
        ),
        (
            ColorRefusal::Monitor(MonitorKeyError::MultipleActiveMonitors),
            Status::Failed,
            "Windows reported more than one active monitor device for the internal display.",
        ),
        (
            ColorRefusal::Monitor(MonitorKeyError::NoDeviceKey),
            Status::Failed,
            "Windows did not report a device key for the active internal display.",
        ),
        (ColorRefusal::PanelChanged, Status::Blocked, PANEL_CHANGED),
        (ColorRefusal::AdmissionDenied, Status::Blocked, STALE),
        (ColorRefusal::Cancelled, Status::Blocked, CANCELED),
    ] {
        assert_eq!(attempt(reason), (status, text.to_owned()));
    }
}

/// Every unverified readback keeps its reason after the plain sentence.
#[test]
fn unverified_readbacks_name_their_reason() {
    for (reason, text) in [
        (
            Unverified::Topology(DisplayReadError::TopologyChanging),
            CHANGING,
        ),
        (
            Unverified::Topology(DisplayReadError::Native {
                operation: Operation::ActivePaths,
                status: 31,
            }),
            "Windows could not read the active display configuration: Win32 error 31.",
        ),
        (Unverified::NoInternalDisplay, NO_INTERNAL),
        (Unverified::MultipleInternalDisplays, MULTIPLE),
        (Unverified::PanelChanged, PANEL_CHANGED_AFTER),
        (Unverified::HdrUnknown, HDR_UNKNOWN),
    ] {
        assert_eq!(unverified_text(reason), text);
    }
}

/// Every transaction fault, with the managed exception message it becomes.
#[test]
fn faults_compose_the_managed_transaction_messages() {
    let read = ProfileReadError::Native {
        operation: ProfileRead::Associations,
        hresult: ACCESS_DENIED,
    };
    for (fault, text) in [
        (
            Fault::Read(read),
            "Windows could not enumerate display color profiles",
        ),
        (
            Fault::AssociateRejected(ACCESS_DENIED),
            "Windows could not associate the color profile",
        ),
        (
            Fault::RemoveRejected(ACCESS_DENIED),
            "Windows could not remove the color-profile association",
        ),
        (
            Fault::DefaultRejected(ACCESS_DENIED),
            "Windows could not make the color profile active",
        ),
        (
            Fault::ScopeRejected(1168),
            "Windows could not change the color-profile scope (Win32 error 1168)",
        ),
        (
            Fault::AssociationUnconfirmed,
            "Windows did not confirm the color-profile association.",
        ),
        (
            Fault::RemovalUnconfirmed,
            "Windows did not confirm removal of the color-profile association and default.",
        ),
        (
            Fault::DefaultUnconfirmed,
            "Windows did not confirm the requested default color profile.",
        ),
        (
            Fault::ScopeUnconfirmed,
            "Windows did not confirm the requested color-profile scope.",
        ),
        (
            Fault::Readback(Readback::Read(read)),
            "Windows could not enumerate display color profiles",
        ),
        (Fault::Readback(Readback::Mismatch), UNCONFIRMED),
        (
            Fault::AssociationsChanged,
            "The current-user color-profile associations changed unexpectedly; restoration \
             stopped without altering them.",
        ),
        (
            Fault::NullDefault,
            "The previous null default cannot be restored without changing a pre-existing \
             association.",
        ),
        (
            Fault::RestorationUnconfirmed,
            "Windows did not confirm restoration of the previous color-profile configuration.",
        ),
    ] {
        assert_eq!(fault_text(fault), text);
    }
    assert_eq!(
        uncertain(Fault::AssociateRejected(ACCESS_DENIED), Ok(())),
        failed("Windows could not associate the color profile")
    );
    assert_eq!(
        uncertain(Fault::DefaultRejected(1), Err(Fault::NullDefault)),
        "The color-profile change failed: Windows could not make the color profile active \
         The previous color-profile configuration could not be restored: The previous null \
         default cannot be restored without changing a pre-existing association."
    );
    // The reply predicate uses managed ProfilesMatch on the active profile.
    let request = SetColorProfile {
        expected_power_source: 1,
        key: SRGB.into(),
    };
    let shown = |state: &Snapshot| ManualRequest::<Host<Panel>>::shown_by(&request, state);
    assert!(shown(&snapshot(false, Some(r"C:\color\SRGB_CTR.ICM"))));
    assert!(!shown(&snapshot(false, Some(NATIVE))));
    assert!(!shown(&snapshot(false, None)));
    assert!(!shown(&snapshot(true, Some(SRGB))));
    assert!(!shown(&Snapshot::unavailable("gone")));
}

/// A managed checkpoint that owns a color baseline and paused writes after an
/// uncertain color change.
const OWNED_PAUSED: &str = r#"{"SchemaVersion":4,"LastReconciledPowerSource":1,"ApplicationProfileId":"9f876742-1234-4321-abcd-d3ea3e1cf102","Baseline":{"RefreshRateKey":"fixed-60","HdrEnabled":null,"ColorProfileKey":"Native_CTR.icm","BrightnessPercent":40},"OwnedRefreshRate":true,"OwnedColorProfile":true,"ManualHold":false,"AutomaticWritesPaused":true,"UncertainColorProfile":true}"#;

#[test]
fn the_pause_gate_precedes_the_checkpoint_begin() {
    // A paused request is refused before Begin: the managed record, with its
    // baseline and ownership, stays byte for byte and nothing is written.
    let file = Memory::with(OWNED_PAUSED);
    let rig = Rig::new(file);
    assert_eq!(rig.unresolved(), Fields::COLOR);
    let mut host = admitted(1);
    for _ in 0..2 {
        let blocked = rig.set(&mut host, 1, SRGB).unwrap();
        assert_eq!(result(&blocked), (Status::Blocked, Some(PAUSED)));
        assert_eq!(rig.file.writes.load(Ordering::SeqCst), 0);
        assert_eq!(
            rig.file.bytes.lock().unwrap().as_deref(),
            Some(OWNED_PAUSED.as_bytes())
        );
        assert_eq!(host.interactions(), (0, 0));
        assert_eq!(host.panel.topologies, 0);
    }
    // After the reopening read, the admitted request begins like managed
    // Begin: it clears the baseline and ownership, then resumes on success.
    rig.explicit_read(&host.panel.reply());
    let outcome = rig.set(&mut host, 1, SRGB).unwrap();
    assert_eq!(result(&outcome), (Status::Succeeded, None));
    let record = rig.file.record();
    assert!(record["Baseline"].is_null());
    assert!(!flag(&record, "OwnedColorProfile") && !flag(&record, "AutomaticWritesPaused"));
}
