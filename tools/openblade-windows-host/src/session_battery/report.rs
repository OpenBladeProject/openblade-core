use super::timestamp::{DAY, Timestamp};
use super::xml::{self, Element};
use serde::Serialize;

const MAX_INTERVALS: usize = 512;
const MAX_CAPACITY: i64 = 1_000_000_000;

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub(super) struct Report {
    source: u8,
    generated_at_utc: Timestamp,
    window_start_utc: Timestamp,
    window_end_utc: Timestamp,
    status: Status,
    intervals: Vec<Interval>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Status {
    design_capacity_milliwatt_hours: Option<i64>,
    full_charge_capacity_milliwatt_hours: Option<i64>,
    health_percent: Option<f64>,
    cycle_count: Option<i64>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Interval {
    start_utc: Timestamp,
    end_utc: Timestamp,
    power_source: u8,
    activity: u8,
    start_charge_milliwatt_hours: Option<i64>,
    full_charge_capacity_milliwatt_hours: Option<i64>,
    discharge_milliwatt_hours: Option<i64>,
    start_charge_percent: Option<f64>,
}

impl Interval {
    fn gap(start_utc: Timestamp, end_utc: Timestamp) -> Self {
        Self {
            start_utc,
            end_utc,
            power_source: 0,
            activity: 0,
            start_charge_milliwatt_hours: None,
            full_charge_capacity_milliwatt_hours: None,
            discharge_milliwatt_hours: None,
            start_charge_percent: None,
        }
    }
}

impl Report {
    pub(super) fn valid_on_wire(&self) -> bool {
        // The managed parser computes the raw ratio. The codec separately
        // rejects a report whose ratio exceeds the wire range.
        self.status
            .health_percent
            .into_iter()
            .chain(
                self.intervals
                    .iter()
                    .filter_map(|entry| entry.start_charge_percent),
            )
            .all(|value| (0.0..=1000.0).contains(&value))
    }
}

pub(super) fn parse(bytes: &[u8]) -> Option<Report> {
    let root = xml::read(bytes)?;
    let info = root.child("ReportInformation")?;
    let generated = Timestamp::parse(&info.child("ScanTime")?.text)?;
    let mut earliest = generated.checked_add(-DAY)?;
    if let Some(start) = info
        .child("ReportStartTime")
        .and_then(|start| Timestamp::parse(&start.text))
        && earliest < start
        && start <= generated
    {
        earliest = start;
    }

    let mut parsed: Vec<_> = root
        .child("RecentUsage")
        .into_iter()
        .flat_map(|usage| &usage.children)
        .filter_map(|entry| read_interval(entry, earliest, generated))
        .collect();
    parsed.sort_by_key(|interval| interval.start_utc);

    let mut intervals = Vec::new();
    let mut previous_end = None;
    for mut interval in parsed {
        if let Some(end) = previous_end {
            if interval.start_utc > end {
                intervals.push(Interval::gap(end, interval.start_utc));
            }
            interval.start_utc = interval.start_utc.max(end);
        }
        if interval.start_utc < interval.end_utc {
            previous_end = Some(interval.end_utc);
            intervals.push(interval);
            if intervals.len() > MAX_INTERVALS {
                return None;
            }
        }
    }
    Some(Report {
        source: 0, // WindowsPowerCfg
        generated_at_utc: generated,
        window_start_utc: earliest,
        window_end_utc: generated,
        status: read_status(&root),
        intervals,
    })
}

fn read_status(root: &Element) -> Status {
    let estimates = root.child("RuntimeEstimates");
    let batteries = root
        .child("Batteries")
        .map_or(&[][..], |batteries| batteries.children.as_slice());
    let total = |name| {
        if batteries.is_empty() || batteries.len() > 16 {
            return None;
        }
        batteries
            .iter()
            .try_fold(0, |sum, battery| {
                Some(sum + capacity(&battery.child(name)?.text)?)
            })
            .filter(|&sum| sum <= MAX_CAPACITY)
    };
    let read = |name| {
        estimates
            .and_then(|estimates| estimates.child(name))
            .and_then(|estimate| capacity(&estimate.child("Capacity")?.text))
            .or_else(|| total(name))
    };
    let design = read("DesignCapacity");
    let full_charge = read("FullChargeCapacity");
    let cycle_count = match batteries {
        [battery] => battery
            .child("CycleCount")
            .and_then(|count| unsigned(&count.text))
            .filter(|&count| count <= 10_000_000),
        _ => None,
    };
    Status {
        design_capacity_milliwatt_hours: design,
        full_charge_capacity_milliwatt_hours: full_charge,
        health_percent: percent(full_charge, design),
        cycle_count,
    }
}

fn read_interval(entry: &Element, earliest: Timestamp, generated: Timestamp) -> Option<Interval> {
    let kind = entry.attr("EntryType").unwrap_or_default();
    if kind.eq_ignore_ascii_case("ReportGenerated") {
        return None;
    }
    let start = Timestamp::parse(entry.attr("Timestamp")?)?;
    let duration = unsigned(entry.attr("Duration")?)?;
    if !(1..=DAY).contains(&duration) {
        return None;
    }
    let end = start.checked_add(duration)?.min(generated);
    let start = start.max(earliest);
    if start >= end {
        return None;
    }
    let charge = entry.attr("ChargeCapacity").and_then(capacity);
    let full_charge = entry.attr("FullChargeCapacity").and_then(capacity);
    let discharge = entry
        .attr("Discharge")
        .and_then(|value| value.parse::<i64>().ok())
        .filter(|value| (-MAX_CAPACITY..=MAX_CAPACITY).contains(value));
    let power_source = match entry.attr("Ac") {
        Some("0") => 1, // Battery
        Some("1") => 2, // Ac
        _ => 0,
    };
    let kind = kind.trim();
    let activity = if kind.eq_ignore_ascii_case("Active") {
        1
    } else if kind.eq_ignore_ascii_case("ConnectedStandby") {
        2
    } else if kind.eq_ignore_ascii_case("Suspended") {
        3
    } else {
        0
    };
    Some(Interval {
        start_utc: start,
        end_utc: end,
        power_source,
        activity,
        start_charge_milliwatt_hours: charge,
        full_charge_capacity_milliwatt_hours: full_charge,
        discharge_milliwatt_hours: discharge,
        start_charge_percent: percent(charge, full_charge),
    })
}

fn unsigned(value: &str) -> Option<i64> {
    value
        .bytes()
        .all(|byte| byte.is_ascii_digit())
        .then(|| value.parse().ok())
        .flatten()
}

fn capacity(value: &str) -> Option<i64> {
    unsigned(value).filter(|&value| value <= MAX_CAPACITY)
}

fn percent(numerator: Option<i64>, denominator: Option<i64>) -> Option<f64> {
    let denominator = denominator.filter(|&value| value > 0)?;
    Some(100.0 * numerator? as f64 / denominator as f64)
}
