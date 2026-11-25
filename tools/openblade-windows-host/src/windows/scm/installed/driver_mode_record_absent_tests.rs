//! A composition without keyboard Driver-mode ownership cannot honour its
//! record, so it keeps the owner directory's complete-set refusal: the
//! record or its stage stops the owner Store from opening at all.
use super::fixture::OwnerRoot;

fn record_bytes() -> String {
    let vectors: serde_json::Value = serde_json::from_str(include_str!(
        "../../../../tests/data/driver-mode-record-v1.json"
    ))
    .unwrap();
    vectors["records"]["acquireClaimed"]["bytes"]
        .as_str()
        .unwrap()
        .into()
}

#[test]
fn a_composition_without_keyboard_driver_mode_refuses_its_record_and_stage() {
    for name in [
        "keyboard-driver-mode.bin",
        "keyboard-driver-mode.bin.pending",
    ] {
        let root = OwnerRoot::new(&format!("absent-{name}"));
        drop(root.active('4'));
        assert!(root.store().is_ok(), "{name}");
        std::fs::write(root.path(name), record_bytes()).unwrap();
        assert!(root.store().is_err(), "{name}");
        assert_eq!(
            std::fs::read(root.path(name)).unwrap(),
            record_bytes().as_bytes()
        );
    }
}
