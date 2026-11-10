use super::*;

mod worker;

#[path = "../../tests/fixtures/fan_stop_entry.rs"]
mod entry_fixture;

use entry_fixture::*;

#[test]
fn admits_manual_usb_c_with_the_pinned_contract_and_an_independent_sample() {
    assert_eq!(replay(&admitted()), Ok(()));
    let mut e = admitted();
    e.entry = None; // the completed initial observation has not yet expired
    assert_eq!(replay(&e), Ok(()));
    let pins: Vec<_> = include_str!("../../tests/oracles/manual_fan_stop/manual-fan-stop-pins.txt")
        .lines()
        .collect();
    assert_eq!(
        pins,
        [
            e.binding.device,
            e.binding.firmware,
            FIRMWARE_SHA256,
            CONTRACT_SHA256,
            DRIVER_SHA256
        ]
    );
}

type Change = fn(&mut ManualEntry<'static>);

#[test]
fn reports_first_service_and_owner_refusal() {
    let cases: &[(Change, Refusal)] = &[
        (
            |e| {
                e.service.caller_controls_session = false;
                e.service.supported = false;
            },
            Refusal::DispatchSession,
        ),
        (
            |e| e.service.supported = false,
            Refusal::UnavailableOrRecovery,
        ),
        (
            |e| e.service.recovery_required = true,
            Refusal::UnavailableOrRecovery,
        ),
        (
            |e| e.service.command_context_admitted = false,
            Refusal::CommandContext,
        ),
        (
            |e| e.service.prior_guardian_owns_hardware = true,
            Refusal::PreviousOwner,
        ),
        (
            |e| e.service.entry_context_open = false,
            Refusal::EntryContext,
        ),
        (
            |e| {
                e.service.transaction_resolved = false;
                e.service.readback = None;
            },
            Refusal::TransactionUnresolved,
        ),
        (|e| e.service.readback = None, Refusal::ServiceAdmission),
        (
            |e| e.service.readback.as_mut().unwrap().run += 1,
            Refusal::ServiceAdmission,
        ),
        (
            |e| e.service.readback.as_mut().unwrap().profile = PerformanceReadback::Other,
            Refusal::ServiceAdmission,
        ),
        (
            |e| e.service.readback.as_mut().unwrap().profile = PerformanceReadback::Unknown,
            Refusal::ServiceAdmission,
        ),
        (
            |e| e.service.power = Power::Unknown,
            Refusal::ServiceAdmission,
        ),
        (
            |e| e.service.power = Power::Battery,
            Refusal::ServiceAdmission,
        ),
        (
            |e| e.service.power = Power::FullAdapter,
            Refusal::ServiceAdmission,
        ),
        (|e| e.settings_valid = false, Refusal::InvalidSettings),
        (|e| e.owner_check_at = ms(5011), Refusal::MetadataStale),
        (|e| e.owner.capture.run += 1, Refusal::MetadataStale),
        (
            |e| e.owner.capture.completed = ms(31),
            Refusal::MetadataStale,
        ),
        (
            |e| {
                e.binding.firmware_sha256 = "";
                e.owner.host = None;
            },
            Refusal::JournalBinding,
        ),
        (|e| e.binding.contract_sha256 = "", Refusal::JournalBinding),
        (
            |e| e.binding.journal_matches_owner = false,
            Refusal::JournalBinding,
        ),
        (|e| e.owner.host = None, Refusal::HostMismatch),
        (
            |e| e.owner.host = Some(("Razer", "Blade 16 - RZ09-0528")),
            Refusal::HostMismatch,
        ),
        (|e| e.owner.x64 = false, Refusal::HostMismatch),
        (
            |e| e.owner.ec_version = Some((1, 5)),
            Refusal::EcVersionMismatch,
        ),
        (|e| e.owner.ec_version = None, Refusal::EcVersionMismatch),
        (|e| e.owner.hid = &[], Refusal::HidIdentityUnavailable),
        (
            |e| e.owner.hid = &[EXACT_HID, EXACT_HID],
            Refusal::HidIdentityUnavailable,
        ),
        (
            |e| {
                e.owner.hid = &[HidCandidate {
                    identity: Some((0x1532, 0x02e0, 1, 2, 90)),
                    ..EXACT_HID
                }]
            },
            Refusal::HidIdentityUnavailable,
        ),
        (
            |e| {
                e.owner.hid = &[HidCandidate {
                    path: r"\\?\hid#vid_1532&pid_02e00#fixture",
                    ..EXACT_HID
                }]
            },
            Refusal::HidIdentityUnavailable,
        ),
        (|e| e.owner.system_session = false, Refusal::OwnerSession),
        (|e| e.owner.reviewed_driver = None, Refusal::DriverMismatch),
        (
            |e| e.owner.reviewed_driver.as_mut().unwrap().bytes -= 1,
            Refusal::DriverMismatch,
        ),
        (
            |e| e.owner.reviewed_driver.as_mut().unwrap().sha256 = "",
            Refusal::DriverMismatch,
        ),
        (
            |e| e.owner.controller_count = None,
            Refusal::ControllerDetected,
        ),
        (
            |e| e.owner.controller_count = Some(1),
            Refusal::ControllerDetected,
        ),
        (
            |e| e.binding.exclusive_delegation = false,
            Refusal::Delegation,
        ),
        (
            |e| e.owner.original_writer_present = false,
            Refusal::Delegation,
        ),
        (|e| e.owner.writer_count = 2, Refusal::Delegation),
        (
            |e| e.owner.console_session = 0,
            Refusal::ActiveConsoleSession,
        ),
        (
            |e| e.owner.service_state = ServiceState::StoppedOrStopPending,
            Refusal::ServiceRunning,
        ),
        (|e| e.owner.writer_count = 0, Refusal::Delegation),
        (
            |e| {
                e.cpu_reader_ready = false;
                e.initial = None;
            },
            Refusal::CpuReaderPreparation,
        ),
        (
            |e| e.initial = None,
            Refusal::InitialSafety(SafetyRefusal::MissingOrExpired),
        ),
        (|e| e.initial_power = None, Refusal::InitialPower),
        (
            |e| e.initial_power.as_mut().unwrap().resolved = false,
            Refusal::InitialPower,
        ),
    ];
    for (change, expected) in cases {
        let mut e = admitted();
        change(&mut e);
        assert_eq!(replay(&e), Err(*expected), "{e:?}");
    }
}

#[test]
fn does_not_attribute_a_temperature_to_a_blocked_or_unobserved_attempt() {
    let mut e = admitted();
    e.entry_volatile.controllers_absent = Some(false);
    e.entry_volatile.session.ac_line = Some(false);
    e.entry.as_mut().unwrap().thermal = ThermalRead::Unavailable;
    assert_eq!(
        replay(&e),
        Err(Refusal::EntryVolatile(VolatileRefusal::Controllers))
    );
    e.entry_volatile.controllers_absent = Some(true);
    e.entry_volatile.session.ac_line = Some(true);
    assert_eq!(
        replay(&e),
        Err(Refusal::EntryThermal(SafetyRefusal::NotObserved(
            ThermalNotObserved::Unavailable
        )))
    );
    e.entry.as_mut().unwrap().thermal = ThermalRead::NotAttempted;
    assert_eq!(
        replay(&e),
        Err(Refusal::EntryThermal(SafetyRefusal::NotObserved(
            ThermalNotObserved::NotAttempted
        )))
    );
    e.entry
        .as_mut()
        .unwrap()
        .volatile
        .original_service_is_sole_writer = None;
    assert_eq!(
        replay(&e),
        Err(Refusal::EntryThermal(SafetyRefusal::Volatile(
            VolatileRefusal::WriterOwnership
        )))
    );
}

#[test]
fn fresh_power_must_be_observed_again_when_ac_changes_or_evidence_ages() {
    let mut e = admitted();
    e.entry_volatile.session.ac_line = None;
    assert_eq!(
        replay(&e),
        Err(Refusal::EntryVolatile(VolatileRefusal::AcLineUnknown))
    );
    e.entry_volatile.session.ac_line = Some(false);
    assert_eq!(replay(&e), Err(Refusal::EntryPowerRead));
    e.refreshed_power = Some(PowerRead {
        source: Power::Unknown,
        ..power(120, 140)
    });
    assert_eq!(replay(&e), Err(Refusal::EntryPower));
    e.refreshed_power = Some(power(120, 140));
    assert_eq!(replay(&e), Ok(()));
    e.apply.console.ac_line = None;
    assert_eq!(
        replay(&e),
        Err(Refusal::EntryCached(CachedRefusal::ConsoleOrAc))
    );

    let mut e = admitted();
    e.refreshed_at = ms(2081);
    e.entry_volatile = volatile(2060, 2061);
    e.entry = Some(safety(2061));
    e.apply.now = ms(2082);
    assert_eq!(replay(&e), Err(Refusal::EntryPowerRead));
    e.refreshed_power = Some(power(2062, 2070));
    assert_eq!(replay(&e), Ok(()));
}

#[test]
fn cached_boundary_expires_power_and_safety_from_observation_start() {
    let mut e = admitted();
    e.apply.now = ms(2080);
    assert_eq!(replay(&e), Ok(()));
    e.apply.now += Duration::from_nanos(100);
    assert_eq!(
        replay(&e),
        Err(Refusal::EntryCached(CachedRefusal::PowerExpired))
    );

    e.initial_power = Some(power(107, 110));
    e.entry = Some(safety(103));
    e.apply.now = ms(2103);
    assert_eq!(replay(&e), Ok(()));
    e.apply.now += Duration::from_nanos(100);
    assert_eq!(
        replay(&e),
        Err(Refusal::EntryCached(CachedRefusal::Safety(
            SafetyRefusal::MissingOrExpired
        )))
    );
    e.newer_safety = Some(safety(2070));
    assert_eq!(replay(&e), Ok(())); // already completed, not merely in flight
    e.newer_safety.as_mut().unwrap().capture.completed = ms(2104);
    assert_eq!(
        replay(&e),
        Err(Refusal::EntryCached(CachedRefusal::Safety(
            SafetyRefusal::MissingOrExpired
        )))
    );
    e.newer_safety = Some(safety(2070));
    e.newer_safety.as_mut().unwrap().capture.run += 1;
    assert_eq!(
        replay(&e),
        Err(Refusal::EntryCached(CachedRefusal::Safety(
            SafetyRefusal::MissingOrExpired
        )))
    );
}

#[test]
fn hard_entry_budget_expires_even_with_fresh_readbacks() {
    let mut e = admitted();
    e.entry_volatile = volatile(45098, 45099);
    e.refreshed_power = Some(power(45100, 45101));
    e.entry = Some(safety(45082));
    e.refreshed_at = ms(45105);
    e.apply.now = ms(45109);
    assert_eq!(replay(&e), Ok(()));
    e.apply.now += ms(1);
    assert_eq!(replay(&e), Err(Refusal::EntryExpired));
    e.apply.canceled = true;
    assert_eq!(replay(&e), Err(Refusal::EntryCanceled));
}

#[test]
fn thermal_entry_matches_pinned_managed_oracle() {
    let corpus = include_str!("../../tests/oracles/manual_fan_stop/manual-fan-stop-entry.tsv");
    let mut count = 0;
    for line in corpus.lines().filter(|line| !line.starts_with('#')) {
        let f: Vec<_> = line.split('\t').collect();
        let policy = match f[1] {
            "Auto" => ThermalPolicy::Auto,
            "Custom" => ThermalPolicy::Custom {
                cpu_cutoff: 80,
                gpu_cutoff: 75,
            },
            _ => panic!("unknown policy"),
        };
        let temperature = |value: &str| {
            if value == "-" {
                None
            } else {
                Some(value.parse().unwrap())
            }
        };
        let reading = Readings {
            cpu: temperature(f[2]),
            gpu: temperature(f[3]),
            gpu_inactive: f[4].parse().unwrap(),
            gpu_powered_down: f[5].parse().unwrap(),
        };
        let age = f[6].parse().ok().map(ms);
        let canceled = f[7].parse().unwrap();
        let verdict = assess_thermal(policy, reading, age, canceled);
        let (reason, cpu, gpu) = match verdict {
            Ok(()) => ("Admitted", None, None),
            Err(diag) => {
                assert_eq!(
                    (diag.policy, diag.reading, diag.age, diag.canceled),
                    (policy, reading, age, canceled)
                );
                match diag.decision {
                    None => ("SafetyCondition", None, None),
                    Some(d) => (
                        match d.rule {
                            ThermalRule::AutoCpuLimit => "AutoCpuLimit",
                            ThermalRule::ImmediateCutoff => "ImmediateCutoff",
                        },
                        d.cpu,
                        d.gpu,
                    ),
                }
            }
        };
        let trigger = |sensor: Option<(i32, i32)>| {
            sensor.map_or("-".to_owned(), |(value, limit)| format!("{value}:{limit}"))
        };
        assert_eq!(
            (reason, trigger(cpu), trigger(gpu)),
            (f[8], f[9].to_owned(), f[10].to_owned()),
            "{}",
            f[0]
        );
        count += 1;
    }
    assert_eq!(count, 23);
}
