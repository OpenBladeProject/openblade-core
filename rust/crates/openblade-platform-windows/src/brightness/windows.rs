//! Fixed local WMI brightness transport. The worker owns this connection.

use super::*;
use crate::display::read_internal_display;
use windows_sys::w;

mod native;

use native::{Connection, Object};

const READINGS: &str = "SELECT Active, InstanceName, CurrentBrightness, Level, Levels FROM WmiMonitorBrightness WHERE Active = TRUE";
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
    fn connection(
        &mut self,
        remaining: &impl Fn() -> Result<Duration, NativeFailure>,
    ) -> Result<&Connection, NativeFailure> {
        remaining()?;
        if self.connection.is_none() {
            self.connection = Some(Connection::connect()?);
        }
        remaining()?;
        Ok(self.connection.as_ref().expect("connection established"))
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
        let connection = self.connection(&remaining)?;
        let mut enumeration = connection.query(READINGS)?;
        collect(
            &remaining,
            row_limit,
            |ms| enumeration.next(ms),
            |row| {
                let instance = row_instance(&row, READING_CLASS)?;
                let percent = row
                    .byte(w!("CurrentBrightness"))?
                    .ok_or(NativeFailure::MissingData)?;
                let count = row
                    .uint32(w!("Levels"))?
                    .ok_or(NativeFailure::MissingData)?;
                let levels = row
                    .bytes(w!("Level"), MAX_LEVEL_ENTRIES)?
                    .ok_or(NativeFailure::MissingData)?;
                if count as usize != levels.len() {
                    return Err(NativeFailure::InvalidData);
                }
                Ok(ReadingRow {
                    instance: Some(instance),
                    percent: Some(percent),
                    levels: Some(levels),
                })
            },
        )
    }

    fn active_methods(
        &mut self,
        budget: &CallBudget<'_>,
        row_limit: usize,
    ) -> Result<Vec<MethodRow<Self::Method>>, NativeFailure> {
        method_rows(self.connection(&|| budget.remaining())?, row_limit, &|| {
            budget.remaining()
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
        if level > 100 {
            return Err(NativeFailure::InvalidData);
        }
        let connection = self.connection(&|| submission.remaining())?;
        let methods = method_rows(connection, MAX_ROWS, &|| submission.remaining())?;
        let fresh = rebind(&method, &methods)?;
        let prepared = connection.prepare(&fresh.path, level, submission)?;
        // Preparation, including all method identity and input checks, is complete.
        submission.begin()?;
        let pending = connection.submit(prepared)?;
        complete(
            &|| submission.remaining(),
            |ms| pending.status(ms),
            |ms| pending.result(ms),
            |object| object.ack(),
        )
    }
}

fn row_instance(row: &Object, expected_class: &str) -> Result<String, NativeFailure> {
    let class = row.text(w!("__CLASS"), 64)?;
    let active = row.boolean(w!("Active"))?;
    let instance = row.text(w!("InstanceName"), MAX_INSTANCE)?;
    match (class, active, instance) {
        (Some(class), Some(true), Some(instance))
            if class.eq_ignore_ascii_case(expected_class) && valid_identity(&instance) =>
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
