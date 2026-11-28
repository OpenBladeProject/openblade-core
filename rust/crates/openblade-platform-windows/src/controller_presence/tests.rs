use super::*;

const EXCLUSIONS: &[u16] = &[0x02e0, 0x02c6];
const ENGINE_ID: ProcessIdentity = ProcessIdentity {
    pid: 10,
    session: 1,
    created: 100,
};
const HELPER_ID: ProcessIdentity = ProcessIdentity {
    pid: 20,
    session: 1,
    created: 200,
};

fn name_only(name: &str) -> Process<'_> {
    Process {
        name,
        pid: None,
        identity: None,
        verified_image: None,
    }
}

fn engine_and_helper() -> [Process<'static>; 2] {
    [
        Process {
            name: ENGINE,
            pid: Some(ENGINE_ID.pid),
            identity: Some(ENGINE_ID),
            verified_image: Some((ENGINE_ID, VerifiedImage::EngineExclusions(EXCLUSIONS))),
        },
        Process {
            name: HELPER,
            pid: Some(HELPER_ID.pid),
            identity: Some(HELPER_ID),
            verified_image: Some((HELPER_ID, VerifiedImage::LampArrayHelper)),
        },
    ]
}

fn classify(processes: Scan<'_, Process<'_>>, services: Scan<'_, Service<'_>>) -> Observation {
    observe(processes, services, None, Scan::Incomplete(&[]), 300)
}

#[test]
fn exact_names_states_and_alias_provenance_match_managed_observation() {
    let processes = [
        name_only("RAZERAPPENGINE"),
        name_only("RazerAppEngine"),
        name_only("RazerAppEngine Helper"),
        name_only("razerwdl"),
        name_only("RGS"),
        name_only("Razer"),
    ];
    let services = [
        Service {
            name: "different",
            display_name: "RGS",
            state: ServiceState::Running,
        },
        Service {
            name: "chroma",
            display_name: "Razer Chroma SDK Server",
            state: ServiceState::ContinuePending,
        },
        Service {
            name: "RazerSynapseService",
            display_name: "Razer Synapse Service",
            state: ServiceState::StartPending,
        },
        Service {
            name: "Razer Chroma Stream Server",
            display_name: "",
            state: ServiceState::Inactive,
        },
    ];
    let result = classify(Scan::Complete(&processes), Scan::Complete(&services));
    assert_eq!(
        result.potential_controllers(),
        [
            "RAZERAPPENGINE",
            "RGS",
            "Razer Chroma SDK Server",
            "Razer Synapse Service",
            "razerwdl",
        ]
    );
    assert_eq!(
        result.hyperboost_blockers(),
        ["RAZERAPPENGINE", "RGS", "Razer Synapse Service", "razerwdl"]
    );
    assert_eq!(result.warning(), None);
    assert!(!result.confirms_absence());
}

#[test]
fn exact_updater_service_is_visible_and_only_the_verified_service_is_thermally_exempt() {
    let mut service = Service {
        name: "rGs",
        display_name: UPDATER_DISPLAY,
        state: ServiceState::Running,
    };
    let result = classify(
        Scan::Complete(&[]),
        Scan::Complete(std::slice::from_ref(&service)),
    );
    assert_eq!(result.potential_controllers(), [UPDATER_DISPLAY]);
    assert!(result.hyperboost_blockers().is_empty());

    service.name = "other";
    service.display_name = "RGS";
    let result = classify(
        Scan::Complete(&[]),
        Scan::Complete(std::slice::from_ref(&service)),
    );
    assert!(result.confirms_absence());

    service.display_name = UPDATER_DISPLAY;
    let result = classify(Scan::Complete(&[]), Scan::Complete(&[service]));
    assert_eq!(result.hyperboost_blockers(), [UPDATER_DISPLAY]);
}

#[test]
fn only_complete_empty_inventory_confirms_absence() {
    assert!(
        classify(
            Scan::Complete(&[name_only("RazerAppEngine-old")]),
            Scan::Complete(&[])
        )
        .confirms_absence()
    );
    for (processes, services, expected) in [
        (
            Scan::Incomplete(&[]),
            Scan::Complete(&[]),
            DetectionWarning::ProcessScan,
        ),
        (
            Scan::Complete(&[]),
            Scan::Incomplete(&[]),
            DetectionWarning::ServiceScan,
        ),
        (
            Scan::Incomplete(&[]),
            Scan::Incomplete(&[]),
            DetectionWarning::ProcessAndServiceScans,
        ),
    ] {
        let result = classify(processes, services);
        assert_eq!(result.warning(), Some(&expected));
        assert!(!result.confirms_absence());
    }
    let result = classify(
        Scan::Incomplete(&[name_only(ENGINE)]),
        Scan::Complete(&[Service {
            name: "RGS",
            display_name: "",
            state: ServiceState::Unknown,
        }]),
    );
    assert_eq!(result.potential_controllers(), [ENGINE]);
    assert_eq!(
        result.warning(),
        Some(&DetectionWarning::ProcessAndServiceScans)
    );
}

#[test]
fn peripheral_only_exclusion_requires_current_unique_parent_and_complete_lamps() {
    let processes = engine_and_helper();
    let mut parents = ParentSnapshot {
        started: 250,
        links: Scan::Complete(&[(20, 10)]),
    };
    let classify = |parents: &_, lamps| {
        observe(
            Scan::Complete(&processes),
            Scan::Complete(&[]),
            Some(parents),
            lamps,
            300,
        )
    };
    assert!(classify(&parents, Scan::Complete(&[])).confirms_absence());
    for lamps in [
        Scan::Incomplete(&[]),
        Scan::Complete(&[LampArray {
            vendor: 0x1532,
            product: 0x02c6,
        }]),
        Scan::Complete(&[LampArray {
            vendor: 0,
            product: 0x02c6,
        }]),
    ] {
        let result = classify(&parents, lamps);
        assert_eq!(result.potential_controllers(), [HELPER]);
        assert!(result.hyperboost_blockers().is_empty());
    }
    for links in [
        Scan::Complete(&[(20, 30)]),
        Scan::Complete(&[(20, 10), (20, 30)]),
        Scan::Incomplete(&[(20, 10)]),
    ] {
        parents.links = links;
        assert_eq!(
            classify(&parents, Scan::Complete(&[])).potential_controllers(),
            [HELPER]
        );
    }

    parents.links = Scan::Complete(&[(20, 10)]);
    parents.started = HELPER_ID.created;
    assert_eq!(
        classify(&parents, Scan::Complete(&[])).potential_controllers(),
        [HELPER]
    );
}

#[test]
fn synapse_thermal_exemption_requires_every_engine_to_have_current_exclusions() {
    let [engine, _] = engine_and_helper();
    let mut second = ProcessIdentity {
        pid: 30,
        ..ENGINE_ID
    };
    let service = Service {
        name: "RazerSynapseService",
        display_name: "Razer Synapse Service",
        state: ServiceState::Running,
    };
    let mut processes = [
        engine,
        Process {
            pid: Some(second.pid),
            identity: Some(second),
            verified_image: Some((second, VerifiedImage::EngineExclusions(EXCLUSIONS))),
            ..engine
        },
    ];
    let classify = |processes: &[Process<'_>]| {
        classify(
            Scan::Complete(processes),
            Scan::Complete(std::slice::from_ref(&service)),
        )
    };
    let result = classify(&processes);
    assert_eq!(result.potential_controllers(), ["Razer Synapse Service"]);
    assert!(result.hyperboost_blockers().is_empty());

    processes[1].verified_image = Some((second, VerifiedImage::EngineExclusions(&[0x02e0])));
    assert_eq!(
        classify(&processes).hyperboost_blockers(),
        ["Razer Synapse Service", ENGINE]
    );

    second.created = 310;
    processes[1].identity = Some(second);
    processes[1].verified_image = Some((second, VerifiedImage::EngineExclusions(EXCLUSIONS)));
    assert_eq!(
        classify(&processes).hyperboost_blockers(),
        ["Razer Synapse Service", ENGINE]
    );
}

#[test]
fn stale_identity_session_and_partial_inventory_preserve_known_controllers() {
    let parents = ParentSnapshot {
        started: 250,
        links: Scan::Complete(&[(20, 10)]),
    };
    let classify = |processes| {
        observe(
            processes,
            Scan::Complete(&[]),
            Some(&parents),
            Scan::Complete(&[]),
            300,
        )
    };
    let original = engine_and_helper();
    let mut processes = original;
    processes[0].identity = Some(ProcessIdentity {
        created: 240,
        ..ENGINE_ID
    });
    let result = classify(Scan::Complete(&processes));
    assert_eq!(result.potential_controllers(), [ENGINE, HELPER]);
    assert_eq!(result.hyperboost_blockers(), [ENGINE]);

    let mut processes = original;
    processes[1].identity = Some(ProcessIdentity {
        created: 260,
        ..HELPER_ID
    });
    let result = classify(Scan::Complete(&processes));
    assert_eq!(result.potential_controllers(), [HELPER]);
    assert_eq!(result.hyperboost_blockers(), [HELPER]);

    let mut processes = original;
    let changed_session = ProcessIdentity {
        session: 2,
        ..HELPER_ID
    };
    processes[1].identity = Some(changed_session);
    processes[1].verified_image = Some((changed_session, VerifiedImage::LampArrayHelper));
    assert_eq!(
        classify(Scan::Complete(&processes)).potential_controllers(),
        [HELPER]
    );

    let processes = [
        original[0],
        original[1],
        Process {
            pid: Some(10),
            ..name_only("other")
        },
    ];
    assert_eq!(
        classify(Scan::Complete(&processes)).potential_controllers(),
        [ENGINE, HELPER]
    );
    let result = classify(Scan::Incomplete(&original));
    assert_eq!(result.potential_controllers(), [ENGINE, HELPER]);
    assert_eq!(result.hyperboost_blockers(), [ENGINE, HELPER]);
}
