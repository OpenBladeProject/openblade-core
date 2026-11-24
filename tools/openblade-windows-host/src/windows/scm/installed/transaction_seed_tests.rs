//! What the journal-fixture histories quarantine at startup.
use super::*;
use serde_json::Value;
use std::{cell::RefCell, collections::VecDeque};

fn fixture(name: &str) -> Value {
    serde_json::from_str(match name {
        "fan" => include_str!("../../../../tests/data/fan-history-v3.json"),
        "battery" => include_str!("../../../../tests/data/battery-history-v1.json"),
        "tfc" => include_str!("../../../../tests/data/temporary-charge-history-v1.json"),
        _ => unreachable!(),
    })
    .unwrap()
}

/// The journal `key` of fixture `name`, or the `tfc` case it names.
fn journal(name: &str, key: &str) -> Vec<u8> {
    let v = fixture(name);
    let raw = if name == "tfc" {
        v["cases"]
            .as_array()
            .unwrap()
            .iter()
            .find(|case| case["name"] == key)
            .unwrap()["journal"]
            .clone()
    } else {
        v[key].clone()
    };
    raw.as_str().unwrap().as_bytes().to_vec()
}

/// The first `rows` rows of `journal`.
fn prefix(journal: &[u8], rows: usize) -> Vec<u8> {
    journal
        .split_inclusive(|b| *b == b'\n')
        .take(rows)
        .flatten()
        .copied()
        .collect()
}

fn range(ids: std::ops::RangeInclusive<u8>) -> Vec<u8> {
    ids.collect()
}

fn concat(parts: &[Vec<u8>]) -> Vec<u8> {
    parts.concat()
}

/// The first row of journal `key` in fixture `name`.
fn first(name: &str, key: &str) -> Entry {
    let raw = journal(name, key);
    serde_json::from_slice(raw.split(|b| *b == b'\n').next().unwrap()).unwrap()
}

/// The rows of request `n` over `entry`'s record, reaching each of `progress`.
fn request(mut entry: Entry, n: u32, progress: &[u8]) -> Vec<u8> {
    entry.request = format!("aaaaaaaa-bbbb-cccc-dddd-{n:012}");
    entry.intent = if let Some(fan) = &entry.fan {
        fan.hash()
    } else if let Some(battery) = &entry.battery {
        battery.hash()
    } else {
        entry.tfc.as_ref().unwrap().hash()
    }
    .unwrap();
    let mut bytes = Vec::new();
    for p in progress {
        entry.progress = *p;
        bytes.extend(serde_json::to_vec(&entry).unwrap());
        bytes.push(b'\n');
    }
    bytes
}

/// The fixture's manual fan entry (preflight 1 to 4, chain 5 to 16, cleanup
/// 17 to 26) with every ID moved up by `by`.
fn fan(by: u8) -> Entry {
    let mut entry = first("fan", "owned");
    let record = entry.fan.as_mut().unwrap();
    let pairs = record.preflight.iter_mut();
    for pair in pairs.chain(&mut record.ids).chain(&mut record.cleanup) {
        pair.iter_mut().for_each(|id| *id += by);
    }
    entry
}

/// A battery request `n` over `ids` whose rows reach each of `progress`.
fn battery_rows(n: u32, ids: [u8; 14], progress: &[u8]) -> Vec<u8> {
    let mut entry = first("battery", "standalone");
    entry.battery.as_mut().unwrap().ids = ids;
    request(entry, n, progress)
}

fn ids_from(first: u8) -> [u8; 14] {
    std::array::from_fn(|i| first + i as u8)
}

/// `rows` admits `journal`, and the seed quarantines exactly `expected`.
fn seeds(journal: &[u8], expected: &[u8]) {
    crate::thermal_history::rows(journal, |_, _| Ok(())).unwrap();
    assert_eq!(unanswered(journal).unwrap(), expected);
}

#[test]
fn the_largest_fan_record_names_the_derived_maximum() {
    // A manual entry under selector 2: preflight, six chain pairs, cleanup.
    let entry = first("fan", "owned");
    assert_eq!(sent(&entry), Some((0, range(1..=26))));
    assert_eq!(FAN_CLEANUP, entry.fan.unwrap().cleanup.len() * 2);
    // The sizes the limit's notes and the thermal history guide state.
    assert_eq!((FAN, LIVE, DEMAND, LIMIT), (26, 78, 66, 80));
    assert_eq!(DRIVER_MODE, 4);
}

#[test]
fn each_familys_newest_request_is_quarantined_whatever_its_progress() {
    // A reply that was read stays in the mailbox, so a confirmed request's
    // IDs, its preflight's included, are quarantined like an unsettled one's.
    let fan = range(1..=26);
    for key in [
        "owned",
        "released",
        "observedAutomatic",
        "retiredThenObservedAutomatic",
    ] {
        seeds(&journal("fan", key), &fan);
    }
    // A chain stopped at 11 and a cleanup stopped at 41.
    seeds(&prefix(&journal("fan", "owned"), 3), &fan);
    seeds(&prefix(&journal("fan", "released"), 13), &fan);
    // A confirmed battery write, and one with only its Prepared row.
    let battery = journal("battery", "standalone");
    seeds(&battery, &range(100..=113));
    seeds(&prefix(&battery, 1), &range(100..=113));
    // Fan and battery requests in one journal, each the newest of its family.
    for key in ["interleaved", "released"] {
        seeds(
            &journal("battery", key),
            &concat(&[range(1..=26), range(100..=113)]),
        );
    }
    // A settled disable, a no-op and a live enable with its reservation.
    seeds(
        &journal("tfc", "cancel-off-unknown-policy"),
        &range(100..=107),
    );
    seeds(
        &journal("tfc", "noop-false-unknown-policy"),
        &range(100..=107),
    );
    seeds(&journal("tfc", "enable"), &range(100..=115));
    seeds(b"", &[]);
}

#[test]
fn earlier_settled_requests_of_a_family_quarantine_nothing() {
    let mut two = battery_rows(1, ids_from(1), &[0, 60, 61, 62]);
    two.extend(battery_rows(2, ids_from(101), &[0, 60, 61, 62]));
    seeds(&two, &ids_from(101));
    // The source cancel is the newest; the enable it ended is not.
    seeds(
        &journal("tfc", "source-cancel-origin-reserved"),
        &range(108..=115),
    );
}

#[test]
fn live_owners_older_than_their_familys_newest_request_keep_their_reservations() {
    // A manual fan owner, then a fan request retired at its Prepared row: a
    // Stop cleanup can send the owner's IDs and fail before its first row.
    let mut fans = journal("fan", "owned");
    fans.extend(request(fan(100), 2, &[0, 6]));
    seeds(&fans, &concat(&[range(101..=126), range(17..=26)]));
    // An enable that found the charge already on (73), then a disable
    // retired at its Prepared row: a source cancel can send the enable's
    // reserved IDs before its own Prepared row.
    let mut enable = first("tfc", "enable");
    enable.tfc.as_mut().unwrap().baseline = 4;
    let mut disable = first("tfc", "cancel-off-unknown-policy");
    disable.tfc.as_mut().unwrap().ids = std::array::from_fn(|i| 120 + i as u8);
    let mut charge = request(enable, 1, &[0, 73]);
    charge.extend(request(disable, 2, &[0, 6]));
    seeds(&charge, &concat(&[range(120..=127), range(108..=115)]));
}

#[test]
fn known_unsent_and_superseded_sets_are_quarantined_newest_first() {
    // The newest cancel names its fresh IDs and the superseded reserved set.
    for key in [
        "source-cancel-fresh-ids-after-known-unsent",
        "source-cancel-fresh-ids-first",
    ] {
        seeds(
            &journal("tfc", key),
            &concat(&[range(120..=127), range(108..=115)]),
        );
    }
    // A fresh-ID cancel retired at 6, before a later enable and its cancel.
    let resets = journal("tfc", "fresh-ids-bound-resets-at-next-enable");
    seeds(
        &resets,
        &concat(&[range(140..=147), range(120..=127), range(108..=115)]),
    );
    // A fresh-ID cancel that confirmed, before the same later enable and
    // cancel: only its superseded set, which an earlier attempt sent.
    let later: Vec<u8> = resets
        .split_inclusive(|b| *b == b'\n')
        .skip(6)
        .flatten()
        .copied()
        .collect();
    seeds(
        &concat(&[journal("tfc", "source-cancel-fresh-ids-first"), later]),
        &concat(&[range(140..=147), range(108..=115)]),
    );
    // A fan request retired at 6 before the newest: its preflight, whose
    // replies it read, and its chain IDs, but not the cleanup it reserved.
    let mut fans = request(fan(100), 1, &[0, 6]);
    fans.extend(request(fan(150), 2, &[0, 6]));
    seeds(&fans, &concat(&[range(151..=176), range(101..=116)]));
    // The owner's settle-only 51 is the newest fan row but sends no 07/8C,
    // so the newest 07/8C reply is the retired request's preflight.
    let owned = journal("fan", "retiredThenObservedAutomatic");
    let rows: Vec<&[u8]> = owned.split_inclusive(|b| *b == b'\n').collect();
    let retired = concat(&[
        rows[..10].concat(),
        request(fan(100), 2, &[0, 6]),
        rows[12].to_vec(),
    ]);
    seeds(&retired, &concat(&[range(1..=26), range(101..=116)]));
    // A superseded set newest in its family stays live, ahead of newer
    // known-not-submitted battery history that fills the limit.
    let mut crowded = journal("tfc", "source-cancel-fresh-ids-first");
    for k in 0..6u8 {
        crowded.extend(battery_rows(
            200 + u32::from(k),
            ids_from(1 + 14 * k),
            &[0, 6],
        ));
    }
    crate::thermal_history::rows(&crowded, |_, _| Ok(())).unwrap();
    let seeded = unanswered(&crowded).unwrap();
    assert_eq!(seeded.len(), LIMIT);
    assert!(range(108..=115).iter().all(|id| seeded.contains(id)));
}

#[test]
fn the_seed_is_bounded_and_keeps_the_live_set_whole() {
    // Ten known-not-submitted battery requests name 140 IDs.
    let mut history = Vec::new();
    for k in 0..10u8 {
        history.extend(battery_rows(
            u32::from(k) + 1,
            ids_from(1 + 14 * k),
            &[0, 6],
        ));
    }
    crate::thermal_history::rows(&history, |_, _| Ok(())).unwrap();
    let seeded = unanswered(&history).unwrap();
    assert_eq!(seeded.len(), LIMIT);
    // The newest come first, until the limit.
    let newest_first = |k: &u8| ids_from(1 + 14 * k);
    let expected: Vec<u8> = (0..10u8)
        .rev()
        .flat_map(|k| newest_first(&k))
        .take(LIMIT)
        .collect();
    assert_eq!(seeded, expected);
    // An unsettled final request is kept whole ahead of the history.
    let mut unsettled = history.clone();
    unsettled.extend(battery_rows(99, ids_from(200), &[0, 60]));
    let seeded = unanswered(&unsettled).unwrap();
    assert_eq!(seeded.len(), LIMIT);
    assert_eq!(seeded[..14], ids_from(200));
    assert_eq!(seeded[14..28], ids_from(1 + 14 * 9));
}

/// Seeds from one result per journal read, with no Driver-mode record: what
/// it quarantined, how many reads it made and the fatal failure it kept.
fn seed(reads: Vec<io::Result<Option<Vec<u8>>>>) -> (Vec<Vec<u8>>, usize, Option<&'static str>) {
    let (calls, made, _, fatal) = seed_with(vec![Ok(None)], reads);
    (calls, made, fatal)
}

type RecordRead = io::Result<Option<[u8; DRIVER_MODE]>>;

/// Seeds from one result per Driver-mode record read and per journal read:
/// what it quarantined, how many journal and record reads it made and the
/// fatal failure it kept.
fn seed_with(
    records: Vec<RecordRead>,
    reads: Vec<io::Result<Option<Vec<u8>>>>,
) -> (Vec<Vec<u8>>, usize, usize, Option<&'static str>) {
    let (offered, offered_records) = (reads.len(), records.len());
    let reads = RefCell::new(VecDeque::from(reads));
    let records = RefCell::new(VecDeque::from(records));
    let calls = RefCell::new(Vec::new());
    let fatal = Mutex::new(None);
    quarantine_unanswered(
        &|| reads.borrow_mut().pop_front().expect("one read too many"),
        &|| {
            records
                .borrow_mut()
                .pop_front()
                .expect("one record read too many")
        },
        &fatal,
        &|ids| calls.borrow_mut().push(ids.to_vec()),
    );
    let made = offered - reads.borrow().len();
    let made_records = offered_records - records.borrow().len();
    (
        calls.into_inner(),
        made,
        made_records,
        fatal.into_inner().unwrap(),
    )
}

fn failed(kind: io::ErrorKind) -> io::Result<Option<Vec<u8>>> {
    Err(kind.into())
}

#[test]
fn an_unreadable_absent_or_invalid_journal_quarantines_nothing() {
    let battery = journal("battery", "standalone");
    // A row that no Prepared row precedes is invalid history.
    let tail: Vec<u8> = battery
        .split_inclusive(|b| *b == b'\n')
        .skip(1)
        .flatten()
        .copied()
        .collect();
    for (reads, made) in [
        (
            vec![
                failed(io::ErrorKind::PermissionDenied),
                failed(io::ErrorKind::PermissionDenied),
            ],
            2,
        ),
        (vec![Ok(None)], 1),
        (vec![Ok(Some(b"not a journal\n".to_vec()))], 1),
        (vec![Ok(Some(tail))], 1),
    ] {
        assert_eq!(seed(reads), (Vec::new(), made, None));
    }
    assert_eq!(
        seed(vec![Ok(Some(prefix(&battery, 2)))]).0,
        [range(100..=113)]
    );
}

#[test]
fn a_failed_journal_read_is_retried_once() {
    let battery = journal("battery", "standalone");
    let (seeded, reads, fatal) = seed(vec![
        failed(io::ErrorKind::Interrupted),
        Ok(Some(battery.clone())),
    ]);
    assert_eq!((seeded, reads, fatal), (vec![range(100..=113)], 2, None));
    let (seeded, reads, fatal) = seed(vec![
        failed(io::ErrorKind::Interrupted),
        failed(io::ErrorKind::Interrupted),
        Ok(Some(battery)),
    ]);
    assert_eq!((seeded, reads, fatal), (Vec::new(), 2, None));
}

#[test]
fn a_fatal_journal_admission_failure_is_kept_and_not_retried() {
    let fatal = super::super::thermal_mutation::fatal_admission("journal owner changed");
    let (seeded, reads, kept) = seed(vec![Err(fatal), Ok(Some(journal("battery", "standalone")))]);
    assert_eq!(
        (seeded, reads, kept),
        (Vec::new(), 1, Some("journal owner changed"))
    );
}

/// The IDs of a Driver-mode job: baseline, setter, readback and spare.
const RECORD: [u8; DRIVER_MODE] = [240, 241, 242, 243];

#[test]
fn the_driver_mode_records_ids_come_first_and_the_live_set_stays_whole() {
    // Alone, it is the whole seed.
    assert_eq!(seeded(Some(RECORD), Vec::new()), RECORD);
    // Ahead of a full journal seed, whose first `LIVE - DRIVER_MODE` IDs
    // stand for the journal's largest live set: the record's four push only
    // the oldest history out.
    let journal: Vec<u8> = (1..=LIMIT as u8).collect();
    let seed = seeded(Some(RECORD), journal.clone());
    assert_eq!(seed.len(), LIMIT);
    assert_eq!(seed[..DRIVER_MODE], RECORD);
    assert_eq!(seed[DRIVER_MODE..], journal[..LIMIT - DRIVER_MODE]);
    // An ID both name is quarantined once, in the record's place.
    let shared = seeded(Some([1, 2, 3, 4]), journal.clone());
    assert_eq!(shared, journal);
    // Without a record, the journal's seed is unchanged.
    assert_eq!(seeded(None, journal.clone()), journal);
}

#[test]
fn a_driver_mode_record_is_seeded_beside_any_journal_outcome() {
    let battery = journal("battery", "standalone");
    // Absent, unreadable and invalid journals each leave the record alone.
    for (reads, made) in [
        (vec![Ok(None)], 1),
        (
            vec![
                failed(io::ErrorKind::PermissionDenied),
                failed(io::ErrorKind::PermissionDenied),
            ],
            2,
        ),
        (
            vec![Ok(Some(
                b"not a journal
"
                .to_vec(),
            ))],
            1,
        ),
    ] {
        assert_eq!(
            seed_with(vec![Ok(Some(RECORD))], reads),
            (vec![RECORD.to_vec()], made, 1, None)
        );
    }
    // Beside a journal, the record's IDs come first.
    assert_eq!(
        seed_with(vec![Ok(Some(RECORD))], vec![Ok(Some(battery))]),
        (
            vec![concat(&[RECORD.to_vec(), range(100..=113)])],
            1,
            1,
            None
        )
    );
}

#[test]
fn a_failed_driver_mode_record_read_is_retried_once_and_adds_nothing() {
    let battery = journal("battery", "standalone");
    let transient = || Err(io::ErrorKind::Interrupted.into());
    // A corrupt, unknown or unbound record is refused (`PermissionDenied`),
    // which a retry does not change.
    let refused = || Err(io::ErrorKind::PermissionDenied.into());
    assert_eq!(
        seed_with(vec![transient(), Ok(Some(RECORD))], vec![Ok(None)]),
        (vec![RECORD.to_vec()], 1, 2, None)
    );
    assert_eq!(
        seed_with(vec![refused(), refused()], vec![Ok(None)]),
        (Vec::new(), 1, 2, None)
    );
    // The journal is still seeded.
    assert_eq!(
        seed_with(vec![refused(), refused()], vec![Ok(Some(battery))]),
        (vec![range(100..=113)], 1, 2, None)
    );
}

#[test]
fn a_fatal_driver_mode_record_failure_is_kept_and_not_retried() {
    let fatal = super::super::thermal_mutation::fatal_admission("record identity changed");
    let battery = journal("battery", "standalone");
    assert_eq!(
        seed_with(vec![Err(fatal)], vec![Ok(Some(battery))]),
        (
            vec![range(100..=113)],
            1,
            1,
            Some("record identity changed")
        )
    );
}
