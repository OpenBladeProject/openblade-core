//! Managed v2 marker wire format and reboot classification.
use super::StartupKind;
use serde::{
    Deserialize, Deserializer, Serialize,
    de::{IgnoredAny, MapAccess, Visitor},
};
use std::{fmt, io};
use windows_sys::Win32::{
    Foundation::{FILETIME, SYSTEMTIME},
    System::{
        SystemInformation::{GetSystemTimePreciseAsFileTime, GetTickCount64},
        Time::{
            DYNAMIC_TIME_ZONE_INFORMATION, FileTimeToSystemTime, GetDynamicTimeZoneInformation,
            GetTimeZoneInformationForYear, SystemTimeToFileTime, SystemTimeToTzSpecificLocalTimeEx,
            TIME_ZONE_ID_INVALID, TIME_ZONE_INFORMATION,
        },
    },
};

const SCHEMA: i32 = 2;
const SECOND: i64 = 10_000_000;
const DAY: i64 = 86_400 * SECOND;
// Shifting by 2000 years preserves Gregorian leap days and lets Windows
// convert the full DateTimeOffset range without negative FILETIME values.
const YEAR_SHIFT: u16 = 2000;
const MIN: i64 = 146_097 * DAY;
const MAX: i64 = MIN + 3_652_059 * DAY - 1;

#[derive(Clone, Copy)]
pub(super) struct Clock {
    utc: i64,
    uptime: i64,
}

impl Clock {
    pub(super) fn capture() -> io::Result<Self> {
        let mut time = FILETIME::default();
        // SAFETY: time points to live, writable FILETIME storage.
        unsafe { GetSystemTimePreciseAsFileTime(&mut time) };
        // SAFETY: GetTickCount64 takes no arguments and has no preconditions.
        let uptime = unsafe { GetTickCount64() };
        let utc = ticks(time)
            .checked_add(5 * 146_097 * DAY)
            .filter(|utc| (MIN..=MAX).contains(utc))
            .ok_or_else(|| io::Error::other("system UTC is out of marker range"))?;
        let uptime = i64::try_from(uptime)
            .ok()
            .and_then(|ms| ms.checked_mul(10_000))
            .ok_or_else(|| io::Error::other("system uptime is out of marker range"))?;
        Ok(Self { utc, uptime })
    }

    #[cfg(test)]
    pub(super) fn scripted(utc: &str, uptime: &str) -> Self {
        Self {
            utc: parse_utc(utc).expect("valid scripted UTC"),
            uptime: parse_uptime(uptime).expect("valid scripted uptime"),
        }
    }
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Document<'a> {
    schema_version: i32,
    started_at_utc: String,
    uptime_at_start: String,
    run_id: &'a str,
}

pub(super) fn encode(clock: Clock, run_id: &str) -> io::Result<Vec<u8>> {
    let time = calendar(clock.utc).ok_or_else(io::Error::last_os_error)?;
    let fraction = clock.utc % SECOND;
    let mut started_at_utc = format!(
        "{:04}-{:02}-{:02}T{:02}:{:02}:{:02}",
        time.wYear, time.wMonth, time.wDay, time.wHour, time.wMinute, time.wSecond,
    );
    if fraction != 0 {
        started_at_utc.push('.');
        started_at_utc.push_str(format!("{fraction:07}").trim_end_matches('0'));
    }
    started_at_utc.push_str("+00:00");

    serde_json::to_string_pretty(&Document {
        schema_version: SCHEMA,
        started_at_utc,
        uptime_at_start: format_uptime(clock.uptime),
        run_id,
    })
    // The pinned .NET 10 indented writer uses Windows newlines.
    .map(|json| json.replace('\n', "\r\n").into_bytes())
    .map_err(io::Error::other)
}

pub(super) fn classify(clock: Clock, bytes: &[u8]) -> StartupKind {
    let Ok(prior) = serde_json::from_slice::<Prior>(bytes) else {
        return StartupKind::UnexpectedExitRecovery;
    };
    let boot = clock
        .utc
        .checked_sub(clock.uptime)
        .filter(|boot| (MIN..=MAX).contains(boot));
    if prior.schema == SCHEMA
        && prior.utc != MIN
        && prior.uptime >= 0
        && clock.uptime >= 0
        && clock.uptime < prior.uptime
        && boot.is_some_and(|boot| prior.utc < boot)
    {
        StartupKind::Normal
    } else {
        StartupKind::UnexpectedExitRecovery
    }
}

struct Prior {
    schema: i32,
    utc: i64,
    uptime: i64,
}

#[derive(Deserialize)]
#[serde(untagged)]
enum Schema {
    Number(i32),
    Text(String),
}

impl<'de> Deserialize<'de> for Prior {
    fn deserialize<D: Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        struct MarkerVisitor;
        impl<'de> Visitor<'de> for MarkerVisitor {
            type Value = Prior;
            fn expecting(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
                formatter.write_str("a service run marker document")
            }
            fn visit_map<M: MapAccess<'de>>(self, mut map: M) -> Result<Prior, M::Error> {
                use serde::de::Error;
                let (mut schema, mut utc, mut uptime) = (None, None, None);
                while let Some(key) = map.next_key::<String>()? {
                    if key.eq_ignore_ascii_case("schemaVersion") {
                        schema = Some(match map.next_value()? {
                            Schema::Number(number) => number,
                            Schema::Text(text) => text.parse().map_err(Error::custom)?,
                        });
                    } else if key.eq_ignore_ascii_case("startedAtUtc") {
                        utc = Some(
                            parse_utc(&map.next_value::<String>()?)
                                .ok_or_else(|| Error::custom("invalid marker timestamp"))?,
                        );
                    } else if key.eq_ignore_ascii_case("uptimeAtStart") {
                        uptime = Some(
                            parse_uptime(&map.next_value::<String>()?)
                                .ok_or_else(|| Error::custom("invalid marker uptime"))?,
                        );
                    } else if key.eq_ignore_ascii_case("runId") {
                        if map
                            .next_value::<Option<String>>()?
                            .is_some_and(|id| !is_guid(&id))
                        {
                            return Err(Error::custom("invalid marker runId"));
                        }
                    } else {
                        map.next_value::<IgnoredAny>()?;
                    }
                }
                Ok(Prior {
                    schema: schema.ok_or_else(|| Error::missing_field("schemaVersion"))?,
                    utc: utc.ok_or_else(|| Error::missing_field("startedAtUtc"))?,
                    uptime: uptime.ok_or_else(|| Error::missing_field("uptimeAtStart"))?,
                })
            }
        }
        deserializer.deserialize_map(MarkerVisitor)
    }
}

fn is_guid(id: &str) -> bool {
    id.len() == 36
        && id.bytes().enumerate().all(|(index, byte)| {
            if matches!(index, 8 | 13 | 18 | 23) {
                byte == b'-'
            } else {
                byte.is_ascii_hexdigit()
            }
        })
}

fn ticks(ft: FILETIME) -> i64 {
    (i64::from(ft.dwHighDateTime) << 32) | i64::from(ft.dwLowDateTime)
}

fn digits(text: &str) -> Option<u16> {
    if text.is_empty() || !text.bytes().all(|byte| byte.is_ascii_digit()) {
        return None;
    }
    text.parse().ok()
}

fn parse_utc(text: &str) -> Option<i64> {
    let (date, time) = text.split_once('T').unwrap_or((text, "00:00"));
    let (year, rest) = date.split_once('-')?;
    let (month, day) = rest.split_once('-')?;
    if year.len() != 4 || month.len() != 2 || day.len() != 2 || year == "0000" {
        return None;
    }
    let zone_start = time.find(['Z', '+', '-']).unwrap_or(time.len());
    let (time, zone) = time.split_at(zone_start);
    let (time, fraction) = if let Some((time, fraction)) = time.split_once('.') {
        if time.len() != 8 {
            return None;
        }
        (time, fraction_ticks(fraction, 16)?)
    } else {
        (time, 0)
    };
    if !matches!(time.len(), 5 | 8)
        || time.get(2..3)? != ":"
        || (time.len() == 8 && time.get(5..6)? != ":")
    {
        return None;
    }
    let system_time = SYSTEMTIME {
        wYear: digits(year)?,
        wMonth: digits(month)?,
        wDay: digits(day)?,
        wHour: digits(time.get(..2)?)?,
        wMinute: digits(time.get(3..5)?)?,
        wSecond: time.get(6..).map_or(Some(0), digits)?,
        ..SYSTEMTIME::default()
    };
    let local_ticks = calendar_ticks(system_time)? + fraction;
    let utc = if zone.is_empty() {
        local_to_utc(system_time.wYear, local_ticks)?
    } else {
        local_ticks - explicit_offset(zone)? * SECOND
    };
    (MIN..=MAX).contains(&utc).then_some(utc)
}

fn explicit_offset(zone: &str) -> Option<i64> {
    if zone == "Z" {
        return Some(0);
    }
    let (sign, offset) = zone.split_at_checked(1)?;
    if !matches!(sign, "+" | "-") {
        return None;
    }
    let (hours, minutes) = if offset.len() == 2 {
        (offset, "00")
    } else {
        offset.split_once(':')?
    };
    if hours.len() != 2 || minutes.len() != 2 {
        return None;
    }
    let (hours, minutes) = (digits(hours)?, digits(minutes)?);
    if hours > 14 || minutes > 59 || (hours == 14 && minutes != 0) {
        return None;
    }
    let seconds = i64::from(hours) * 3600 + i64::from(minutes) * 60;
    Some(if sign == "-" { -seconds } else { seconds })
}

fn calendar_ticks(mut system_time: SYSTEMTIME) -> Option<i64> {
    system_time.wYear += YEAR_SHIFT;
    let mut ft = FILETIME::default();
    // SAFETY: Both pointers refer to live values of the required types.
    if unsafe { SystemTimeToFileTime(&system_time, &mut ft) } == 0 {
        return None;
    }
    Some(ticks(ft))
}

fn calendar(ticks: i64) -> Option<SYSTEMTIME> {
    let ft = FILETIME {
        dwLowDateTime: ticks as u32,
        dwHighDateTime: (ticks >> 32) as u32,
    };
    let mut time = SYSTEMTIME::default();
    // SAFETY: Both pointers refer to live values of the required types.
    if unsafe { FileTimeToSystemTime(&ft, &mut time) } == 0 {
        return None;
    }
    time.wYear -= YEAR_SHIFT;
    Some(time)
}

// System.Text.Json accepts offsetless dates as local time. DateTimeOffset
// uses standard time both for repeated wall times and for gaps.
fn local_to_utc(year: u16, local: i64) -> Option<i64> {
    let mut zone = DYNAMIC_TIME_ZONE_INFORMATION::default();
    let mut rules = TIME_ZONE_INFORMATION::default();
    // SAFETY: All pointers refer to live values of the required types.
    if unsafe { GetDynamicTimeZoneInformation(&mut zone) } == TIME_ZONE_ID_INVALID
        || unsafe { GetTimeZoneInformationForYear(year, &zone, &mut rules) } == 0
    {
        return None;
    }
    let base = local + i64::from(rules.Bias) * 60 * SECOND;
    let standard = base + i64::from(rules.StandardBias) * 60 * SECOND;
    if zone.DynamicDaylightTimeDisabled || rules.DaylightDate.wMonth == 0 {
        return Some(standard);
    }
    let daylight = base + i64::from(rules.DaylightBias) * 60 * SECOND;
    let local_millis = local - local % 10_000;
    let round_trips = |candidate: i64| {
        let Some(candidate) = calendar(candidate) else {
            return false;
        };
        let mut observed = SYSTEMTIME::default();
        // SAFETY: All pointers refer to live values of the required types.
        (unsafe { SystemTimeToTzSpecificLocalTimeEx(&zone, &candidate, &mut observed) }) != 0
            && calendar_ticks(observed) == Some(local_millis)
    };
    Some(if !round_trips(standard) && round_trips(daylight) {
        daylight
    } else {
        standard
    })
}

fn fraction_ticks(text: &str, limit: usize) -> Option<i64> {
    if text.is_empty() || text.len() > limit || !text.bytes().all(|byte| byte.is_ascii_digit()) {
        return None;
    }
    let width = text.len().min(7);
    Some(text[..width].parse::<i64>().ok()? * 10_i64.pow((7 - width) as u32))
}

fn parse_uptime(text: &str) -> Option<i64> {
    let (negative, text) = text
        .strip_prefix('-')
        .map_or((false, text), |text| (true, text));
    let Some((first, time)) = text.split_once(':') else {
        let days = parse_days(text)?.checked_mul(DAY)?;
        return Some(if negative { -days } else { days });
    };
    let (days, hours) = match first.split_once('.') {
        Some((days, hours)) => (parse_days(days)?, digits(hours)?),
        None => (0, digits(first)?),
    };
    let (minutes, seconds) = time.split_once(':').unwrap_or((time, "0"));
    let minutes = digits(minutes)?;
    let (seconds, fraction) = match seconds.split_once('.') {
        Some((seconds, fraction)) => (digits(seconds)?, fraction_ticks(fraction, 7)?),
        None => (digits(seconds)?, 0),
    };
    if hours >= 24 || minutes >= 60 || seconds >= 60 {
        return None;
    }
    let time_ticks = (i64::from(hours) * 3600 + i64::from(minutes) * 60 + i64::from(seconds))
        * SECOND
        + fraction;
    let days = days.checked_mul(DAY)?;
    if negative {
        (-days).checked_sub(time_ticks)
    } else {
        days.checked_add(time_ticks)
    }
}

fn parse_days(text: &str) -> Option<i64> {
    if text.is_empty() || !text.bytes().all(|byte| byte.is_ascii_digit()) {
        return None;
    }
    text.parse().ok()
}

fn format_uptime(ticks: i64) -> String {
    let unsigned = ticks.unsigned_abs();
    let seconds = unsigned / SECOND as u64;
    let days = seconds / 86_400;
    let mut result = String::new();
    if ticks < 0 {
        result.push('-');
    }
    if days != 0 {
        result.push_str(&format!("{days}."));
    }
    result.push_str(&format!(
        "{:02}:{:02}:{:02}",
        seconds / 3600 % 24,
        seconds / 60 % 60,
        seconds % 60
    ));
    let fraction = unsigned % SECOND as u64;
    if fraction != 0 {
        result.push_str(&format!(".{fraction:07}"));
    }
    result
}
