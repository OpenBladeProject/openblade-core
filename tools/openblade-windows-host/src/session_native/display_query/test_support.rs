//! Test doubles shared by the display write tests: an in-memory managed
//! checkpoint and, for the manual HDR, refresh and color writes, the
//! brightness owner and recovery state, power, fence and reply query they run
//! against. No Windows file, WMI, CCD or WCS call is used.
use super::recovery::CheckpointFile;
use std::{
    io,
    sync::{
        Arc, Mutex,
        atomic::{AtomicUsize, Ordering},
    },
};

/// Shared bytes survive a simulated Agent restart; writes can be refused.
#[derive(Clone)]
pub(super) struct Memory {
    pub(super) bytes: Arc<Mutex<Option<Vec<u8>>>>,
    /// Successful writes beyond this count fail (persistence fault).
    pub(super) allow: Arc<AtomicUsize>,
    pub(super) writes: Arc<AtomicUsize>,
}

impl Default for Memory {
    fn default() -> Self {
        Self {
            bytes: Arc::default(),
            allow: Arc::new(AtomicUsize::new(usize::MAX)),
            writes: Arc::default(),
        }
    }
}

impl CheckpointFile for Memory {
    fn read(&mut self) -> io::Result<Option<Vec<u8>>> {
        Ok(self.bytes.lock().unwrap().clone())
    }

    fn replace(&mut self, bytes: &[u8]) -> io::Result<()> {
        if self.writes.load(Ordering::SeqCst) >= self.allow.load(Ordering::SeqCst) {
            return Err(io::Error::other("injected checkpoint failure"));
        }
        self.writes.fetch_add(1, Ordering::SeqCst);
        *self.bytes.lock().unwrap() = Some(bytes.to_vec());
        Ok(())
    }
}

impl Memory {
    pub(super) fn with(bytes: &str) -> Self {
        let file = Self::default();
        *file.bytes.lock().unwrap() = Some(bytes.into());
        file
    }

    pub(super) fn record(&self) -> serde_json::Value {
        serde_json::from_slice(self.bytes.lock().unwrap().as_deref().unwrap()).unwrap()
    }

    /// A prior managed legacy checkpoint supplies the last reconciled source.
    pub(super) fn legacy(source: u8) -> Self {
        Self::with(&format!(
            r#"{{"SchemaVersion":1,"LastReconciledPowerSource":{source}}}"#
        ))
    }
}
#[cfg(openblade_display_manual_write)]
pub(super) use manual::{FencedTopology, Host, Rig, TestPanel, flag, result};

#[cfg(openblade_display_manual_write)]
mod manual {
    use super::Memory;
    use crate::session_native::{
        display_query::{
            manual_write::{self, ManualHost, ManualRequest, ManualWriteOutcome},
            observation::{BrightnessOwner, SharedBrightness, Snapshot},
            recovery::{Fields, Recovery, Status},
            write::explicit_read,
        },
        power_admission::PowerContext,
    };
    use openblade_platform_windows::display::{
        DisplayReadError, InternalDisplayTopology, SubmissionFence,
    };
    use std::{
        cell::Cell,
        io,
        sync::{
            Arc, Mutex,
            atomic::{AtomicBool, AtomicUsize, Ordering},
        },
    };

    /// A fresh fenced topology read, captured from the scripted panel when
    /// the fence is created (nothing is submitted before it is read).
    pub(in super::super) type FencedTopology =
        Box<dyn Fn() -> Result<InternalDisplayTopology, DisplayReadError> + Send + Sync>;

    /// A scripted CCD panel and the request type it serves.
    pub(in super::super) trait TestPanel: Sized {
        type Value;
        type Request: ManualRequest<Host<Self>>;
        fn request(expected_power_source: u8, value: Self::Value) -> Self::Request;
        /// The fence's fresh topology for the panel's current state.
        fn fenced(&self) -> FencedTopology;
        /// The reply query's snapshot for the panel's current state.
        fn reply(&self) -> Snapshot;
    }

    /// Injected power, fence and reply query around one scripted panel.
    pub(in super::super) struct Host<P> {
        pub(in super::super) panel: P,
        pub(in super::super) power: Option<PowerContext>,
        /// Power reported by every capture after the first (post-success drift).
        pub(in super::super) later: Option<Option<PowerContext>>,
        pub(in super::super) captures: Cell<usize>,
        /// The fence's fresh topology shows no internal display.
        pub(in super::super) changed: bool,
        pub(in super::super) admit: bool,
        pub(in super::super) fence_checks: Arc<AtomicUsize>,
        pub(in super::super) queries: usize,
        /// The reply query fails fatally.
        pub(in super::super) fail_query: bool,
    }

    impl<P> Host<P> {
        pub(in super::super) fn admitted(source: u8, panel: P) -> Self {
            Self {
                panel,
                power: Some(PowerContext {
                    source,
                    generation: 7,
                }),
                later: None,
                captures: Cell::new(0),
                changed: false,
                admit: true,
                fence_checks: Arc::default(),
                queries: 0,
                fail_query: false,
            }
        }
    }

    impl<P: TestPanel> ManualHost for Host<P> {
        type Native = P;

        fn power(&self) -> Option<PowerContext> {
            let capture = self.captures.get();
            self.captures.set(capture + 1);
            match self.later {
                Some(later) if capture > 0 => later,
                _ => self.power,
            }
        }

        fn fence(&self, admitted: PowerContext) -> Arc<dyn SubmissionFence> {
            assert_eq!(
                Some(admitted),
                self.power,
                "fence binds the captured context"
            );
            Arc::new(TestFence {
                topology: self.panel.fenced(),
                changed: self.changed,
                admit: self.admit,
                checks: self.fence_checks.clone(),
            })
        }

        fn native(&mut self) -> &mut P {
            &mut self.panel
        }

        fn query(&mut self, _: &SharedBrightness, canceled: &AtomicBool) -> io::Result<Snapshot> {
            self.queries += 1;
            if self.fail_query {
                return Err(io::Error::other("injected reply query failure"));
            }
            if canceled.load(Ordering::SeqCst) {
                return Err(io::ErrorKind::Interrupted.into());
            }
            Ok(self.panel.reply())
        }
    }

    struct TestFence {
        topology: FencedTopology,
        changed: bool,
        admit: bool,
        checks: Arc<AtomicUsize>,
    }

    impl SubmissionFence for TestFence {
        fn topology(&self) -> Result<InternalDisplayTopology, DisplayReadError> {
            self.checks.fetch_add(1, Ordering::SeqCst);
            if self.changed {
                return Ok(InternalDisplayTopology::NoInternalDisplay);
            }
            (self.topology)()
        }

        fn admit(&self) -> bool {
            self.admit
        }
    }

    /// The durable recovery state and an inert brightness owner for one CCD
    /// write test; a new `Rig` over the same `Memory` is an Agent restart.
    pub(in super::super) struct Rig {
        pub(in super::super) owner: SharedBrightness,
        pub(in super::super) file: Memory,
        pub(in super::super) recovery: Mutex<Recovery<Memory>>,
    }

    impl Rig {
        pub(in super::super) fn new(file: Memory) -> Self {
            Self {
                owner: Arc::new(Mutex::new(BrightnessOwner::inert())),
                recovery: Mutex::new(Recovery::load(file.clone())),
                file,
            }
        }

        pub(in super::super) fn unresolved(&self) -> Fields {
            self.recovery.lock().unwrap().unresolved()
        }

        /// A completed explicit query through the production resume path.
        pub(in super::super) fn explicit_read(&self, state: &Snapshot) {
            explicit_read(&self.owner, &self.recovery, state).unwrap();
        }

        /// The complete manual request through the shared manual-write frame.
        pub(in super::super) fn set<P: TestPanel>(
            &self,
            host: &mut Host<P>,
            expected: u8,
            value: P::Value,
        ) -> io::Result<ManualWriteOutcome> {
            self.set_canceled(host, expected, value, &AtomicBool::new(false))
        }

        pub(in super::super) fn set_canceled<P: TestPanel>(
            &self,
            host: &mut Host<P>,
            expected: u8,
            value: P::Value,
            canceled: &AtomicBool,
        ) -> io::Result<ManualWriteOutcome> {
            let request = P::request(expected, value);
            manual_write::run(host, &self.owner, &self.recovery, &request, canceled)
        }
    }

    pub(in super::super) fn result(outcome: &ManualWriteOutcome) -> (Status, Option<&str>) {
        (outcome.status, outcome.error.as_deref())
    }

    pub(in super::super) fn flag(record: &serde_json::Value, name: &str) -> bool {
        record[name].as_bool().unwrap()
    }
}
