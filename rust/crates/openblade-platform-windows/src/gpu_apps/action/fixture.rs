use super::*;
use crate::gpu_apps::sampling::scripted::{SESSION, USER, compare_names};

pub fn simulate(
    inventory: &GpuAppInventory,
    targets: &[Target],
    kind: Kind,
    cancelled: &AtomicBool,
) -> Report {
    decide(
        inventory,
        targets,
        kind,
        &CurrentUser {
            sid: USER,
            session_id: SESSION,
        },
        compare_names,
        cancelled,
        &mut Script,
    )
}

struct Script;

struct Retained {
    observation: Observation<'static>,
    has_window: bool,
}

impl Backend for Script {
    type Retained = Retained;

    fn retain(&mut self, pid: i32) -> Result<Option<Retained>, ()> {
        let (image_path, has_window) = match pid {
            41 => (r"C:\Fixture\active-game.exe", true),
            42 => (r"C:\Fixture\idle-holder.exe", false),
            _ => return Ok(None),
        };
        Ok(Some(Retained {
            observation: Observation {
                process_id: pid,
                start_time_utc_ticks: 504_911_232_000_000_000 + i64::from(pid),
                user_sid: USER,
                session_id: SESSION,
                image_path,
                exited: false,
                // These two owning apps are part of the fixed script, not
                // inferred from the request or the displayed row.
                owning_process_id: Some(pid),
            },
            has_window,
        }))
    }
}

impl RetainedTarget for Retained {
    fn observe(&self) -> Observation<'_> {
        self.observation
    }

    fn submit(&mut self, kind: Kind) -> Submission {
        if kind == Kind::Close && !self.has_window {
            Submission::NoWindow
        } else {
            Submission::Accepted
        }
    }
}
