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
    profile_reads: Vec<HdrState>,
}

impl<'a> Fixture<'a> {
    fn new(case: &'a str) -> Self {
        Self {
            case,
            cancel_after_profiles: None,
            topology_reads: 0,
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
}
