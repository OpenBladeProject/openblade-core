//! Fixed local WMI brightness transport. The worker owns this connection.

use super::*;
use crate::display::read_internal_display;
use windows_sys::Win32::{
    Foundation::{RPC_E_DISCONNECTED, RPC_E_SERVER_DIED, RPC_E_SERVER_DIED_DNE},
    System::{Rpc::RPC_S_SERVER_UNAVAILABLE, Wmi::WBEM_E_TRANSPORT_FAILURE},
};
use windows_sys::w;

mod native;

pub(super) use native::ordinal_equal;
use native::{Connection, Object};
pub use native::{ProcessSecurityApartment, initialize_process_security, process_security_status};

const READINGS: &str = "SELECT Active, InstanceName, CurrentBrightness, Level, Levels FROM WmiMonitorBrightness WHERE Active = TRUE";
const QUERY_READINGS: &str =
    "SELECT InstanceName, CurrentBrightness, Level FROM WmiMonitorBrightness WHERE Active = TRUE";
const METHODS: &str =
    "SELECT Active, InstanceName FROM WmiMonitorBrightnessMethods WHERE Active = TRUE";
const READING_CLASS: &str = "WmiMonitorBrightness";
const METHOD_CLASS: &str = "WmiMonitorBrightnessMethods";
const MAX_INSTANCE: usize = 512;
const MAX_PATH: usize = MAX_INSTANCE * 2 + 64;
const POLL_MS: i32 = 25;

pub(super) enum Next<T> {
    Item(T),
    End,
    Pending,
}

#[derive(Default)]
pub(super) struct WindowsWmi {
    connection: Option<Connection>,
}

pub(super) struct MethodIdentity {
    instance: String,
    path: String,
}

impl WindowsWmi {
    fn with_connection<T>(
        &mut self,
        operation: impl FnOnce(&Connection) -> Result<T, NativeFailure>,
    ) -> Result<T, NativeFailure> {
        if self.connection.is_none() {
            self.connection = Some(Connection::connect()?);
        }
        let result = operation(self.connection.as_ref().expect("connection established"));
        if matches!(&result, Err(NativeFailure::Status(status)) if disconnected(*status)) {
            // Retire the dead proxy. A subsequent operation can connect afresh
            // while the Phase 6g owner retains any existing write pause.
            self.connection = None;
        }
        result
    }
}

impl WmiBrightnessNative for WindowsWmi {
    type Method = MethodIdentity;

    fn topology(
        &mut self,
        budget: &CallBudget<'_>,
    ) -> Result<InternalDisplayTopology, TopologyFailure> {
        budget.remaining().map_err(TopologyFailure::Native)?;
        read_internal_display().map_err(TopologyFailure::Display)
    }

    fn active_readings(
        &mut self,
        budget: &CallBudget<'_>,
        row_limit: usize,
    ) -> Result<Vec<ReadingRow>, NativeFailure> {
        let remaining = || budget.remaining();
        remaining()?;
        self.with_connection(|connection| {
            remaining()?;
            let mut enumeration = connection.query(READINGS)?;
            collect(
                &remaining,
                row_limit,
                |ms| enumeration.next(ms),
                reading_row,
            )
        })
    }

    fn query_readings(
        &mut self,
        budget: &CallBudget<'_>,
        row_limit: usize,
    ) -> Result<Vec<ReadingRow>, NativeFailure> {
        let remaining = || budget.remaining();
        remaining()?;
        self.with_connection(|connection| {
            remaining()?;
            let mut enumeration = connection.query(QUERY_READINGS)?;
            collect(&remaining, row_limit, |ms| enumeration.next(ms), query_row)
        })
    }

    fn active_methods(
        &mut self,
        budget: &CallBudget<'_>,
        row_limit: usize,
    ) -> Result<Vec<MethodRow<Self::Method>>, NativeFailure> {
        budget.remaining()?;
        self.with_connection(|connection| {
            method_rows(connection, row_limit, &|| budget.remaining())
        })
        .map(|methods| {
            methods
                .into_iter()
                .map(|handle| MethodRow {
                    instance: Some(handle.instance.clone()),
                    handle,
                })
                .collect()
        })
    }

    fn set_brightness(
        &mut self,
        method: Self::Method,
        level: u8,
        submission: &mut Submission<'_>,
    ) -> Result<Acknowledgement, NativeFailure> {
        submission.remaining()?;
        self.with_connection(|connection| {
            // Recheck active method identity after Phase 6g's independent
            // fresh reading and topology checks.
            let methods = method_rows(connection, MAX_ROWS, &|| submission.remaining())?;
            let fresh = rebind(&method, &methods)?;
            let prepared = connection.prepare(&fresh.path, level, submission)?;
            submission.begin()?;
            let pending = connection.submit(prepared)?;
            complete(
                &|| submission.remaining(),
                |ms| pending.status(ms),
                |ms| pending.result(ms),
                |object| object.ack(),
            )
        })
    }
}

fn disconnected(status: i32) -> bool {
    const SERVER_UNAVAILABLE: i32 = (0x8007_0000 | RPC_S_SERVER_UNAVAILABLE as u32) as i32;
    matches!(
        status,
        RPC_E_DISCONNECTED
            | RPC_E_SERVER_DIED
            | RPC_E_SERVER_DIED_DNE
            | SERVER_UNAVAILABLE
            | WBEM_E_TRANSPORT_FAILURE
    )
}

fn query_row(row: Object) -> Result<ReadingRow, NativeFailure> {
    decode_query_row(
        row.text(w!("InstanceName"), MAX_INSTANCE),
        || row.byte(w!("CurrentBrightness")),
        || row.bytes(w!("Level"), MAX_LEVEL_ENTRIES),
    )
}

fn decode_query_row(
    instance: Result<Option<String>, NativeFailure>,
    percent: impl FnOnce() -> Result<Option<u8>, NativeFailure>,
    levels: impl FnOnce() -> Result<Option<Vec<u8>>, NativeFailure>,
) -> Result<ReadingRow, NativeFailure> {
    // The fixed WQL class and Active predicate select these observations.
    // The managed reader consumes only the typed values. Keep provider errors
    // distinct from an individual row with missing or wrongly typed data.
    fn property<T>(value: Result<Option<T>, NativeFailure>) -> Result<Option<T>, NativeFailure> {
        match value {
            Err(NativeFailure::InvalidData | NativeFailure::MissingData) => Ok(None),
            other => other,
        }
    }
    let instance = property(instance)?;
    let percent = if instance.is_some() {
        property(percent())?
    } else {
        None
    };
    let levels = if matches!(percent, Some(0..=100)) {
        property(levels())?
    } else {
        None
    };
    Ok(ReadingRow {
        instance,
        percent,
        levels,
        data_failure: None,
    })
}

fn reading_row(row: Object) -> Result<ReadingRow, NativeFailure> {
    let instance = Some(row_instance(&row, READING_CLASS)?);
    let values = reading_values(&row);
    match values {
        Ok((percent, levels)) => Ok(ReadingRow {
            instance,
            percent: Some(percent),
            levels: Some(levels),
            data_failure: None,
        }),
        Err(failure @ (NativeFailure::MissingData | NativeFailure::InvalidData)) => {
            Ok(ReadingRow {
                instance,
                percent: None,
                levels: None,
                data_failure: Some(failure),
            })
        }
        Err(failure) => Err(failure),
    }
}

fn reading_values(row: &Object) -> Result<(u8, Vec<u8>), NativeFailure> {
    let percent = row
        .byte(w!("CurrentBrightness"))?
        .ok_or(NativeFailure::MissingData)?;
    let count = row
        .uint32(w!("Levels"))?
        .ok_or(NativeFailure::MissingData)? as usize;
    if count > MAX_LEVEL_ENTRIES {
        return Err(NativeFailure::InvalidData);
    }
    let levels = row
        .bytes(w!("Level"), MAX_LEVEL_ENTRIES)?
        .ok_or(NativeFailure::MissingData)?;
    if count != levels.len() {
        return Err(NativeFailure::InvalidData);
    }
    Ok((percent, levels))
}

fn row_instance(row: &Object, expected_class: &str) -> Result<String, NativeFailure> {
    let class = row.text(w!("__CLASS"), 64)?;
    let active = row.boolean(w!("Active"))?;
    let instance = row.text(w!("InstanceName"), MAX_INSTANCE)?;
    match (class, active, instance) {
        (Some(class), Some(true), Some(instance))
            if class.eq_ignore_ascii_case(expected_class)
                && !instance.trim().is_empty()
                && !instance.chars().any(char::is_control) =>
        {
            Ok(instance)
        }
        (None, _, _) | (_, None, _) | (_, _, None) => Err(NativeFailure::MissingData),
        _ => Err(NativeFailure::InvalidData),
    }
}

fn method_rows(
    connection: &Connection,
    row_limit: usize,
    remaining: &impl Fn() -> Result<Duration, NativeFailure>,
) -> Result<Vec<MethodIdentity>, NativeFailure> {
    remaining()?;
    let mut enumeration = connection.query(METHODS)?;
    collect(
        remaining,
        row_limit,
        |ms| enumeration.next(ms),
        |row| {
            let instance = row_instance(&row, METHOD_CLASS)?;
            let path = row
                .text(w!("__RELPATH"), MAX_PATH)?
                .ok_or(NativeFailure::MissingData)?;
            validate_method_path(&instance, &path)?;
            Ok(MethodIdentity { instance, path })
        },
    )
}

fn validate_method_path(instance: &str, path: &str) -> Result<(), NativeFailure> {
    // Match the exact local class and single string key. Only the provider's
    // validated relative path is passed to ExecMethod.
    let mut expected = String::from("WmiMonitorBrightnessMethods.InstanceName=\"");
    for character in instance.chars() {
        if matches!(character, '\\' | '"') {
            expected.push('\\');
        }
        expected.push(character);
    }
    expected.push('"');
    if path.eq_ignore_ascii_case(&expected) {
        Ok(())
    } else {
        Err(NativeFailure::InvalidData)
    }
}

fn rebind<'a>(
    method: &MethodIdentity,
    methods: &'a [MethodIdentity],
) -> Result<&'a MethodIdentity, NativeFailure> {
    let mut matching = methods
        .iter()
        .filter(|fresh| fresh.instance.eq_ignore_ascii_case(&method.instance));
    let fresh = matching.next().ok_or(NativeFailure::MissingData)?;
    if matching.next().is_some() {
        return Err(NativeFailure::AmbiguousData);
    }
    if !fresh.path.eq_ignore_ascii_case(&method.path) {
        return Err(NativeFailure::InvalidData);
    }
    Ok(fresh)
}

fn poll_ms(remaining: &impl Fn() -> Result<Duration, NativeFailure>) -> Result<i32, NativeFailure> {
    Ok(remaining()?.as_millis().min(POLL_MS as u128) as i32)
}

fn collect<T, R>(
    remaining: &impl Fn() -> Result<Duration, NativeFailure>,
    limit: usize,
    mut next: impl FnMut(i32) -> Result<Next<T>, NativeFailure>,
    mut decode: impl FnMut(T) -> Result<R, NativeFailure>,
) -> Result<Vec<R>, NativeFailure> {
    let mut rows = Vec::new();
    loop {
        let step = next(poll_ms(remaining)?)?;
        remaining()?;
        match step {
            Next::End => return Ok(rows),
            Next::Pending => {}
            Next::Item(item) if rows.len() < limit => rows.push(decode(item)?),
            Next::Item(_) => return Err(NativeFailure::InvalidData),
        }
    }
}

fn complete<T>(
    remaining: &impl Fn() -> Result<Duration, NativeFailure>,
    mut status: impl FnMut(i32) -> Result<Option<i32>, NativeFailure>,
    mut result: impl FnMut(i32) -> Result<Next<T>, NativeFailure>,
    decode: impl FnOnce(T) -> Result<Acknowledgement, NativeFailure>,
) -> Result<Acknowledgement, NativeFailure> {
    loop {
        let outcome = status(poll_ms(remaining)?)?;
        remaining()?;
        match outcome {
            None => {}
            Some(0) => break,
            Some(status) => return Err(NativeFailure::Status(status)),
        }
    }
    loop {
        let outcome = result(poll_ms(remaining)?)?;
        remaining()?;
        match outcome {
            Next::Item(item) => {
                let acknowledgement = decode(item)?;
                remaining()?;
                return Ok(acknowledgement);
            }
            Next::End => return Err(NativeFailure::MissingData),
            Next::Pending => {}
        }
    }
}

#[cfg(test)]
mod tests;
