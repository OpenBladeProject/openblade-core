//! The ledger over an in-memory custody: memory follows only written
//! records, every failed write closes it for good, a claim charges the floor
//! for each of its record writes and a settle the last measured one.
use super::*;
use std::sync::Mutex as StdMutex;

const OWNER: &str = "a";

#[derive(Default)]
struct Custody {
    /// Refuses every custody admission.
    refused: AtomicBool,
    record: StdMutex<Option<Record>>,
    fail: AtomicBool,
    delay: StdMutex<Option<Duration>>,
    /// Every replace: the cleanup scope it held, `None` for the ordinary.
    writers: StdMutex<Vec<Option<CleanupScope>>>,
}
impl Custody {
    fn replace(
        &self,
        scope: Option<CleanupScope>,
        expected: Option<&Record>,
        next: &Record,
    ) -> io::Result<()> {
        self.writers.lock().unwrap().push(scope);
        if let Some(delay) = *self.delay.lock().unwrap() {
            std::thread::sleep(delay);
        }
        let mut record = self.record.lock().unwrap();
        if self.fail.load(Ordering::Acquire) || record.as_ref() != expected {
            return Err(io::ErrorKind::PermissionDenied.into());
        }
        *record = Some(next.clone());
        Ok(())
    }
}
impl ReadCustody for Custody {
    fn authorize(&self) -> Option<ReadAdmission> {
        (!self.refused.load(Ordering::Acquire)).then_some(ReadAdmission {
            lifecycle: 1,
            generation: [7; 32],
        })
    }
    fn authorize_cleanup(&self, _: CleanupScope) -> Option<ReadAdmission> {
        ReadCustody::authorize(self)
    }
    fn revoke(&self) {}
    fn retire(&self) -> io::Result<()> {
        Ok(())
    }
    fn confirm_drained(&self) -> io::Result<()> {
        Ok(())
    }
    fn driver_mode_record_read(&self) -> io::Result<Option<Record>> {
        Ok(self.record.lock().unwrap().clone())
    }
    fn driver_mode_record_owner(&self) -> io::Result<String> {
        Ok(OWNER.repeat(64))
    }
    fn driver_mode_record_replace(
        &self,
        expected: Option<&Record>,
        next: &Record,
    ) -> io::Result<()> {
        self.replace(None, expected, next)
    }
    fn driver_mode_record_replace_cleanup(
        &self,
        scope: CleanupScope,
        expected: Option<&Record>,
        next: &Record,
    ) -> io::Result<()> {
        self.replace(Some(scope), expected, next)
    }
}

fn ledger(custody: &Arc<Custody>) -> Ledger {
    Ledger::load(custody.clone(), &Arc::default()).unwrap()
}

const IDS_A: [u8; IDS] = [1, 2, 3, 4];

#[test]
fn memory_follows_only_a_written_record() {
    let custody = Arc::new(Custody::default());
    let ledger = ledger(&custody);
    assert_eq!(ledger.standing(), Standing::Clear);
    ledger
        .claim(Writer::Ordinary, Intent::Acquire, IDS_A)
        .unwrap();
    assert_eq!(ledger.standing(), Standing::AcquireIntent);
    assert_eq!(
        custody.record.lock().unwrap().as_ref().map(Record::ids),
        Some(IDS_A)
    );
    ledger.settle(Writer::Ordinary, Settled::Owned).unwrap();
    assert_eq!(ledger.standing(), Standing::Owned);
    assert_eq!(ledger.claims(), 1);
}

#[test]
fn a_failed_write_closes_the_ledger_for_good() {
    let custody = Arc::new(Custody::default());
    let ledger = ledger(&custody);
    ledger
        .claim(Writer::Ordinary, Intent::Acquire, IDS_A)
        .unwrap();
    custody.fail.store(true, Ordering::Release);
    assert!(ledger.settle(Writer::Ordinary, Settled::Owned).is_err());
    assert_eq!(ledger.standing(), Standing::Closed);
    // A custody that writes again does not reopen it.
    custody.fail.store(false, Ordering::Release);
    assert!(ledger.settle(Writer::Ordinary, Settled::Owned).is_err());
    assert!(ledger.revert(Writer::Ordinary).is_err());
    assert!(
        ledger
            .claim(Writer::Ordinary, Intent::Acquire, [5, 6, 7, 8])
            .is_err()
    );
    assert_eq!(ledger.standing(), Standing::Closed);
    assert_eq!(custody.writers.lock().unwrap().len(), 2);
    let held = custody.record.lock().unwrap().clone().unwrap();
    assert_eq!(held.state(), State::AcquireIntent);
}

#[test]
fn an_edge_the_domain_refuses_closes_the_ledger_without_a_write() {
    let custody = Arc::new(Custody::default());
    let ledger = ledger(&custody);
    ledger
        .claim(Writer::Ordinary, Intent::Acquire, IDS_A)
        .unwrap();
    // An intent over an intent.
    assert!(
        ledger
            .claim(Writer::Ordinary, Intent::Acquire, [5, 6, 7, 8])
            .is_err()
    );
    assert_eq!(ledger.standing(), Standing::Closed);
    assert_eq!(custody.writers.lock().unwrap().len(), 1);
}

#[test]
fn an_unreadable_record_closes_the_ledger_and_fatal_evidence_fails_the_load() {
    struct Refusing(bool);
    impl ReadCustody for Refusing {
        fn authorize(&self) -> Option<ReadAdmission> {
            None
        }
        fn revoke(&self) {}
        fn retire(&self) -> io::Result<()> {
            Ok(())
        }
        fn confirm_drained(&self) -> io::Result<()> {
            Ok(())
        }
        fn driver_mode_record_read(&self) -> io::Result<Option<Record>> {
            if self.0 {
                Err(fatal_admission("injected record custody lost"))
            } else {
                Err(io::ErrorKind::PermissionDenied.into())
            }
        }
    }
    let fatal = Arc::default();
    let closed = Ledger::load(Arc::new(Refusing(false)), &fatal).unwrap();
    assert_eq!(closed.standing(), Standing::Closed);
    assert!(verify_retained_failure(&fatal).is_ok());
    assert!(Ledger::load(Arc::new(Refusing(true)), &fatal).is_err());
    assert!(verify_retained_failure(&fatal).is_err());
}

/// A release that finds an acquire in doubt records the read of Driver
/// its baseline made, then claims, so no intent ever follows an intent.
#[test]
fn a_release_over_an_acquire_in_doubt_resolves_it_first() {
    let custody = Arc::new(Custody::default());
    let ledger = ledger(&custody);
    ledger
        .claim(Writer::Ordinary, Intent::Acquire, IDS_A)
        .unwrap();
    let cleanup = Writer::Cleanup(CleanupScope::OwnedStop);
    assert_eq!(ledger.claim_allowance(Intent::Release), REPLACE_FLOOR * 2);
    ledger
        .claim(cleanup, Intent::Release, [5, 6, 7, 8])
        .unwrap();
    assert_eq!(
        ledger.standing(),
        Standing::ReleaseIntent {
            prior: Settled::Owned
        }
    );
    assert_eq!(
        *custody.writers.lock().unwrap(),
        [
            None,
            Some(CleanupScope::OwnedStop),
            Some(CleanupScope::OwnedStop)
        ]
    );
    ledger.revert(cleanup).unwrap();
    assert_eq!(ledger.standing(), Standing::Owned);
    // On the IDs of the record the claim replaced, the resolved acquire's.
    let held = custody.record.lock().unwrap().clone().unwrap();
    assert_eq!(held.ids(), IDS_A);
}

/// A claim is charged the floor whatever a replace measured, so a slow
/// write never refuses a later claim; the settle after it is charged the
/// last measured replace, which a fast one replaces.
#[test]
fn a_claim_charges_the_floor_and_a_settle_the_last_measured_replace() {
    let custody = Arc::new(Custody::default());
    let ledger = ledger(&custody);
    assert_eq!(ledger.claim_allowance(Intent::Acquire), REPLACE_FLOOR);
    assert_eq!(ledger.write_allowance(), REPLACE_FLOOR);
    let slow = REPLACE_FLOOR * 4;
    *custody.delay.lock().unwrap() = Some(slow);
    ledger
        .claim(Writer::Ordinary, Intent::Acquire, IDS_A)
        .unwrap();
    assert!(ledger.write_allowance() >= slow);
    assert_eq!(ledger.claim_allowance(Intent::Release), REPLACE_FLOOR * 2);
    ledger.settle(Writer::Ordinary, Settled::Clear).unwrap();
    assert_eq!(ledger.claim_allowance(Intent::Acquire), REPLACE_FLOOR);
    *custody.delay.lock().unwrap() = None;
    ledger
        .claim(Writer::Ordinary, Intent::Acquire, [5, 6, 7, 8])
        .unwrap();
    assert_eq!(ledger.write_allowance(), REPLACE_FLOOR);
}

/// A settle whose custody admission is refused before anything is written
/// keeps the intent and leaves the ledger open, so a later read or settle
/// can still resolve it; a claim has no such pass.
#[test]
fn a_settle_refused_before_writing_keeps_the_intent_open() {
    let custody = Arc::new(Custody::default());
    let ledger = ledger(&custody);
    let cleanup = Writer::Cleanup(CleanupScope::OwnedStop);
    ledger.claim(cleanup, Intent::Release, IDS_A).unwrap();
    custody.refused.store(true, Ordering::Release);
    assert!(!ledger.settle(cleanup, Settled::Clear).unwrap());
    assert!(!ledger.revert(cleanup).unwrap());
    let intent = Standing::ReleaseIntent {
        prior: Settled::Clear,
    };
    assert_eq!(ledger.standing(), intent);
    assert_eq!(custody.writers.lock().unwrap().len(), 1);
    custody.refused.store(false, Ordering::Release);
    assert!(ledger.settle(Writer::Ordinary, Settled::Clear).unwrap());
    assert_eq!(ledger.standing(), Standing::Clear);
}

/// A revert writes the intent's prior on the IDs of the record this
/// process's claim replaced, whose 00/04 reply is still the newest. An
/// intent this process did not claim over a record it holds has no such
/// record, so its revert is refused by the domain and closes the ledger.
#[test]
fn a_revert_restores_the_ids_of_the_record_the_claim_replaced() {
    let custody = Arc::new(Custody::default());
    let ledger = ledger(&custody);
    let held = || custody.record.lock().unwrap().clone().unwrap();
    ledger
        .claim(Writer::Ordinary, Intent::Acquire, IDS_A)
        .unwrap();
    assert!(ledger.revert(Writer::Ordinary).unwrap());
    // The first claim replaced no record, so it keeps its own IDs.
    assert_eq!((held().state(), held().ids()), (State::Clear, IDS_A));
    ledger
        .claim(Writer::Ordinary, Intent::Acquire, [5, 6, 7, 8])
        .unwrap();
    assert!(ledger.revert(Writer::Ordinary).unwrap());
    assert_eq!((held().state(), held().ids()), (State::Clear, IDS_A));
    // A settle keeps the intent's own IDs.
    ledger
        .claim(Writer::Ordinary, Intent::Acquire, [9, 10, 11, 12])
        .unwrap();
    assert!(ledger.settle(Writer::Ordinary, Settled::Owned).unwrap());
    assert_eq!(held().ids(), [9, 10, 11, 12]);
    // An intent loaded from an earlier process.
    let cleanup = Writer::Cleanup(CleanupScope::OwnedStop);
    ledger
        .claim(cleanup, Intent::Release, [13, 14, 15, 16])
        .unwrap();
    let loaded = self::ledger(&custody);
    let writes = custody.writers.lock().unwrap().len();
    assert!(loaded.revert(cleanup).is_err());
    assert_eq!(loaded.standing(), Standing::Closed);
    assert_eq!(custody.writers.lock().unwrap().len(), writes);
}
