//! The ledger over an in-memory custody: memory follows only written
//! records, every failed write closes it for good, and a claim charges its
//! record writes at the longest of the floor and the last measured one.
use super::*;
use std::sync::Mutex as StdMutex;

const OWNER: &str = "a";

#[derive(Default)]
struct Custody {
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
}

#[test]
fn a_claim_charges_the_floor_until_a_slower_replace_is_measured() {
    let custody = Arc::new(Custody::default());
    let ledger = ledger(&custody);
    assert_eq!(ledger.claim_allowance(Intent::Acquire), REPLACE_FLOOR);
    let slow = REPLACE_FLOOR * 4;
    *custody.delay.lock().unwrap() = Some(slow);
    ledger
        .claim(Writer::Ordinary, Intent::Acquire, IDS_A)
        .unwrap();
    assert!(ledger.claim_allowance(Intent::Acquire) >= slow);
    *custody.delay.lock().unwrap() = None;
    ledger.settle(Writer::Ordinary, Settled::Clear).unwrap();
    assert_eq!(ledger.claim_allowance(Intent::Acquire), REPLACE_FLOOR);
}
