//! The installed reader over the real ReadSource, Worker, Reader and
//! retirement, with a fake keyboard behind the Port, for the Driver-mode
//! tests with and without `service-keyboard-driver-mode-read-v1`.
// Without the read feature only the absent-feature pin uses the rig.
#![cfg_attr(
    not(feature = "service-keyboard-driver-mode-read-v1"),
    allow(dead_code)
)]
use super::*;
use crate::discovery::performance::device_mode_tests::{KeyboardIo, keyboard_io_worker};
use serde_json::Value;
use std::{sync::atomic::AtomicBool, time::Duration};

pub(super) const DRIVER: (u8, [u8; 2]) = (2, [3, 0]);
pub(super) const NORMAL: (u8, [u8; 2]) = (2, [0, 0]);
// A correlated FAILED status: a terminal reply that confirms no mode.
pub(super) const FAILED: (u8, [u8; 2]) = (3, [3, 0]);

pub(super) struct Custody {
    pub(super) live: AtomicBool,
    pub(super) generation: AtomicU8,
}
impl ReadCustody for Custody {
    fn authorize(&self) -> Option<ReadAdmission> {
        self.live.load(Ordering::Acquire).then(|| ReadAdmission {
            lifecycle: 1,
            generation: [self.generation.load(Ordering::Acquire); 32],
        })
    }
    fn revoke(&self) {}
    fn retire(&self) -> io::Result<()> {
        Ok(())
    }
    fn confirm_drained(&self) -> io::Result<()> {
        Ok(())
    }
}

pub(super) fn status(access: i32) -> Snapshot {
    Snapshot::observed_read_only(0, access, false, false, vec![], None, "Awaiting.".into())
}

pub(super) struct Rig {
    pub(super) io: Arc<KeyboardIo>,
    pub(super) custody: Arc<Custody>,
    pub(super) source: ReadSource,
    pub(super) publisher: watch::Sender<Arc<Publication>>,
}

impl Rig {
    pub(super) fn new() -> Self {
        let io = KeyboardIo::new(None);
        let custody = Arc::new(Custody {
            live: AtomicBool::new(true),
            generation: AtomicU8::new(7),
        });
        let device = io.clone();
        let source = ReadSource::compose(custody.clone(), move |lease| {
            keyboard_io_worker(device, move || lease.authorize())
        });
        let publisher = watch::channel(Arc::new(Publication {
            active: status(0),
            inactive: status(1),
        }))
        .0;
        Self {
            io,
            custody,
            source,
            publisher,
        }
    }

    pub(super) fn reply(&self, (status, payload): (u8, [u8; 2])) {
        *self.io.mode.lock().unwrap() = Some((status, payload.to_vec()));
    }

    /// A fresh observation with a two-second deadline, or None when the
    /// reader admitted no read.
    pub(super) async fn observe(&self) -> Option<Value> {
        self.observe_within(Duration::from_secs(2)).await
    }

    pub(super) async fn observe_within(&self, budget: Duration) -> Option<Value> {
        let observed = self.snapshot_within(budget).await?;
        Some(serde_json::to_value(observed).unwrap())
    }

    pub(super) async fn snapshot_within(&self, budget: Duration) -> Option<Snapshot> {
        self.source
            .observe(status(0), Instant::now() + budget, self.publisher.clone())
            .await
            .unwrap()
    }

    /// Both caller projections of the subscription feed.
    pub(super) fn published(&self) -> [Value; 2] {
        let publication = self.publisher.borrow().clone();
        [&publication.active, &publication.inactive].map(|s| serde_json::to_value(s).unwrap())
    }

    pub(super) fn assert_reads_only(&self) {
        for (class, id, payload) in self.io.sent.lock().unwrap().iter() {
            match (class, id) {
                (0x0d, 0x82) => assert_eq!(payload[0], 1),
                // The query alone; the 00/04 setters are never submitted.
                (0x00, 0x84) => assert_eq!(payload, &[0, 0]),
                _ => panic!("unadmitted command {class:02X}/{id:02X}"),
            }
        }
    }
}
