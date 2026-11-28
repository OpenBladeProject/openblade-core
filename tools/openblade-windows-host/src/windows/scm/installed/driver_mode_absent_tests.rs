//! Without `service-keyboard-driver-mode-read-v1` the installed reader is as
//! it was before the read existed: no 00/84 query and no
//! `keyboardDriverModeActive` member in any status it publishes.
use super::driver_mode_rig::{DRIVER, Rig, status};
use serde_json::Value;

fn assert_no_member(status: &Value) {
    assert!(status.get("keyboardDriverModeActive").is_none(), "{status}");
}

#[tokio::test]
async fn without_the_feature_no_query_is_sent_and_no_member_is_published() {
    let rig = Rig::new();
    rig.reply(DRIVER);
    assert_no_member(&serde_json::to_value(status(0)).unwrap());
    for _ in 0..3 {
        let observed = rig.observe().await.unwrap();
        assert_eq!(observed["deviceVerified"], true);
        assert_no_member(&observed);
        for published in rig.published() {
            assert_no_member(&published);
        }
    }
    assert_eq!(rig.io.queries(), 0);
    assert_eq!(rig.io.sent.lock().unwrap().len(), 3 * 2);
    rig.assert_reads_only();
}
