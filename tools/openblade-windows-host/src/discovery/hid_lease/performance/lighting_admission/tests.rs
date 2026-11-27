use super::*;

fn layout() -> matrix::KeyboardLayout<'static> {
    lighting_layout::source_maps().unwrap().layout()
}

fn manifest() -> Value {
    serde_json::from_slice(MANIFEST).unwrap()
}

fn oracle() -> Value {
    serde_json::from_slice(ORACLE).unwrap()
}

/// A named edit of a parsed document.
type Edit = (&'static str, Box<dyn Fn(&mut Value)>);

/// `document` with one space after its opening brace: the same JSON in
/// other bytes, which only the pin refuses.
fn respaced(document: &[u8]) -> Vec<u8> {
    let at = document.iter().position(|&b| b == b'{').unwrap() + 1;
    let mut changed = document.to_vec();
    changed.insert(at, b' ');
    changed
}

/// The pinned manifest and oracle admit this codec, as managed's admit its
/// own.
#[test]
fn the_pinned_documents_admit_the_native_commands() {
    assert!(admits(MANIFEST, &layout()));
    assert!(oracle_admits(ORACLE, MANIFEST));
    assert!(approves(MANIFEST, ORACLE));
    assert!(approved());
}

/// The native projection and known answer reproduce managed's fingerprints.
#[test]
fn the_matrix_section_is_the_manifest_matrix() {
    let section = matrix_section(&layout());
    assert_eq!(
        section["regionalProjectionSha256"],
        "6004087E107C6CE91A1A2AE84A6E5A5394AF2C9E90EDF76CC510B2CF0C7E8285"
    );
    assert_eq!(
        section["knownAnswerFeatureReportSha256"],
        "7AFEE96E76C0547BE237465CAA2149E9254BD71DC1DE8D3EC17E5F2F0976C98F"
    );
    assert_eq!(section, manifest()["matrix"]);
}

/// Any change to the manifest bytes fails the pin, even one that leaves its
/// meaning unchanged; a section that differs from the codec fails the
/// comparison even under a matching pin.
#[test]
fn a_changed_manifest_or_section_admits_nothing() {
    let layout = layout();
    let changed = respaced(MANIFEST);
    assert!(matches(&serde_json::from_slice(&changed).unwrap(), &layout));
    assert!(!admits(&changed, &layout));
    assert!(!approves(&changed, ORACLE));
    let manifest = manifest();
    for (section, member, value) in [
        ("device", "productId", json!(0x02c6)),
        ("device", "model", json!("RZ09-0528")),
        ("controlEnvelope", "reportLength", json!(91)),
        ("deviceMode", "query", json!("00/84:0300")),
        ("brightness", "query", json!("03/83:010400")),
        ("brightness", "setPrefix", json!("03/03:0104")),
        ("brightness", "percent77Raw", json!(196)),
        ("logo", "queryState", json!("03/80:010500")),
        ("logo", "setEffectPrefix", json!("03/02:0105")),
        ("logo", "supportedModes", json!(["static", "off"])),
        ("matrix", "command", json!("03/0C")),
        ("matrix", "envelopeLength", json!(373)),
        ("matrix", "featureReportLength", json!(374)),
        ("matrix", "slotCount", json!(101)),
        ("matrix", "holeSlots", json!([0, 17, 31])),
        ("matrix", "sourceLayoutSha256", json!("00")),
        ("matrix", "regionalProjectionSha256", json!("00")),
        ("matrix", "knownAnswerTransactionId", json!(91)),
        ("matrix", "knownAnswerColor", json!("47E10D")),
        ("matrix", "knownAnswerFeatureReportSha256", json!("00")),
    ] {
        let mut edited = manifest.clone();
        edited[section][member] = value;
        assert!(!matches(&edited, &layout), "{section}.{member}");
    }
    for effects in [json!(["static"]), json!(["off", "breathing"])] {
        let mut edited = manifest.clone();
        edited["supportedEffects"] = effects;
        assert!(!matches(&edited, &layout));
    }
    let mut edited = manifest.clone();
    edited["matrix"]["extra"] = json!(1);
    assert!(!matches(&edited, &layout));
    assert!(matches(&manifest, &layout));
}

/// Changed oracle bytes fail the pin, even ones that leave its meaning
/// unchanged, and approval needs the oracle as well as the manifest; each
/// reviewed oracle member that differs refuses even under a matching pin.
#[test]
fn a_changed_oracle_admits_nothing() {
    let manifest = manifest();
    let changed = respaced(ORACLE);
    assert!(oracle_matches(
        &serde_json::from_slice(&changed).unwrap(),
        &manifest
    ));
    assert!(!oracle_admits(&changed, MANIFEST));
    assert!(!approves(MANIFEST, &changed));
    let oracle = oracle();
    let edits: Vec<Edit> = vec![
        ("schema", Box::new(|root| root["schemaVersion"] = json!(2))),
        (
            "kind",
            Box::new(|root| root["evidenceKind"] = json!("other")),
        ),
        (
            "device",
            Box::new(|root| root["device"]["productId"] = json!(0x02c6)),
        ),
        (
            "bios",
            Box::new(|root| root["device"]["biosVersion"] = json!("4.00")),
        ),
        (
            "capture pin",
            Box::new(|root| root["coreCapture"]["sha256"] = json!("00")),
        ),
        (
            "capture decoded",
            Box::new(|root| root["rippleCapture"]["decoded"] = json!(false)),
        ),
        (
            "layout query",
            Box::new(|root| root["protocol"]["transitionRequests"][0] = json!("00/86:010000")),
        ),
        (
            "handshake",
            Box::new(|root| root["protocol"]["transitionRequests"][2] = json!("03/0A:0501")),
        ),
        (
            "handshake reply",
            Box::new(|root| root["protocol"]["transitionResponses"][2] = json!("03/0A:0501")),
        ),
        (
            "matrix",
            Box::new(|root| root["protocol"]["matrixFeatureReportLength"] = json!(374)),
        ),
        (
            "brightness",
            Box::new(|root| root["protocol"]["brightnessQuery"] = json!("03/83:010400")),
        ),
        ("added", Box::new(|root| root["extra"] = json!(true))),
        (
            "missing",
            Box::new(|root| {
                root.as_object_mut().unwrap().remove("variantCapture");
            }),
        ),
    ];
    for (name, edit) in edits {
        let mut edited = oracle.clone();
        edit(&mut edited);
        assert!(!oracle_matches(&edited, &manifest), "{name}");
    }
    assert!(oracle_matches(&oracle, &manifest));
}
