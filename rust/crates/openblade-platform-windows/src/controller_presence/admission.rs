//! Opt-in, read-only controller evidence for development admission checks.

use std::time::{Duration, Instant};

use super::{Observation, read_native};

// A slow inventory can straddle a controller change. Reject it rather than
// treating the two inventory results as current at the submission boundary.
const MAX_OBSERVATION_TIME: Duration = Duration::from_secs(1);

pub struct Sample {
    started: Instant,
    completed: Instant,
    observation: Observation,
}

impl Sample {
    /// The reader must collect fresh inputs synchronously within this call.
    pub fn capture(read: impl FnOnce() -> Observation) -> Self {
        let started = Instant::now();
        let observation = read();
        Self {
            started,
            completed: Instant::now(),
            observation,
        }
    }

    fn confirms_absence_within(&self, requested: Instant, returned: Instant) -> bool {
        requested <= self.started
            && self.started <= self.completed
            && self.completed <= returned
            && returned
                .checked_duration_since(requested)
                .is_some_and(|duration| duration <= MAX_OBSERVATION_TIME)
            && self.observation.confirms_absence()
    }
}

/// Samples controller presence on demand. A clear result is only one input
/// to a caller's independent identity, session, power and ownership checks.
pub struct ReadOnlyAdmission {
    read: Box<dyn FnMut() -> Sample + Send>,
}

impl ReadOnlyAdmission {
    /// Opt in to the native process and query-only SCM inputs. No inventory is
    /// read until a caller explicitly requests a check.
    pub fn native() -> Self {
        Self::from_source(|| Sample::capture(read_native))
    }

    pub fn from_source(read: impl FnMut() -> Sample + Send + 'static) -> Self {
        Self {
            read: Box::new(read),
        }
    }

    pub fn confirms_fresh_absence(&mut self) -> bool {
        let requested = Instant::now();
        let sample = (self.read)();
        sample.confirms_absence_within(requested, Instant::now())
    }
}

#[cfg(test)]
mod tests;
