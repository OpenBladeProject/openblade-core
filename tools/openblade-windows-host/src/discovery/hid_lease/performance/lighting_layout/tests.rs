use super::*;
use serde_json::json;

fn fixture() -> Value {
    serde_json::from_slice(FIXTURE).unwrap()
}

/// A named edit of a parsed document.
type Edit = (&'static str, Box<dyn Fn(&mut Value)>);

#[test]
fn the_pinned_fixture_loads_the_reviewed_maps() {
    let maps = source_maps().expect("the reviewed fixture loads");
    let layout = maps.layout();
    assert_eq!(maps.tables.len(), 6);
    let holes: Vec<usize> = (0..SLOT_COUNT)
        .filter(|&slot| layout.canvas[slot] == 0)
        .collect();
    assert_eq!(holes, CANVAS_HOLES);
    // Layout bytes 0, 1, 5 and 11 select the ANSI canvas table.
    for id in [0, 1, 5, 11] {
        assert_eq!(usize::from(maps.selector[id]), maps.canvas);
    }
    assert_ne!(usize::from(maps.selector[8]), maps.canvas);
    assert!(verify(&fixture()).is_some());
}

/// Changed fixture bytes fail the pin, even ones that leave the document's
/// meaning, and so every reviewed identity, unchanged.
#[test]
fn changed_fixture_bytes_fail_the_pin() {
    let at = FIXTURE.iter().position(|&b| b == b'{').unwrap() + 1;
    let mut bytes = FIXTURE.to_vec();
    bytes.insert(at, b' ');
    assert!(verify(&serde_json::from_slice(&bytes).unwrap()).is_some());
    assert!(load(&bytes).is_none());
}

/// Each reviewed identity managed requires refuses the maps when it
/// differs, even with a pin that would have matched.
#[test]
fn every_changed_source_identity_is_refused() {
    let edits: Vec<Edit> = vec![
        ("model", Box::new(|root| root["model"] = json!("RZ09-0528"))),
        (
            "productId",
            Box::new(|root| root["productId"] = json!(0x02c6)),
        ),
        ("columns", Box::new(|root| root["columns"] = json!(18))),
        (
            "image",
            Box::new(|root| root["source"]["imageSha256"] = json!("00")),
        ),
        (
            "table offset",
            Box::new(|root| root["tables"][1]["offset"] = json!(0xb346)),
        ),
        (
            "table usage",
            Box::new(|root| root["tables"][0]["hidUsages"][1] = json!(0)),
        ),
        (
            "duplicate usage",
            Box::new(|root| {
                let other = root["tables"][0]["hidUsages"][2].clone();
                root["tables"][0]["hidUsages"][1] = other;
            }),
        ),
        (
            "usage range",
            Box::new(|root| root["tables"][0]["hidUsages"][1] = json!(256)),
        ),
        (
            "duplicate layout",
            Box::new(|root| {
                let id = root["tables"][0]["layoutIds"][0].clone();
                root["tables"][1]["layoutIds"]
                    .as_array_mut()
                    .unwrap()
                    .push(id);
            }),
        ),
        (
            "missing table",
            Box::new(|root| {
                root["tables"].as_array_mut().unwrap().pop();
            }),
        ),
        (
            "selector address",
            Box::new(|root| root["selector"]["storedByteAddress"] = json!(0)),
        ),
        (
            "selector query",
            Box::new(|root| root["selector"]["query"] = json!("00/87")),
        ),
        (
            "selector branch",
            Box::new(|root| root["selector"]["allByteValues"][0]["branchOffset"] = json!(0)),
        ),
        (
            "selector table",
            Box::new(|root| {
                root["selector"]["allByteValues"][0]["tableOffset"] = json!(0xb915);
            }),
        ),
        (
            "selector coverage",
            Box::new(|root| {
                root["selector"]["allByteValues"]
                    .as_array_mut()
                    .unwrap()
                    .pop();
            }),
        ),
        (
            "union holes",
            Box::new(|root| root["unionHoles"] = json!([0, 17])),
        ),
    ];
    for (name, edit) in edits {
        let mut root = fixture();
        edit(&mut root);
        assert!(verify(&root).is_none(), "{name}");
    }
}
