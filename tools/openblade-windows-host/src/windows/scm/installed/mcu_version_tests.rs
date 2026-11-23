//! The MCU version inventory: the bounded budget of sent queries under one
//! read admission, the version an accepted reading confirmed, and the record
//! it completes.
use super::{Inventory, MAXIMUM_ATTEMPTS};
use crate::discovery::performance::{McuVersionRead, ReadAdmission};
use openblade_protocol::mcu_version::McuVersion;

const FIRST: ReadAdmission = ReadAdmission {
    lifecycle: 1,
    generation: [7; 32],
};
const RESUMED: ReadAdmission = ReadAdmission {
    lifecycle: 2,
    ..FIRST
};

fn confirmed() -> McuVersionRead {
    McuVersionRead::Confirmed(McuVersion([1, 5, 0, 0]))
}

/// Managed `MaximumMcuVersionReadAttempts`.
#[test]
fn the_budget_is_three_reads() {
    assert_eq!(MAXIMUM_ATTEMPTS, 3);
}

/// Each sent query spends one attempt; a job given the counter that sent
/// nothing spends none, and after three sent queries none is asked again.
#[test]
fn only_sent_queries_spend_the_budget() {
    let inventory = Inventory::default();
    for _ in 0..5 {
        assert!(inventory.query(FIRST).is_some());
    }
    for _ in 0..MAXIMUM_ATTEMPTS {
        inventory.query(FIRST).unwrap().sent_for_tests();
        inventory.record(McuVersionRead::Unconfirmed);
    }
    assert!(inventory.query(FIRST).is_none());
    // A query an earlier observation sent counts when it was sent.
    let inventory = Inventory::default();
    let held = inventory.query(FIRST).unwrap();
    for _ in 0..MAXIMUM_ATTEMPTS - 1 {
        inventory.query(FIRST).unwrap().sent_for_tests();
    }
    assert!(inventory.query(FIRST).is_some());
    held.sent_for_tests();
    assert!(inventory.query(FIRST).is_none());
}

/// A new admission, as resume mints, restores the budget, and only the
/// queries sent under it count.
#[test]
fn a_new_admission_restores_the_budget() {
    let inventory = Inventory::default();
    for _ in 0..MAXIMUM_ATTEMPTS {
        inventory.query(FIRST).unwrap().sent_for_tests();
    }
    assert!(inventory.query(FIRST).is_none());
    for _ in 0..MAXIMUM_ATTEMPTS {
        inventory.query(RESUMED).unwrap().sent_for_tests();
    }
    assert!(inventory.query(RESUMED).is_none());
    assert_eq!(inventory.sends_for_tests().count(), 2 * MAXIMUM_ATTEMPTS);
}

/// Once confirmed, the version is never read again, under any admission, and
/// only a confirmed read is kept.
#[test]
fn a_confirmed_version_ends_the_reads_and_completes_the_record() {
    let inventory = Inventory::default();
    let record = super::super::unavailable();
    for read in [McuVersionRead::NotRequested, McuVersionRead::Unconfirmed] {
        inventory.record(read);
        assert_eq!(inventory.apply(record.clone()), record);
        assert!(inventory.query(FIRST).is_some());
    }
    inventory.record(confirmed());
    assert!(inventory.query(FIRST).is_none());
    assert!(inventory.query(RESUMED).is_none());
    let mut expect = record.clone();
    expect.mcu_version = "1.5.0.0".into();
    assert_eq!(inventory.apply(record), expect);
    // A later failure does not clear it.
    inventory.record(McuVersionRead::Unconfirmed);
    assert_eq!(
        inventory.apply(super::super::unavailable()).mcu_version,
        "1.5.0.0"
    );
}
