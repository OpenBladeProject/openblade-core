use super::*;
use crate::controller_presence::{DetectionWarning, Observation, observe_inventory};

fn entry(pid: u32, parent: u32, name: &str) -> PROCESSENTRY32W {
    let mut row = PROCESSENTRY32W {
        th32ProcessID: pid,
        th32ParentProcessID: parent,
        ..Default::default()
    };
    for (slot, unit) in row.szExeFile.iter_mut().zip(name.encode_utf16()) {
        *slot = unit;
    }
    row
}

fn inventory(
    rows: &[PROCESSENTRY32W],
    exhausted: bool,
    query: impl FnMut(u32) -> Option<ProcessImageEvidence>,
) -> ProcessInventory {
    collect(100, |visit| rows.iter().all(visit) && exhausted, query)
}

fn image(pid: u32, created: u64, path: &str) -> ProcessImageEvidence {
    ProcessImageEvidence {
        identity: ProcessIdentity {
            pid,
            session: 3,
            created,
        },
        path: PathBuf::from(path),
    }
}

fn classify(inventory: &ProcessInventory) -> Observation {
    observe_inventory(inventory, Scan::Complete(&[]), 110)
}

#[test]
fn invalid_inventory_clock_does_not_confirm_absence() {
    let inventory = inventory(&[], true, |_| panic!("empty snapshot queried"));
    for observed_at in [0, 99] {
        let result = observe_inventory(&inventory, Scan::Complete(&[]), observed_at);
        assert_eq!(result.warning(), Some(&DetectionWarning::ProcessScan));
        assert!(!result.confirms_absence());
    }
}

#[test]
fn complete_unrelated_snapshot_requires_no_image_queries() {
    let inventory = inventory(&[entry(1, 0, "unrelated.exe")], true, |_| {
        panic!("unrelated process queried")
    });
    assert!(classify(&inventory).confirms_absence());
}

#[test]
fn snapshot_open_or_traversal_failure_cannot_confirm_absence() {
    for rows in [&[][..], &[entry(2, 1, "unrelated.exe")][..]] {
        let inventory = inventory(rows, false, |_| panic!("incomplete snapshot queried"));
        assert_eq!(
            classify(&inventory).warning(),
            Some(&DetectionWarning::ProcessScan)
        );
        assert!(!classify(&inventory).confirms_absence());
    }
}

#[test]
fn partial_snapshot_retains_positive_names_but_no_parent_proof() {
    let inventory = inventory(&[entry(2, 1, "razerwdl.exe")], false, |_| {
        panic!("incomplete snapshot queried")
    });
    assert_eq!(classify(&inventory).potential_controllers(), ["razerwdl"]);
    assert!(matches!(
        inventory.parent_snapshot().links,
        Scan::Incomplete(&[(2, 1)])
    ));
    assert_eq!(inventory.image_evidence().count(), 0);
}

#[test]
fn query_failure_retains_name_and_reports_incomplete_observation() {
    let inventory = inventory(&[entry(2, 1, "razerwdl.exe")], true, |_| None);
    let observation = classify(&inventory);
    assert_eq!(observation.hyperboost_blockers(), ["razerwdl"]);
    assert_eq!(observation.warning(), Some(&DetectionWarning::ProcessScan));
    assert!(inventory.process_rows()[0].identity.is_none());
}

#[test]
fn reused_pid_at_or_after_snapshot_start_and_zero_birth_are_rejected() {
    for birth in [0, 100, 101] {
        let inventory = inventory(&[entry(2, 1, "razerwdl.exe")], true, |pid| {
            Some(image(pid, birth, r"C:\Razer\razerwdl.exe"))
        });
        assert_eq!(inventory.image_evidence().count(), 0);
        assert!(!inventory.is_complete());
        assert_eq!(classify(&inventory).hyperboost_blockers(), ["razerwdl"]);
    }
}

#[test]
fn wrong_pid_or_image_and_relative_paths_are_rejected() {
    for (pid, path) in [
        (1, r"C:\Razer\razerwdl.exe"),
        (2, r"C:\Razer\razerwdl-other.exe"),
        (2, r"C:\Razer\razerwdl"),
        (2, r"Razer\razerwdl.exe"),
    ] {
        let inventory = inventory(&[entry(2, 1, "razerwdl.exe")], true, |_| {
            Some(image(pid, 80, path))
        });
        assert!(!inventory.is_complete());
        assert_eq!(inventory.image_evidence().count(), 0);
    }
}

#[test]
fn duplicate_or_malformed_toolhelp_rows_preclude_complete_inventory() {
    let mut malformed = entry(1, 0, "");
    malformed.szExeFile[0] = 0xD800;
    for suspect in [entry(2, 1, "same-pid.exe"), malformed] {
        let inventory = inventory(&[entry(2, 1, "razerwdl.exe"), suspect], true, |_| {
            panic!("unreliable snapshot queried")
        });
        assert!(!inventory.is_complete());
        assert_eq!(classify(&inventory).potential_controllers(), ["razerwdl"]);
    }
}

#[test]
fn accepted_identity_and_path_never_become_verified_images() {
    let inventory = inventory(
        &[
            entry(1, 0, "RazerAppEngine.exe"),
            entry(2, 1, "razerwdl.exe"),
        ],
        true,
        |pid| {
            let path = if pid == 1 {
                r"C:\Razer\RAZERAPPENGINE.EXE"
            } else {
                r"C:\Razer\RazerAppEngine\User Data\Apps\Common\LampArray\razerwdl.exe"
            };
            Some(image(pid, 40 + u64::from(pid), path))
        },
    );
    let rows = inventory.process_rows();
    assert!(inventory.is_complete());
    assert_eq!(inventory.image_evidence().count(), 2);
    assert_eq!(
        rows[1].identity,
        Some(ProcessIdentity {
            pid: 2,
            session: 3,
            created: 42
        })
    );
    assert!(rows.iter().all(|row| row.verified_image.is_none()));
    assert_eq!(
        classify(&inventory).hyperboost_blockers(),
        ["RazerAppEngine", "razerwdl"]
    );
}
