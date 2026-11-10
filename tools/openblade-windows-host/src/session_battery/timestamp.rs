//! ISO 8601 timestamps at the report's 100 ns precision.
#![allow(unsafe_code)]

use serde::{Serialize, Serializer};
use windows_sys::Win32::{
    Foundation::{FILETIME, SYSTEMTIME},
    System::Time::{FileTimeToSystemTime, SystemTimeToFileTime},
};

const TICKS_PER_SECOND: i64 = 10_000_000;
pub(super) const DAY: i64 = 86_400 * TICKS_PER_SECOND;
// Windows FILETIME starts at 1601. Shift by five Gregorian cycles to
// preserve leap days while representing the full managed year range.
const YEAR_SHIFT: u16 = 2000;
const MIN: i64 = 146_097 * DAY;
const MAX: i64 = MIN + 3_652_059 * DAY - 1;

#[derive(Clone, Copy, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub(super) struct Timestamp(i64);

impl Timestamp {
    pub fn parse(value: &str) -> Option<Self> {
        let value = value.trim();
        let number = |start, end| digits(value.get(start..end)?);
        let year = number(0, 4)?;
        if year == 0 || value.get(4..5)? != "-" || value.get(7..8)? != "-" {
            return None;
        }
        let mut time = SYSTEMTIME {
            wYear: year + YEAR_SHIFT,
            wMonth: number(5, 7)?,
            wDay: number(8, 10)?,
            ..SYSTEMTIME::default()
        };
        let mut rest = value.get(10..)?;
        if rest.starts_with(['T', 't', ' ']) {
            if rest.get(3..4)? != ":" || rest.get(6..7)? != ":" {
                return None;
            }
            time.wHour = number(11, 13)?;
            time.wMinute = number(14, 16)?;
            time.wSecond = number(17, 19)?;
            rest = value.get(19..)?;
        }
        let mut fraction = 0;
        if let Some(decimal) = rest.strip_prefix('.') {
            let width = decimal.bytes().take_while(u8::is_ascii_digit).count();
            if !(1..=7).contains(&width) {
                return None;
            }
            fraction = decimal[..width].parse::<i64>().ok()? * 10_i64.pow((7 - width) as u32);
            rest = &decimal[width..];
        }
        let offset = match rest.as_bytes() {
            b"" | b"Z" | b"z" => 0,
            [sign @ (b'+' | b'-'), _, _, b':', _, _] => {
                let hours = digits(&rest[1..3])?;
                let minutes = digits(&rest[4..6])?;
                if hours > 14 || minutes > 59 || (hours == 14 && minutes != 0) {
                    return None;
                }
                let seconds = i64::from(hours) * 3600 + i64::from(minutes) * 60;
                if *sign == b'-' { -seconds } else { seconds }
            }
            _ => return None,
        };
        let mut ft = FILETIME::default();
        // SAFETY: The pointers refer to live SYSTEMTIME/FILETIME values.
        if unsafe { SystemTimeToFileTime(&time, &mut ft) } == 0 {
            return None;
        }
        let ticks = (i64::from(ft.dwHighDateTime) << 32) | i64::from(ft.dwLowDateTime);
        Self(ticks).checked_add(fraction - offset * TICKS_PER_SECOND)
    }

    pub fn checked_add(self, ticks: i64) -> Option<Self> {
        let result = self.0.checked_add(ticks)?;
        (MIN..=MAX).contains(&result).then_some(Self(result))
    }
}

impl Serialize for Timestamp {
    fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        let ft = FILETIME {
            dwHighDateTime: (self.0 >> 32) as u32,
            dwLowDateTime: self.0 as u32,
        };
        let mut time = SYSTEMTIME::default();
        // SAFETY: The pointers refer to live FILETIME/SYSTEMTIME values.
        if unsafe { FileTimeToSystemTime(&ft, &mut time) } == 0 {
            return Err(serde::ser::Error::custom(
                "invalid battery report timestamp",
            ));
        }
        let mut timestamp = format!(
            "{:04}-{:02}-{:02}T{:02}:{:02}:{:02}",
            time.wYear - YEAR_SHIFT,
            time.wMonth,
            time.wDay,
            time.wHour,
            time.wMinute,
            time.wSecond,
        );
        let fraction = self.0 % TICKS_PER_SECOND;
        if fraction != 0 {
            let decimal = format!("{fraction:07}");
            timestamp.push('.');
            timestamp.push_str(decimal.trim_end_matches('0'));
        }
        timestamp.push_str("+00:00");
        serializer.serialize_str(&timestamp)
    }
}

fn digits(value: &str) -> Option<u16> {
    if value.is_empty() || !value.bytes().all(|byte| byte.is_ascii_digit()) {
        return None;
    }
    value.parse().ok()
}
