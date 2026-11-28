use super::*;

#[test]
fn ac_line_status_maps_to_power_evidence_and_keeps_the_console_session() {
    for (status, power) in [
        (Some(0), PowerSource::Battery),
        (Some(1), PowerSource::ExternalUnknown),
        (Some(255), PowerSource::Unknown),
        (Some(2), PowerSource::Unknown),
        (None, PowerSource::Unknown),
    ] {
        let observation = Observation::from_windows(status, Some(19));
        assert_eq!(observation.power, power, "{status:?}");
        assert_eq!(observation.console_session, Some(19));
    }
    assert_eq!(
        Observation::from_windows(Some(0), None).console_session,
        None
    );
}
