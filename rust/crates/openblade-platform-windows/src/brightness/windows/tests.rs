use super::*;
use std::cell::Cell;
use std::collections::VecDeque;

#[test]
fn malformed_query_properties_skip_later_reads_and_provider_errors_survive() {
    let row = decode_query_row(
        Err(NativeFailure::InvalidData),
        || panic!("unreachable percent"),
        || panic!("unreachable levels"),
    )
    .unwrap();
    assert!(row.instance.is_none());
    for percent in [None, Some(101)] {
        let row = decode_query_row(
            Ok(Some("panel".into())),
            || Ok(percent),
            || panic!("unreachable levels"),
        )
        .unwrap();
        assert!(row.levels.is_none());
    }
    let result = decode_query_row(
        Ok(Some("panel".into())),
        || Ok(Some(0)),
        || Err(NativeFailure::Status(-1)),
    );
    assert!(matches!(result, Err(NativeFailure::Status(-1))));
}

fn remaining() -> Result<Duration, NativeFailure> {
    Ok(Duration::from_millis(60))
}

#[test]
fn only_transport_statuses_retire_the_cached_proxy() {
    assert!(disconnected(RPC_E_DISCONNECTED));
    assert!(disconnected(0x8007_06ba_u32 as i32));
    assert!(disconnected(WBEM_E_TRANSPORT_FAILURE));
    assert!(!disconnected(
        windows_sys::Win32::System::Wmi::WBEM_E_NOT_FOUND
    ));
}

#[test]
fn enumeration_requires_end_after_delays_and_allows_no_overflow() {
    let mut steps = VecDeque::from([Next::Pending, Next::Item(1), Next::Pending, Next::End]);
    assert_eq!(
        collect(
            &remaining,
            1,
            |ms| {
                assert_eq!(ms, POLL_MS);
                Ok(steps.pop_front().unwrap())
            },
            Ok
        ),
        Ok(vec![1])
    );
    assert_eq!(
        collect(&remaining, 1, |_| Ok(Next::Item(1)), Ok),
        Err(NativeFailure::InvalidData)
    );
}

#[test]
fn late_partial_enumeration_never_admits_a_row() {
    let expired = Cell::new(false);
    let budget = || {
        if expired.get() {
            Err(NativeFailure::DeadlineExceeded)
        } else {
            remaining()
        }
    };
    let mut steps = VecDeque::from([Next::Item(1), Next::Pending]);
    let result = collect(
        &budget,
        64,
        |_| {
            let next = steps.pop_front().unwrap();
            expired.set(matches!(next, Next::Pending));
            Ok(next)
        },
        Ok,
    );
    assert_eq!(result, Err(NativeFailure::DeadlineExceeded));
}

#[test]
fn exact_relative_method_path_and_full_instance_are_required() {
    let instance = r"DISPLAY\ABC\1_0";
    let path = r#"WmiMonitorBrightnessMethods.InstanceName="DISPLAY\\ABC\\1_0""#;
    assert_eq!(validate_method_path(instance, path), Ok(()));
    for invalid in [
        r#"WmiMonitorBrightness.InstanceName="DISPLAY\\ABC\\1_0""#,
        r#"WmiMonitorBrightnessMethods.InstanceName="DISPLAY\\ABC\\1_1""#,
        r#"\\other\root\wmi:WmiMonitorBrightnessMethods.InstanceName="DISPLAY\\ABC\\1_0""#,
        r#"WmiMonitorBrightnessMethods.InstanceName="DISPLAY\\ABC\\1_0",Other=1"#,
    ] {
        assert_eq!(
            validate_method_path(instance, invalid),
            Err(NativeFailure::InvalidData)
        );
    }
    let original = MethodIdentity {
        instance: instance.into(),
        path: path.into(),
    };
    let candidate = || MethodIdentity {
        instance: instance.into(),
        path: path.into(),
    };
    let sibling = MethodIdentity {
        instance: r"DISPLAY\ABC\1_1".into(),
        path: path.into(),
    };
    assert!(matches!(
        rebind(&original, &[sibling]),
        Err(NativeFailure::MissingData)
    ));
    assert!(matches!(
        rebind(&original, &[candidate(), candidate()]),
        Err(NativeFailure::AmbiguousData)
    ));
    assert!(rebind(&original, &[candidate()]).is_ok());
}

#[test]
fn invocation_status_and_provider_acknowledgement_are_separate() {
    let mut statuses = VecDeque::from([None, Some(0)]);
    let mut results = VecDeque::from([Next::Pending, Next::Item(23)]);
    assert_eq!(
        complete(
            &remaining,
            |_| Ok(statuses.pop_front().unwrap()),
            |_| Ok(results.pop_front().unwrap()),
            |code| Ok(Acknowledgement::Status(code))
        ),
        Ok(Acknowledgement::Status(23))
    );
    assert_eq!(
        complete::<()>(
            &remaining,
            |_| Ok(Some(-1)),
            |_| panic!("no output"),
            |_| unreachable!()
        ),
        Err(NativeFailure::Status(-1))
    );
    assert_eq!(
        complete::<()>(
            &remaining,
            |_| Ok(Some(0)),
            |_| Ok(Next::End),
            |_| unreachable!()
        ),
        Err(NativeFailure::MissingData)
    );
}

#[test]
fn late_result_cannot_become_acknowledgement() {
    let expired = Cell::new(false);
    assert_eq!(
        complete(
            &|| if expired.get() {
                Err(NativeFailure::DeadlineExceeded)
            } else {
                remaining()
            },
            |_| Ok(Some(0)),
            |_| {
                expired.set(true);
                Ok(Next::Item(()))
            },
            |_| panic!("must not decode a late reply")
        ),
        Err(NativeFailure::DeadlineExceeded)
    );
}
