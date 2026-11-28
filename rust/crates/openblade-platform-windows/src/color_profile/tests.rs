use std::{
    cell::Cell,
    ffi::{OsStr, OsString},
};

use crate::display::{
    ActiveInternalDisplay, DisplayReadError, EndpointId, InternalDisplayTopology,
};

use super::{
    HdrState, Monitor, ProfileRead, ProfileReadError, ProfileReads, ProfileScope,
    native::returned,
    profile_ids_equal, profiles_match, query_color_profiles, snapshot,
    transaction::{
        CurrentUserChange, Disposition, Failure, Outcome, ScriptedOperations, SetColorProfile,
        set_color_profile as guarded_set_color_profile,
    },
    valid_monitor_rgb_header,
};

const NATIVE: &str = "Native_CTR.icm";
const FACTORY: &str = "Blade.icm";
const MONITOR: &str = r"\\?\DISPLAY#RAZER#INTERNAL";
const KEY: &str = r"\Registry\Machine\System\Monitor\0001";
const ACCESS_DENIED: i32 = 0x8007_0005u32 as i32;

fn display() -> ActiveInternalDisplay {
    let mut display = ActiveInternalDisplay::brightness_fixture(MONITOR, 32);
    display.source = EndpointId {
        adapter_low: 11,
        adapter_high: -1,
        id: 12,
    };
    display.target = EndpointId {
        adapter_low: 31,
        adapter_high: 1,
        id: 32,
    };
    display
}

fn names(list: &[&str]) -> Vec<OsString> {
    list.iter().map(OsString::from).collect()
}

fn set_color_profile(
    request: SetColorProfile<'_>,
    api: &mut Script,
    topology: impl FnMut() -> Result<InternalDisplayTopology, DisplayReadError>,
    hdr: impl FnMut(&ActiveInternalDisplay) -> HdrState,
) -> Result<Outcome, super::transaction::Rejected> {
    guarded_set_color_profile(request, api, topology, hdr, &|| true, |_, _, _| Ok(()))
}

fn sdr() -> Result<InternalDisplayTopology, DisplayReadError> {
    Ok(InternalDisplayTopology::Single(display()))
}

fn read_error(operation: ProfileRead) -> ProfileReadError {
    ProfileReadError::Native {
        operation,
        hresult: ACCESS_DENIED,
    }
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum Effect {
    Normal,
    ScopeOnAdd,
    DefaultOnly,
    NoChange,
    ForeignDefault,
    SameNameForeignDefault,
}

struct Script {
    scope: ProfileScope,
    system: Vec<OsString>,
    system_default: Option<OsString>,
    user: Vec<OsString>,
    user_default: Option<OsString>,
    presets: Vec<OsString>,
    monitors: Vec<Monitor>,
    calls: Vec<(ProfileRead, Option<ProfileScope>)>,
    writes: Vec<String>,
    add: Effect,
    fail: Option<(usize, bool)>,
    default_effect: Effect,
    hide_preset_after_default: bool,
    fail_associations_after: Option<usize>,
    lose_monitor_after: Option<usize>,
    read_failure: Option<ProfileRead>,
}

impl Default for Script {
    fn default() -> Self {
        Self {
            scope: ProfileScope::SystemWide,
            system: names(&[FACTORY]),
            system_default: Some(FACTORY.into()),
            user: Vec::new(),
            user_default: None,
            presets: names(&[NATIVE]),
            monitors: vec![Monitor {
                interface: MONITOR.into(),
                key: KEY.into(),
                active: true,
            }],
            calls: Vec::new(),
            writes: Vec::new(),
            add: Effect::Normal,
            fail: None,
            default_effect: Effect::Normal,
            hide_preset_after_default: false,
            fail_associations_after: None,
            lose_monitor_after: None,
            read_failure: None,
        }
    }
}

impl Script {
    fn run(&mut self) -> Result<Outcome, super::transaction::Rejected> {
        set_color_profile(SetColorProfile(OsStr::new(NATIVE)), self, sdr, |_| {
            HdrState::Off
        })
    }

    fn read(
        &mut self,
        operation: ProfileRead,
        scope: Option<ProfileScope>,
    ) -> Result<(), ProfileReadError> {
        self.calls.push((operation, scope));
        if self.read_failure == Some(operation) {
            return Err(read_error(operation));
        }
        Ok(())
    }

    fn assert_writes(&self, expected: &[&str]) {
        assert_eq!(self.writes, expected);
    }
}

impl ProfileReads for Script {
    fn scope(&mut self, _: &ActiveInternalDisplay) -> Result<ProfileScope, ProfileReadError> {
        self.read(ProfileRead::Scope, None)?;
        Ok(self.scope)
    }

    fn associations(
        &mut self,
        _: &ActiveInternalDisplay,
        scope: ProfileScope,
    ) -> Result<Vec<OsString>, ProfileReadError> {
        self.read(ProfileRead::Associations, Some(scope))?;
        if self
            .fail_associations_after
            .is_some_and(|count| self.writes.len() >= count)
        {
            return Err(read_error(ProfileRead::Associations));
        }
        Ok(match scope {
            ProfileScope::CurrentUser => self.user.clone(),
            ProfileScope::SystemWide => self.system.clone(),
        })
    }

    fn default_profile(
        &mut self,
        _: &ActiveInternalDisplay,
        scope: ProfileScope,
    ) -> Result<Option<OsString>, ProfileReadError> {
        self.read(ProfileRead::Default, Some(scope))?;
        Ok(match scope {
            ProfileScope::CurrentUser => self.user_default.clone(),
            ProfileScope::SystemWide => self.system_default.clone(),
        })
    }

    fn presets(&mut self) -> Vec<OsString> {
        if self.hide_preset_after_default
            && self
                .writes
                .iter()
                .any(|write| write.starts_with("default:"))
        {
            Vec::new()
        } else {
            self.presets.clone()
        }
    }
}

impl ScriptedOperations for Script {
    fn monitors(&mut self, display: &ActiveInternalDisplay) -> Result<Vec<Monitor>, Failure> {
        assert_eq!(display.source_name, r"\\.\DISPLAY1");
        if self
            .lose_monitor_after
            .is_some_and(|count| self.writes.len() >= count)
        {
            return Ok(Vec::new());
        }
        Ok(self
            .monitors
            .iter()
            .map(|monitor| Monitor {
                interface: monitor.interface.clone(),
                key: monitor.key.clone(),
                active: monitor.active,
            })
            .collect())
    }

    fn submit(
        &mut self,
        target: &ActiveInternalDisplay,
        key: &OsStr,
        change: CurrentUserChange<'_>,
    ) -> Result<(), Failure> {
        assert_eq!(*target, display());
        assert_eq!(key, KEY);
        let label = match change {
            CurrentUserChange::Associate(profile) => format!("add:{}", profile.to_string_lossy()),
            CurrentUserChange::Remove(profile) => format!("remove:{}", profile.to_string_lossy()),
            CurrentUserChange::SetDefault(profile) => {
                format!("default:{}", profile.to_string_lossy())
            }
            CurrentUserChange::SelectScope(scope) => format!("scope:{scope:?}"),
        };
        self.writes.push(label);
        let failed = self.fail.filter(|&(index, _)| index == self.writes.len());
        if matches!(failed, Some((_, false))) {
            return Err(Failure::Submit(ACCESS_DENIED));
        }
        match change {
            CurrentUserChange::Associate(profile) => match self.add {
                Effect::DefaultOnly => self.user_default = Some(profile.to_owned()),
                Effect::ScopeOnAdd => {
                    self.user.push(profile.to_owned());
                    self.scope = ProfileScope::CurrentUser;
                }
                Effect::NoChange => {}
                _ => self.user.push(profile.to_owned()),
            },
            CurrentUserChange::Remove(profile) => {
                self.user.retain(|id| id != profile);
                if self.user_default.as_deref() == Some(profile) {
                    self.user_default = None;
                }
            }
            CurrentUserChange::SetDefault(profile) => match self.default_effect {
                Effect::NoChange => {}
                Effect::ForeignDefault => self.user_default = Some(r"C:\external\Other.icm".into()),
                Effect::SameNameForeignDefault => {
                    self.user_default = Some(r"C:\external\Native_CTR.icm".into())
                }
                _ => self.user_default = Some(profile.to_owned()),
            },
            CurrentUserChange::SelectScope(scope) => self.scope = scope,
        }
        if failed.is_some() {
            Err(Failure::Submit(ACCESS_DENIED))
        } else {
            Ok(())
        }
    }
}

#[test]
fn snapshot_uses_selected_scope_and_retains_first_id_per_windows_filename() {
    // WindowsDisplaySettingsController.BuildSnapshot and GetFriendlyProfileName.
    let mut api = Script {
        scope: ProfileScope::CurrentUser,
        user: names(&[
            r"C:\one\Display-P3.icm",
            r"D:/two/display-p3.ICM",
            "Blade.icm",
            "alpha.icc",
            "Zeta.icc",
            "BT709_CTR.icm",
            "Adobe_RGB.icm",
            "sRGB.icm",
            "  ",
        ]),
        user_default: Some(r"D:/two/display-p3.ICM".into()),
        ..Script::default()
    };
    let _native_entry_point = std::hint::black_box(query_color_profiles);
    let result = snapshot(&mut api, &display(), HdrState::Off).unwrap();
    assert_eq!(result.scope, ProfileScope::CurrentUser);
    assert_eq!(result.selected, Some(r"C:\one\Display-P3.icm".into()));
    assert_eq!(
        result
            .profiles
            .iter()
            .filter(|profile| profile.active)
            .count(),
        1
    );
    assert_eq!(
        result
            .profiles
            .iter()
            .map(|profile| profile.name.as_str())
            .collect::<Vec<_>>(),
        [
            "Native",
            "DCI-P3",
            "sRGB",
            "Rec. 709",
            "Adobe RGB",
            "alpha",
            "Factory calibration",
            "Zeta"
        ]
    );
    assert_eq!(
        api.calls,
        [
            (ProfileRead::Scope, None),
            (ProfileRead::Associations, Some(ProfileScope::CurrentUser)),
            (ProfileRead::Default, Some(ProfileScope::CurrentUser)),
        ]
    );
}

#[test]
fn hdr_and_unobserved_hdr_keep_profiles_without_reading_sdr_default() {
    for hdr in [HdrState::On, HdrState::Unknown] {
        let mut api = Script {
            read_failure: Some(ProfileRead::Default),
            ..Script::default()
        };
        let result = snapshot(&mut api, &display(), hdr).unwrap();
        assert_eq!(result.hdr, hdr);
        assert_eq!(result.profiles.len(), 2);
        assert_eq!(result.selected, None);
        assert!(!result.profiles.iter().any(|profile| profile.active));
        assert_eq!(
            api.calls.last(),
            Some(&(ProfileRead::Associations, Some(ProfileScope::SystemWide)))
        );
    }
}

#[test]
fn missing_or_unlisted_default_is_not_invented() {
    for system_default in [None, Some("Unlisted.icm".into())] {
        let mut api = Script {
            system_default,
            ..Script::default()
        };
        let result = snapshot(&mut api, &display(), HdrState::Off).unwrap();
        assert_eq!(result.selected, None);
        assert_eq!(result.profiles.len(), 2);
    }
}

#[test]
fn hresult_not_found_is_absence_only_for_list_and_default() {
    let missing = 0x8007_0002u32 as i32;
    for operation in [ProfileRead::Associations, ProfileRead::Default] {
        assert_eq!(returned(operation, missing), Ok(false));
        assert_eq!(returned(operation, 1), Ok(true));
        assert_eq!(
            returned(operation, ACCESS_DENIED),
            Err(read_error(operation))
        );
    }
    assert_eq!(
        returned(ProfileRead::Scope, missing),
        Err(ProfileReadError::Native {
            operation: ProfileRead::Scope,
            hresult: missing,
        })
    );
    for operation in [
        ProfileRead::Scope,
        ProfileRead::Associations,
        ProfileRead::Default,
    ] {
        let mut api = Script {
            read_failure: Some(operation),
            ..Script::default()
        };
        assert_eq!(
            snapshot(&mut api, &display(), HdrState::Off),
            Err(read_error(operation))
        );
    }
}

#[test]
fn preset_requires_the_three_managed_icc_header_signatures() {
    let mut header = [0; 128];
    header[12..16].copy_from_slice(b"mntr");
    header[16..20].copy_from_slice(b"RGB ");
    header[36..40].copy_from_slice(b"acsp");
    assert!(valid_monitor_rgb_header(&header));
    for offset in [12, 16, 36] {
        let mut wrong = header;
        wrong[offset] = b'?';
        assert!(!valid_monitor_rgb_header(&wrong));
    }
}

#[test]
fn adds_before_scope_and_skips_scope_if_add_already_activated_it() {
    // FirstAssociation_IsCreatedBeforePerUserScopeIsEnabled,
    // AssociationThatActivatesPerUserScope_DoesNotRepeatScopeWrite,
    // InactiveAssociationVisibleOnlyAsDefault_IsAcceptedBeforeScopeActivation.
    for effect in [Effect::Normal, Effect::ScopeOnAdd, Effect::DefaultOnly] {
        let mut api = Script {
            add: effect,
            ..Script::default()
        };
        let Outcome::Applied(result) = api.run().unwrap() else {
            panic!("expected applied")
        };
        assert_eq!(result.scope, ProfileScope::CurrentUser);
        assert_eq!(result.selected, Some(NATIVE.into()));
        if effect == Effect::ScopeOnAdd {
            api.assert_writes(&["add:Native_CTR.icm", "default:Native_CTR.icm"]);
        } else {
            api.assert_writes(&[
                "add:Native_CTR.icm",
                "scope:CurrentUser",
                "default:Native_CTR.icm",
            ]);
        }
        assert_eq!(api.system, names(&[FACTORY]));
        assert_eq!(api.system_default, Some(FACTORY.into()));
    }
}

#[test]
fn existing_association_is_never_added_and_active_profile_is_a_verified_noop() {
    let mut api = Script {
        user: names(&[NATIVE]),
        ..Script::default()
    };
    assert!(matches!(api.run(), Ok(Outcome::Applied(_))));
    api.assert_writes(&["scope:CurrentUser", "default:Native_CTR.icm"]);
    api.writes.clear();
    api.calls.clear();
    let result = api.run().unwrap();
    assert!(matches!(result, Outcome::AlreadySelected(_)));
    assert!(api.writes.is_empty());
    assert_eq!(
        api.calls
            .iter()
            .filter(|(operation, _)| *operation == ProfileRead::Default)
            .count(),
        2
    );

    let query = Cell::new(0);
    let result = set_color_profile(
        SetColorProfile(OsStr::new(NATIVE)),
        &mut api,
        || {
            let mut display = display();
            display.target.id += query.get();
            query.set(query.get() + 1);
            Ok(InternalDisplayTopology::Single(display))
        },
        |_| HdrState::Off,
    )
    .unwrap_err();
    assert_eq!(result.cause, Failure::Target);
    assert_eq!(result.disposition, Disposition::NoSubmission);
    assert!(api.writes.is_empty());
}

#[test]
fn refuses_hdr_unknown_hdr_unlisted_profile_and_ambiguous_topology() {
    let mut api = Script::default();
    for hdr in [HdrState::On, HdrState::Unknown] {
        let result = set_color_profile(SetColorProfile(OsStr::new(NATIVE)), &mut api, sdr, |_| hdr)
            .unwrap_err();
        assert_eq!(result.cause, Failure::Hdr(hdr));
        assert_eq!(result.disposition, Disposition::NoSubmission);
    }
    let result = set_color_profile(
        SetColorProfile(OsStr::new("Unlisted.icm")),
        &mut api,
        sdr,
        |_| HdrState::Off,
    )
    .unwrap_err();
    assert_eq!(result.cause, Failure::ProfileUnavailable);
    let result = set_color_profile(
        SetColorProfile(OsStr::new(NATIVE)),
        &mut api,
        || Ok(InternalDisplayTopology::MultipleInternalDisplays),
        |_| HdrState::Off,
    )
    .unwrap_err();
    assert_eq!(result.cause, Failure::Target);
    assert!(api.writes.is_empty());
}

#[test]
fn device_key_requires_one_active_exact_monitor_with_a_key() {
    // DisplayMonitorDeviceKeyResolverTests uses exact interface identity.
    for monitors in [
        vec![Monitor {
            interface: format!("{MONITOR}#OTHER").into(),
            key: KEY.into(),
            active: true,
        }],
        vec![Monitor {
            interface: MONITOR.into(),
            key: KEY.into(),
            active: false,
        }],
        vec![Monitor {
            interface: MONITOR.into(),
            key: " ".into(),
            active: true,
        }],
        vec![
            Monitor {
                interface: MONITOR.into(),
                key: KEY.into(),
                active: true,
            },
            Monitor {
                interface: MONITOR.to_lowercase().into(),
                key: KEY.into(),
                active: true,
            },
        ],
    ] {
        let mut api = Script {
            monitors,
            ..Script::default()
        };
        assert_eq!(api.run().unwrap_err().cause, Failure::Target);
        assert!(api.writes.is_empty());
    }
    let mut api = Script::default();
    api.monitors[0].interface = MONITOR.to_lowercase().into();
    assert!(matches!(api.run(), Ok(Outcome::Applied(_))));
}

#[test]
fn lost_admission_stops_before_submission_or_after_the_first_known_change() {
    for admitted_reads in [0, 2] {
        let mut api = Script::default();
        let calls = Cell::new(0);
        let failure = guarded_set_color_profile(
            SetColorProfile(OsStr::new(NATIVE)),
            &mut api,
            sdr,
            |_| HdrState::Off,
            &|| {
                let count = calls.get();
                calls.set(count + 1);
                count < admitted_reads
            },
            |_, _, _| Ok(()),
        )
        .unwrap_err();
        assert_eq!(failure.cause, Failure::Context);
        if admitted_reads == 0 {
            assert_eq!(failure.disposition, Disposition::NoSubmission);
            api.assert_writes(&[]);
        } else {
            assert_eq!(
                failure.disposition,
                Disposition::Unresolved(Failure::StateChanged)
            );
            api.assert_writes(&["add:Native_CTR.icm"]);
        }
    }
}

#[test]
fn unchanged_failed_first_submission_is_reconciled_without_retry() {
    let mut api = Script {
        fail: Some((1, false)),
        ..Script::default()
    };
    let failure = api.run().unwrap_err();
    assert_eq!(failure.cause, Failure::Submit(ACCESS_DENIED));
    assert_eq!(failure.disposition, Disposition::BaselineConfirmed);
    api.assert_writes(&["add:Native_CTR.icm"]);
}

#[test]
fn failed_scope_without_side_effect_removes_only_proven_owned_association() {
    // FailedScopeChange_RemovesNewAssociationWithoutRepeatingScopeWrite,
    // InactiveAssociationVisibleOnlyAsDefault_IsRemovedDuringRollback.
    for add in [Effect::Normal, Effect::DefaultOnly] {
        let mut api = Script {
            add,
            fail: Some((2, false)),
            ..Script::default()
        };
        assert_eq!(api.run().unwrap_err().disposition, Disposition::Restored);
        api.assert_writes(&[
            "add:Native_CTR.icm",
            "scope:CurrentUser",
            "remove:Native_CTR.icm",
        ]);
        assert_eq!(api.scope, ProfileScope::SystemWide);
        assert_eq!(api.user_default, None);
        assert!(api.user.is_empty());
    }
}

#[test]
fn failed_submission_with_effect_is_not_owned() {
    // Stricter than the managed failed-scope and failed-default side-effect
    // fixtures. An error receipt cannot attribute the resulting state.
    for (index, writes) in [
        (1, &["add:Native_CTR.icm"][..]),
        (2, &["add:Native_CTR.icm", "scope:CurrentUser"][..]),
        (
            3,
            &[
                "add:Native_CTR.icm",
                "scope:CurrentUser",
                "default:Native_CTR.icm",
            ][..],
        ),
    ] {
        let mut api = Script {
            fail: Some((index, true)),
            ..Script::default()
        };
        let failure = api.run().unwrap_err();
        assert_eq!(failure.cause, Failure::Submit(ACCESS_DENIED));
        assert_eq!(
            failure.disposition,
            Disposition::Unresolved(Failure::StateChanged)
        );
        api.assert_writes(writes);
    }
}

#[test]
fn failed_default_without_effect_preserves_original_user_configuration() {
    let mut api = Script {
        scope: ProfileScope::CurrentUser,
        user: names(&[FACTORY, "sRGB_CTR.icm"]),
        user_default: Some(FACTORY.into()),
        fail: Some((2, false)),
        ..Script::default()
    };
    assert_eq!(api.run().unwrap_err().disposition, Disposition::Restored);
    api.assert_writes(&[
        "add:Native_CTR.icm",
        "default:Native_CTR.icm",
        "remove:Native_CTR.icm",
    ]);
    assert_eq!(api.user, names(&[FACTORY, "sRGB_CTR.icm"]));
    assert_eq!(api.user_default, Some(FACTORY.into()));
}

#[test]
fn failed_final_snapshot_restores_empty_baseline_and_selected_scope() {
    // FailedFinalReadback_RestoresExactEmptyBaseline, with the preset no
    // longer enumerated during the independent final snapshot.
    let mut api = Script {
        add: Effect::DefaultOnly,
        hide_preset_after_default: true,
        ..Script::default()
    };
    let failure = api.run().unwrap_err();
    assert_eq!(failure.cause, Failure::Readback);
    assert_eq!(failure.disposition, Disposition::Restored);
    api.assert_writes(&[
        "add:Native_CTR.icm",
        "scope:CurrentUser",
        "default:Native_CTR.icm",
        "remove:Native_CTR.icm",
        "scope:SystemWide",
    ]);
    assert_eq!(api.scope, ProfileScope::SystemWide);
    assert_eq!(api.user_default, None);
    assert!(api.user.is_empty());
}

#[test]
fn failed_final_snapshot_restores_original_default_before_removing_association() {
    let mut api = Script {
        scope: ProfileScope::CurrentUser,
        user: names(&[FACTORY, "sRGB_CTR.icm"]),
        user_default: Some(FACTORY.into()),
        add: Effect::DefaultOnly,
        hide_preset_after_default: true,
        ..Script::default()
    };
    assert_eq!(api.run().unwrap_err().disposition, Disposition::Restored);
    api.assert_writes(&[
        "add:Native_CTR.icm",
        "default:Native_CTR.icm",
        "default:Blade.icm",
    ]);
    assert_eq!(api.user, names(&[FACTORY, "sRGB_CTR.icm"]));
    assert_eq!(api.user_default, Some(FACTORY.into()));
}

#[test]
fn unconfirmed_default_restores_prior_steps_if_the_last_known_state_is_intact() {
    let mut api = Script {
        default_effect: Effect::NoChange,
        ..Script::default()
    };
    let failure = api.run().unwrap_err();
    assert_eq!(failure.cause, Failure::Readback);
    assert_eq!(failure.disposition, Disposition::Restored);
    api.assert_writes(&[
        "add:Native_CTR.icm",
        "scope:CurrentUser",
        "default:Native_CTR.icm",
        "remove:Native_CTR.icm",
        "scope:SystemWide",
    ]);
}

#[test]
fn unrelated_default_is_preserved_and_no_restoration_is_attempted() {
    let mut api = Script {
        default_effect: Effect::ForeignDefault,
        ..Script::default()
    };
    let failure = api.run().unwrap_err();
    assert_eq!(failure.cause, Failure::Readback);
    assert_eq!(
        failure.disposition,
        Disposition::Unresolved(Failure::StateChanged)
    );
    api.assert_writes(&[
        "add:Native_CTR.icm",
        "scope:CurrentUser",
        "default:Native_CTR.icm",
    ]);
    assert_eq!(api.user_default, Some(r"C:\external\Other.icm".into()));
}

#[test]
fn unreadable_or_rebound_target_stops_restoration() {
    // FailedReconciliationRead_StopsWithoutRestorationWrites, plus target/HDR loss.
    let mut api = Script {
        fail_associations_after: Some(1),
        ..Script::default()
    };
    assert_eq!(
        api.run().unwrap_err().disposition,
        Disposition::Unresolved(Failure::Read(read_error(ProfileRead::Associations)))
    );
    api.assert_writes(&["add:Native_CTR.icm"]);

    let mut api = Script {
        lose_monitor_after: Some(1),
        ..Script::default()
    };
    let failure = api.run().unwrap_err();
    assert_eq!(failure.cause, Failure::Target);
    assert_eq!(
        failure.disposition,
        Disposition::Unresolved(Failure::StateChanged)
    );
    api.assert_writes(&["add:Native_CTR.icm"]);

    let hdr_reads = Cell::new(0);
    let mut api = Script::default();
    let failure = set_color_profile(SetColorProfile(OsStr::new(NATIVE)), &mut api, sdr, |_| {
        let count = hdr_reads.get();
        hdr_reads.set(count + 1);
        // Initial read, journal preflight, and the first setter admission.
        if count < 3 {
            HdrState::Off
        } else {
            HdrState::On
        }
    })
    .unwrap_err();
    assert_eq!(failure.cause, Failure::Hdr(HdrState::On));
    assert_eq!(
        failure.disposition,
        Disposition::Unresolved(Failure::StateChanged)
    );
    api.assert_writes(&["add:Native_CTR.icm"]);
}

#[test]
fn same_filename_at_a_different_full_path_does_not_prove_ownership() {
    let mut api = Script {
        presets: names(&[r"C:\color\Native_CTR.icm"]),
        default_effect: Effect::SameNameForeignDefault,
        ..Script::default()
    };
    let failure = api.run().unwrap_err();
    assert_eq!(
        failure.disposition,
        Disposition::Unresolved(Failure::StateChanged)
    );
    api.assert_writes(&[
        r"add:C:\color\Native_CTR.icm",
        "scope:CurrentUser",
        r"default:C:\color\Native_CTR.icm",
    ]);
    assert_eq!(api.user_default, Some(r"C:\external\Native_CTR.icm".into()));
}

#[test]
fn successful_submit_without_an_association_is_not_replayed() {
    let mut api = Script {
        add: Effect::NoChange,
        ..Script::default()
    };
    let failure = api.run().unwrap_err();
    assert_eq!(failure.cause, Failure::Readback);
    assert_eq!(failure.disposition, Disposition::BaselineConfirmed);
    api.assert_writes(&["add:Native_CTR.icm"]);
}

#[test]
fn whole_profile_ids_compare_ordinally_ignoring_case_without_file_names() {
    let same = |left: &str, right: &str| profile_ids_equal(OsStr::new(left), OsStr::new(right));
    assert!(same(NATIVE, "native_ctr.ICM"));
    assert!(same("", ""));
    assert!(!same(NATIVE, FACTORY));
    assert!(!same(NATIVE, ""));
    // The file-name equivalence belongs to profiles_match only.
    let path = r"C:\Windows\System32\spool\drivers\color\Native_CTR.icm";
    assert!(!same(path, NATIVE));
    assert!(profiles_match(OsStr::new(path), OsStr::new(NATIVE)));
}
