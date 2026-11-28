//! A scripted NVIDIA driver for the clock-offset backend: the management
//! machine, the worker and the thermal integration's tests drive it. No test
//! loads nvml.dll.
use super::{ADMITTED_NAME, ClockKind, ClockOffset, NAME_CAPACITY, Nvml, SUCCESS};
use std::sync::{Arc, Mutex, mpsc};

#[derive(Default)]
pub(in super::super) struct Driver {
    /// NVML units: memory doubled.
    pub(in super::super) core: (i32, i32, i32),
    pub(in super::super) memory: (i32, i32, i32),
    pub(in super::super) queries_fail: bool,
    pub(in super::super) sets_fail: bool,
    /// How many of the failing sets still store their value before failing.
    pub(in super::super) storing_failures: usize,
    /// The readback reports this core instead (a driver that clamps).
    pub(in super::super) clamp_core: Option<i32>,
    pub(in super::super) inits: usize,
    pub(in super::super) shutdowns: usize,
    pub(in super::super) sets: Vec<(i32, i32)>,
    /// Blocks the next set until released.
    pub(in super::super) hold: Option<mpsc::Receiver<()>>,
    /// The next set stores its value and then panics, as a bug would.
    pub(in super::super) panic_on_set: bool,
    /// Every set makes the driver unreadable from then on: a GPU lost during
    /// the submission.
    pub(in super::super) lose_on_set: bool,
}

#[derive(Clone)]
pub(in super::super) struct Fake(pub(in super::super) Arc<Mutex<Driver>>);

impl Fake {
    pub(in super::super) fn new(core: i32, memory: i32) -> Self {
        Self(Arc::new(Mutex::new(Driver {
            core: (-1000, 1000, core),
            memory: (-2000, 6000, memory * 2),
            ..Driver::default()
        })))
    }

    pub(in super::super) fn with<R>(&self, f: impl FnOnce(&mut Driver) -> R) -> R {
        f(&mut self.0.lock().unwrap())
    }

    pub(in super::super) fn current(&self) -> (i32, i32) {
        self.with(|d| (d.core.2, d.memory.2 / 2))
    }

    pub(in super::super) fn inits(&self) -> usize {
        self.with(|d| d.inits)
    }

    pub(in super::super) fn sets(&self) -> usize {
        self.with(|d| d.sets.len())
    }
}

impl Nvml for Fake {
    fn init(&mut self) -> i32 {
        self.with(|d| d.inits += 1);
        SUCCESS
    }

    fn shutdown(&mut self) -> i32 {
        self.with(|d| d.shutdowns += 1);
        SUCCESS
    }

    fn device_count(&mut self) -> Result<u32, i32> {
        Ok(1)
    }

    fn device(&mut self, _: u32) -> Result<usize, i32> {
        Ok(1)
    }

    fn name(&mut self, _: usize, name: &mut [u8; NAME_CAPACITY]) -> i32 {
        name[..ADMITTED_NAME.len()].copy_from_slice(ADMITTED_NAME.as_bytes());
        SUCCESS
    }

    fn clock_offsets(&mut self, _: usize, info: &mut ClockOffset) -> i32 {
        self.with(|d| {
            if d.queries_fail {
                // NVML_ERROR_GPU_IS_LOST.
                return 15;
            }
            let (minimum, maximum, current) = if info.kind == ClockKind::Graphics as i32 {
                let core = d.core;
                (core.0, core.1, d.clamp_core.unwrap_or(core.2))
            } else {
                d.memory
            };
            (info.minimum, info.maximum, info.current) = (minimum, maximum, current);
            SUCCESS
        })
    }

    fn set_clock_offsets(&mut self, _: usize, info: &mut ClockOffset) -> i32 {
        if let Some(hold) = self.with(|d| d.hold.take()) {
            let _ = hold.recv();
        }
        let (status, panics) = self.with(|d| {
            d.sets.push((info.kind, info.current));
            if d.lose_on_set {
                d.queries_fail = true;
            }
            if d.sets_fail {
                if d.storing_failures == 0 {
                    return (3, false);
                }
                d.storing_failures -= 1;
            }
            if info.kind == ClockKind::Graphics as i32 {
                d.core.2 = info.current;
            } else {
                d.memory.2 = info.current;
            }
            let status = if d.sets_fail { 3 } else { SUCCESS };
            (status, std::mem::take(&mut d.panic_on_set))
        });
        // Outside the lock, so the driver stays readable.
        assert!(!panics, "a bug after the NVML write");
        status
    }
}
