use super::*;

const FIXTURE: &str =
    include_str!("../../../../tests/OpenBlade.Service.Tests/WindowsReplay/v1/service-runtime.json");

fn baseline() -> Value {
    let fixture: Value = serde_json::from_str(include_str!(
        "../../../../tests/OpenBlade.Contracts.Tests/WindowsReplay/v1/service-status-event.json"
    ))
    .unwrap();
    fixture["status"].clone()
}

#[test]
fn restart_observations_settle_like_managed_replay_without_any_setter() {
    let fixture: Value = serde_json::from_str(FIXTURE).unwrap();
    for (id, expected_effect) in [
        (
            "restart-read-unavailable",
            Effect::ReadUnavailable(LaptopGetter::FunctionKeyPrimary),
        ),
        (
            "restart-matching-readback",
            Effect::JournalSettled(LaptopGetter::FunctionKeyPrimary, EntryChange::Remove),
        ),
        (
            "restart-differing-readback",
            Effect::JournalSettled(LaptopGetter::FunctionKeyPrimary, EntryChange::KeepTombstone),
        ),
    ] {
        let case = fixture["laptopRecovery"]
            .as_array()
            .unwrap()
            .iter()
            .find(|case| case["id"] == id)
            .unwrap();
        assert_eq!(case["input"]["action"], "Restart");
        assert_eq!(case["input"]["identity"], IDENTITY);
        assert_eq!(case["input"]["requested"], "FunctionKeys");
        let (readback, wire_value) = match case["input"]["readback"].as_str() {
            None => (None, Value::Null),
            Some("FunctionKeys") => (Some(FunctionKeyMode::FunctionKeys), 0.into()),
            Some("MultimediaKeys") => (Some(FunctionKeyMode::MultimediaKeys), 1.into()),
            value => panic!("unexpected frozen readback: {value:?}"),
        };

        let mut replay = Replay::new(baseline());
        assert_eq!(
            replay
                .step(1, Some(Observation::Recovery(readback)))
                .unwrap(),
            expected_effect
        );
        let journal = match replay.entry {
            None => "Removed",
            Some(entry) => {
                assert_eq!(entry.requested, REQUESTED);
                if entry.reconciliation_required {
                    "Pending"
                } else {
                    "Tombstone"
                }
            }
        };
        assert_eq!(case["expected"]["journal"], journal);
        assert_eq!(case["expected"]["setters"], 0);
        assert_eq!(replay.current["functionKeyPrimary"], wire_value);
        assert_eq!(replay.current["writesBlocked"], true);
        assert_eq!(replay.current["revision"], 2);
    }
}

#[test]
fn canceled_and_stale_observations_do_not_read_settle_or_publish() {
    let mut replay = Replay::new(baseline());
    let initial = replay.current.clone();
    let pending = replay.entry;
    assert_eq!(replay.step(2, None).unwrap(), Effect::Ignored);
    for tick in [1, 2] {
        assert_eq!(
            replay
                .step(
                    tick,
                    Some(Observation::Recovery(Some(FunctionKeyMode::FunctionKeys)))
                )
                .unwrap(),
            Effect::Ignored
        );
    }
    assert_eq!(replay.entry, pending);
    assert_eq!(replay.current, initial);
    assert_eq!(
        replay.step(3, Some(Observation::Battery)).unwrap(),
        Effect::ProfileSelected
    );
    assert_eq!(replay.current["activePowerSource"], 1);
    assert_eq!(replay.current["desiredState"]["performance"]["value"], 3);
    assert_eq!(replay.current["confirmedState"], initial["confirmedState"]);
    assert_eq!(replay.current["functionKeyPrimary"], Value::Null);
    assert_eq!(replay.entry, pending);
}
