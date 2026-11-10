use super::*;

pub(super) const EXACT_HID: HidCandidate<'_> = HidCandidate {
    path: r"\\?\hid#vid_1532&pid_02e0&mi_03#fixture",
    identity: Some((0x1532, 0x02e0, 1, 2, 91)),
};

pub(super) fn ms(value: u64) -> Duration {
    Duration::from_millis(value)
}

pub(super) fn span(started: u64, completed: u64) -> Span {
    Span {
        run: 7,
        started: ms(started),
        completed: ms(completed),
    }
}

pub(super) fn console() -> ConsoleEvidence {
    ConsoleEvidence {
        console: 5,
        system_session: true,
        ac_line: Some(true),
    }
}

pub(super) fn volatile(started: u64, completed: u64) -> VolatileEvidence {
    VolatileEvidence {
        capture: span(started, completed),
        session: console(),
        controllers_absent: Some(true),
        original_service_is_sole_writer: Some(true),
    }
}

pub(super) fn safety(started: u64) -> SafetyObservation {
    safety_with(
        started,
        ThermalRead::Observed {
            started: ms(started + 6),
            reading: Readings {
                cpu: Some(52),
                gpu: None,
                gpu_inactive: true,
                gpu_powered_down: false,
            },
            canceled: false,
        },
    )
}

pub(super) fn safety_with<T>(started: u64, thermal: T) -> SafetyObservation<T> {
    SafetyObservation {
        capture: span(started, started + 20),
        volatile: volatile(started + 1, started + 5),
        thermal,
    }
}

pub(super) fn power(started: u64, completed: u64) -> PowerRead {
    PowerRead {
        capture: span(started, completed),
        resolved: true,
        source: Power::UsbC,
    }
}

pub(super) fn admitted() -> ManualEntry<'static> {
    let mut entry = admitted_with(safety(40).thermal, true);
    entry.entry = Some(safety(120));
    entry
}

pub(super) fn admitted_with<T, R>(thermal: T, readiness: R) -> ManualEntry<'static, T, R> {
    ManualEntry {
        service: ServiceEntry {
            caller_controls_session: true,
            supported: true,
            recovery_required: false,
            command_context_admitted: true,
            prior_guardian_owns_hardware: false,
            entry_context_open: true,
            transaction_resolved: true,
            readback: Some(VerifiedProfileRead {
                run: 7,
                profile: PerformanceReadback::Balanced,
            }),
            power: Power::UsbC,
        },
        binding: OwnerBinding {
            run: 7,
            device: "RZ09-0581/1532:02E0",
            firmware: "SO6120-EC1.04",
            contract_sha256: CONTRACT_SHA256,
            firmware_sha256: FIRMWARE_SHA256,
            journal_matches_owner: true,
            exclusive_delegation: true,
            bound_console: 5,
        },
        owner: OwnerMetadata {
            capture: span(10, 20),
            x64: true,
            host: Some(("Razer", "Blade 16 - RZ09-0581")),
            ec_version: Some((1, 4)),
            hid: &[EXACT_HID],
            system_session: true,
            reviewed_driver: Some(ReviewedDriver {
                path_and_registration_match: true,
                bytes: 15008,
                sha256: DRIVER_SHA256,
            }),
            controller_count: Some(0),
            console_session: 5,
            service_state: ServiceState::Running,
            writer_count: 1,
            original_writer_present: true,
        },
        owner_check_at: ms(30),
        initial_check_at: ms(70),
        cpu_reader_ready: readiness,
        settings_valid: true,
        policy: ThermalPolicy::Auto,
        initial: Some(safety_with(40, thermal)),
        initial_power: Some(power(80, 100)),
        entry_started: ms(110),
        refreshed_at: ms(150),
        entry_canceled: false,
        entry_volatile: volatile(111, 119),
        refreshed_power: None,
        entry: None,
        apply: ApplyEvidence {
            now: ms(155),
            canceled: false,
            console: console(),
        },
        newer_safety: None,
    }
}
