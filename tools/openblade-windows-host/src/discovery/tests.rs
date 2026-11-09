use super::*;
use serde_json::Value;

struct Synthetic {
    host: HostObservation,
    rows: Vec<Option<Interface>>,
    scan: ScanState,
    cancel_after_scan: bool,
}

impl DiscoverySource for Synthetic {
    fn read_host(&self, cancelled: &AtomicBool) -> io::Result<HostObservation> {
        check_cancelled(cancelled)?;
        let copy = |source: &RegistrySource| RegistrySource {
            manufacturer: source.manufacturer.clone(),
            product_name: source.product_name.clone(),
        };
        Ok(HostObservation {
            platform: self.host.platform,
            bios: copy(&self.host.bios),
            system_information: copy(&self.host.system_information),
            sku: self.host.sku.clone(),
            bios_version: self.host.bios_version.clone(),
        })
    }

    fn scan_hid(
        &self,
        cancelled: &AtomicBool,
        mut visit: impl FnMut(Option<Interface>) -> io::Result<()>,
    ) -> io::Result<ScanState> {
        for row in &self.rows {
            visit(row.as_ref().map(|row| Interface {
                path: row.path.clone(),
                hardware_ids: row.hardware_ids.clone(),
            }))?;
        }
        if self.cancel_after_scan {
            cancelled.store(true, Ordering::Relaxed);
        }
        Ok(self.scan)
    }
}

fn fixture(product: &str) -> Synthetic {
    let source = || RegistrySource {
        manufacturer: RegistryText::Present("Razer".into()),
        product_name: RegistryText::Present(product.into()),
    };
    Synthetic {
        host: HostObservation {
            platform: Platform::Supported,
            bios: source(),
            system_information: source(),
            sku: RegistryText::Present("SYNTHETIC-VARIANT".into()),
            bios_version: RegistryText::Unavailable,
        },
        rows: Vec::new(),
        scan: ScanState::Complete,
        cancel_after_scan: false,
    }
}

fn row(path: &str, ids: &[&str]) -> Option<Interface> {
    Some(Interface {
        path: path.into(),
        hardware_ids: Some(ids.iter().map(|id| (*id).into()).collect()),
    })
}

fn snapshot(fixture: &Synthetic) -> Value {
    let text = discover(fixture, &AtomicBool::new(false)).unwrap();
    assert!(!text.contains("opaque-device"));
    serde_json::from_str(&text).unwrap()
}

const BLADE_0581: &str = "Blade 16 - RZ09-0581";
const EXACT: &[&str] = &[
    r"HID\VID_1532&PID_02E0&REV_0001&MI_00&COL01",
    r"HID\VID_1532&PID_02E0&MI_00&COL01",
    "HID_DEVICE_UP:0001_U:0002",
    "HID_DEVICE_SYSTEM_MOUSE",
    "HID_DEVICE",
];

#[test]
fn all_projected_models_require_both_exact_oem_registry_labels() {
    let inventory = Inventory::load().unwrap();
    for model in inventory.models {
        let mut fixture = fixture(model.windows_registry_product_name);
        let result = snapshot(&fixture);
        assert_eq!(result["registryMatch"]["state"], "matched");
        assert_eq!(result["registryMatch"]["modelNumber"], model.model_number);
        assert_eq!(result["registryMatch"]["supportBasis"], model.support_basis);
        assert_eq!(result["registryMatch"]["sku"]["value"], "SYNTHETIC-VARIANT");
        assert_eq!(
            result["registryMatch"]["biosVersion"]["state"],
            "unavailable"
        );
        assert_eq!(result["hid"]["candidates"], "none");

        fixture.host.system_information.manufacturer = RegistryText::Present("razer".into());
        let result = snapshot(&fixture);
        assert_eq!(result["registryMatch"]["state"], "contradictory");
        assert_eq!(result["hid"]["candidates"], "notEvaluated");
    }
}

#[test]
fn usb_pid_sku_partial_or_case_changed_labels_do_not_identify_a_model() {
    for label in [
        "Razer Blade 16",
        "blade 16 - RZ09-0581",
        "Blade 16 - RZ09-0581 ",
        "RZ09-0581",
    ] {
        let mut fixture = fixture(label);
        fixture.host.sku = RegistryText::Present("RZ09-05819EN4".into());
        fixture.rows = vec![row("opaque-device-a", EXACT)];
        let result = snapshot(&fixture);
        assert_eq!(result["registryMatch"]["state"], "unknown");
        assert_eq!(result["hid"]["inventory"], "complete");
        assert_eq!(result["hid"]["candidates"], "notEvaluated");
        assert!(result["hid"]["counts"].is_null());
    }
}

#[test]
fn incomplete_registry_fields_are_distinct_and_platform_does_not_grant_selection() {
    let mut fixture = fixture(BLADE_0581);
    fixture.host.system_information.manufacturer = RegistryText::Unavailable;
    fixture.host.system_information.product_name = RegistryText::Malformed;
    fixture.host.bios.product_name = RegistryText::Missing;
    let result = snapshot(&fixture);
    assert_eq!(result["registryMatch"]["state"], "incomplete");
    assert_eq!(result["registryMatch"]["biosProductName"], "missing");
    assert_eq!(result["registryMatch"]["systemManufacturer"], "unavailable");
    assert_eq!(result["registryMatch"]["systemProductName"], "malformed");

    for platform in [Platform::Unavailable, Platform::Unsupported] {
        let mut fixture = self::fixture(BLADE_0581);
        fixture.host.platform = platform;
        fixture.rows = vec![row("opaque-device-a", EXACT)];
        let result = snapshot(&fixture);
        assert_eq!(result["registryMatch"]["state"], "matched");
        assert_eq!(result["hid"]["candidates"], "notEvaluated");
    }
}

#[test]
fn physical_candidate_pid_and_collection_usage_are_distinct_hints() {
    let mut fixture = fixture(BLADE_0581);
    fixture.rows = vec![
        row("opaque-device-a", EXACT),
        row(
            "opaque-device-b",
            &[r"HID\VID_1532&PID_02C6", "HID_DEVICE_UP:0001_U:0002"],
        ),
        row(
            "opaque-device-c",
            &[
                r"HID\VID_1532&PID_02E0&MI_01&COL01",
                "HID_DEVICE_UP:0001_U:0006",
            ],
        ),
    ];
    let result = snapshot(&fixture);
    assert_eq!(result["hid"]["candidates"], "singleUnresolved");
    assert_eq!(result["hid"]["counts"]["matchingHints"], 1);
    assert_eq!(result["hid"]["counts"]["wrongUsageHint"], 1);
    fixture.rows.pop();
    fixture
        .rows
        .push(row("opaque-device-c", &[r"HID\VID_1532&PID_02E0&COL02"]));
    let result = snapshot(&fixture);
    assert_eq!(result["hid"]["candidates"], "incomplete");
    assert_eq!(result["hid"]["counts"]["missingUsageHint"], 1);
}

#[test]
fn source_reviewed_candidates_do_not_inherit_a_physical_usage_selector() {
    let mut fixture = fixture("Blade 15 Base Model (Early 2020) - RZ09-0328");
    for usage in ["HID_DEVICE_UP:FF00_U:0001", "HID_DEVICE"] {
        fixture.rows = vec![row("opaque-device-a", &[r"HID\VID_1532&PID_0255", usage])];
        let result = snapshot(&fixture);
        assert_eq!(result["hid"]["candidates"], "singleUnresolved");
        assert_eq!(result["hid"]["counts"]["matchingHints"], 1);
    }
}

#[test]
fn distinct_interfaces_are_ambiguous_and_repeated_paths_are_incomplete() {
    let mut fixture = fixture(BLADE_0581);
    fixture.rows = vec![row("opaque-device-a", EXACT), row("opaque-device-b", EXACT)];
    assert_eq!(snapshot(&fixture)["hid"]["candidates"], "ambiguous");

    fixture.rows[1] = row("OPAQUE-DEVICE-A", EXACT);
    let result = snapshot(&fixture);
    assert_eq!(result["hid"]["candidates"], "incomplete");
    assert_eq!(result["hid"]["duplicateInterfaces"], 1);
    assert_eq!(result["hid"]["counts"]["matchingHints"], 1);

    fixture.rows[1] = row(
        "OPAQUE-DEVICE-A",
        &[
            r"HID\VID_1532&PID_02E0&MI_00&COL02",
            "HID_DEVICE_UP:0001_U:0002",
        ],
    );
    let result = snapshot(&fixture);
    assert_eq!(result["hid"]["candidates"], "incomplete");
    assert_eq!(result["hid"]["counts"]["matchingHints"], 0);
}

#[test]
fn failed_scan_or_bad_row_cannot_become_a_complete_empty_inventory() {
    let mut fixture = fixture(BLADE_0581);
    fixture.scan = ScanState::Unavailable;
    let result = snapshot(&fixture);
    assert_eq!(result["hid"]["inventory"], "unavailable");
    assert_eq!(result["hid"]["candidates"], "incomplete");

    fixture.scan = ScanState::Incomplete;
    assert_eq!(snapshot(&fixture)["hid"]["inventory"], "incomplete");
    fixture.scan = ScanState::Complete;
    fixture.rows = vec![
        None,
        row("opaque-device-a", &[r"HID\VID_1532&PID_02E0&COL1"]),
        row(
            "opaque-device-b",
            &[
                r"HID\VID_1532&PID_02E0&MI_00",
                r"HID\VID_1532&PID_02E0&MI_01",
            ],
        ),
        row(
            "opaque-device-c",
            &[r"HID\VID_1532&PID_02E0", r"HID\VID_1532&PID_02C6"],
        ),
        row(
            "opaque-device-d",
            &[
                r"HID\VID_1532&PID_02E0",
                "HID_DEVICE_UP:0001_U:0002",
                "HID_DEVICE_UP:0001_U:0006",
            ],
        ),
    ];
    let result = snapshot(&fixture);
    assert_eq!(result["hid"]["inventory"], "incomplete");
    assert_eq!(result["hid"]["unreadableInterfaces"], 5);
    assert_eq!(result["hid"]["candidates"], "incomplete");
}

#[test]
fn bounded_registry_and_multistring_decoders_reject_malformed_input() {
    assert_eq!(
        RegistryText::from_utf16(&[65, 0]),
        RegistryText::Present("A".into())
    );
    assert_eq!(RegistryText::from_utf16(&[0]), RegistryText::Missing);
    for malformed in [
        vec![65],
        vec![0xd800, 0],
        vec![65, 0, 0],
        vec![65; MAX_REGISTRY_TEXT + 2],
    ] {
        assert_eq!(
            RegistryText::from_utf16(&malformed),
            RegistryText::Malformed
        );
    }
    let well_formed = r"HID\VID_1532&PID_02E0"
        .encode_utf16()
        .chain([0, 0])
        .collect::<Vec<_>>();
    assert_eq!(
        pnp::hardware_ids(&well_formed),
        Some(vec![r"HID\VID_1532&PID_02E0".into()])
    );
    assert!(pnp::hardware_ids(&well_formed[..well_formed.len() - 1]).is_none());
    assert!(pnp::hardware_ids(&[0xd800, 0, 0]).is_none());
    assert!(pnp::hardware_ids(&[65, 0, 0, 66, 0, 0]).is_none());
}

#[test]
fn cancellation_discards_partial_results_and_bounds_the_visitor() {
    let mut fixture = fixture(BLADE_0581);
    assert_eq!(
        discover(&fixture, &AtomicBool::new(true))
            .unwrap_err()
            .kind(),
        io::ErrorKind::Interrupted
    );
    fixture.rows = vec![row("opaque-device-a", EXACT)];
    fixture.cancel_after_scan = true;
    assert_eq!(
        discover(&fixture, &AtomicBool::new(false))
            .unwrap_err()
            .kind(),
        io::ErrorKind::Interrupted
    );

    fixture.rows = (0..=MAX_INTERFACES).map(|_| None).collect();
    assert_eq!(
        discover(&fixture, &AtomicBool::new(false))
            .unwrap_err()
            .kind(),
        io::ErrorKind::InvalidData
    );
}
