//! Causal terminal diagnostics, carried with the original failure rather than
//! guessed from an outer I/O kind or a global last-error flag.
use openblade_platform_windows::agent_diagnostics::Event;
use std::{error::Error, fmt, io};

#[derive(Debug)]
struct Classified {
    event: Event,
    error: io::Error,
    attempted: bool,
    /// The first-instance pipe claim failed because another Agent owns it; the
    /// OS error that said so.
    conflict: Option<i32>,
}

impl fmt::Display for Classified {
    fn fmt(&self, out: &mut fmt::Formatter<'_>) -> fmt::Result {
        out.write_str("classified native Agent failure")
    }
}

impl Error for Classified {
    fn source(&self) -> Option<&(dyn Error + 'static)> {
        Some(&self.error)
    }
}

fn classified(error: &io::Error) -> Option<&Classified> {
    let mut current: &(dyn Error + 'static) = error;
    // Actual paths use at most two Service-authority I/O wrappers. Bound this
    // diagnostic walk; it never affects authority or retries.
    for _ in 0..16 {
        if let Some(cause) = current.downcast_ref::<Classified>() {
            return Some(cause);
        }
        if let Some(io) = current.downcast_ref::<io::Error>()
            && let Some(inner) = io.get_ref()
        {
            current = inner;
            continue;
        }
        current = current.source()?;
    }
    None
}

fn attach(error: io::Error, event: Event, attempted: bool) -> io::Error {
    attach_as(error, event, attempted, None)
}

fn attach_as(error: io::Error, event: Event, attempted: bool, conflict: Option<i32>) -> io::Error {
    if classified(&error).is_some() {
        return error;
    }
    io::Error::new(
        error.kind(),
        Classified {
            event,
            error,
            attempted,
            conflict,
        },
    )
}

pub(crate) fn origin(error: io::Error) -> io::Error {
    let event = Event::Origin(error.kind(), error.raw_os_error());
    attach(error, event, false)
}

/// Whether this failure was classified as an origin loss.
#[cfg_attr(test, allow(dead_code))]
pub(crate) fn is_origin(error: &io::Error) -> bool {
    matches!(
        classified(error),
        Some(Classified {
            event: Event::Origin(..),
            ..
        })
    )
}

// A first-instance claim that finds the pipe owned fails with access denied or
// pipe busy. Managed returns 0 quietly when another Agent owns its pipes, so
// that failure is tagged and the exit classification treats it as quiet.
#[cfg_attr(
    not(any(
        feature = "agent-battery-report-v1",
        feature = "agent-display-query-v1",
        feature = "agent-gpu-v1"
    )),
    allow(dead_code)
)]
pub(super) fn claim(error: io::Error) -> io::Error {
    const ERROR_ACCESS_DENIED: i32 = 5;
    const ERROR_PIPE_BUSY: i32 = 231;
    let conflict = error
        .raw_os_error()
        .filter(|code| matches!(*code, ERROR_ACCESS_DENIED | ERROR_PIPE_BUSY));
    let event = Event::Listener(error.kind(), error.raw_os_error());
    attach_as(error, event, false, conflict)
}

/// The OS error of a tagged claim conflict.
#[cfg_attr(test, allow(dead_code))]
pub(crate) fn claim_conflict(error: &io::Error) -> Option<i32> {
    classified(error).and_then(|cause| cause.conflict)
}

// Only the endpoints that serve a pipe claim a listener.
#[cfg_attr(
    not(any(
        feature = "agent-battery-report-v1",
        feature = "agent-display-query-v1"
    )),
    allow(dead_code)
)]
pub(super) fn listener(error: io::Error) -> io::Error {
    let event = Event::Listener(error.kind(), error.raw_os_error());
    attach(error, event, false)
}

#[cfg(feature = "agent-battery-report-v1")]
pub(super) fn attempted(error: io::Error, event: Event) -> io::Error {
    // This means delivery was attempted, not that persistence or stderr succeeded.
    attach(error, event, true)
}

pub(crate) fn terminal(error: &io::Error, emit: impl FnOnce(Event)) {
    if let Some(cause) = classified(error) {
        if !cause.attempted {
            emit(cause.event);
        }
    } else {
        emit(Event::AgentFailure(error.kind(), error.raw_os_error()));
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn only_an_origin_classified_failure_is_an_origin_loss() {
        assert!(is_origin(&origin(io::Error::from_raw_os_error(5))));
        assert!(!is_origin(&listener(io::Error::from_raw_os_error(5))));
        assert!(!is_origin(&io::Error::from_raw_os_error(5)));
    }

    #[test]
    fn unclassified_same_kind_failure_gets_neutral_agent_label() {
        let mut events = Vec::new();
        for message in [
            "wrong working directory",
            "origin listener status words are not evidence",
        ] {
            let error = io::Error::new(io::ErrorKind::PermissionDenied, message);
            terminal(&error, |event| events.push(event));
        }
        assert_eq!(
            events,
            [
                Event::AgentFailure(io::ErrorKind::PermissionDenied, None),
                Event::AgentFailure(io::ErrorKind::PermissionDenied, None),
            ]
        );
        let mut classified_events = Vec::new();
        for error in [
            origin(io::Error::from_raw_os_error(5)),
            listener(io::Error::from_raw_os_error(5)),
        ] {
            terminal(&error, |event| classified_events.push(event));
        }
        assert_eq!(
            classified_events,
            [
                Event::Origin(io::ErrorKind::PermissionDenied, Some(5)),
                Event::Listener(io::ErrorKind::PermissionDenied, Some(5)),
            ]
        );
    }
}
