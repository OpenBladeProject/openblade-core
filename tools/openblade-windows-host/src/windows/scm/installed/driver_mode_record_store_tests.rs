//! The Driver-mode record on real files in an owner directory: the staged
//! replace and its crash points, compare-and-swap, the writer gate and the
//! fail-closed reads.
use super::super::fixture::{OwnerRoot, reservation};
use super::*;
use crate::installed::readiness::thermal_mutation::is_fatal_admission;
use openblade_domain::driver_mode_history::{Intent, Settled};
use serde_json::Value;

const OWNER: char = '4';

fn owner(nonce: char) -> String {
    nonce.to_string().repeat(64)
}

fn vectors() -> Value {
    serde_json::from_str(include_str!(
        "../../../../tests/data/driver-mode-record-v1.json"
    ))
    .unwrap()
}

fn claimed(nonce: char, ids: [u8; 4]) -> DriverModeRecord {
    DriverModeRecord::claim(None, Intent::Acquire, &owner(nonce), ids).unwrap()
}

/// The record file's bytes, `None` while there is none.
fn on_disk(root: &OwnerRoot, name: &str) -> Option<Vec<u8>> {
    std::fs::read(root.path(name)).ok()
}

/// A Store whose `history_boundary` fails at `point`, and whether it did.
fn failing_at(store: &mut Store, point: &'static str) -> Arc<std::sync::atomic::AtomicBool> {
    let failed = Arc::new(std::sync::atomic::AtomicBool::new(false));
    let seen = failed.clone();
    store.history_boundary = Some(Arc::new(move |at| {
        if at == point {
            seen.store(true, std::sync::atomic::Ordering::Release);
            return Err(io::ErrorKind::StorageFull.into());
        }
        Ok(())
    }));
    failed
}

#[test]
fn a_record_survives_the_process_that_wrote_it() {
    let root = OwnerRoot::new("record-round-trip");
    let store = root.active(OWNER);
    assert_eq!(store.driver_mode_record_read().unwrap(), None);
    let first = claimed(OWNER, [11, 12, 13, 14]);
    store.driver_mode_record_replace(None, &first).unwrap();
    let owned = first.settle(Settled::Owned, &owner(OWNER)).unwrap();
    store
        .driver_mode_record_replace(Some(&first), &owned)
        .unwrap();
    assert_eq!(on_disk(&root, RECORD).unwrap(), owned.bytes());
    assert_eq!(on_disk(&root, STAGE), None);
    drop(store);
    // A later process, before it activates, reads exactly what was written.
    let later = root.store().unwrap();
    assert_eq!(later.driver_mode_record_read().unwrap(), Some(owned));
}

#[test]
fn a_replace_is_compare_and_swap_on_the_record_it_read() {
    let root = OwnerRoot::new("record-cas");
    let store = root.active(OWNER);
    let first = claimed(OWNER, [11, 12, 13, 14]);
    store.driver_mode_record_replace(None, &first).unwrap();
    let owned = first.settle(Settled::Owned, &owner(OWNER)).unwrap();
    // Expecting no record while one exists.
    assert!(store.driver_mode_record_replace(None, &first).is_err());
    // A record changed outside this process since it was read.
    let other = claimed(OWNER, [21, 22, 23, 24]);
    std::fs::write(root.path(RECORD), other.bytes()).unwrap();
    assert!(
        store
            .driver_mode_record_replace(Some(&first), &owned)
            .is_err()
    );
    assert_eq!(on_disk(&root, RECORD).unwrap(), other.bytes());
    assert_eq!(on_disk(&root, STAGE), None);
    // The record actually there is the only one a replace may expect.
    let settled = other.settle(Settled::Owned, &owner(OWNER)).unwrap();
    store
        .driver_mode_record_replace(Some(&other), &settled)
        .unwrap();
}

#[test]
fn only_this_processs_active_owner_writes_and_only_admitted_edges() {
    let root = OwnerRoot::new("record-writer");
    let first = claimed(OWNER, [11, 12, 13, 14]);
    // A custody that never activated, as a recovery-only process holds,
    // reads but never writes.
    let mut store = root.store().unwrap();
    assert_eq!(store.driver_mode_record_read().unwrap(), None);
    assert!(store.driver_mode_record_replace(None, &first).is_err());
    store.activate_injected(reservation(OWNER)).unwrap();
    // Another owner's record, and an edge the domain refuses.
    assert!(
        store
            .driver_mode_record_replace(None, &claimed('5', [11, 12, 13, 14]))
            .is_err()
    );
    let settled_first = DriverModeRecord::parse(
        format!(
            "{}\n{}\n1\n{}\nClear\nClear\n11,12,13,14\n",
            history::HEADER,
            history::CONTRACT,
            owner(OWNER)
        )
        .as_bytes(),
    )
    .unwrap();
    assert!(
        store
            .driver_mode_record_replace(None, &settled_first)
            .is_err()
    );
    assert_eq!(
        (on_disk(&root, RECORD), on_disk(&root, STAGE)),
        (None, None)
    );
    store.driver_mode_record_replace(None, &first).unwrap();
    // A retired owner writes nothing more.
    store.retire_active().unwrap();
    let owned = first.settle(Settled::Owned, &owner(OWNER)).unwrap();
    assert!(
        store
            .driver_mode_record_replace(Some(&first), &owned)
            .is_err()
    );
    drop(store);
    // A later generation resolves the earlier owner's intent as itself.
    let mut later = root.store().unwrap();
    later.activate_injected(reservation('5')).unwrap();
    assert!(
        later
            .driver_mode_record_replace(Some(&first), &owned)
            .is_err()
    );
    let resolved = first.settle(Settled::Clear, &owner('5')).unwrap();
    later
        .driver_mode_record_replace(Some(&first), &resolved)
        .unwrap();
    assert_eq!(later.driver_mode_record_read().unwrap(), Some(resolved));
}

#[test]
fn a_process_that_did_not_activate_never_writes_as_the_active_owner() {
    // An earlier process activated and left its owner Active, as a crash
    // does. A successor that has not activated, as a recovery-only process
    // never does, reads that owner's record but never writes as it.
    let root = OwnerRoot::new("record-unactivated");
    let first = claimed(OWNER, [11, 12, 13, 14]);
    let earlier = root.active(OWNER);
    earlier.driver_mode_record_replace(None, &first).unwrap();
    drop(earlier);
    let successor = root.store().unwrap();
    assert_eq!(
        successor.driver_mode_record_read().unwrap(),
        Some(first.clone())
    );
    let owned = first.settle(Settled::Owned, &owner(OWNER)).unwrap();
    assert!(
        successor
            .driver_mode_record_replace(Some(&first), &owned)
            .is_err()
    );
    // It has no writer nonce to name either.
    assert!(successor.driver_mode_record_owner().is_err());
    assert_eq!(on_disk(&root, RECORD).unwrap(), first.bytes());
    assert_eq!(on_disk(&root, STAGE), None);
}

#[test]
fn the_writer_nonce_is_the_activated_owners_own() {
    let root = OwnerRoot::new("record-writer-nonce");
    let store = root.active(OWNER);
    assert_eq!(store.driver_mode_record_owner().unwrap(), owner(OWNER));
}

#[test]
fn a_record_changed_during_a_replace_is_not_overwritten() {
    // The record changes after the replace read it and before the rename:
    // the stage is kept as evidence and the changed record stays.
    let root = OwnerRoot::new("record-changed-during");
    let mut store = root.active(OWNER);
    let first = claimed(OWNER, [11, 12, 13, 14]);
    store.driver_mode_record_replace(None, &first).unwrap();
    let owned = first.settle(Settled::Owned, &owner(OWNER)).unwrap();
    let other = claimed(OWNER, [21, 22, 23, 24]);
    let (path, bytes) = (root.path(RECORD), other.bytes());
    store.history_boundary = Some(Arc::new(move |at| {
        if at == "driver-mode:flushed" {
            std::fs::write(&path, &bytes)?;
        }
        Ok(())
    }));
    assert!(
        store
            .driver_mode_record_replace(Some(&first), &owned)
            .is_err()
    );
    assert_eq!(on_disk(&root, RECORD).unwrap(), other.bytes());
    assert_eq!(on_disk(&root, STAGE).unwrap(), owned.bytes());
}

#[test]
fn a_cut_at_each_boundary_leaves_the_old_or_the_new_record() {
    for (point, published) in [
        ("driver-mode:created", false),
        ("driver-mode:written", false),
        ("driver-mode:flushed", false),
        ("driver-mode:readback", false),
        ("driver-mode:renamed", true),
        ("driver-mode:verified", true),
    ] {
        let root = OwnerRoot::new(&format!("record-cut-{}", &point[12..]));
        let mut store = root.active(OWNER);
        let first = claimed(OWNER, [11, 12, 13, 14]);
        store.driver_mode_record_replace(None, &first).unwrap();
        let owned = first.settle(Settled::Owned, &owner(OWNER)).unwrap();
        let failed = failing_at(&mut store, point);
        assert!(
            store
                .driver_mode_record_replace(Some(&first), &owned)
                .is_err(),
            "{point}"
        );
        assert!(failed.load(std::sync::atomic::Ordering::Acquire), "{point}");
        drop(store);
        let later = root.store().unwrap();
        if published {
            assert_eq!(
                later.driver_mode_record_read().unwrap(),
                Some(owned),
                "{point}"
            );
            assert_eq!(on_disk(&root, STAGE), None, "{point}");
        } else {
            // The stage is evidence that a replace may still be running or
            // was cut: the record is refused, and both files are kept.
            let stage = on_disk(&root, STAGE).unwrap();
            assert!(later.driver_mode_record_read().is_err(), "{point}");
            assert!(
                later
                    .driver_mode_record_replace(Some(&first), &owned)
                    .is_err(),
                "{point}"
            );
            assert_eq!(on_disk(&root, RECORD).unwrap(), first.bytes(), "{point}");
            assert_eq!(on_disk(&root, STAGE).unwrap(), stage, "{point}");
            assert!(owned.bytes().starts_with(&stage), "{point}");
        }
    }
}

#[test]
fn a_leftover_stage_refuses_the_record_and_is_kept() {
    let root = OwnerRoot::new("record-stage");
    let store = root.active(OWNER);
    let first = claimed(OWNER, [11, 12, 13, 14]);
    for stage in [&b""[..], b"partial", &first.bytes()] {
        std::fs::write(root.path(STAGE), stage).unwrap();
        assert!(store.driver_mode_record_read().is_err());
        assert!(store.driver_mode_record_replace(None, &first).is_err());
        assert_eq!(on_disk(&root, STAGE).unwrap(), stage);
        assert_eq!(on_disk(&root, RECORD), None);
    }
    // A directory in the stage's place refuses as well.
    std::fs::remove_file(root.path(STAGE)).unwrap();
    std::fs::create_dir(root.path(STAGE)).unwrap();
    assert!(store.driver_mode_record_read().is_err());
    assert!(store.driver_mode_record_replace(None, &first).is_err());
    assert!(root.path(STAGE).is_dir());
}

#[test]
fn a_corrupt_unknown_or_unbound_record_is_refused_and_kept() {
    let root = OwnerRoot::new("record-corrupt");
    let store = root.active(OWNER);
    let first = claimed(OWNER, [11, 12, 13, 14]);
    let v = vectors();
    let mut refused: Vec<Vec<u8>> = v["rejected"]
        .as_array()
        .unwrap()
        .iter()
        .map(|vector| vector["bytes"].as_str().unwrap().as_bytes().to_vec())
        .collect();
    // A valid record of an owner this history never made Active.
    refused.push(claimed('9', [11, 12, 13, 14]).bytes());
    for bytes in &refused {
        std::fs::write(root.path(RECORD), bytes).unwrap();
        let read = store.driver_mode_record_read();
        assert!(
            read.as_ref().is_err_and(|e| !is_fatal_admission(e)),
            "{:?}: {read:?}",
            String::from_utf8_lossy(bytes)
        );
        // No compare-and-swap can match a record that was refused.
        assert!(store.driver_mode_record_replace(None, &first).is_err());
        assert_eq!(on_disk(&root, RECORD).unwrap(), *bytes);
        assert_eq!(on_disk(&root, STAGE), None);
    }
    // Neither does a directory in the record's place.
    std::fs::remove_file(root.path(RECORD)).unwrap();
    std::fs::create_dir(root.path(RECORD)).unwrap();
    assert!(store.driver_mode_record_read().is_err());
    assert!(store.driver_mode_record_replace(None, &first).is_err());
}

#[test]
fn a_record_of_another_identity_or_protection_is_fatal_evidence() {
    // A second link to the record.
    let root = OwnerRoot::new("record-link");
    let store = root.active(OWNER);
    let first = claimed(OWNER, [11, 12, 13, 14]);
    store.driver_mode_record_replace(None, &first).unwrap();
    std::fs::hard_link(root.path(RECORD), root.path("copy.bin")).unwrap();
    let read = store.driver_mode_record_read();
    assert!(read.is_err_and(|e| is_fatal_admission(&e)));
    drop(store);
    std::fs::remove_file(root.path("copy.bin")).unwrap();
    // A record whose protection no longer verifies.
    let root = OwnerRoot::new("record-protection");
    let refused = Arc::new(Mutex::new(None));
    let identity = refused.clone();
    let mut store = root
        .store_verified(Arc::new(move |file| {
            let current = paths::handle_information(file).map(|i| paths::file_identity(&i))?;
            if *identity.lock().unwrap() == Some(current) {
                return Err(io::ErrorKind::PermissionDenied.into());
            }
            Ok(())
        }))
        .unwrap();
    store.activate_injected(reservation(OWNER)).unwrap();
    let first = claimed(OWNER, [11, 12, 13, 14]);
    store.driver_mode_record_replace(None, &first).unwrap();
    let file = std::fs::File::open(root.path(RECORD)).unwrap();
    *refused.lock().unwrap() = Some(paths::file_identity(
        &paths::handle_information(&file).unwrap(),
    ));
    drop(file);
    assert!(
        store
            .driver_mode_record_read()
            .is_err_and(|e| is_fatal_admission(&e))
    );
    let owned = first.settle(Settled::Owned, &owner(OWNER)).unwrap();
    assert!(
        store
            .driver_mode_record_replace(Some(&first), &owned)
            .is_err_and(|e| is_fatal_admission(&e))
    );
    assert_eq!(on_disk(&root, RECORD).unwrap(), first.bytes());
}

#[test]
fn this_composition_opens_over_its_record_and_stage() {
    let root = OwnerRoot::new("record-composition");
    let store = root.active(OWNER);
    let first = claimed(OWNER, [11, 12, 13, 14]);
    store.driver_mode_record_replace(None, &first).unwrap();
    drop(store);
    std::fs::write(root.path(STAGE), b"evidence").unwrap();
    // The owner history still loads; only the record is refused.
    let store = root.store().unwrap();
    assert!(store.driver_mode_record_read().is_err());
    assert_eq!(on_disk(&root, STAGE).unwrap(), b"evidence");
}

/// The store admits a replace by the domain's edges alone: every transition
/// vector, written over its prior by this process's Active owner, is
/// admitted exactly when the vector says the edge follows, and a refused
/// one leaves the file as it was. `follows` ignores the owner, so each
/// record is rewritten as this owner's.
#[test]
fn every_transition_vector_is_admitted_exactly_as_it_follows() {
    let root = OwnerRoot::new("record-vectors");
    let store = root.active(OWNER);
    let as_owner = |text: &Value| {
        let mut lines: Vec<String> = text
            .as_str()
            .unwrap()
            .split('\n')
            .map(str::to_owned)
            .collect();
        lines[3] = owner(OWNER);
        DriverModeRecord::parse(lines.join("\n").as_bytes()).unwrap()
    };
    let v = vectors();
    let transitions = v["transitions"].as_array().unwrap();
    assert!(transitions.len() >= 30);
    for t in transitions {
        let name = t["name"].as_str().unwrap();
        let prior = (!t["prior"].is_null()).then(|| as_owner(&t["prior"]));
        let next = as_owner(&t["next"]);
        match &prior {
            Some(prior) => std::fs::write(root.path(RECORD), prior.bytes()).unwrap(),
            None => {
                let _ = std::fs::remove_file(root.path(RECORD));
            }
        }
        let follows = t["follows"].as_bool().unwrap();
        let written = store.driver_mode_record_replace(prior.as_ref(), &next);
        assert_eq!(written.is_ok(), follows, "{name}: {written:?}");
        let kept = if follows { Some(&next) } else { prior.as_ref() };
        assert_eq!(
            on_disk(&root, RECORD),
            kept.map(DriverModeRecord::bytes),
            "{name}"
        );
        assert_eq!(on_disk(&root, STAGE), None, "{name}");
    }
}
