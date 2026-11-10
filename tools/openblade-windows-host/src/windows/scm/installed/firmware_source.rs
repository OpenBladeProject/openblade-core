use std::{io, rc::Rc};

use tokio::time::Instant;

use super::{Caller, Logon, Observation, Snapshot};
use crate::discovery::performance::{Admission, ObservedPerformance, Worker, native_worker};

type Project = dyn Fn(Admission) -> Option<Snapshot>;

// An installed owner must supply both live HID admission and independently
// verified v79 status guards tied to the same authority epoch. The metadata
// source alone cannot supply either.
#[derive(Clone)]
pub(in crate::installed) struct FirmwareSource {
    worker: Worker,
    project: Rc<Project>,
}

impl FirmwareSource {
    #[allow(dead_code)] // No installed owner can prove this authority yet.
    pub(in crate::installed) fn native(
        authorize: impl Fn() -> Option<Admission> + Send + Sync + 'static,
        project: impl Fn(Admission) -> Option<Snapshot> + 'static,
    ) -> Self {
        Self::new(native_worker(authorize), project)
    }

    pub(super) fn new(
        worker: Worker,
        project: impl Fn(Admission) -> Option<Snapshot> + 'static,
    ) -> Self {
        Self {
            worker,
            project: Rc::new(project),
        }
    }

    pub(super) fn observe(&self, caller: Caller, deadline: Instant) -> Observation {
        let source = self.clone();
        Box::pin(async move {
            let session = (caller.logon == Logon::Interactive).then_some(caller.session);
            let Some(reading) = source.worker.observe(deadline, session).await? else {
                return Ok(None);
            };
            let preset = match reading.performance {
                ObservedPerformance::BalancedAutomatic => 1,
                ObservedPerformance::PerformanceAutomatic => 2,
            };
            Ok((source.project)(reading.admission)
                .and_then(|status| status.with_standard_performance(preset)))
        })
    }

    pub(super) async fn stop(&self) -> io::Result<()> {
        self.worker.stop().await
    }
}
