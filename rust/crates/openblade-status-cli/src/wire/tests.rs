use super::*;
use std::future::{pending, ready};
use tokio::io::{AsyncWriteExt, duplex};

const ID: &str = "11111111-2222-4333-8444-555555555555";
const RESPONSE: &str = include_str!("../../fixtures/response.json");

fn run(future: impl std::future::Future<Output = ()>) {
    tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()
        .unwrap()
        .block_on(future);
}
fn framed(bytes: &[u8]) -> Vec<u8> {
    let mut result = (bytes.len() as i32).to_le_bytes().to_vec();
    result.extend_from_slice(bytes);
    result
}

#[test]
fn fixed_request_and_partial_write_match_independent_contract() {
    assert_eq!(
        request(ID),
        include_str!("../../fixtures/request.json")
            .trim_end()
            .as_bytes()
    );
    run(async {
        let (mut client, mut server) = duplex(1);
        let (sent, received) = tokio::join!(write_request(&mut client, ID), async {
            let mut bytes = [0; 93];
            server.read_exact(&mut bytes).await.unwrap();
            bytes
        });
        sent.unwrap();
        assert_eq!(&received[..4], &[89, 0, 0, 0]);
        assert_eq!(
            &received[4..],
            include_str!("../../fixtures/request.json")
                .trim_end()
                .as_bytes()
        );
    });
}

#[test]
fn summary_matches_independent_whitelist_and_preserves_nulls_numbers() {
    let output = summarize(RESPONSE.as_bytes(), ID).unwrap();
    assert_eq!(
        output,
        include_str!("../../fixtures/summary.json").trim_end()
    );
    assert!(!output.contains("SYNTHETIC"));
    assert!(output.len() < 1024);
    for (before, after) in [
        ("\"activePowerSource\":2", "\"activePowerSource\":123456"),
        ("\"controlAccess\":0", "\"controlAccess\":123456"),
        ("\"deviceVerified\":true", "\"deviceVerified\":false"),
    ] {
        let changed = RESPONSE.replace(before, after);
        assert!(summarize(changed.as_bytes(), ID).is_ok());
    }
    let value: Value = serde_json::from_str(RESPONSE).unwrap();
    let mut value = value;
    value["status"]["liveReadings"] = Value::Null;
    let output = summarize(&serde_json::to_vec(&value).unwrap(), ID).unwrap();
    assert!(output.contains("\"liveReadings\":null"));
    value["status"]
        .as_object_mut()
        .unwrap()
        .remove("liveReadings");
    assert!(
        summarize(&serde_json::to_vec(&value).unwrap(), ID)
            .unwrap()
            .contains("\"liveReadings\":null")
    );
}

#[test]
fn rejects_mismatch_duplicates_malformed_and_conflicting_status() {
    for (before, after) in [
        ("\"version\":51", "\"version\":78"),
        ("\"frame\":\"response\"", "\"frame\":\"statusEvent\""),
        (ID, "00000000-0000-0000-0000-000000000001"),
    ] {
        assert_eq!(
            summarize(RESPONSE.replace(before, after).as_bytes(), ID),
            Err(Error::MismatchedResponse)
        );
    }
    for input in [
        RESPONSE.replace("\"success\":true", "\"success\":true,\"Success\":false"),
        RESPONSE.replace("\"fan1Rpm\":2700", "\"fan1Rpm\":2700,\"fan1Rpm\":5"),
        RESPONSE.replace("\"serial\":", "\"serial\":0,\"serial\":"),
        RESPONSE.replace(
            "\"success\":true",
            "\"success\":true,\"\\u0073uccess\":false",
        ),
        format!("{RESPONSE} {{}}"),
        RESPONSE.replace("\"fan1Rpm\":2700", "\"fan1Rpm\":\"2700\""),
        RESPONSE.replace(
            "\"revision\":9223372036854775807",
            "\"revision\":9223372036854775808",
        ),
        "{".to_string(),
    ] {
        assert_eq!(summarize(input.as_bytes(), ID), Err(Error::InvalidJson));
    }
    assert_eq!(summarize(&[0xff], ID), Err(Error::InvalidJson));
    assert_eq!(
        summarize(
            RESPONSE
                .replace("\"success\":true", "\"success\":false")
                .as_bytes(),
            ID
        ),
        Err(Error::ServiceRejected)
    );
    for input in [
        RESPONSE.replace("\"error\":null", "\"error\":\"SYNTHETIC-SECRET\""),
        RESPONSE.replace("\"result\":null", "\"result\":{}"),
        RESPONSE.replace("\"archive\":null", "\"archive\":{}"),
        RESPONSE.replace("\"revision\":9223372036854775807", "\"revision\":-1"),
        RESPONSE.replace("\"fan1Rpm\":2700", "\"fan1Rpm\":20001"),
        RESPONSE.replace(
            "\"cpuTemperatureCelsius\":65",
            "\"cpuTemperatureCelsius\":116",
        ),
        RESPONSE.replace(
            "\"batteryPowerFlowMilliwatts\":-32768",
            "\"batteryPowerFlowMilliwatts\":-1000001",
        ),
    ] {
        assert_eq!(summarize(input.as_bytes(), ID), Err(Error::InvalidStatus));
    }
    let mut value: Value = serde_json::from_str(RESPONSE).unwrap();
    value["status"] = Value::Null;
    assert_eq!(
        summarize(&serde_json::to_vec(&value).unwrap(), ID),
        Err(Error::InvalidStatus)
    );
}

#[test]
fn handles_partial_prefix_body_exact_bound_and_truncation() {
    run(async {
        let (mut client, mut server) = duplex(1);
        let frame = framed(RESPONSE.as_bytes());
        let (read, ()) = tokio::join!(read_frame(&mut client), async {
            for byte in frame {
                server.write_all(&[byte]).await.unwrap();
            }
        });
        assert_eq!(read.unwrap(), RESPONSE.as_bytes());
        for bytes in [
            vec![],
            vec![1],
            vec![1, 0, 0],
            vec![2, 0, 0, 0, b'{'],
            vec![0; 4],
            (-1_i32).to_le_bytes().to_vec(),
            65537_i32.to_le_bytes().to_vec(),
        ] {
            assert_eq!(
                read_frame(&mut bytes.as_slice()).await,
                Err(Error::InvalidFrame)
            );
        }
        let exact = framed(&vec![b' '; MAX_FRAME]);
        assert_eq!(
            read_frame(&mut exact.as_slice()).await.unwrap().len(),
            MAX_FRAME
        );
        assert_eq!(
            summarize(&vec![b' '; MAX_FRAME + 1], ID),
            Err(Error::InvalidFrame)
        );
    });
}

#[test]
fn complete_exchange_submits_only_fixed_getstatus_then_reads_once() {
    run(async {
        let (mut client, mut server) = duplex(7);
        let (result, ()) = tokio::join!(exchange(&mut client, ID), async {
            let mut request = vec![0; 93];
            server.read_exact(&mut request).await.unwrap();
            assert_eq!(
                request,
                framed(
                    include_str!("../../fixtures/request.json")
                        .trim_end()
                        .as_bytes()
                )
            );
            server
                .write_all(&framed(RESPONSE.as_bytes()))
                .await
                .unwrap();
        });
        assert_eq!(
            result.unwrap(),
            include_str!("../../fixtures/summary.json").trim_end()
        );
    });
}

#[test]
fn bounded_read_write_overall_and_cancellation_paths() {
    run(async {
        let (mut client, _server) = duplex(1);
        assert_eq!(read_frame(&mut client).await, Err(Error::Timeout));
        let (mut client, _server) = duplex(1);
        assert_eq!(write_request(&mut client, ID).await, Err(Error::Timeout));
        assert_eq!(bounded(pending(), pending()).await, Err(Error::Timeout));
        assert_eq!(bounded(pending(), ready(())).await, Err(Error::Cancelled));
        // Cancellation wins before even polling a ready operation.
        assert_eq!(
            bounded(
                async { panic!("operation polled after cancellation") },
                ready(())
            )
            .await,
            Err(Error::Cancelled)
        );
    });
}

#[cfg(not(windows))]
#[test]
fn unsupported_transport_does_not_invent_status() {
    assert_eq!(
        crate::transport::get_status(),
        Err(Error::UnsupportedTransport)
    );
}
