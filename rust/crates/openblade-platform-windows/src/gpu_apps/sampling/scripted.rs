//! Fixed observations for the private Windows Session GPU fixture.

use super::*;
use std::{thread, time::Instant};

#[cfg(feature = "scripted-gpu-close")]
use crate::gpu_apps::action::fixture;
#[cfg(feature = "scripted-gpu-close")]
pub use crate::gpu_apps::action::{Kind, Report, Target};

const ADAPTER: NvidiaAdapterLuid = NvidiaAdapterLuid {
    high: -1,
    low: 0x1234,
};
pub(in crate::gpu_apps) const USER: &[u8] = b"fixture-user";
pub(in crate::gpu_apps) const SESSION: u32 = 5;

#[derive(Clone, Copy)]
pub enum Scenario {
    Available,
    #[cfg(feature = "scripted-gpu-close")]
    SimulatedClose,
    Empty,
    NoData,
    Large,
    Stalled,
}

pub fn sample(
    scenario: Scenario,
    cancelled: &AtomicBool,
) -> Result<GpuAppInventory, QueryUnavailable> {
    #[cfg(feature = "scripted-gpu-close")]
    let scenario = match scenario {
        Scenario::SimulatedClose => Scenario::Available,
        other => other,
    };
    scan(
        &mut Script {
            scenario,
            sample: 0,
        },
        cancelled,
    )
}

#[cfg(feature = "scripted-gpu-close")]
pub fn simulate_close(
    targets: &[Target],
    kind: Kind,
    cancelled: &AtomicBool,
) -> Result<(GpuAppInventory, Report), QueryUnavailable> {
    // Match against the complete fresh projection, even if the wire would
    // omit a lower-ranked app. Only the response is subject to the list cap.
    let inventory = scan_with_limit(
        &mut Script {
            scenario: Scenario::Available,
            sample: 0,
        },
        cancelled,
        usize::MAX,
    )?;
    let report = fixture::simulate(&inventory, targets, kind, cancelled);
    Ok((inventory, report))
}

struct Script {
    scenario: Scenario,
    sample: u32,
}

impl SamplingApi for Script {
    type Query = ();

    fn current_user(&mut self) -> Option<(Vec<u8>, u32)> {
        Some((USER.to_vec(), SESSION))
    }

    fn locate_adapter(&mut self) -> Result<NvidiaAdapterLuid, Unavailable> {
        Ok(ADAPTER)
    }

    fn open_query(&mut self) -> Result<(), Unavailable> {
        Ok(())
    }

    fn collect(&mut self, _: &mut ()) -> Result<i64, Unavailable> {
        self.sample += 1;
        if matches!(self.scenario, Scenario::NoData) {
            return Err(Unavailable::MissingData);
        }
        Ok(1000 + i64::from(self.sample))
    }

    fn counter_array(&mut self, _: &mut (), bytes: u32) -> Result<CounterArray, Unavailable> {
        let pids = match self.scenario {
            Scenario::Available if self.sample > 1 => 41..=46,
            Scenario::Large if self.sample > 1 => 1..=129,
            _ => return Ok(CounterArray::Size(0)),
        };
        if bytes == 0 {
            return Ok(CounterArray::Size(8192));
        }
        let adapter = ADAPTER.counter_token();
        Ok(CounterArray::Items(
            pids.map(|pid| EngineCounter {
                instance: format!("pid_{pid}_{adapter}_phys_0_eng_0_engtype_3D"),
                status: 0,
                utilization_percent: if pid == 42 && matches!(self.scenario, Scenario::Available) {
                    0.0
                } else {
                    7.5
                },
            })
            .collect(),
        ))
    }

    fn inspect(&mut self, pid: i32) -> Inspection {
        let name = match self.scenario {
            Scenario::Large => "界".repeat(260),
            _ => match pid {
                41 | 46 => "active-game".into(),
                42 => "idle-holder".into(),
                43 => "other-session".into(),
                44 => "reused-pid".into(),
                _ => return Inspection::Unavailable,
            },
        };
        Inspection::Identified(Identity {
            created_filetime: if pid == 44
                && self.sample > 2
                && matches!(self.scenario, Scenario::Available)
            {
                900
            } else {
                i64::from(pid)
            },
            user_sid: USER.to_vec(),
            session_id: if pid == 43 && matches!(self.scenario, Scenario::Available) {
                SESSION + 1
            } else {
                SESSION
            },
            name,
        })
    }

    fn read_parents(&mut self, _: &AtomicBool) -> BTreeMap<i32, i32> {
        match self.scenario {
            Scenario::Available => BTreeMap::from([(46, 41)]),
            _ => BTreeMap::new(),
        }
    }

    fn pause(&mut self, _: Duration, cancelled: &AtomicBool) {
        if matches!(self.scenario, Scenario::Stalled) {
            let until = Instant::now() + Duration::from_secs(10);
            while !cancelled.load(AtomicOrdering::Relaxed) && Instant::now() < until {
                thread::sleep(Duration::from_millis(10));
            }
        }
    }

    fn compare_names(&self, left: &str, right: &str) -> Ordering {
        compare_names(left, right)
    }
}

pub(in crate::gpu_apps) fn compare_names(left: &str, right: &str) -> Ordering {
    // Fixture names use ASCII case or the same uncased Unicode character.
    left.to_ascii_uppercase().cmp(&right.to_ascii_uppercase())
}
