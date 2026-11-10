use super::*;
use crate::controller_presence::{DetectionWarning, Observation, observe};
use windows_sys::Win32::System::Services::{
    SERVICE_KERNEL_DRIVER, SERVICE_PAUSE_PENDING, SERVICE_PAUSED, SERVICE_STOP_PENDING,
    SERVICE_WIN32_OWN_PROCESS,
};

fn names(key: &str, display: &str) -> Names {
    Names {
        key: key.to_owned(),
        display: display.to_owned(),
    }
}

fn resolve_from(services: &[(&str, &str)], alias: Alias) -> Result<Option<Names>, ()> {
    Ok(services.iter().find_map(|&(key, display)| {
        let matches = match alias {
            Alias::Key(alias) => eq(alias, key),
            Alias::Display(alias) => eq(alias, display),
        };
        matches.then(|| names(key, display))
    }))
}

fn scripted(
    resolve: impl FnMut(Alias) -> Result<Option<Names>, ()>,
    mut query: impl FnMut(&str) -> ServiceState,
) -> ServiceInventory {
    collect(resolve, |key| (query(key), ()))
}

fn classify(inventory: &ServiceInventory) -> Observation {
    let rows = inventory.service_rows();
    observe(
        Scan::Complete(&[]),
        inventory.scan(&rows),
        None,
        Scan::Incomplete(&[]),
        100,
    )
}

#[test]
fn all_exact_aliases_absent_after_both_lookups_confirms_absence() {
    let mut count = 0;
    let inventory = scripted(
        |_| {
            count += 1;
            Ok(None)
        },
        |_| panic!("absent key queried"),
    );
    assert_eq!(count, 30);
    assert!(classify(&inventory).confirms_absence());
}

#[test]
fn display_alias_finds_an_arbitrary_key_and_matching_is_exact() {
    let services = [
        ("UnrelatedKey", "rAZER sYNAPSE sERVICE"),
        ("NotAnAlias", "Razer Synapse Service Extra"),
        ("UpdaterDisplayOnly", "RGS"),
    ];
    let inventory = scripted(
        |alias| resolve_from(&services, alias),
        |key| {
            assert_eq!(key, "UnrelatedKey");
            ServiceState::Running
        },
    );
    let observation = classify(&inventory);
    assert_eq!(
        observation.potential_controllers(),
        ["rAZER sYNAPSE sERVICE"]
    );
    assert_eq!(observation.hyperboost_blockers(), ["rAZER sYNAPSE sERVICE"]);
    assert!(inventory.is_complete());
}

#[test]
fn a_known_key_and_display_alias_query_one_service_and_preserve_rgs_policy() {
    let services = [("rGs", UPDATER_DISPLAY)];
    let mut queries = 0;
    let inventory = scripted(
        |alias| resolve_from(&services, alias),
        |key| {
            assert!(eq(key, UPDATER));
            queries += 1;
            ServiceState::StartPending
        },
    );
    assert_eq!(queries, 1);
    assert_eq!(
        classify(&inventory).potential_controllers(),
        [UPDATER_DISPLAY]
    );
    assert!(classify(&inventory).hyperboost_blockers().is_empty());
}

#[test]
fn unresolved_alias_lookup_keeps_other_positive_rows_and_warns() {
    let inventory = scripted(
        |alias| match alias {
            Alias::Key(UPDATER) => Err(()),
            _ => resolve_from(&[("UnrelatedKey", "Razer Chroma SDK Service")], alias),
        },
        |_| ServiceState::ContinuePending,
    );
    let observation = classify(&inventory);
    assert_eq!(
        observation.potential_controllers(),
        ["Razer Chroma SDK Service"]
    );
    assert_eq!(observation.warning(), Some(&DetectionWarning::ServiceScan));
}

#[test]
fn failed_status_query_cannot_confirm_absence_even_with_stable_aliases() {
    let inventory = scripted(
        |alias| resolve_from(&[(UPDATER, UPDATER_DISPLAY)], alias),
        |_| ServiceState::Unknown,
    );
    assert!(!inventory.is_complete());
    assert_eq!(
        classify(&inventory).warning(),
        Some(&DetectionWarning::ServiceScan)
    );
    assert!(!classify(&inventory).confirms_absence());
}

#[test]
fn appearance_disappearance_and_replacement_during_scan_are_incomplete() {
    for (old, new) in [
        (None, Some("new")),
        (Some("old"), None),
        (Some("old"), Some("new")),
    ] {
        let mut second = false;
        let inventory = scripted(
            |alias| match alias {
                Alias::Display(UPDATER_DISPLAY) => {
                    let key = if second { new } else { old };
                    second = true;
                    Ok(key.map(|key| names(key, UPDATER_DISPLAY)))
                }
                _ => Ok(None),
            },
            |_| ServiceState::Running,
        );
        let observation = classify(&inventory);
        assert_eq!(observation.warning(), Some(&DetectionWarning::ServiceScan));
        assert_eq!(
            observation.potential_controllers().len(),
            usize::from(old.is_some())
        );
        assert!(!observation.confirms_absence());
    }
}

#[test]
fn conflicting_key_and_display_lookups_cannot_confirm_absence() {
    let inventory = scripted(
        |alias| match alias {
            Alias::Key(UPDATER) => Ok(Some(names(UPDATER, UPDATER_DISPLAY))),
            Alias::Display(UPDATER_DISPLAY) => Ok(None),
            _ => Ok(None),
        },
        |_| ServiceState::Inactive,
    );
    assert_eq!(
        classify(&inventory).warning(),
        Some(&DetectionWarning::ServiceScan)
    );
}

#[test]
fn only_stopped_win32_services_and_out_of_scope_drivers_confirm_inactive() {
    let kind = SERVICE_WIN32_OWN_PROCESS;
    for (service_type, state) in [
        (kind, SERVICE_STOPPED),
        (SERVICE_KERNEL_DRIVER, SERVICE_RUNNING),
    ] {
        let inventory = scripted(
            |alias| resolve_from(&[(UPDATER, UPDATER_DISPLAY)], alias),
            |_| map_status(service_type, state),
        );
        assert!(classify(&inventory).confirms_absence());
    }
    for state in [
        SERVICE_STOP_PENDING,
        SERVICE_PAUSE_PENDING,
        SERVICE_PAUSED,
        0,
        u32::MAX,
    ] {
        assert!(matches!(map_status(kind, state), ServiceState::Unknown));
    }
    assert!(matches!(
        map_status(0, SERVICE_RUNNING),
        ServiceState::Unknown
    ));
}

#[test]
fn active_windows_statuses_keep_the_managed_classification() {
    let kind = SERVICE_WIN32_OWN_PROCESS;
    assert!(matches!(
        map_status(kind, SERVICE_RUNNING),
        ServiceState::Running
    ));
    assert!(matches!(
        map_status(kind, SERVICE_START_PENDING),
        ServiceState::StartPending
    ));
    assert!(matches!(
        map_status(kind, SERVICE_CONTINUE_PENDING),
        ServiceState::ContinuePending
    ));
}

#[test]
fn service_handle_stays_held_through_alias_recheck() {
    use std::cell::Cell;

    struct Handle<'a>(&'a Cell<bool>);
    impl Drop for Handle<'_> {
        fn drop(&mut self) {
            self.0.set(false);
        }
    }

    let held = Cell::new(false);
    let mut resolved = 0;
    let inventory = collect(
        |alias| {
            if resolved >= 15 {
                assert!(held.get());
            }
            resolved += 1;
            resolve_from(&[(UPDATER, UPDATER_DISPLAY)], alias)
        },
        |_| {
            held.set(true);
            (ServiceState::Inactive, Handle(&held))
        },
    );
    assert!(!held.get());
    assert!(classify(&inventory).confirms_absence());
}
