//! The store's ledger: read once at open, closed on anything it cannot
//! admit or confirm, and replaced only by compare-and-swap.
use super::super::{Files, Storage};
use openblade_domain::thermal_convergence::{
    Component, Contradiction, Disposition, Guid, Intent, LEDGER, Power, Refresh, State, encode,
};
use serde_json::json;
use std::{
    collections::BTreeMap,
    io,
    sync::{Arc, Mutex},
};

const SID: &str = "S-1-5-21-111-222-333-1001";

/// In-memory files with an exact-bytes `replace`, which can be made to fail
/// or to land without confirming.
#[derive(Clone, Default)]
struct Memory {
    bytes: Arc<Mutex<BTreeMap<String, Vec<u8>>>>,
    fail_read: Arc<Mutex<bool>>,
    // The replace writes and then reports failure, as an unconfirmed
    // rename does.
    unconfirmed: Arc<Mutex<bool>>,
}

impl Memory {
    fn get(&self, name: &str) -> Option<Vec<u8>> {
        self.bytes.lock().unwrap().get(name).cloned()
    }
    fn put(&self, name: &str, bytes: &[u8]) {
        self.bytes
            .lock()
            .unwrap()
            .insert(name.into(), bytes.to_vec());
    }
}

impl Files for Memory {
    fn read(&self, name: &str) -> io::Result<Option<Vec<u8>>> {
        if name == LEDGER && *self.fail_read.lock().unwrap() {
            return Err(io::ErrorKind::PermissionDenied.into());
        }
        Ok(self.get(name))
    }
    fn replace(&self, name: &str, expected: Option<&[u8]>, bytes: &[u8]) -> io::Result<()> {
        if self.get(name).as_deref() != expected {
            return Err(io::ErrorKind::PermissionDenied.into());
        }
        self.put(name, bytes);
        if *self.unconfirmed.lock().unwrap() {
            return Err(io::ErrorKind::Other.into());
        }
        Ok(())
    }
    fn discard_stage(&self, _: &str) -> io::Result<()> {
        Ok(())
    }
}

fn intent(performance: i32) -> Intent {
    serde_json::from_value(json!({
        "performance": {"value": performance, "source": 1},
        "fans": {"value": 0, "source": 1},
        "lighting": null,
        "battery": null,
    }))
    .unwrap()
}

fn refreshed(prior: &State, performance: i32) -> State {
    prior.refresh_intent(
        Refresh {
            desired: intent(performance),
            policy: 0,
            disclosure: 0,
            destination: Some(Power::BATTERY),
            fingerprint: "OpenBlade:RZ09-0581",
            device_invalidated: false,
            policy_changed: false,
        },
        || Guid::parse("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0").unwrap(),
    )
}

fn open(files: &Memory) -> Arc<Storage> {
    Storage::open(Box::new(files.clone()), SID).expect("open")
}

#[test]
fn a_missing_ledger_is_empty_and_authoritative_and_a_write_creates_it() {
    let files = Memory::default();
    let storage = open(&files);
    let empty = storage.convergence_ledger().expect("authoritative");
    assert!(empty.is_empty());
    // Managed never persists an empty state; refusing one closes nothing.
    assert!(storage.replace_convergence_ledger(&empty, &empty).is_err());
    assert_eq!(storage.convergence_ledger(), Some(empty.clone()));
    let next = refreshed(&empty, 1);
    storage.replace_convergence_ledger(&empty, &next).unwrap();
    assert_eq!(storage.convergence_ledger(), Some(next.clone()));
    assert_eq!(files.get(LEDGER), Some(encode(&next, SID).unwrap().0));
    assert!(storage.convergence_ledger_holds());
    // A reopened store reads what was written.
    assert_eq!(open(&files).convergence_ledger(), Some(next));
}

#[test]
fn a_ledger_it_cannot_admit_closes_convergence_and_is_never_written() {
    let valid = encode(&refreshed(&State::empty(SID), 1), SID).unwrap().0;
    let future = String::from_utf8(valid.clone()).unwrap().replacen(
        "\"schemaVersion\": 1",
        "\"schemaVersion\": 2",
        1,
    );
    let other = encode(
        &refreshed(&State::empty("S-1-5-21-111-222-333-1002"), 1),
        "S-1-5-21-111-222-333-1002",
    )
    .unwrap()
    .0;
    for (name, bytes) in [
        ("future", future.into_bytes()),
        ("other user", other),
        ("garbage", b"{".to_vec()),
    ] {
        let files = Memory::default();
        files.put(LEDGER, &bytes);
        let storage = open(&files);
        assert_eq!(storage.convergence_ledger(), None, "{name}");
        let empty = State::empty(SID);
        assert!(
            storage
                .replace_convergence_ledger(&empty, &refreshed(&empty, 1))
                .is_err(),
            "{name}"
        );
        assert_eq!(files.get(LEDGER), Some(bytes), "{name}");
        assert!(!storage.convergence_ledger_holds(), "{name}");
    }
    // An unreadable ledger closes it too, without refusing the store.
    let files = Memory::default();
    *files.fail_read.lock().unwrap() = true;
    assert_eq!(open(&files).convergence_ledger(), None);
}

#[test]
fn a_ledger_changed_outside_the_service_is_not_overwritten_and_closes() {
    let files = Memory::default();
    let storage = open(&files);
    let empty = storage.convergence_ledger().unwrap();
    let first = refreshed(&empty, 1);
    storage.replace_convergence_ledger(&empty, &first).unwrap();
    // Another writer replaced the file after the store last held it.
    let outside = encode(&refreshed(&empty, 2), SID).unwrap().0;
    files.put(LEDGER, &outside);
    assert!(!storage.convergence_ledger_holds());
    assert_eq!(storage.convergence_ledger(), None);
    assert!(
        storage
            .replace_convergence_ledger(&first, &refreshed(&first, 3))
            .is_err()
    );
    assert_eq!(files.get(LEDGER), Some(outside.clone()));

    // A replace that reaches a changed file is refused by the swap, and the
    // store does not trust either version afterwards.
    let files = Memory::default();
    let storage = open(&files);
    let empty = storage.convergence_ledger().unwrap();
    files.put(LEDGER, &outside);
    assert!(
        storage
            .replace_convergence_ledger(&empty, &refreshed(&empty, 1))
            .is_err()
    );
    assert_eq!(files.get(LEDGER), Some(outside));
    assert_eq!(storage.convergence_ledger(), None);
}

#[test]
fn an_unconfirmed_write_closes_the_ledger_and_a_stale_expectation_writes_nothing() {
    let files = Memory::default();
    let storage = open(&files);
    let empty = storage.convergence_ledger().unwrap();
    let first = refreshed(&empty, 1);
    // A caller holding an older state than the store's writes nothing.
    storage.replace_convergence_ledger(&empty, &first).unwrap();
    assert!(
        storage
            .replace_convergence_ledger(&empty, &refreshed(&empty, 2))
            .is_err()
    );
    assert_eq!(storage.convergence_ledger(), Some(first.clone()));
    // The reservation may have landed: neither version is trusted.
    let reserved = first.reserve(
        &openblade_domain::thermal_convergence::Candidate {
            destination: Power::BATTERY,
            contradiction: Contradiction::PERFORMANCE,
            performance_revision: first.performance_desired_revision.clone().unwrap(),
            fan_revision: first.fan_desired_revision.clone().unwrap(),
        },
        Guid::parse("11111111-2222-3333-4444-555555555555").unwrap(),
        openblade_domain::thermal_convergence::Observation::thermal(
            Some(2),
            Some(0),
            None,
            Some(false),
        ),
        first.effective_intent.clone().unwrap(),
        &[Component::PERFORMANCE],
    );
    *files.unconfirmed.lock().unwrap() = true;
    assert!(
        storage
            .replace_convergence_ledger(&first, &reserved)
            .is_err()
    );
    assert_eq!(storage.convergence_ledger(), None);
    assert!(!storage.convergence_ledger_holds());
    // A reopened store reads the landed reservation, which only a read-only
    // reconciliation retires.
    *files.unconfirmed.lock().unwrap() = false;
    let reopened = open(&files).convergence_ledger().unwrap();
    assert!(reopened.requires_read_only_reconciliation());
    let completed = reopened.complete(
        Power::BATTERY,
        Contradiction::PERFORMANCE,
        &[Component::PERFORMANCE],
        Disposition::EXTERNAL,
    );
    assert!(completed.validate(SID, true).is_ok());
}
