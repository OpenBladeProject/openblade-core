use super::*;
use serde_json::{Value, json};

const ID: &str = "9f876742-1234-4321-abcd-d3ea3e1cf102";
const HEAD: &str = "<BatteryReport><ReportInformation><ScanTime>2026-01-02T12:00:00Z</ScanTime></ReportInformation>";

fn typed(xml: impl AsRef<[u8]>) -> Option<Value> {
    serde_json::to_value(report::parse(xml.as_ref())?).ok()
}

#[test]
fn shared_timestamp_cases_match_managed_parser() {
    for case in include_str!("datetime-cases.tsv").lines() {
        let (input, expected) = case.split_once('\t').unwrap();
        let xml = format!(
            "<BatteryReport><ReportInformation><ScanTime>{input}</ScanTime></ReportInformation></BatteryReport>"
        );
        let report = typed(xml);
        if expected == "invalid" {
            assert!(report.is_none(), "{input}");
        } else {
            assert_eq!(
                report.unwrap_or_else(|| panic!("rejected {input}"))["generatedAtUtc"],
                expected,
                "{input}"
            );
        }
    }
}

#[test]
fn equal_clipped_starts_keep_report_order_across_large_sort() {
    let report = typed(include_bytes!("equal-start.xml")).unwrap();
    let intervals = report["intervals"].as_array().unwrap();
    assert_eq!(intervals.len(), 2);
    assert_eq!(intervals[0]["startUtc"], report["windowStartUtc"]);
    assert_eq!(intervals[0]["endUtc"], "2026-01-01T13:00:00+00:00");
    assert_eq!(intervals[0]["startChargeMilliwattHours"], 1);
    assert_eq!(intervals[1]["startUtc"], intervals[0]["endUtc"]);
    assert_eq!(intervals[1]["endUtc"], "2026-01-01T14:00:00+00:00");
    assert_eq!(intervals[1]["startChargeMilliwattHours"], 2);
}

#[test]
fn pinned_managed_timeline_clips_and_marks_interior_gap() {
    let value = typed(TIMELINE).expect("pinned report must parse");
    assert_eq!(value["generatedAtUtc"], "2026-01-02T12:00:00+00:00");
    assert_eq!(value["windowStartUtc"], "2026-01-01T12:00:00+00:00");
    assert_eq!(value["windowEndUtc"], value["generatedAtUtc"]);
    assert_eq!(value["source"], 0);
    assert_eq!(value["status"]["designCapacityMilliwattHours"], 60_000);
    assert_eq!(value["status"]["fullChargeCapacityMilliwattHours"], 50_000);
    assert_eq!(value["status"]["cycleCount"], 120);
    assert!((value["status"]["healthPercent"].as_f64().unwrap() - 83.33333333333333).abs() < 1e-12);
    let intervals = value["intervals"].as_array().unwrap();
    assert_eq!(intervals.len(), 3);
    assert_eq!(intervals[0]["startUtc"], value["windowStartUtc"]);
    assert_eq!(intervals[0]["endUtc"], "2026-01-01T13:30:00+00:00");
    assert_eq!(intervals[0]["powerSource"], 1);
    assert_eq!(intervals[0]["activity"], 1);
    assert_eq!(intervals[0]["startChargeMilliwattHours"], 40_000);
    assert_eq!(intervals[0]["startChargePercent"], 80.0);
    assert_eq!(intervals[0]["dischargeMilliwattHours"], 5_000);
    assert_eq!(intervals[1]["startUtc"], intervals[0]["endUtc"]);
    assert_eq!(intervals[1]["endUtc"], intervals[2]["startUtc"]);
    assert_eq!(intervals[1]["powerSource"], 0);
    assert_eq!(intervals[1]["activity"], 0);
    assert!(intervals[1]["dischargeMilliwattHours"].is_null());
    assert_eq!(intervals[2]["endUtc"], "2026-01-01T14:30:00+00:00");
    assert_eq!(intervals[2]["powerSource"], 2);
    assert_eq!(intervals[2]["activity"], 2);
}

#[test]
fn normalizes_offsets_and_preserves_precision_in_utf16() {
    let xml = r#"<?xml version="1.0" encoding="utf-16"?>
<BatteryReport><ReportInformation>
<ScanTime>2026-01-02T18:00:00.0000001+06:00</ScanTime>
<ReportStartTime>2026-01-02T16:00:00+06:00</ReportStartTime>
</ReportInformation><RecentUsage>
<UsageEntry Timestamp="2026-01-02T17:00:00.0000001+06:00" Duration="36000000000" />
</RecentUsage></BatteryReport>"#;
    let bytes: Vec<_> = [0xff, 0xfe]
        .into_iter()
        .chain(xml.encode_utf16().flat_map(u16::to_le_bytes))
        .collect();
    let value = typed(bytes).expect("UTF-16 report must parse");
    assert_eq!(value["generatedAtUtc"], "2026-01-02T12:00:00.0000001+00:00");
    assert_eq!(value["windowStartUtc"], "2026-01-02T10:00:00+00:00");
    assert_eq!(
        value["intervals"][0]["startUtc"],
        "2026-01-02T11:00:00.0000001+00:00"
    );
    assert_eq!(value["intervals"][0]["endUtc"], value["generatedAtUtc"]);
    assert!(value["intervals"][0]["dischargeMilliwattHours"].is_null());
}

#[test]
fn sums_valid_batteries_and_leaves_optional_values_unknown() {
    let value = typed(format!("{HEAD}<Batteries>\
<Battery><DesignCapacity>30000</DesignCapacity><FullChargeCapacity>25000</FullChargeCapacity><CycleCount>20</CycleCount></Battery>\
<Battery><DesignCapacity>20000</DesignCapacity><FullChargeCapacity>15000</FullChargeCapacity><CycleCount>30</CycleCount></Battery>\
</Batteries><RecentUsage><UsageEntry Timestamp=\"2026-01-02T11:00:00Z\" Duration=\"6000000000\" \
Ac=\"future\" EntryType=\"other\" ChargeCapacity=\"+40\" Discharge=\"-7\" /></RecentUsage></BatteryReport>")).unwrap();
    assert_eq!(
        value["status"],
        json!({
            "designCapacityMilliwattHours": 50000, "fullChargeCapacityMilliwattHours": 40000,
            "healthPercent": 80.0, "cycleCount": null,
        })
    );
    let interval = &value["intervals"][0];
    assert_eq!(interval["powerSource"], 0);
    assert_eq!(interval["activity"], 0);
    assert!(interval["startChargeMilliwattHours"].is_null());
    assert!(interval["startChargePercent"].is_null());
    assert_eq!(interval["dischargeMilliwattHours"], -7);
}

#[test]
fn rejects_invalid_xml_and_checks_byte_and_projection_bounds() {
    for xml in [
        "",
        "<BatteryReport/>",
        "<BatteryReport><ReportInformation><ScanTime>2026-02-31T12:00:00Z</ScanTime></ReportInformation></BatteryReport>",
        "<BatteryReport><ReportInformation><ScanTime>2026-01-02T12:00:00Z</ScanTime></ReportInformation>",
        "<!DOCTYPE BatteryReport [<!ENTITY scan '2026-01-02T12:00:00Z'>]><BatteryReport><ReportInformation><ScanTime>&scan;</ScanTime></ReportInformation></BatteryReport>",
    ] {
        assert!(typed(xml).is_none(), "{xml}");
    }
    assert!(typed(vec![b' '; xml::MAX_BYTES + 1]).is_none());
    let usage = "<UsageEntry/>".repeat(1025);
    assert!(
        typed(format!(
            "{HEAD}<RecentUsage>{usage}</RecentUsage></BatteryReport>"
        ))
        .is_none()
    );
    assert!(
        typed(format!(
            "{HEAD}<RecentUsage>{}</RecentUsage></BatteryReport>",
            "<UsageEntry/>".repeat(1024)
        ))
        .is_some()
    );

    let separated = |count| {
        let mut xml = format!("{HEAD}<RecentUsage>");
        for index in 0..count {
            use std::fmt::Write;
            write!(
                xml,
                r#"<UsageEntry Timestamp="2026-01-02T10:{:02}:{:02}Z" Duration="10000000" />"#,
                index / 30,
                index % 30 * 2
            )
            .unwrap();
        }
        xml.push_str("</RecentUsage></BatteryReport>");
        xml
    };
    assert_eq!(
        typed(separated(256)).unwrap()["intervals"]
            .as_array()
            .unwrap()
            .len(),
        511
    );
    assert!(typed(separated(257)).is_none());
}

#[test]
fn request_requires_v1_and_one_nonempty_guid_with_no_other_members() {
    let accepted = format!(r#"{{"VERSION":"1","RequestId":"{ID}"}}"#);
    assert_eq!(
        serde_json::from_str::<Request>(&accepted)
            .unwrap()
            .request_id,
        ID
    );
    for bad in [
        format!(r#"{{"version":2,"requestId":"{ID}"}}"#),
        format!(r#"{{"version":1,"requestId":"{ID}","REQUESTID":"{ID}"}}"#),
        format!(r#"{{"version":1,"requestId":"{ID}","outputPath":"report.xml"}}"#),
        r#"{"version":1,"requestId":"00000000-0000-0000-0000-000000000000"}"#.to_owned(),
        r#"{"version":1}"#.to_owned(),
        "null".to_owned(),
    ] {
        assert!(serde_json::from_str::<Request>(&bad).is_err(), "{bad}");
    }
}

#[tokio::test(start_paused = true)]
async fn frames_distinguish_missing_malformed_bounded_and_failed_reports() {
    for (scenario, status, error) in [
        (Scenario::Available, 0, None),
        (Scenario::Missing, 1, Some(UNAVAILABLE)),
        (Scenario::Malformed, 1, Some(UNAVAILABLE)),
        (Scenario::Oversized, 1, Some(UNAVAILABLE)),
        (Scenario::Failed, 2, Some(FAILED)),
    ] {
        let result = timeout(DISPATCH_TIMEOUT, scenario.read())
            .await
            .unwrap_or(Err(()));
        let frame = Response::from_result(ID, result).frame().unwrap();
        assert_eq!(
            i32::from_le_bytes(frame[..4].try_into().unwrap()) as usize,
            frame.len() - 4
        );
        let value: Value = serde_json::from_slice(&frame[4..]).unwrap();
        assert_eq!(value["version"], 1);
        assert_eq!(value["requestId"], ID);
        assert_eq!(value["status"], status);
        assert_eq!(value["error"], json!(error));
        assert_eq!(value["report"].is_null(), status != 0);
    }
}

#[tokio::test(start_paused = true)]
async fn stalled_dispatch_retires_on_deadline_or_client_close_without_a_reply() {
    let (mut server, client) = tokio::io::duplex(16);
    let started = tokio::time::Instant::now();
    assert!(dispatch(&mut server, Scenario::Stalled).await.is_none());
    assert_eq!(started.elapsed(), DISPATCH_TIMEOUT);

    drop(client);
    let started = tokio::time::Instant::now();
    assert!(dispatch(&mut server, Scenario::Stalled).await.is_none());
    assert_eq!(started.elapsed(), Duration::ZERO);
}

#[tokio::test]
async fn bounded_frame_reader_rejects_prefix_before_waiting_for_body() {
    for length in [-1, 0, MAX_FRAME + 1] {
        let prefix = length.to_le_bytes();
        assert_eq!(
            read_frame_body(&mut prefix.as_slice(), MAX_FRAME)
                .await
                .unwrap_err()
                .kind(),
            io::ErrorKind::InvalidData
        );
    }
    let mut truncated = [1, 0, 0, 0].as_slice();
    assert_eq!(
        read_frame_body(&mut truncated, MAX_FRAME)
            .await
            .unwrap_err()
            .kind(),
        io::ErrorKind::UnexpectedEof
    );
}
