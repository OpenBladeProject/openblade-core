//! The shared record vectors, which the C# Setup reader replays too, and the
//! complete transition table.
use super::*;
use serde_json::Value;

fn vectors() -> Value {
    serde_json::from_str(include_str!(
        "../../../../tools/openblade-windows-host/tests/data/driver-mode-record-v1.json"
    ))
    .unwrap()
}

fn text(value: &Value) -> &[u8] {
    value.as_str().unwrap().as_bytes()
}

fn state(name: &str) -> State {
    State::parse(name).unwrap()
}

#[test]
fn the_vectors_name_this_format() {
    let v = vectors();
    assert_eq!(v["header"], HEADER);
    assert_eq!(v["contract"], CONTRACT);
    assert_eq!(v["maxBytes"], MAX_BYTES);
}

#[test]
fn every_admitted_vector_parses_to_its_fields_and_back_to_its_bytes() {
    let v = vectors();
    let records = v["records"].as_object().unwrap();
    assert!(records.len() >= 10);
    for (name, vector) in records {
        let bytes = text(&vector["bytes"]);
        let record = Record::parse(bytes).unwrap_or_else(|e| std::panic!("{name}: {e}"));
        assert_eq!(record.bytes(), bytes, "{name}");
        assert_eq!(
            record.sequence().to_string(),
            vector["sequence"].as_str().unwrap(),
            "{name}"
        );
        assert_eq!(record.owner(), vector["owner"], "{name}");
        assert_eq!(record.state(), state(vector["state"].as_str().unwrap()));
        assert_eq!(
            State::from(record.prior()),
            state(vector["prior"].as_str().unwrap())
        );
        let ids: Vec<u8> = vector["ids"]
            .as_array()
            .unwrap()
            .iter()
            .map(|id| id.as_u64().unwrap() as u8)
            .collect();
        assert_eq!(record.ids()[..], ids[..], "{name}");
    }
}

#[test]
fn the_known_answer_bytes_are_pinned() {
    let record = Record::claim(None, Intent::Acquire, &"a".repeat(64), [11, 12, 13, 14]).unwrap();
    assert_eq!(
        record.bytes(),
        b"OpenBlade.NativeDriverMode.v1\n\
          service-keyboard-driver-mode-v1/rz09-0581-v1\n\
          1\n\
          aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n\
          AcquireIntent\n\
          Clear\n\
          11,12,13,14\n"
    );
    assert_eq!(
        record.bytes(),
        text(&vectors()["records"]["acquireClaimed"]["bytes"])
    );
}

#[test]
fn every_rejected_vector_and_every_truncation_is_refused() {
    let v = vectors();
    let rejected = v["rejected"].as_array().unwrap();
    assert!(rejected.len() >= 40);
    for vector in rejected {
        let name = vector["name"].as_str().unwrap();
        assert!(Record::parse(text(&vector["bytes"])).is_err(), "{name}");
    }
    let oversize = rejected
        .iter()
        .find(|vector| vector["name"] == "oversize")
        .unwrap();
    assert!(text(&oversize["bytes"]).len() > MAX_BYTES);
    // Every prefix of a valid record is incomplete, so a torn write is
    // never read as some shorter record.
    for (name, vector) in v["records"].as_object().unwrap() {
        let bytes = text(&vector["bytes"]);
        for n in 0..bytes.len() {
            assert!(Record::parse(&bytes[..n]).is_err(), "{name} cut at {n}");
        }
    }
}

#[test]
fn every_byte_outside_printable_ascii_and_newline_is_refused() {
    let bytes = Record::claim(None, Intent::Release, &"b".repeat(64), [1, 2, 3, 4])
        .unwrap()
        .bytes();
    for b in (0..=255u8).filter(|b| *b != b'\n' && !(0x20..=0x7e).contains(b)) {
        let mut changed = bytes.clone();
        changed[40] = b;
        assert!(Record::parse(&changed).is_err(), "{b:#04x}");
    }
}

#[test]
fn every_transition_vector_is_honoured_by_follows() {
    let v = vectors();
    let transitions = v["transitions"].as_array().unwrap();
    assert!(transitions.len() >= 30);
    for t in transitions {
        let name = t["name"].as_str().unwrap();
        let prior = (!t["prior"].is_null()).then(|| Record::parse(text(&t["prior"])).unwrap());
        let next = Record::parse(text(&t["next"])).unwrap_or_else(|e| std::panic!("{name}: {e}"));
        assert_eq!(
            follows(prior.as_ref(), &next),
            t["follows"].as_bool().unwrap(),
            "{name}"
        );
    }
}

/// Every valid record over a small universe: each state with each prior,
/// two ID sets and each of `sequences`.
fn universe(sequences: std::ops::RangeInclusive<u64>) -> Vec<Record> {
    let mut all = Vec::new();
    for state in [
        State::AcquireIntent,
        State::Owned,
        State::ReleaseIntent,
        State::Clear,
    ] {
        for prior in [Settled::Owned, Settled::Clear] {
            for ids in [[1, 2, 3, 4], [5, 6, 7, 8]] {
                for sequence in sequences.clone() {
                    let record = Record {
                        sequence,
                        owner: "c".repeat(64),
                        state,
                        prior,
                        ids,
                    };
                    if record.validate().is_ok() {
                        all.push(record);
                    }
                }
            }
        }
    }
    all
}

/// The edges the module documents, written out independently of `follows`.
fn expected(prior: Option<&Record>, next: &Record) -> bool {
    use State::{AcquireIntent as A, Clear as C, Owned as O, ReleaseIntent as R};
    let Some(p) = prior else {
        return next.sequence == 1 && matches!(next.state, A | R) && next.prior == Settled::Clear;
    };
    if next.sequence != p.sequence + 1 {
        return false;
    }
    let kept = next.ids == p.ids;
    // Back to the intent's prior: a revert, on any IDs.
    let to_prior = next.prior == p.prior;
    match (p.state, next.state) {
        (C, A | R) => next.prior == Settled::Clear,
        (O, A | R) => next.prior == Settled::Owned,
        (A, O) | (A, C) | (R, C) => kept || to_prior,
        (O, C) => kept,
        (R, O) => p.prior == Settled::Owned,
        _ => false,
    }
}

#[test]
fn follows_admits_exactly_the_documented_edges() {
    let priors = universe(1..=3);
    let (mut first, mut admitted) = (0, 0);
    for next in &universe(1..=4) {
        let actual = follows(None, next);
        assert_eq!(actual, expected(None, next), "{next:?}");
        first += usize::from(actual);
        for prior in &priors {
            let actual = follows(Some(prior), next);
            assert_eq!(actual, expected(Some(prior), next), "{prior:?} -> {next:?}");
            admitted += usize::from(actual);
        }
    }
    // The first record is either intent on either ID set.
    assert_eq!(first, 4);
    // Over 3 sequences and 2 ID sets: a Clear prior claims either intent on
    // either ID set (4 edges); Owned the same and its Normal read (5);
    // AcquireIntent, under either prior, reverts to it on either ID set (2)
    // and settles to the other state on its own (1); ReleaseIntent reverts
    // to an Owned prior on either ID set or clears (3), and with a Clear
    // prior reverts or clears on either ID set (2).
    assert_eq!(admitted, 3 * 2 * (4 + 5 + 2 * 3 + 3 + 2));
}

/// A revert returns the intent to its prior on the IDs of the record the
/// claim replaced, whose 00/04 reply is still the newest, and only over
/// that record: its predecessor, in the intent's prior state.
#[test]
fn a_revert_restores_the_replaced_records_ids() {
    let owner = "d".repeat(64);
    let first = Record::claim(None, Intent::Acquire, &owner, [1, 2, 3, 4]).unwrap();
    // No record before the first claim: the intent keeps its own IDs.
    let none = first.revert(None, &owner).unwrap();
    assert_eq!((none.state(), none.ids()), (State::Clear, [1, 2, 3, 4]));
    let owned = first.settle(Settled::Owned, &owner).unwrap();
    for intent in [Intent::Release, Intent::Acquire] {
        let claimed = Record::claim(Some(&owned), intent, &owner, [5, 6, 7, 8]).unwrap();
        let reverted = claimed.revert(Some(&owned), &owner).unwrap();
        assert_eq!(
            (reverted.state(), reverted.sequence(), reverted.ids()),
            (State::Owned, 4, [1, 2, 3, 4])
        );
        assert!(follows(Some(&claimed), &reverted));
        assert_eq!(Record::parse(&reverted.bytes()).unwrap(), reverted);
        // Not the record it was claimed over, or no record at all.
        assert!(claimed.revert(Some(&first), &owner).is_err());
        assert!(claimed.revert(Some(&none), &owner).is_err());
        assert!(claimed.revert(None, &owner).is_err());
        // A settled record has nothing to revert.
        assert!(reverted.revert(Some(&claimed), &owner).is_err());
    }
    // A settle or resolution into the prior keeps the intent's IDs.
    let claimed = Record::claim(Some(&owned), Intent::Release, &owner, [5, 6, 7, 8]).unwrap();
    assert_eq!(
        claimed.settle(Settled::Owned, &owner).unwrap().ids(),
        [5, 6, 7, 8]
    );
}

#[test]
fn claim_and_settle_make_only_admitted_records() {
    let owner = "d".repeat(64);
    let claimed = Record::claim(None, Intent::Acquire, &owner, [1, 2, 3, 4]).unwrap();
    assert_eq!((claimed.sequence(), claimed.prior()), (1, Settled::Clear));
    // A claim over an intent, an invalid owner or an invalid ID set.
    assert!(Record::claim(Some(&claimed), Intent::Release, &owner, [5, 6, 7, 8]).is_err());
    assert!(Record::claim(None, Intent::Acquire, "D", [1, 2, 3, 4]).is_err());
    assert!(Record::claim(None, Intent::Acquire, &owner, [1, 2, 3, 1]).is_err());
    assert!(Record::claim(None, Intent::Acquire, &owner, [0, 2, 3, 4]).is_err());
    assert!(Record::claim(None, Intent::Acquire, &owner, [1, 2, 3, 255]).is_err());
    let owned = claimed.settle(Settled::Owned, &owner).unwrap();
    assert_eq!((owned.sequence(), owned.ids()), (2, [1, 2, 3, 4]));
    assert!(owned.settle(Settled::Owned, &owner).is_err());
    let release = Record::claim(Some(&owned), Intent::Release, &owner, [9, 10, 11, 12]).unwrap();
    assert_eq!(release.prior(), Settled::Owned);
    // A later generation resolves it.
    let other = "e".repeat(64);
    let reverted = release.settle(Settled::Owned, &other).unwrap();
    assert_eq!(
        (reverted.owner(), reverted.ids()),
        (&*other, [9, 10, 11, 12])
    );
    let clear = reverted.settle(Settled::Clear, &other).unwrap();
    assert!(clear.settle(Settled::Owned, &other).is_err());
    // A release claimed over Clear reverts only to Clear.
    let release = Record::claim(Some(&clear), Intent::Release, &other, [1, 2, 3, 4]).unwrap();
    assert!(release.settle(Settled::Owned, &other).is_err());
    assert!(release.settle(Settled::Clear, &other).is_ok());
    // Each result parses back to itself.
    for record in [claimed, owned, reverted, clear, release] {
        assert_eq!(Record::parse(&record.bytes()).unwrap(), record);
    }
}

#[test]
fn the_largest_sequence_has_no_successor() {
    let v = vectors();
    let last = Record::parse(text(&v["records"]["largestSequence"]["bytes"])).unwrap();
    assert_eq!(last.sequence(), u64::MAX);
    assert!(Record::claim(Some(&last), Intent::Acquire, last.owner(), [1, 2, 3, 4]).is_err());
}
