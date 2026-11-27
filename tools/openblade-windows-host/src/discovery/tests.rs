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
        Ok(self.host.clone())
    }

    fn scan_hid(
        &self,
        cancelled: &AtomicBool,
        mut visit: impl FnMut(Option<Interface>) -> io::Result<()>,
    ) -> io::Result<ScanState> {
        for row in &self.rows {
            visit(row.clone())?;
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
    r"HID\VID_1532&UP:0001_U:0002",
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

/// An alternate ID family is readable only with another vendor's alias: a
/// family without one establishes no vendor and leaves the scan incomplete.
#[test]
fn alternate_hid_id_families_need_a_foreign_alias_and_vendor_aliases_require_consensus() {
    let mut fixture = fixture(BLADE_0581);
    fixture.rows = vec![
        row("opaque-device-a", EXACT),
        row(
            "opaque-device-b",
            &[
                r"HID\VEN_SYNA&DEV_1234&COL01",
                r"HID\VID_06CB&UP:000D_U:0005",
                "HID_DEVICE_UP:000D_U:0005",
            ],
        ),
        row(
            "opaque-device-c",
            &[
                r"HID\CONVERTEDDEVICE&COL01",
                r"HID\VID_045E&UP:0001_U:0006",
                "HID_DEVICE_SYSTEM_KEYBOARD",
            ],
        ),
    ];
    let result = snapshot(&fixture);
    assert_eq!(result["hid"]["inventory"], "complete");
    assert_eq!(result["hid"]["candidates"], "singleUnresolved");
    assert_eq!(result["hid"]["unreadableInterfaces"], 0);

    let mut aliasless = fixture.rows.clone();
    aliasless[1] = row(
        "opaque-device-b",
        &[r"HID\VEN_SYNA&DEV_1234&COL01", "HID_DEVICE_UP:000D_U:0005"],
    );
    let result = snapshot(&Synthetic {
        rows: aliasless,
        ..self::fixture(BLADE_0581)
    });
    assert_eq!(result["hid"]["inventory"], "incomplete");
    assert_eq!(result["hid"]["unreadableInterfaces"], 1);

    for alias in [
        r"HID\VID_1111&UP:0001_U:0002",
        r"HID\VID_1532&UP:0001_U:0006",
    ] {
        let mut conflicting = EXACT.to_vec();
        conflicting.push(alias);
        fixture.rows[0] = row("opaque-device-a", &conflicting);
        let result = snapshot(&fixture);
        assert_eq!(result["hid"]["inventory"], "incomplete");
        assert_eq!(result["hid"]["counts"]["matchingHints"], 0);
    }
    fixture.rows[0] = row("opaque-device-a", &[r"HID\VID_1532&UP:0001_U:0002"]);
    assert_eq!(snapshot(&fixture)["hid"]["candidates"], "incomplete");
}

#[test]
fn source_reviewed_cardinality_counts_interfaces_without_collapsing_collections() {
    let mut fixture = fixture("Blade 14 - RZ09-0482");
    fixture.rows = vec![
        row(
            "opaque-device-a",
            &[
                r"HID\VID_1532&PID_029D&MI_00&COL01",
                "HID_DEVICE_UP:FF00_U:0001",
            ],
        ),
        row(
            "opaque-device-b",
            &[
                r"HID\VID_1532&PID_029D&MI_00&COL02",
                "HID_DEVICE_UP:0001_U:0006",
            ],
        ),
    ];
    let result = snapshot(&fixture);
    assert_eq!(result["hid"]["candidates"], "ambiguous");
    assert_eq!(result["hid"]["counts"]["matchingHints"], 2);
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

/// The owned-HyperBoost cooling-pad probe: absence only from a complete scan
/// in which every interface's IDs were read; any matching ID, in any case, is
/// presence.
#[test]
fn hardware_id_presence_needs_a_complete_readable_scan() {
    const PAD: &str = "VID_1532&PID_0F43";
    let present = |rows: Vec<Option<Interface>>, scan: ScanState| {
        let mut fixture = fixture("Blade 16 - RZ09-0581");
        fixture.rows = rows;
        fixture.scan = scan;
        hardware_id_present(&fixture, &AtomicBool::new(false), PAD).map_err(|e| e.to_string())
    };
    assert_eq!(present(Vec::new(), ScanState::Complete), Ok(false));
    assert_eq!(
        present(vec![row("opaque-device-a", EXACT)], ScanState::Complete),
        Ok(false)
    );
    for id in [
        r"HID\VID_1532&PID_0F43&MI_00",
        r"hid\vid_1532&pid_0f43&col01",
    ] {
        assert_eq!(
            present(
                vec![row("opaque-device-a", EXACT), row("opaque-device-b", &[id])],
                ScanState::Complete
            ),
            Ok(true),
            "{id}"
        );
    }
    // A row or ID list that cannot be read, or an incomplete scan, is no
    // evidence of absence.
    assert!(
        present(
            vec![row("opaque-device-a", EXACT), None],
            ScanState::Complete
        )
        .is_err()
    );
    assert!(
        present(
            vec![Some(Interface {
                path: "opaque-device-a".into(),
                hardware_ids: None
            })],
            ScanState::Complete
        )
        .is_err()
    );
    for scan in [ScanState::Incomplete, ScanState::Unavailable] {
        assert!(present(Vec::new(), scan).is_err(), "{scan:?}");
    }
}

fn probed_host() -> Synthetic {
    let mut fixture = fixture(BLADE_0581);
    fixture.rows = probe_inventory::RZ09_0581_HOST
        .iter()
        .map(|(label, ids)| row(label, ids))
        .collect();
    fixture
}

/// The facts of one devnode, with the catalog's vendors protected.
fn facts(ids: &[&str]) -> Option<pnp::Facts> {
    let ids: Vec<String> = ids.iter().map(|id| (*id).into()).collect();
    pnp::facts(&ids, &Inventory::load().unwrap().vendors())
}

const LE: &str = r"HID\{00001812-0000-1000-8000-00805f9b34fb}_Dev_VID&";
const CLASSIC: &str = r"HID\{00001124-0000-1000-8000-00805f9b34fb}_VID&";

/// The probed RZ09-0581 host, all 46 interfaces: its foreign devnodes,
/// another Razer device and nested collections are readable, so the scan
/// is complete and MI_02 is the one control hint. Its nine other exact
/// collections, the nested ones among them, are counted with another usage.
#[test]
fn the_probed_rz09_0581_inventory_is_complete_with_one_control_candidate() {
    let fixture = probed_host();
    let result = snapshot(&fixture);
    assert_eq!(result["hid"]["inventory"], "complete");
    assert_eq!(result["hid"]["interfaceCount"], 46);
    assert_eq!(result["hid"]["unreadableInterfaces"], 0);
    assert_eq!(result["hid"]["candidates"], "singleUnresolved");
    assert_eq!(result["hid"]["counts"]["matchingHints"], 1);
    assert_eq!(result["hid"]["counts"]["wrongUsageHint"], 9);
    assert_eq!(result["hid"]["counts"]["missingUsageHint"], 0);

    let inventory = Inventory::load().unwrap();
    let (_, model) = fixture.host.resolve(&inventory);
    let (_, hint) = discover_hid(&fixture, &inventory, model, &AtomicBool::new(false)).unwrap();
    assert_eq!(hint.unwrap().path, "02e0-mi02");
}

/// Common peripherals read in full, beside the probed host: another
/// vendor's device whatever its remainder, game controllers, Bluetooth LE
/// and classic devices, and Razer peripherals with another product ID,
/// which are wrong products rather than unreadable rows.
#[test]
fn peripherals_beside_the_probed_host_keep_the_scan_complete() {
    let razer_le_mouse = [
        format!("{LE}021532_PID&0095_REV&0001&Col01"),
        format!("{LE}021532_PID&0095&Col01"),
        r"HID\{00001812-0000-1000-8000-00805f9b34fb}_LOCALMFG&0002&Col01".into(),
        r"HID\VID_1532&UP:0001_U:0002".into(),
        "HID_DEVICE_SYSTEM_MOUSE".into(),
        "HID_DEVICE_UP:0001_U:0002".into(),
        "HID_DEVICE".into(),
    ];
    let razer_classic_mouse = [
        format!("{CLASSIC}00021532_PID&007a&Col01"),
        r"HID\VID_1532&UP:0001_U:0002".into(),
        "HID_DEVICE_UP:0001_U:0002".into(),
    ];
    let classic_pad = [
        format!("{CLASSIC}0002054c_PID&05c4&Col01"),
        r"HID\VID_054C&UP:0001_U:0005".into(),
        "HID_DEVICE_SYSTEM_GAME".into(),
    ];
    let xinput_classic = [
        format!("{CLASSIC}0002045e_PID&02e0&IG_00"),
        r"HID\VID_045E&UP:0001_U:0005".into(),
    ];
    let le_sig_source = [
        format!("{LE}011532_PID&0001&Col01"),
        r"HID\VID_1532&UP:0001_U:0002".into(),
    ];
    fn owned(ids: &[String]) -> Vec<&str> {
        ids.iter().map(String::as_str).collect()
    }
    let rows = [
        (
            "xinput-usb-pad",
            vec![
                r"HID\VID_045E&PID_0B12&IG_00",
                r"HID\VID_045E&UP:0001_U:0005",
                "HID_DEVICE_SYSTEM_GAME",
                "HID_DEVICE_UP:0001_U:0005",
                "HID_DEVICE",
            ],
            Some((0x045e, 0x0b12)),
        ),
        (
            "razer-xinput-pad",
            vec![
                r"HID\VID_1532&PID_0A14&IG_00",
                r"HID\VID_1532&UP:0001_U:0005",
                "HID_DEVICE_UP:0001_U:0005",
            ],
            Some((0x1532, 0x0a14)),
        ),
        (
            "foreign-unknown-remainder",
            vec![
                r"HID\VID_046D&PID_C52B&REV_1210&MI_02&Col01&Col02&Col03",
                r"HID\VID_046D&PID_C52B&XX_01",
                r"HID\VID_046D&UP:0001_U:0002",
                "HID_DEVICE",
            ],
            None,
        ),
        (
            "razer-le-mouse",
            owned(&razer_le_mouse),
            Some((0x1532, 0x0095)),
        ),
        (
            "razer-classic-mouse",
            owned(&razer_classic_mouse),
            Some((0x1532, 0x007a)),
        ),
        ("classic-pad", owned(&classic_pad), Some((0x054c, 0x05c4))),
        (
            "xinput-classic",
            owned(&xinput_classic),
            Some((0x045e, 0x02e0)),
        ),
        // Source 1 (Bluetooth SIG) still names the vendor value 1532, so
        // its product is compared like any other.
        (
            "le-sig-source",
            owned(&le_sig_source),
            Some((0x1532, 0x0001)),
        ),
        // Another vendor's device ID establishes that vendor by itself.
        (
            "foreign-unread-without-alias",
            vec![r"HID\VID_046D&PID_C52B&XX_01", "HID_DEVICE"],
            None,
        ),
        (
            "acpi-family-foreign-alias",
            vec![
                r"HID\VEN_RAZR&DEV_0001&Col01",
                r"HID\VID_0000&UP:0001_U:0002",
            ],
            None,
        ),
    ];
    for (label, ids, device) in rows {
        let parsed = facts(&ids).unwrap_or_else(|| panic!("{label}"));
        assert_eq!(parsed.device(), device, "{label}");
        let mut fixture = probed_host();
        fixture.rows.push(row(label, &ids));
        let result = snapshot(&fixture);
        assert_eq!(result["hid"]["inventory"], "complete", "{label}");
        assert_eq!(result["hid"]["candidates"], "singleUnresolved", "{label}");
    }
}

/// A Bluetooth ID of the exact device is its candidate, a hint the opened
/// handle must confirm: beside the control collection it makes the
/// candidates ambiguous, so nothing is selected without a handle.
#[test]
fn a_bluetooth_id_of_the_exact_device_is_a_candidate() {
    for first in [
        format!("{LE}021532_PID&02e0&Col01"),
        format!("{CLASSIC}00021532_PID&02e0&Col01"),
    ] {
        let ids = [first.as_str(), "HID_DEVICE_UP:0001_U:0002"];
        assert_eq!(facts(&ids).unwrap().device(), Some((0x1532, 0x02e0)));
        let mut fixture = probed_host();
        fixture.rows.push(row("bluetooth", &ids));
        let result = snapshot(&fixture);
        assert_eq!(result["hid"]["inventory"], "complete", "{first}");
        assert_eq!(result["hid"]["candidates"], "ambiguous", "{first}");
    }
}

/// A protected-vendor interface whose identity is not fully parsed, added
/// to the probed host, leaves the scan incomplete and selects nothing.
#[test]
fn an_unparsed_razer_interface_makes_the_probed_inventory_incomplete() {
    let le_unknown_field = format!("{LE}021532_PID&02e0&XX_01");
    for unparsed in [
        // A third nesting level.
        &[
            r"HID\VID_1532&PID_02E0&MI_00&Col03&Col01&Col02",
            "HID_DEVICE_UP:0001_U:0002",
        ][..],
        // An unknown field.
        &[r"HID\VID_1532&PID_0A14&XX_00", "HID_DEVICE_UP:0001_U:0002"],
        // A Razer alias without a product ID.
        &[
            r"HID\VEN_RAZR&DEV_0001&Col01",
            r"HID\VID_1532&UP:0001_U:0002",
        ],
        // A Bluetooth Razer ID with a field the grammar does not read.
        &[le_unknown_field.as_str(), r"HID\VID_1532&UP:0001_U:0002"],
    ] {
        let mut fixture = probed_host();
        fixture.rows.push(row("unparsed", unparsed));
        let result = snapshot(&fixture);
        assert_eq!(result["hid"]["inventory"], "incomplete", "{unparsed:?}");
        assert_eq!(result["hid"]["unreadableInterfaces"], 1, "{unparsed:?}");
        assert_eq!(result["hid"]["candidates"], "incomplete", "{unparsed:?}");
    }
}

/// Every probed row is readable with its device; and a devnode without
/// facts is one that names a protected vendor outside the grammar, has a
/// protected alias without a product, disagrees, has no device ID, or
/// establishes no vendor at all.
#[test]
fn foreign_devnodes_are_readable_only_when_their_ids_rule_out_razer() {
    for (label, ids) in probe_inventory::RZ09_0581_HOST {
        let parsed = facts(ids).unwrap_or_else(|| panic!("{label}"));
        let expected = match label.split('-').next() {
            Some("02e0") => Some((0x1532, 0x02e0)),
            Some("0f43") => Some((0x1532, 0x0f43)),
            Some("ble") => Some((0x046d, 0xb037)),
            Some("vhf") => Some((0x025d, 0x0721)),
            _ => None,
        };
        assert_eq!(parsed.device(), expected, "{label}");
    }
    let classic_without_source = format!("{CLASSIC}1532_PID&02e0");
    let le_razer_foreign_alias = format!("{LE}021532_PID&b037&Col01");
    let classic_razer_foreign_alias = format!("{CLASSIC}00021532_PID&02e0&Col01");
    let le_unknown_source = format!("{LE}031532_PID&0001");
    let classic_unknown_source = format!("{CLASSIC}00031532_PID&0001");
    for unread in [
        &[r"HID\INTC816&Col01", r"HID\VID_1532&UP:0001_U:0006"][..],
        &[r"HID\INTC816&Col01", r"USB\VID_1532&PID_02E0&MI_00"],
        &[
            r"HID\FOO_VID_15320001&Col01",
            r"HID\VID_046D&UP:0001_U:0002",
        ],
        &[r"HID\WEIRD&VID&00021532", r"HID\VID_046D&UP:0001_U:0002"],
        // Another vendor's device ID whose unread remainder names Razer.
        &[
            r"HID\VID_046D&PID_C52B&XX_VID_1532",
            r"HID\VID_046D&UP:0001_U:0002",
        ],
        &[
            classic_without_source.as_str(),
            r"HID\VID_046D&UP:0001_U:0002",
        ],
        // A vendor-ID source other than Bluetooth SIG or USB-IF, even with
        // an agreeing alias.
        &[le_unknown_source.as_str(), r"HID\VID_1532&UP:0001_U:0002"],
        &[
            classic_unknown_source.as_str(),
            r"HID\VID_1532&UP:0001_U:0002",
        ],
        // A protected vendor in a transport ID, contradicted by the alias.
        &[
            le_razer_foreign_alias.as_str(),
            r"HID\VID_046D&UP:0001_U:0002",
        ],
        &[
            classic_razer_foreign_alias.as_str(),
            r"HID\VID_046D&UP:0001_U:0002",
        ],
        // Another vendor's unread device ID, contradicted by the alias.
        &[
            r"HID\VID_046D&PID_C52B&XX_01",
            r"HID\VID_045E&UP:0001_U:0002",
        ],
        // A two-digit-source Razer token under another service UUID, with
        // an alias that does not refuse the row by itself.
        &[
            r"HID\{0000fff0-0000-1000-8000-00805f9b34fb}_Dev_VID&021532_PID&0001",
            r"HID\VID_046D&UP:0001_U:0002",
        ],
        // An exact identity followed by an ID naming Razer outside it, or
        // by an unread ID of the same vendor.
        &[
            r"HID\VID_1532&PID_02E0&MI_02",
            r"HID\WEIRD_VID_1532&PID_02E0",
            "HID_DEVICE_UP:0001_U:0002",
        ],
        &[
            r"HID\VID_1532&PID_02E0&MI_02",
            r"HID\VID_1532&PID_02E0&XX_01",
            "HID_DEVICE_UP:0001_U:0002",
        ],
        // A lone alias, without a device ID.
        &[r"HID\VID_8087&UP:000C_U:0001", "HID_DEVICE"],
        // A device ID family with no vendor alias.
        &[
            r"HID\RZCONTROL&Col01",
            "HID_DEVICE_UP:0001_U:0002",
            "HID_DEVICE",
        ],
        &[r"HID\VEN_SYNA&DEV_1234&COL01", "HID_DEVICE_UP:000D_U:0005"],
    ] {
        assert_eq!(facts(unread), None, "{unread:?}");
    }
}

/// Nested collections parse to their full path, outermost first, up to the
/// two levels HIDClass lists on RZ09-0581, in HIDClass field order only.
#[test]
fn nested_collections_parse_with_exact_bounded_grammar() {
    let nested = facts(&[
        r"HID\VID_1532&PID_02E0&REV_0200&MI_00&Col03&Col01",
        r"HID\VID_1532&PID_02E0&MI_00&COL03&COL01",
        "HID_DEVICE_UP:000C_U:0001",
    ])
    .unwrap();
    assert_eq!(nested.device(), Some((0x1532, 0x02e0)));
    assert_eq!(nested.usage, Some((0x0c, 0x01)));
    for distinct in [
        r"HID\VID_1532&PID_02E0&MI_00&Col03",
        r"HID\VID_1532&PID_02E0&MI_00&Col01",
        r"HID\VID_1532&PID_02E0&MI_00&Col01&Col03",
        r"HID\VID_1532&PID_02E0&MI_00&Col03&Col02",
        r"HID\VID_1532&PID_02E0&MI_00&IG_00&Col03&Col01",
    ] {
        let other = facts(&[distinct, "HID_DEVICE_UP:000C_U:0001"]).unwrap();
        assert_ne!(other, nested, "{distinct}");
    }
    let le_twice_revised = format!("{LE}021532_PID&0095_REV&0001&REV_0001");
    let le_three_levels = format!("{LE}021532_PID&0095&Col01&Col02&Col03");
    let le_short_product = format!("{LE}021532_PID&095");
    // Text straight after a Bluetooth product or revision.
    let le_after_product = format!("{LE}021532_PID&0095X");
    let le_field_after_product = format!("{LE}021532_PID&02e0_FOO");
    let le_after_revision = format!("{LE}021532_PID&02e0_REV&0001X");
    let classic_field_after_product = format!("{CLASSIC}00021532_PID&02e0_FOO");
    for malformed in [
        r"HID\VID_1532&PID_02E0&MI_00&Col03&Col01&Col02",
        r"HID\VID_1532&PID_02E0&Col03&MI_00",
        r"HID\VID_1532&PID_02E0&MI_00&MI_01",
        r"HID\VID_1532&PID_02E0&REV_0200&REV_0200",
        r"HID\VID_1532&PID_02E0&Col03&REV_0200",
        r"HID\VID_1532&PID_02E0&IG_00&MI_00",
        r"HID\VID_1532&PID_02E0&IG_00&IG_01",
        r"HID\VID_1532&PID_02E0&IG_0",
        r"HID\VID_1532&PID_02E0&MI_00&Col3&Col01",
        r"HID\VID_1532&PID_02E0&MI_00&Col03&",
        r"HID\VID_1532&PID_02E0&MI_00&Col03&Col01X",
        &le_twice_revised,
        &le_three_levels,
        &le_short_product,
        &le_after_product,
        &le_field_after_product,
        &le_after_revision,
        &classic_field_after_product,
    ] {
        assert_eq!(facts(&[malformed]), None, "{malformed}");
    }
    // The revisioned and unrevisioned paths must agree, and so must two
    // revisions.
    assert_eq!(
        facts(&[
            r"HID\VID_1532&PID_02E0&REV_0200&MI_00&Col03&Col01",
            r"HID\VID_1532&PID_02E0&MI_00&Col03&Col02",
        ]),
        None
    );
    assert_eq!(
        facts(&[
            r"HID\VID_1532&PID_02E0&REV_0200&MI_02",
            r"HID\VID_1532&PID_02E0&REV_0100&MI_02",
        ]),
        None
    );
}

/// A vendor prefix whose vendor, vendor-ID source or alias usage does not
/// parse, or a malformed usage ID, refuses the row for every vendor: the
/// ID establishes nothing the row can be read by.
#[test]
fn malformed_vendor_prefixes_and_usage_ids_refuse_the_row() {
    let le_unknown_source = format!("{LE}03046d_PID&0001");
    let classic_short_vendor = format!("{CLASSIC}0002046_PID&0001");
    for malformed in [
        // A malformed alias of another vendor, alone or beside its device.
        &[r"HID\VID_046D&UP:0001_U:00"][..],
        &[r"HID\VID_046D&PID_C52B", r"HID\VID_046D&UP:0001_U:00"],
        &[r"HID\VID_046D&PID_C52B", r"HID\VID_046D&UP:0001"],
        // A vendor that is not four hex digits, beside an exact identity or
        // another vendor's alias.
        &[r"HID\VID_1532&PID_02E0&MI_02", r"HID\VID_ZZZZ&PID_02E0"],
        &[r"HID\VID_ZZZZ&PID_0001", r"HID\VID_046D&UP:0001_U:0002"],
        &[r"HID\VID_046", r"HID\VID_046D&UP:0001_U:0002"],
        // An unknown or short Bluetooth vendor-ID field of another vendor.
        &[le_unknown_source.as_str(), r"HID\VID_046D&UP:0001_U:0002"],
        &[
            classic_short_vendor.as_str(),
            r"HID\VID_046D&UP:0001_U:0002",
        ],
        // A malformed usage ID beside an exact identity.
        &[r"HID\VID_1532&PID_02E0&MI_02", "HID_DEVICE_UP:0001_U:02"],
    ] {
        assert_eq!(facts(malformed), None, "{malformed:?}");
        let mut fixture = probed_host();
        fixture.rows.push(row("malformed", malformed));
        let result = snapshot(&fixture);
        assert_eq!(result["hid"]["inventory"], "incomplete", "{malformed:?}");
        assert_eq!(result["hid"]["candidates"], "incomplete", "{malformed:?}");
    }
}

/// A virtual HID device lists a family ID, a USB-form device ID and an
/// alias. The device ID gives the row its device, so one that names the
/// exact device is a candidate the opened handle must confirm, and the IDs
/// must agree.
#[test]
fn a_family_id_beside_a_device_id_reads_as_that_device() {
    let vhf = probe_inventory::RZ09_0581_HOST
        .iter()
        .find(|(label, _)| *label == "vhf")
        .unwrap()
        .1;
    let parsed = facts(vhf).unwrap();
    assert_eq!(parsed.device(), Some((0x025d, 0x0721)));
    assert_eq!(parsed.usage, Some((0x0c, 0x01)));

    let exact = [
        r"HID\HID_DEVICE_SYSTEM_VHF",
        r"HID\VID_1532&PID_02E0",
        r"HID\VID_1532&UP:0001_U:0002",
        "HID_DEVICE_UP:0001_U:0002",
    ];
    assert_eq!(facts(&exact).unwrap().device(), Some((0x1532, 0x02e0)));
    let mut fixture = probed_host();
    fixture.rows.push(row("vhf-exact", &exact));
    let result = snapshot(&fixture);
    assert_eq!(result["hid"]["inventory"], "complete");
    assert_eq!(result["hid"]["candidates"], "ambiguous");

    assert_eq!(
        facts(&[
            r"HID\HID_DEVICE_SYSTEM_VHF",
            r"HID\VID_025D&PID_0721",
            r"HID\VID_1532&UP:000C_U:0001",
        ]),
        None
    );
}
