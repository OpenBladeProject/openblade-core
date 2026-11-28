//! Pure argument parsing only. No pipe, token or process APIs are called.
#![cfg(windows)]
#[path = "fixtures/scratch_child_channels.rs"]
#[allow(dead_code)] // Only parsing runs here; the candidate receipt fixture opens channels.
mod scratch_child_channels;

#[test]
fn only_canonical_nonsecret_routing_is_accepted() {
    let id = "1234567890abcdef1234567890abcdef";
    let args = ["--scratch-channels".into(), id.into(), "42".into()];
    assert_eq!(scratch_child_channels::parse(&args).unwrap(), (id, 42));
    for args in [
        vec![],
        vec!["wrong".into(), id.into(), "42".into()],
        vec!["--scratch-channels".into(), id.to_uppercase(), "42".into()],
        vec!["--scratch-channels".into(), id.into(), "0".into()],
        vec!["--scratch-channels".into(), id.into(), "4294967296".into()],
        vec!["--scratch-channels".into(), "0".repeat(32), "42".into()],
    ] {
        assert!(scratch_child_channels::parse(&args).is_err());
    }
}
