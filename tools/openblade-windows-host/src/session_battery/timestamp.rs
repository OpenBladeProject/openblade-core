//! Report timestamps at 100 ns precision, with pinned invariant date forms.
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
        let slash_date = value.as_bytes().get(2) == Some(&b'/');
        let (year, month, day) = if slash_date {
            if value.get(5..6)? != "/" {
                return None;
            }
            (
                digits(value.get(6..10)?)?,
                digits(&value[..2])?,
                digits(value.get(3..5)?)?,
            )
        } else {
            if value.get(4..5)? != "-" || value.get(7..8)? != "-" {
                return None;
            }
            (
                digits(value.get(..4)?)?,
                digits(value.get(5..7)?)?,
                digits(value.get(8..10)?)?,
            )
        };
        if year == 0 {
            return None;
        }
        let mut time = SYSTEMTIME {
            wYear: year + YEAR_SHIFT,
            wMonth: month,
            wDay: day,
            ..SYSTEMTIME::default()
        };
        let mut rest = value.get(10..)?;
        let mut fraction = 0;
        if rest.starts_with(['T', 't', ' ']) {
            let separator = rest.as_bytes()[0];
            rest = &rest[1..];
            if separator == b' ' {
                rest = rest.trim_start();
            } else if slash_date {
                return None;
            }
            time.wHour = digits(rest.get(..2)?)?;
            time.wMinute = digits(rest.get(2..5)?.strip_prefix(':')?)?;
            rest = &rest[5..];
            if let Some(seconds) = rest.strip_prefix(':') {
                time.wSecond = digits(seconds.get(..2)?)?;
                rest = &seconds[2..];
                if let Some(decimal) = rest.strip_prefix('.') {
                    let width = decimal.bytes().take_while(u8::is_ascii_digit).count();
                    let significant = width.min(7);
                    fraction = decimal[..significant].parse::<i64>().ok()?
                        * 10_i64.pow((7 - significant) as u32);
                    if width > 7 && decimal.as_bytes()[7] >= b'5' {
                        fraction += 1;
                    }
                    rest = &decimal[width..];
                }
            }
            if slash_date && rest.starts_with(' ') {
                let marker = rest.trim_start();
                if let Some(meridiem) = marker.get(..2)
                    && (meridiem.eq_ignore_ascii_case("AM") || meridiem.eq_ignore_ascii_case("PM"))
                {
                    if !(1..=12).contains(&time.wHour) {
                        return None;
                    }
                    time.wHour %= 12;
                    if meridiem.eq_ignore_ascii_case("PM") {
                        time.wHour += 12;
                    }
                    rest = &marker[2..];
                }
            }
        }
        let offset = offset_seconds(rest.trim_start())?;
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

fn offset_seconds(value: &str) -> Option<i64> {
    if matches!(value, "" | "Z" | "z") {
        return Some(0);
    }
    let (sign, zone) = value.split_at_checked(1)?;
    if !matches!(sign, "+" | "-") {
        return None;
    }
    let (hours, minutes) = match zone.as_bytes() {
        [_] | [_, _] => (digits(zone)?, 0),
        [_, _, _, _] => (digits(zone.get(..2)?)?, digits(zone.get(2..)?)?),
        [_, _, b':', _, _] => (digits(zone.get(..2)?)?, digits(zone.get(3..)?)?),
        _ => return None,
    };
    if hours > 14 || minutes > 59 || (hours == 14 && minutes != 0) {
        return None;
    }
    let seconds = i64::from(hours) * 3600 + i64::from(minutes) * 60;
    Some(if sign == "-" { -seconds } else { seconds })
}
