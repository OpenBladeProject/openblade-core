use super::*;
use serde_json::json;

const SID: &str = "S-1-5-21-1000-2000-3000-1001";

fn guid(text: &str) -> Guid {
    Guid::parse(text).expect("guid")
}

fn intent(performance: i32, fans: i32) -> Intent {
    serde_json::from_value(json!({
        "performance": {"value": performance, "source": 1},
        "fans": {"value": fans, "source": 1},
        "lighting": {"value": {"effect": 1, "color": {"red": 0, "green": 255, "blue": 0},
            "brightness": 50, "parameters": null, "logoMode": null}, "source": 1},
        "battery": {"configuredPolicy": {"chargeLimitPercent": 80},
            "protectionEnabled": true, "temporaryFullCharge": false},
    }))
    .expect("intent")
}

fn refresh(desired: Intent, destination: Power) -> Refresh<'static> {
    Refresh {
        desired,
        policy: 0,
        disclosure: 0,
        destination: Some(destination),
        fingerprint: "OpenBlade:RZ09-0581",
        device_invalidated: false,
        policy_changed: false,
    }
}

const EPOCH: &str = "0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0";
const ATTEMPT: &str = "11111111-2222-3333-4444-555555555555";

fn refreshed() -> State {
    State::empty(SID).refresh_intent(refresh(intent(1, 0), Power::BATTERY), || guid(EPOCH))
}

fn candidate(state: &State, contradiction: Contradiction) -> Candidate {
    Candidate {
        destination: Power::BATTERY,
        contradiction,
        performance_revision: state.performance_desired_revision.clone().unwrap(),
        fan_revision: state.fan_desired_revision.clone().unwrap(),
    }
}

fn reserved(state: &State) -> State {
    state.reserve(
        &candidate(state, Contradiction::PERFORMANCE),
        guid(ATTEMPT),
        Observation::thermal(Some(2), Some(0), None, Some(false)),
        state.effective_intent.clone().unwrap(),
        &[Component::PERFORMANCE],
    )
}

#[test]
fn guid_round_trips_the_d_form_in_any_case() {
    let parsed = guid("0F1E2D3C-4B5A-6978-8796-A5B4C3D2E1F0");
    assert_eq!(parsed.to_string(), EPOCH);
    for text in [
        "0f1e2d3c4b5a69788796a5b4c3d2e1f0",
        "{0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0}",
        "0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1fg",
        "0f1e2d3c-4b5a+6978-8796-a5b4c3d2e1f0",
    ] {
        assert_eq!(Guid::parse(text), None, "{text}");
    }
}

#[test]
fn revision_reads_the_hash_as_a_dotnet_guid() {
    // .NET reverses the first three groups of the first 16 hash bytes.
    let hash = Sha256::digest(b"{}");
    let text = revision("{}");
    let expected = format!(
        "{:02x}{:02x}{:02x}{:02x}-{:02x}{:02x}-{:02x}{:02x}-{:02x}{:02x}-{}",
        hash[3],
        hash[2],
        hash[1],
        hash[0],
        hash[5],
        hash[4],
        hash[7],
        hash[6],
        hash[8],
        hash[9],
        hash[10..16]
            .iter()
            .map(|byte| format!("{byte:02x}"))
            .collect::<String>()
    );
    assert_eq!(text, expected);
}

#[test]
fn refresh_starts_a_generation_and_reserve_then_complete_retire_it() {
    let state = refreshed();
    assert_eq!(state.intent_generation, 1);
    assert_eq!(state.device_authority_epoch, guid(EPOCH));
    assert!(state.validate(SID, true).is_ok());
    // An unchanged refresh keeps the generation and the epoch.
    let again = state.refresh_intent(refresh(intent(1, 0), Power::BATTERY), || {
        core::panic!("the device authority is unchanged")
    });
    assert_eq!(again, state);

    let reserved = reserved(&state);
    assert!(reserved.requires_read_only_reconciliation());
    assert!(reserved.validate(SID, true).is_ok());
    assert_eq!(reserved.budgets.len(), 1);
    assert_eq!(reserved.budgets[0].consumed_claims, 1);
    assert!(reserved.is_expected_reservation(&reserved));
    // A reservation is preserved by a refresh to new intent.
    assert_eq!(
        reserved.refresh_intent(refresh(intent(2, 0), Power::BATTERY), Guid::default),
        reserved
    );

    let completed = reserved.complete(
        Power::BATTERY,
        Contradiction::PERFORMANCE,
        &[Component::PERFORMANCE],
        Disposition::CONFIRMED,
    );
    assert_eq!(completed.in_flight, None);
    assert_eq!(completed.tombstones.len(), 1);
    assert!(completed.tombstones[0].fan_desired_revision.is_none());
    assert_eq!(completed.terminal_disposition, Some(Disposition::CONFIRMED));
    assert!(!completed.is_expected_reservation(&reserved));
    assert!(completed.validate(SID, true).is_ok());
    // An equal tombstone replaces the earlier one.
    let twice = completed.complete(
        Power::BATTERY,
        Contradiction::PERFORMANCE,
        &[Component::PERFORMANCE],
        Disposition::EXTERNAL,
    );
    assert_eq!(twice.tombstones.len(), 2);
    assert!(twice.validate(SID, true).is_err());
}

#[test]
fn an_intent_change_at_the_destination_prunes_only_its_evidence() {
    let completed = reserved(&refreshed()).complete(
        Power::BATTERY,
        Contradiction::PERFORMANCE,
        &[Component::PERFORMANCE],
        Disposition::CONFIRMED,
    );
    // Elsewhere, both are kept.
    let elsewhere =
        completed.refresh_intent(refresh(intent(2, 0), Power::PLUGGED_IN), Guid::default);
    assert_eq!(elsewhere.tombstones.len(), 1);
    assert_eq!(elsewhere.budgets.len(), 1);
    assert_eq!(elsewhere.intent_generation, 2);
    assert_eq!(elsewhere.terminal_disposition, None);
    // A fan-only change keeps the performance evidence.
    let fans = completed.refresh_intent(refresh(intent(1, 1), Power::BATTERY), Guid::default);
    assert_eq!(fans.tombstones.len(), 1);
    assert_eq!(fans.budgets.len(), 1);
    let performance =
        completed.refresh_intent(refresh(intent(2, 0), Power::BATTERY), Guid::default);
    assert!(performance.tombstones.is_empty());
    assert!(performance.budgets.is_empty());
    let policy = completed.refresh_intent(
        Refresh {
            policy_changed: true,
            ..refresh(intent(1, 0), Power::PLUGGED_IN)
        },
        Guid::default,
    );
    assert!(policy.tombstones.is_empty() && policy.budgets.is_empty());
}

#[test]
fn validation_rejects_what_managed_rejects() {
    let base = reserved(&refreshed());
    type Mutation = Box<dyn Fn(&mut State)>;
    let cases: Vec<(&str, Mutation)> = vec![
        ("sid", Box::new(|s| s.active_user_sid = "S-1-5-21-9".into())),
        (
            "epoch",
            Box::new(|s| s.device_authority_epoch = Guid::EMPTY),
        ),
        ("generation", Box::new(|s| s.intent_generation = 0)),
        (
            "revision",
            Box::new(|s| s.policy_revision = Some(Guid::EMPTY.to_string())),
        ),
        (
            "destination",
            Box::new(|s| s.settled_destination = Some(Power(3))),
        ),
        (
            "contradiction",
            Box::new(|s| s.contradiction = Contradiction(4)),
        ),
        (
            "target",
            Box::new(|s| {
                s.in_flight.as_mut().unwrap().desired_target = intent(2, 0);
            }),
        ),
        ("declared", Box::new(|s| s.contradiction = Contradiction(3))),
        (
            "unsorted",
            Box::new(|s| {
                s.in_flight.as_mut().unwrap().declared_components =
                    vec![Component::FANS, Component::PERFORMANCE];
            }),
        ),
        (
            "fan control",
            Box::new(|s| {
                s.effective_intent.as_mut().unwrap().fan_control = None;
                s.in_flight.as_mut().unwrap().desired_target.fan_control = None;
            }),
        ),
        ("budget", Box::new(|s| s.budgets[0].maximum_claims = 2)),
        (
            "terminal",
            Box::new(|s| s.terminal_disposition = Some(Disposition::CONFIRMED)),
        ),
        (
            "reconciliation",
            Box::new(|s| s.in_flight.as_mut().unwrap().reconciliation_required = false),
        ),
    ];
    assert!(base.validate(SID, true).is_ok());
    for (name, mutate) in cases {
        let mut state = base.clone();
        mutate(&mut state);
        assert!(state.validate(SID, true).is_err(), "{name}");
    }
    assert!(State::empty(SID).validate(SID, false).is_ok());
    assert!(State::empty(SID).validate(SID, true).is_err());
}

#[test]
fn the_aggregate_cap_counts_every_component_at_a_destination_and_policy() {
    let mut state = reserved(&refreshed());
    let budget = state.budgets[0].clone();
    for index in 0..4 {
        state.budgets.push(Budget {
            desired_revision: format!("00000000-0000-0000-0000-00000000000{}", index + 1),
            ..budget.clone()
        });
    }
    assert!(state.validate(SID, true).is_err());
    state.budgets.pop();
    assert!(state.validate(SID, true).is_ok());
}

#[test]
fn decode_fails_closed_and_names_a_future_schema() {
    let state = reserved(&refreshed());
    let (bytes, sha) = encode(&state, SID).expect("encode");
    assert_eq!(
        decode(Some(&bytes), SID),
        Load::Admitted {
            state: Box::new(state.clone()),
            sha256: Some(sha)
        }
    );
    assert_eq!(
        decode(None, SID),
        Load::Admitted {
            state: Box::new(State::empty(SID)),
            sha256: None
        }
    );
    let text = String::from_utf8(bytes).unwrap();
    let future = text.replacen("\"schemaVersion\": 1", "\"schemaVersion\": 2", 1);
    assert_eq!(
        decode(Some(future.as_bytes()), SID),
        Load::Quarantined(Quarantine::FutureSchema)
    );
    let unknown = text.replacen("{", "{\"extra\": 1,", 1);
    let duplicate = text.replacen("{", "{\"schemaVersion\": 1,", 1);
    let missing = text.replacen("\"terminalDisposition\": null,", "", 1);
    for bytes in [
        unknown.as_bytes(),
        duplicate.as_bytes(),
        missing.as_bytes(),
        b"{",
        b"",
    ] {
        assert_eq!(
            decode(Some(bytes), SID),
            Load::Quarantined(Quarantine::InvalidDocument),
            "{}",
            String::from_utf8_lossy(bytes)
        );
    }
    assert_eq!(
        decode(Some(text.as_bytes()), "S-1-5-21-9"),
        Load::Quarantined(Quarantine::InvalidDocument)
    );
    assert!(encode(&State::empty(SID), SID).is_err());
}

#[test]
fn a_tombstone_suppresses_only_its_own_key() {
    let state = refreshed();
    let performance = candidate(&state, Contradiction::PERFORMANCE);
    let completed = reserved(&state).complete(
        Power::BATTERY,
        Contradiction::PERFORMANCE,
        &[Component::PERFORMANCE],
        Disposition::BLOCKED,
    );
    assert!(!state.tombstoned(&performance));
    assert!(completed.tombstoned(&performance));
    // Another contradiction, destination or performance revision is not
    // suppressed; the fan revision is not compared for a performance key.
    assert!(!completed.tombstoned(&candidate(&state, Contradiction::FAN_TOPOLOGY)));
    assert!(!completed.tombstoned(&Candidate {
        destination: Power::PLUGGED_IN,
        ..performance.clone()
    }));
    assert!(!completed.tombstoned(&Candidate {
        performance_revision: "other".into(),
        ..performance.clone()
    }));
    assert!(completed.tombstoned(&Candidate {
        fan_revision: "other".into(),
        ..performance.clone()
    }));
    // Another policy revision is not the tombstone's.
    let other_policy = State {
        policy_revision: Some("other".into()),
        ..completed.clone()
    };
    assert!(!other_policy.tombstoned(&performance));
}

#[test]
fn a_spent_claim_exhausts_only_its_components_budget() {
    let state = refreshed();
    let performance = candidate(&state, Contradiction::PERFORMANCE);
    let reserved = reserved(&state);
    assert!(!state.budget_exhausted(&performance));
    assert!(reserved.budget_exhausted(&performance));
    assert!(!reserved.budget_exhausted(&candidate(&state, Contradiction::FAN_TOPOLOGY)));
    // Both components are affected when both contradict.
    let both = Contradiction(Contradiction::PERFORMANCE.0 | Contradiction::FAN_TOPOLOGY.0);
    assert!(reserved.budget_exhausted(&candidate(&state, both)));
    assert!(!reserved.budget_exhausted(&Candidate {
        performance_revision: "other".into(),
        ..performance
    }));
}

#[test]
fn a_rearm_retires_every_suppression_and_keeps_the_rest() {
    let state = refreshed();
    let completed = reserved(&state).complete(
        Power::BATTERY,
        Contradiction::PERFORMANCE,
        &[Component::PERFORMANCE],
        Disposition::EXTERNAL,
    );
    assert!(completed.suppresses());
    let rearmed = completed.rearmed();
    assert!(!rearmed.suppresses());
    assert_eq!(rearmed.terminal_disposition, None);
    assert!(rearmed.terminal_affected_components.is_empty());
    assert_eq!(rearmed.intent_generation, completed.intent_generation);
    assert_eq!(rearmed.effective_intent, completed.effective_intent);
    assert!(rearmed.validate(SID, true).is_ok());
    assert!(!state.suppresses());
}

/// Managed binds the record's lighting with its polymorphic parameters,
/// which need their `kind` first (`kind_first`); serde_json alone sorts it
/// after `color`.
#[test]
fn the_record_writes_lighting_parameters_kind_first() {
    let mut desired = intent(1, 0);
    desired.lighting["value"]["parameters"] =
        json!({"color": {"red": 0, "green": 255, "blue": 0}, "kind": "static"});
    let state = reserved(
        &State::empty(SID).refresh_intent(refresh(desired, Power::BATTERY), || guid(EPOCH)),
    );
    let (bytes, _) = encode(&state, SID).expect("encode");
    let text = String::from_utf8(bytes.clone()).unwrap();
    let parameters = text.find("\"parameters\": {").expect("parameters");
    let first = text[parameters + "\"parameters\": {".len()..].trim_start();
    assert!(first.starts_with("\"kind\": \"static\","), "{text}");
    assert!(matches!(decode(Some(&bytes), SID), Load::Admitted { .. }));
}
