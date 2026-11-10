//! Fixed observations for the private Windows Session GPU fixture.

use super::*;
use std::{thread, time::Instant};

const ADAPTER: NvidiaAdapterLuid = NvidiaAdapterLuid {
    high: -1,
    low: 0x1234,
};
const USER: &[u8] = b"fixture-user";
const SESSION: u32 = 5;

#[derive(Clone, Copy)]
pub enum Scenario {
    Available,
    Empty,
    NoData,
    Large,
    Stalled,
}

pub fn sample(
    scenario: Scenario,
    cancelled: &AtomicBool,
) -> Result<GpuAppInventory, QueryUnavailable> {
    scan(
        &mut Script {
            scenario,
            sample: 0,
        },
        cancelled,
    )
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
            Scenario::Available if self.sample > 1 => 41..=45,
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
                41 => "active-game".into(),
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

    fn pause(&mut self, _: Duration, cancelled: &AtomicBool) {
        if matches!(self.scenario, Scenario::Stalled) {
            let until = Instant::now() + Duration::from_secs(10);
            while !cancelled.load(AtomicOrdering::Relaxed) && Instant::now() < until {
                thread::sleep(Duration::from_millis(10));
            }
        }
    }

    fn compare_names(&self, left: &str, right: &str) -> Ordering {
        // Fixture names use ASCII case or the same uncased Unicode character.
        left.to_ascii_uppercase().cmp(&right.to_ascii_uppercase())
    }
}
