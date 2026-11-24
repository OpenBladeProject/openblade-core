//! The MCU version inventory: the bounded budget of sent queries since one
//! resume, the lifetime cap, the version an accepted reading confirmed, and
//! the record it completes.
use super::{Inventory, MAXIMUM_ATTEMPTS, MAXIMUM_SENDS};
use crate::discovery::performance::McuVersionRead;
use openblade_protocol::mcu_version::McuVersion;

/// Low-power gate generations: the start, and after one resume.
const FIRST: Option<u64> = Some(0);
const RESUMED: Option<u64> = Some(2);

fn confirmed() -> McuVersionRead {
    McuVersionRead::Confirmed(McuVersion([1, 5, 0, 0]))
}

/// Managed `MaximumMcuVersionReadAttempts`, and the native lifetime cap of
/// four budgets.
#[test]
fn the_budget_is_three_reads_and_the_cap_four_budgets() {
    assert_eq!(MAXIMUM_ATTEMPTS, 3);
    assert_eq!(MAXIMUM_SENDS, 12);
}

/// Each sent query spends one attempt; a job given the counter that sent
/// nothing spends none, and after three sent queries none is asked again.
#[test]
fn only_sent_queries_spend_the_budget() {
    let inventory = Inventory::default();
    for _ in 0..5 {
        assert!(inventory.query(|| FIRST).is_some());
    }
    for _ in 0..MAXIMUM_ATTEMPTS {
        inventory.query(|| FIRST).unwrap().sent_for_tests();
        inventory.record(McuVersionRead::Unconfirmed);
    }
    assert!(inventory.query(|| FIRST).is_none());
    // A query an earlier observation sent counts when it was sent.
    let inventory = Inventory::default();
    let held = inventory.query(|| FIRST).unwrap();
    for _ in 0..MAXIMUM_ATTEMPTS - 1 {
        inventory.query(|| FIRST).unwrap().sent_for_tests();
    }
    assert!(inventory.query(|| FIRST).is_some());
    held.sent_for_tests();
    assert!(inventory.query(|| FIRST).is_none());
}

/// A resume restores the budget, and only the queries sent since it count.
/// Without a gate generation, as with an unreadable gate, nothing is asked.
#[test]
fn a_resume_restores_the_budget() {
    let inventory = Inventory::default();
    for _ in 0..MAXIMUM_ATTEMPTS {
        inventory.query(|| FIRST).unwrap().sent_for_tests();
    }
    assert!(inventory.query(|| FIRST).is_none());
    assert!(inventory.query(|| None).is_none());
    for _ in 0..MAXIMUM_ATTEMPTS {
        inventory.query(|| RESUMED).unwrap().sent_for_tests();
    }
    assert!(inventory.query(|| RESUMED).is_none());
    assert_eq!(inventory.sends_for_tests().count(), 2 * MAXIMUM_ATTEMPTS);
    let inventory = Inventory::default();
    assert!(inventory.query(|| None).is_none());
    assert_eq!(inventory.budget.get(), None);
}

/// However many resumes there are, one source sends at most four budgets,
/// and then no longer reads the gate.
#[test]
fn the_cap_ends_the_reads_across_resumes() {
    let inventory = Inventory::default();
    let mut resume = 0;
    while let Some(sends) = inventory.query(|| Some(resume)) {
        sends.sent_for_tests();
        if inventory.sends_for_tests().count() % MAXIMUM_ATTEMPTS == 0 {
            resume += 2;
        }
        assert!(resume < 100, "the cap never ended the reads");
    }
    assert_eq!(inventory.sends_for_tests().count(), MAXIMUM_SENDS);
    assert_eq!(resume, 8);
    assert!(
        inventory
            .query(|| panic!("the gate is read after the cap"))
            .is_none()
    );
}

/// Once confirmed, the version is never read again, after any resume, and
/// only a confirmed read is kept. A known version no longer reads the gate.
#[test]
fn a_confirmed_version_ends_the_reads_and_completes_the_record() {
    let inventory = Inventory::default();
    let record = super::super::unavailable();
    for read in [McuVersionRead::NotRequested, McuVersionRead::Unconfirmed] {
        inventory.record(read);
        assert_eq!(inventory.apply(record.clone()), record);
        assert!(inventory.query(|| FIRST).is_some());
    }
    inventory.record(confirmed());
    assert!(
        inventory
            .query(|| panic!("the gate is read for a known version"))
            .is_none()
    );
    assert!(inventory.query(|| RESUMED).is_none());
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
