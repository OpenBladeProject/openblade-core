//! Sticky loaded-origin outcome shared by ingress and publication checks.
use std::{
    io,
    sync::atomic::{AtomicU8, Ordering},
};
const LIVE: u8 = 0;
const STOP: u8 = 1;
const LOST: u8 = 2;
#[derive(Default)]
pub(crate) struct Lifetime(AtomicU8);
impl Lifetime {
    pub(crate) fn stop(&self) {
        let _ = self
            .0
            .compare_exchange(LIVE, STOP, Ordering::AcqRel, Ordering::Acquire);
    }
    pub(crate) fn lose_trust(&self) {
        self.0.store(LOST, Ordering::Release);
    }
    pub(crate) fn verify(&self) -> io::Result<()> {
        match self.0.load(Ordering::Acquire) {
            LIVE => Ok(()),
            STOP => Err(io::Error::new(io::ErrorKind::Interrupted, ExpectedStop)),
            _ => Err(io::Error::new(
                io::ErrorKind::PermissionDenied,
                "loaded origin trust was lost",
            )),
        }
    }
}
#[derive(Debug)]
struct ExpectedStop;
impl std::fmt::Display for ExpectedStop {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str("loaded origin revoked for expected STOP")
    }
}
impl std::error::Error for ExpectedStop {}
pub(crate) fn expected_stop(error: &io::Error) -> bool {
    error
        .get_ref()
        .is_some_and(|cause| cause.is::<ExpectedStop>())
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn trust_loss_is_sticky_and_generic_errors_are_not_expected_stop() {
        let state = Lifetime::default();
        state.lose_trust();
        state.stop();
        assert!(!expected_stop(&state.verify().unwrap_err()));
        let state = Lifetime::default();
        state.stop();
        assert!(expected_stop(&state.verify().unwrap_err()));
        state.lose_trust();
        state.stop();
        assert!(!expected_stop(&state.verify().unwrap_err()));
        assert!(!expected_stop(&io::Error::new(
            io::ErrorKind::Interrupted,
            "stop"
        )));
        assert!(!expected_stop(&io::Error::from(
            io::ErrorKind::PermissionDenied
        )));
    }
}
