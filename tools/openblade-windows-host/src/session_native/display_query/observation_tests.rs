use super::*;
use openblade_platform_windows::{
    color_profile::{ColorProfile as ObservedProfile, ProfileRead, ProfileScope},
    display::scripted::{RefreshTiming, Scenario},
};
use std::{ffi::OsString, os::windows::ffi::OsStringExt};

// Scripted CCD packets construct observations only in tests. WCS, HDR flags
// and the sequence of independent observations are controlled here.
struct Fixture<'a> {
    case: &'a str,
    cancel_after_profiles: Option<&'a AtomicBool>,
    topology_reads: usize,
    brightness_reads: usize,
    brightness_owner: Option<SharedBrightness>,
    profile_reads: Vec<HdrState>,
}

impl<'a> Fixture<'a> {
    fn new(case: &'a str) -> Self {
        Self {
            case,
            cancel_after_profiles: None,
            topology_reads: 0,
            brightness_reads: 0,
            brightness_owner: None,
            profile_reads: Vec::new(),
        }
    }

    fn scenario(&self) -> Scenario {
        if self.case == "hdr-unknown" {
            Scenario::HdrError
        } else {
            Scenario::Available
        }
    }

    fn panel(&self, panel: &mut ActiveInternalDisplay) {
        // The default scripted packets report split 60/240 timings. Model a
        // fixed 240/240 observation except in the unknown-refresh case.
        if self.case != "missing-refresh" {
            panel.desktop_rate = panel.physical_rate;
        }
        if self.case == "missing-identity" {
            panel.monitor_name.as_mut().unwrap().device_path = None;
        }
    }
}

impl DisplayReads for Fixture<'_> {
    fn brightness(
        &mut self,
        panel: &ActiveInternalDisplay,
        canceled: &AtomicBool,
    ) -> io::Result<Result<BrightnessSnapshot, BrightnessError>> {
        self.brightness_reads += 1;
        if let Some(owner) = &self.brightness_owner {
            let target = BrightnessQueryTarget::from_display(panel).unwrap();
            return owner.lock().unwrap().read(target, canceled);
        }
        if !self.case.starts_with("brightness-") {
            return Ok(Err(BrightnessError::ReadingUnavailable));
        }
        let target = BrightnessQueryTarget::from_display(panel).unwrap();
        let native = QueryWmi::new(self.case, panel);
        let worker = WindowsBrightnessWorker::start_scripted(move || native)?;
        let mut owner = BrightnessOwner::with_worker(worker);
        let result = owner.read(target, canceled);
        owner.shutdown()?;
        result
    }

    fn refresh(&mut self) -> Result<InternalDisplayRefresh, DisplayReadError> {
        match self.case {
            "no-panel" => return Ok(InternalDisplayRefresh::NoInternalDisplay),
            "empty" => return Ok(InternalDisplayRefresh::Empty),
            "ambiguous" => return Ok(InternalDisplayRefresh::MultipleInternalDisplays),
            "read-failure" => return Err(DisplayReadError::CurrentModeUnavailable),
            _ => {}
        }
        let mut observed = self.scenario().read_with_hdr(
            self.case == "hdr-on",
            RefreshTiming::default(),
            false,
        )?;
        let InternalDisplayRefresh::Single(refresh) = &mut observed else {
            panic!("synthetic internal panel expected")
        };
        self.panel(&mut refresh.display);
        if self.case != "missing-refresh" {
            refresh.current_mode_hertz = 240;
            refresh.current_refresh_key = Some("fixed-240".into());
            refresh.message = None;
        }
        Ok(observed)
    }

    fn topology(&mut self) -> Result<InternalDisplayTopology, DisplayReadError> {
        self.topology_reads += 1;
        let replaced = self.case == "changed-before-wcs"
            || (self.case == "topology-change" && self.topology_reads == 2);
        let hdr = self.case == "hdr-on" || (self.case == "hdr-change" && self.topology_reads == 2);
        let mut observed =
            self.scenario()
                .topology_with_hdr(hdr, RefreshTiming::default(), replaced)?;
        if let InternalDisplayTopology::Single(panel) = &mut observed {
            self.panel(panel);
            if self.case == "monitor-change" && self.topology_reads == 2 {
                panel.monitor_name.as_mut().unwrap().device_path = Some("REPLACEMENT".into());
            }
        }
        Ok(observed)
    }

    fn profiles(
        &mut self,
        panel: &ActiveInternalDisplay,
        hdr: HdrState,
    ) -> Result<ColorProfileSnapshot, ProfileReadError> {
        assert_eq!(panel.target.id, 4);
        self.profile_reads.push(hdr);
        if let Some(canceled) = self.cancel_after_profiles {
            canceled.store(true, Ordering::Release);
        }
        if self.case == "wcs-failure" {
            return Err(ProfileReadError::InvalidResponse(ProfileRead::Scope));
        }
        let id = match self.case {
            "oversized-key" => OsString::from("😀".repeat(MAX_KEY / 2 + 1)),
            "invalid-utf16" => OsString::from_wide(&[0xd800]),
            _ => OsString::from("Synthetic.icm"),
        };
        let count = match self.case {
            "absent-profiles" => 0,
            "oversized-list" => MAX_OPTIONS + 1,
            _ => 1,
        };
        let active = count != 0 && hdr == HdrState::Off;
        let profiles = (0..count)
            .map(|_| ObservedProfile {
                id: id.clone(),
                name: String::from("Synthetic"),
                active,
            })
            .collect();
        Ok(ColorProfileSnapshot {
            scope: ProfileScope::CurrentUser,
            hdr,
            selected: active.then_some(id),
            profiles,
        })
    }
}

pub(in super::super) fn fixture_query(case: &str) -> Snapshot {
    observe(&mut Fixture::new(case), &AtomicBool::new(false)).unwrap()
}

pub(in super::super) fn fixture_query_owned(
    owner: SharedBrightness,
    canceled: &AtomicBool,
) -> io::Result<Snapshot> {
    let mut reads = Fixture::new("available");
    reads.brightness_owner = Some(owner);
    observe(&mut reads, canceled)
}

#[test]
fn projects_observed_refresh_hdr_icc_and_missing_brightness() {
    let snapshot = fixture_query("available");
    assert!(snapshot.valid() && snapshot.is_available);
    assert_eq!(snapshot.display_name.as_deref(), Some("Synthetic panel"));
    assert_eq!((snapshot.width, snapshot.height), (2560, 1600));
    assert_eq!(snapshot.current_refresh_key.as_deref(), Some("fixed-240"));
    assert!(
        snapshot
            .refresh_options
            .iter()
            .any(|o| o.key == "fixed-240")
    );
    assert!(snapshot.hdr_supported && !snapshot.hdr_enabled);
    assert!(snapshot.color_profiles_enabled);
    assert_eq!(
        snapshot.active_color_profile_id.as_deref(),
        Some("Synthetic.icm")
    );
    assert!(snapshot.color_profiles[0].is_active);
    assert_eq!(snapshot.bits_per_color_channel, Some(10));
    assert_eq!(snapshot.brightness_percent, None);
    assert!(snapshot.supported_brightness_levels.is_empty());
}

#[test]
fn hdr_on_enumerates_associations_without_an_active_sdr_default() {
    let mut fixture = Fixture::new("hdr-on");
    let snapshot = observe(&mut fixture, &AtomicBool::new(false)).unwrap();
    assert_eq!(fixture.profile_reads, [HdrState::On]);
    assert!(snapshot.is_available && snapshot.hdr_enabled);
    assert!(!snapshot.color_profiles_enabled);
    assert!(snapshot.active_color_profile_id.is_none());
    assert_eq!(snapshot.color_profiles.len(), 1);
    assert!(!snapshot.color_profiles[0].is_active);
}

#[test]
fn rejects_changed_target_monitor_or_hdr_around_wcs() {
    for (case, expected_profiles) in [
        ("changed-before-wcs", 0),
        ("topology-change", 1),
        ("monitor-change", 1),
        ("hdr-change", 1),
    ] {
        let mut fixture = Fixture::new(case);
        let snapshot = observe(&mut fixture, &AtomicBool::new(false)).unwrap();
        assert_eq!(fixture.profile_reads.len(), expected_profiles, "{case}");
        assert!(!snapshot.is_available, "{case}");
        assert_eq!(snapshot.message.as_deref(), Some(CHANGED), "{case}");
        assert!(snapshot.active_color_profile_id.is_none(), "{case}");
    }
}

#[test]
fn missing_ambiguous_failed_and_unknown_hdr_observations_skip_wcs() {
    for case in [
        "empty",
        "no-panel",
        "ambiguous",
        "missing-identity",
        "read-failure",
        "hdr-unknown",
    ] {
        let mut fixture = Fixture::new(case);
        let snapshot = observe(&mut fixture, &AtomicBool::new(false)).unwrap();
        assert!(snapshot.valid() && !snapshot.is_available, "{case}");
        assert!(snapshot.message.is_some(), "{case}");
        assert!(fixture.profile_reads.is_empty(), "{case}");
        if case == "hdr-unknown" {
            assert_eq!(snapshot.message.as_deref(), Some(HDR_UNKNOWN));
        }
    }
}

#[test]
fn absent_profiles_and_missing_refresh_key_remain_independent_observations() {
    let snapshot = fixture_query("absent-profiles");
    assert!(snapshot.is_available && snapshot.color_profiles_enabled);
    assert!(snapshot.color_profiles.is_empty() && snapshot.active_color_profile_id.is_none());
    let snapshot = fixture_query("missing-refresh");
    assert!(snapshot.is_available);
    assert!(snapshot.current_refresh_key.is_none() && snapshot.message.is_some());
    assert!(!snapshot.refresh_options.is_empty());
    assert!(snapshot.active_color_profile_id.is_some());
    assert!(!fixture_query("wcs-failure").is_available);
}

#[test]
fn rejects_oversized_counts_invalid_utf16_and_oversized_utf16_fields() {
    for case in ["oversized-key", "oversized-list", "invalid-utf16"] {
        let snapshot = fixture_query(case);
        assert!(snapshot.valid() && !snapshot.is_available, "{case}");
        assert_eq!(snapshot.message.as_deref(), Some(OUT_OF_BOUNDS));
    }
    let mut snapshot = fixture_query("available");
    snapshot.message = Some("😀".repeat(MAX_TEXT / 2));
    snapshot.current_refresh_key = Some("😀".repeat(MAX_KEY / 2));
    assert!(snapshot.valid());
    snapshot.message.as_mut().unwrap().push('a');
    assert!(!snapshot.valid());
    snapshot.message = None;
    snapshot.current_refresh_key.as_mut().unwrap().push('a');
    assert!(!snapshot.valid());
    for key in ["", " trailing", "key\u{0007}", "ends\u{00a0}"] {
        assert!(!valid_key(key));
    }
    snapshot.current_refresh_key = None;
    snapshot.width = -1;
    assert!(!snapshot.valid());
}

#[test]
fn canceled_between_suppliers_skips_the_next_read() {
    let canceled = AtomicBool::new(false);
    let mut fixture = Fixture::new("available");
    fixture.cancel_after_profiles = Some(&canceled);
    let err = observe(&mut fixture, &canceled).unwrap_err();
    assert_eq!(err.kind(), io::ErrorKind::Interrupted);
    assert_eq!(fixture.topology_reads, 1);
    assert_eq!(fixture.brightness_reads, 0);
}

// The real query worker and read policy run against injected CCD/WMI data.
// A mutation attempt fails immediately rather than being simulated as success.
use openblade_platform_windows::brightness::{
    Acknowledgement, CallBudget, MethodRow, NativeFailure, ReadingRow, Submission, TopologyFailure,
    WmiBrightnessNative,
};

struct QueryWmi {
    case: String,
    rows: Option<Vec<ReadingRow>>,
    failure: bool,
    changed: bool,
    topology_reads: usize,
}
impl QueryWmi {
    fn new(case: &str, panel: &ActiveInternalDisplay) -> Self {
        let path = panel
            .monitor_name
            .as_ref()
            .unwrap()
            .device_path
            .as_ref()
            .unwrap()
            .to_str()
            .unwrap();
        let instance = path
            .strip_prefix(r"\\?\")
            .unwrap_or(path)
            .split('#')
            .take(3)
            .collect::<Vec<_>>()
            .join(r"\");
        let row = |percent, levels, foreign: bool| ReadingRow {
            instance: Some(if foreign {
                String::from(r"DISPLAY\FOREIGN\1_0")
            } else {
                format!("{instance}_0")
            }),
            percent,
            levels,
            data_failure: None,
        };
        let percent = if case == "brightness-zero" { 0 } else { 100 };
        let levels = if case == "brightness-all101" {
            (0..=100).rev().chain([50, 255]).collect()
        } else {
            vec![100, 0, 50, 50, 255]
        };
        let rows = match case {
            "brightness-missing" => vec![],
            "brightness-foreign" => vec![row(Some(50), Some(vec![0, 50, 100]), true)],
            "brightness-malformed" => vec![row(None, Some(vec![0, 100]), false)],
            "brightness-invalid-percent" => vec![row(Some(101), Some(vec![0, 100]), false)],
            "brightness-empty-levels" => vec![row(Some(50), Some(vec![255]), false)],
            "brightness-ambiguous" => vec![
                row(Some(10), Some(vec![0, 100]), false),
                row(Some(90), Some(vec![0, 100]), false),
            ],
            "brightness-valid-and-malformed" => vec![
                row(None, None, false),
                row(Some(100), Some(vec![0, 100]), false),
            ],
            _ => vec![row(Some(percent), Some(levels), false)],
        };
        Self {
            case: case.into(),
            rows: Some(rows),
            failure: case == "brightness-wmi-failure",
            changed: case == "brightness-target-changed",
            topology_reads: 0,
        }
    }
}
impl WmiBrightnessNative for QueryWmi {
    type Method = ();
    fn topology(
        &mut self,
        budget: &CallBudget<'_>,
    ) -> Result<InternalDisplayTopology, TopologyFailure> {
        budget.remaining().map_err(TopologyFailure::Native)?;
        self.topology_reads += 1;
        let InternalDisplayTopology::Single(mut panel) =
            Fixture::new(&self.case).topology().unwrap()
        else {
            panic!("panel")
        };
        if self.changed && self.topology_reads == 2 {
            panel.target.id += 1;
        }
        Ok(InternalDisplayTopology::Single(panel))
    }
    fn active_readings(
        &mut self,
        budget: &CallBudget<'_>,
        _limit: usize,
    ) -> Result<Vec<ReadingRow>, NativeFailure> {
        budget.remaining()?;
        if self.failure {
            Err(NativeFailure::Status(0x80041003_u32 as i32))
        } else {
            Ok(self.rows.take().unwrap())
        }
    }
    fn active_methods(
        &mut self,
        _: &CallBudget<'_>,
        _: usize,
    ) -> Result<Vec<MethodRow<()>>, NativeFailure> {
        panic!("query reached mutation method enumeration")
    }
    fn set_brightness(
        &mut self,
        _: (),
        _: u8,
        _: &mut Submission<'_>,
    ) -> Result<Acknowledgement, NativeFailure> {
        panic!("query reached setter")
    }
}

#[test]
fn owned_targeted_brightness_projects_boundaries_and_all_levels() {
    for (case, percent, levels) in [
        ("brightness-zero", 0, vec![0, 50, 100]),
        ("brightness-hundred", 100, vec![0, 50, 100]),
        ("brightness-all101", 100, (0..=100).collect()),
        ("brightness-valid-and-malformed", 100, vec![0, 100]),
    ] {
        let snapshot = fixture_query(case);
        assert!(snapshot.valid() && snapshot.is_available, "{case}");
        assert_eq!(snapshot.brightness_percent, Some(percent), "{case}");
        assert_eq!(snapshot.supported_brightness_levels, levels, "{case}");
        assert_eq!(
            snapshot.active_color_profile_id.as_deref(),
            Some("Synthetic.icm")
        );
    }
}
#[test]
fn failed_wmi_retains_other_fields_but_target_replacement_discards_snapshot() {
    for case in [
        "brightness-missing",
        "brightness-foreign",
        "brightness-malformed",
        "brightness-invalid-percent",
        "brightness-empty-levels",
        "brightness-ambiguous",
        "brightness-wmi-failure",
    ] {
        let snapshot = fixture_query(case);
        assert!(snapshot.valid() && snapshot.is_available, "{case}");
        assert_eq!((snapshot.width, snapshot.height), (2560, 1600));
        assert_eq!(snapshot.current_refresh_key.as_deref(), Some("fixed-240"));
        assert!(snapshot.hdr_supported && snapshot.color_profiles_enabled);
        assert_eq!(
            snapshot.active_color_profile_id.as_deref(),
            Some("Synthetic.icm")
        );
        assert_eq!(snapshot.brightness_percent, None, "{case}");
        assert!(snapshot.supported_brightness_levels.is_empty(), "{case}");
    }
    let snapshot = fixture_query("brightness-target-changed");
    assert!(!snapshot.is_available);
    assert_eq!(snapshot.message.as_deref(), Some(CHANGED));
}
#[test]
fn snapshot_rejects_unpaired_out_of_range_or_unnormalized_brightness() {
    let mut snapshot = fixture_query("brightness-zero");
    snapshot.brightness_percent = Some(101);
    assert!(!snapshot.valid());
    snapshot.brightness_percent = Some(0);
    snapshot.supported_brightness_levels = vec![0, 0, 100];
    assert!(!snapshot.valid());
    snapshot.supported_brightness_levels = vec![100, 0];
    assert!(!snapshot.valid());
    snapshot.supported_brightness_levels = vec![0, 101];
    assert!(!snapshot.valid());
    snapshot.supported_brightness_levels.clear();
    assert!(!snapshot.valid());
    snapshot.brightness_percent = None;
    assert!(snapshot.valid());
}
