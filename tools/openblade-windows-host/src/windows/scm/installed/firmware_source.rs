use std::{io, rc::Rc, sync::Arc};

use tokio::time::Instant;

use super::ownership::{Authority, Binding};
use super::{Caller, Logon, Observation, Snapshot};
use crate::discovery::performance::{Admission, ObservedPerformance, Worker};

#[cfg(feature = "service-hardware-compile-check")]
use crate::discovery::performance::native_worker;

type Project = dyn Fn(Admission) -> Option<Snapshot>;

// An installed owner must supply both live HID admission and independently
// verified v91 status guards tied to the same authority epoch. The metadata
// source alone cannot supply either.
#[derive(Clone)]
pub(in crate::installed) struct FirmwareSource {
    worker: Worker,
    project: Rc<Project>,
    ownership: Option<Arc<Binding>>,
}

impl FirmwareSource {
    #[cfg(feature = "service-hardware-compile-check")]
    pub(in crate::installed) fn native(
        launch_pid: u32,
        authority: Option<&dyn Authority>,
        project: impl Fn(Admission) -> Option<Snapshot> + 'static,
    ) -> io::Result<Self> {
        Self::bind(
            launch_pid,
            authority,
            |binding| native_worker(move || binding.authorize()),
            project,
        )
    }

    #[cfg(feature = "service-hardware-compile-check")]
    pub(in crate::installed) fn native_with_package(
        launch_pid: u32,
        authority: Option<&dyn Authority>,
        package: crate::candidate_receipt::CommittedServiceImage,
        project: impl Fn(Admission) -> Option<Snapshot> + 'static,
    ) -> io::Result<Self> {
        Self::bind(
            launch_pid,
            authority,
            |binding| {
                // Attach before constructing any worker. The same binding is kept
                // by native work and quarantines files along with its owner fence.
                binding.retain_package(package);
                native_worker(move || binding.authorize())
            },
            project,
        )
    }

    pub(in crate::installed) fn bind(
        launch_pid: u32,
        authority: Option<&dyn Authority>,
        worker: impl FnOnce(Arc<Binding>) -> Worker,
        project: impl Fn(Admission) -> Option<Snapshot> + 'static,
    ) -> io::Result<Self> {
        let binding = Binding::acquire(launch_pid, authority)?;
        Ok(Self {
            worker: worker(binding.clone()),
            project: Rc::new(project),
            ownership: Some(binding),
        })
    }

    #[cfg(test)]
    pub(super) fn new(
        worker: Worker,
        project: impl Fn(Admission) -> Option<Snapshot> + 'static,
    ) -> Self {
        Self {
            worker,
            project: Rc::new(project),
            ownership: None,
        }
    }

    pub(super) fn observe(&self, caller: Caller, deadline: Instant) -> Observation {
        let source = self.clone();
        Box::pin(async move {
            if source
                .ownership
                .as_ref()
                .is_some_and(|owner| owner.authorize().is_none())
            {
                return Ok(None);
            }
            let session = (caller.logon == Logon::Interactive).then_some(caller.session);
            let Some(reading) = source.worker.observe(deadline, session).await? else {
                return Ok(None);
            };
            let preset = match reading.performance {
                ObservedPerformance::BalancedAutomatic => 1,
                ObservedPerformance::PerformanceAutomatic => 2,
                _ => return Ok(None), // Legacy write-authority prototype retains its old projection.
            };
            if source
                .ownership
                .as_ref()
                .is_some_and(|owner| owner.authorize() != Some(reading.admission))
            {
                return Ok(None);
            }
            let status = (source.project)(reading.admission)
                .and_then(|status| status.with_standard_performance(preset));
            Ok(status.filter(|_| {
                source
                    .ownership
                    .as_ref()
                    .is_none_or(|owner| owner.authorize() == Some(reading.admission))
            }))
        })
    }

    pub(super) async fn stop(&self) -> io::Result<()> {
        if let Some(owner) = &self.ownership {
            owner.revoke();
        }
        self.worker.stop().await?;
        if let Some(owner) = &self.ownership {
            owner.retire()?;
        }
        Ok(())
    }
}
