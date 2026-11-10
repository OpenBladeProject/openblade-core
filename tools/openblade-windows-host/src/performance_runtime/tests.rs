use super::{
    device::{
        Channel::{First, Second},
        Command::{Read, Set},
    },
    *,
};

const READ: [device::Command; 2] = [Read(First), Read(Second)];
const SET: [device::Command; 2] = [
    Set(First, Preset::Performance),
    Set(Second, Preset::Performance),
];

fn controller() -> Controller {
    let mut controller = Controller::new(Store::new(Preset::Balanced), Device::new());
    assert_eq!(controller.device.commands, READ);
    controller.device.commands.clear();
    controller
}

#[test]
fn typed_commands_match_the_exact_device_capture() {
    let capture: Value = serde_json::from_str(include_str!(
        "../../../../evidence/legacy-captures/decoded/rz09-0581-pid-02e0-bios-3.01-thermal-write-validation-fixture.json"
    )).unwrap();
    assert_eq!(capture["device"]["model"], "RZ09-0581");
    assert_eq!(capture["device"]["vendorId"], "1532");
    assert_eq!(capture["device"]["productId"], "02E0");
    let run = capture["runs"]
        .as_array()
        .unwrap()
        .iter()
        .find(|run| run["target"] == "performance")
        .unwrap();
    assert_eq!(run["gates"]["success"], true);
    for (commands, rows, key) in [
        (
            READ,
            &capture["readbackCommands"]["thermalPair"],
            "requestPayloadHex",
        ),
        (SET, &run["applyActions"], "payloadHex"),
        (
            [Set(First, Preset::Balanced), Set(Second, Preset::Balanced)],
            &run["restorationActions"],
            "payloadHex",
        ),
    ] {
        let rows = rows.as_array().unwrap();
        assert_eq!(commands.len(), rows.len());
        for (command, row) in commands.into_iter().zip(rows) {
            let (class, id, payload) = command.spec();
            let [a, b, c, d] = payload;
            assert_eq!(row["classHex"], format!("{class:02X}"));
            assert_eq!(row["idHex"], format!("{id:02X}"));
            assert_eq!(row[key], format!("{a:02X}{b:02X}{c:02X}{d:02X}"));
            if key == "payloadHex" {
                assert_eq!(row["statusHex"], "02");
                assert_eq!(row["echoHex"], row[key]);
            }
        }
    }
}

#[test]
fn direct_transition_reads_both_channels_before_and_after_the_pair() {
    let mut c = controller();
    let intent = Intent::Persistent(Preset::Performance);
    assert_eq!(c.apply(intent).unwrap().status, ScopedStatus::Applied);
    assert_eq!(c.device.commands, [READ, READ, SET, READ].concat());
    assert_eq!(c.store.saves, 1);
    assert_eq!(c.store.profile, Preset::Balanced);
    assert_eq!(c.confirmed, Some(Preset::Performance));
    assert_eq!(c.desired(), (Preset::Performance, 4));

    c.device.commands.clear();
    assert_eq!(c.apply(intent).unwrap().status, ScopedStatus::Unchanged);
    assert_eq!(c.device.commands, READ);
    assert_eq!(c.store.saves, 1);

    c.device.commands.clear();
    assert_eq!(
        c.apply(Intent::Clear).unwrap().status,
        ScopedStatus::Applied
    );
    let balanced = [Set(First, Preset::Balanced), Set(Second, Preset::Balanced)];
    assert_eq!(c.device.commands, [READ, READ, balanced, READ].concat());
    assert_eq!(c.store.persistent, None);
}

#[test]
fn target_seen_at_port_preflight_is_confirmed_without_attributing_a_write() {
    let mut c = controller();
    c.device.arm(Fault::TargetDuringPortBaseline);
    let result = c.apply(Intent::Persistent(Preset::Performance)).unwrap();
    assert_eq!(result.status, ScopedStatus::Unchanged);
    assert_eq!(c.device.commands, [READ, READ, READ].concat());
    assert_eq!(c.confirmed, Some(Preset::Performance));
    assert_eq!(c.pending, None);
    assert!(c.external);
}

#[test]
fn incompatible_fixture_context_is_rejected() {
    let fixture: Value = serde_json::from_str(crate::wire::CURRENT_FIXTURE).unwrap();
    for (field, replacement) in [
        ("performanceOverride", json!({"value": 2, "lifetime": 1})),
        (
            "activeApplicationProfileId",
            json!("11111111-2222-4333-8444-555555555555"),
        ),
        ("fanOverride", json!({"value": 1, "lifetime": 0})),
    ] {
        let mut status = fixture["status"].clone();
        status[field] = replacement;
        assert!(Scenario::new(status).is_err(), "{field}");
    }
}

#[test]
fn cancellation_does_not_submit_or_roll_back_accepted_intent() {
    let mut c = controller();
    c.device.arm(Fault::CancelBeforeFirst);
    let result = c.apply(Intent::Persistent(Preset::Performance));
    assert_eq!(result.err().unwrap().kind(), io::ErrorKind::Interrupted);
    assert_eq!(c.device.commands, [READ, READ].concat());
    assert_eq!(c.confirmed, Some(Preset::Balanced));
    assert_eq!(c.pending, None);

    let restarted = Controller::new(c.store, Device::new());
    assert_eq!(restarted.desired(), (Preset::Performance, 4));
    assert_eq!(restarted.confirmed, Some(Preset::Balanced));
    assert_eq!(restarted.device.commands, READ);
}

#[test]
fn unavailable_confirmation_blocks_replacement_and_only_reads_to_settle() {
    for fault in [Fault::LoseSecondResponse, Fault::LoseConfirmation] {
        let mut c = controller();
        c.device.arm(fault);
        assert_eq!(
            c.apply(Intent::Persistent(Preset::Performance))
                .unwrap()
                .status,
            ScopedStatus::Failed
        );
        let failed_reads = if fault == Fault::LoseConfirmation {
            2
        } else {
            1
        };
        let mut expected = [READ, READ, SET].concat();
        expected.extend(std::iter::repeat_n(Read(First), failed_reads));
        assert_eq!(c.device.commands, expected);
        assert_eq!(c.confirmed, Some(Preset::Balanced));
        assert_eq!(c.pending, Some(Preset::Performance));
        assert_eq!(c.store.persistent, Some(Preset::Performance));

        c.device.commands.clear();
        assert_eq!(
            c.apply(Intent::Clear).unwrap().status,
            ScopedStatus::Blocked
        );
        assert!(c.device.commands.is_empty());
        c.observe();
        assert_eq!(c.device.commands, READ);
        assert_eq!(c.pending, None);
        assert_eq!(c.confirmed, Some(Preset::Performance));
        assert!(!c.external);
    }
}

#[test]
fn wrong_first_echo_or_lost_second_admission_stops_the_pair() {
    for fault in [Fault::WrongFirstEcho, Fault::DenySecond] {
        let mut c = controller();
        c.device.arm(fault);
        assert_eq!(
            c.apply(Intent::Temporary(Preset::Performance))
                .unwrap()
                .status,
            ScopedStatus::Failed
        );
        let mut expected = [READ.as_slice(), &READ, &[SET[0]]].concat();
        if fault == Fault::WrongFirstEcho {
            expected.extend(READ);
        }
        assert_eq!(c.device.commands, expected);
        assert_eq!(c.confirmed, Some(Preset::Balanced));
        assert_eq!(c.pending, Some(Preset::Performance));
        assert_eq!(c.store.persistent, None);

        c.device.admission.owns_transport = true;
        c.device.commands.clear();
        c.observe();
        assert_eq!(c.device.commands, READ);
        assert_eq!(
            c.pending,
            Some(Preset::Performance),
            "the channels still disagree"
        );
        c.device.replace_observed(Preset::Balanced);
        c.device.commands.clear();
        c.observe();
        assert_eq!(c.device.commands, READ);
        assert_eq!(c.pending, None);
        assert!(c.external);
        assert_eq!(c.desired(), (Preset::Performance, 3));
    }
}

#[test]
fn storage_and_admission_failures_never_submit() {
    let mut c = controller();
    c.store.fail_save = true;
    let intent = Intent::Persistent(Preset::Performance);
    assert_eq!(c.apply(intent).unwrap().status, ScopedStatus::Failed);
    assert_eq!(c.store.persistent, None);
    assert!(c.device.commands.is_empty());

    c.store.fail_save = false;
    c.device.admission.active_session = false;
    assert_eq!(c.apply(intent).unwrap().status, ScopedStatus::Blocked);
    assert_eq!(c.store.persistent, None);

    c.device.admission.active_session = true;
    c.device.admission.identity = ("RZ09-0581", 0x1532, 0x02c6);
    assert_eq!(c.apply(intent).unwrap().status, ScopedStatus::Blocked);
    assert_eq!(c.store.persistent, Some(Preset::Performance));
    assert!(c.device.commands.is_empty());
}
