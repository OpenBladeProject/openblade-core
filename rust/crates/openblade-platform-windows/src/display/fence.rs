//! Host authority re-checked at a native display submission.

use super::{DisplayReadError, InternalDisplayTopology};

/// Host authority re-checked at the one possible native display submission,
/// after all fallible preparation. A fresh internal-panel topology must still
/// identify the admitted target, and the host's current lifetime/session/power
/// admission must still hold. Either refusal precedes the submission, so it
/// can never be a possible submission. Brightness (WmiSetBrightness), HDR
/// (SET_HDR_STATE), refresh (SetDisplayConfig APPLY, after its
/// non-submitting VALIDATE) and the color profile (before the first WCS
/// setter of its transaction) share this contract.
pub trait SubmissionFence: Send + Sync {
    fn topology(&self) -> Result<InternalDisplayTopology, DisplayReadError>;
    fn admit(&self) -> bool;
}

#[cfg(any(
    feature = "display-hdr-write",
    feature = "display-refresh-write",
    feature = "display-color-write"
))]
pub(crate) use submission::{FenceRefusal, PanelIdentity, admit_submission};

/// The fence sequence and panel identity shared by the HDR, refresh and
/// color-profile writers.
#[cfg(any(
    feature = "display-hdr-write",
    feature = "display-refresh-write",
    feature = "display-color-write"
))]
mod submission {
    use super::{InternalDisplayTopology, SubmissionFence};
    use crate::display::{ActiveInternalDisplay, EndpointId};
    use std::ffi::OsString;

    /// Why [`admit_submission`] refused. Each refusal precedes the
    /// submission; every writer error converts it with `From`.
    #[derive(Clone, Copy, Debug, PartialEq, Eq)]
    pub(crate) enum FenceRefusal {
        Cancelled,
        /// The fenced topology is not a single internal panel.
        PanelChanged,
        AdmissionDenied,
    }

    /// The fence sequence of every writer, immediately before its first
    /// submission: caller cancellation, then one fenced topology whose
    /// single panel `fresh` must accept, then the host admission, then
    /// cancellation again, because the admission may wait. `fresh` refuses
    /// with the writer's own reason.
    pub(crate) fn admit_submission<E: From<FenceRefusal>>(
        fence: &dyn SubmissionFence,
        cancelled: &dyn Fn() -> bool,
        fresh: impl FnOnce(&ActiveInternalDisplay) -> Result<(), E>,
    ) -> Result<(), E> {
        if cancelled() {
            return Err(FenceRefusal::Cancelled.into());
        }
        match fence.topology() {
            Ok(InternalDisplayTopology::Single(display)) => fresh(&display)?,
            _ => return Err(FenceRefusal::PanelChanged.into()),
        }
        if !fence.admit() {
            return Err(FenceRefusal::AdmissionDenied.into());
        }
        if cancelled() {
            return Err(FenceRefusal::Cancelled.into());
        }
        Ok(())
    }

    /// The internal panel admitted for one HDR, refresh or color-profile
    /// write: the same CCD endpoints and the same monitor device path,
    /// compared ASCII case-insensitively like managed `PrepareBrightness`
    /// (`OrdinalIgnoreCase`), or both unreadable. Endpoint IDs alone can be
    /// reused by another panel.
    #[derive(Debug)]
    pub(crate) struct PanelIdentity {
        source: EndpointId,
        target: EndpointId,
        path: Option<OsString>,
    }

    impl PanelIdentity {
        pub(crate) fn of(display: &ActiveInternalDisplay) -> Self {
            Self {
                source: display.source,
                target: display.target,
                path: device_path(display).cloned(),
            }
        }

        #[cfg(feature = "display-hdr-write")]
        pub(crate) fn target(&self) -> EndpointId {
            self.target
        }

        pub(crate) fn same_as(&self, display: &ActiveInternalDisplay) -> bool {
            self.source == display.source
                && self.target == display.target
                && match (self.path.as_ref(), device_path(display)) {
                    (None, None) => true,
                    (Some(left), Some(right)) => {
                        left == right
                            || left
                                .to_str()
                                .zip(right.to_str())
                                .is_some_and(|(left, right)| left.eq_ignore_ascii_case(right))
                    }
                    _ => false,
                }
        }
    }

    fn device_path(display: &ActiveInternalDisplay) -> Option<&OsString> {
        display
            .monitor_name
            .as_ref()
            .ok()
            .and_then(|name| name.device_path.as_ref())
    }

    #[cfg(test)]
    mod tests {
        use super::*;
        use crate::display::{DisplayReadError, MonitorName};
        use std::{
            cell::Cell,
            sync::atomic::{AtomicUsize, Ordering},
        };

        const PATH: &str = r"\?\DISPLAY#SYN0001#FIXTURE#{guid}";

        type Topology = fn() -> Result<InternalDisplayTopology, DisplayReadError>;

        fn panel() -> ActiveInternalDisplay {
            ActiveInternalDisplay::brightness_fixture(PATH, 2)
        }

        #[test]
        fn both_endpoints_are_part_of_the_identity() {
            let identity = PanelIdentity::of(&panel());
            assert!(identity.same_as(&panel()));
            let mut source = panel();
            source.source.id = 4;
            assert!(!identity.same_as(&source));
            let mut adapter = panel();
            adapter.source.adapter_low = 13;
            assert!(!identity.same_as(&adapter));
            assert!(!identity.same_as(&ActiveInternalDisplay::brightness_fixture(PATH, 3)));
        }

        #[test]
        fn unreadable_device_paths_match_only_each_other() {
            let mut failed = panel();
            failed.monitor_name = Err(50);
            let mut missing = panel();
            missing.monitor_name = Ok(MonitorName {
                friendly_name: None,
                device_path: None,
            });
            let identity = PanelIdentity::of(&failed);
            assert!(identity.same_as(&failed));
            assert!(identity.same_as(&missing));
            assert!(!identity.same_as(&panel()));
            assert!(!PanelIdentity::of(&panel()).same_as(&missing));
        }

        #[test]
        fn the_gdi_source_name_is_not_part_of_the_identity() {
            let mut renamed = panel();
            renamed.source_name = r"\.\DISPLAY7".into();
            assert!(PanelIdentity::of(&panel()).same_as(&renamed));
        }

        /// A writer error: the shared refusals, or the writer's own.
        #[derive(Debug, PartialEq, Eq)]
        enum Refused {
            Fence(FenceRefusal),
            Stale,
        }

        impl From<FenceRefusal> for Refused {
            fn from(refusal: FenceRefusal) -> Self {
                Self::Fence(refusal)
            }
        }

        struct Fence {
            topology: Topology,
            admit: bool,
            admissions: AtomicUsize,
        }

        impl SubmissionFence for Fence {
            fn topology(&self) -> Result<InternalDisplayTopology, DisplayReadError> {
                (self.topology)()
            }

            fn admit(&self) -> bool {
                self.admissions.fetch_add(1, Ordering::SeqCst);
                self.admit
            }
        }

        fn fence(topology: Topology, admit: bool) -> Fence {
            Fence {
                topology,
                admit,
                admissions: AtomicUsize::new(0),
            }
        }

        fn single() -> Result<InternalDisplayTopology, DisplayReadError> {
            Ok(InternalDisplayTopology::Single(panel()))
        }

        /// The fence result, how many panels `fresh` saw, and admissions.
        fn admit(
            fence: &Fence,
            cancelled: &dyn Fn() -> bool,
            fresh: Result<(), Refused>,
        ) -> (Result<(), Refused>, usize, usize) {
            let seen = Cell::new(0);
            let result = admit_submission(fence, cancelled, |display| {
                assert!(PanelIdentity::of(&panel()).same_as(display));
                seen.set(seen.get() + 1);
                fresh
            });
            (result, seen.get(), fence.admissions.load(Ordering::SeqCst))
        }

        #[test]
        fn a_fresh_single_panel_and_the_admission_admit_the_submission() {
            assert_eq!(
                admit(&fence(single, true), &|| false, Ok(())),
                (Ok(()), 1, 1)
            );
        }

        #[test]
        fn a_non_single_fenced_topology_is_a_panel_change_before_the_admission() {
            let topologies: [Topology; 4] = [
                || Ok(InternalDisplayTopology::Empty),
                || Ok(InternalDisplayTopology::NoInternalDisplay),
                || Ok(InternalDisplayTopology::MultipleInternalDisplays),
                || Err(DisplayReadError::TopologyChanging),
            ];
            for topology in topologies {
                assert_eq!(
                    admit(&fence(topology, true), &|| false, Ok(())),
                    (Err(Refused::Fence(FenceRefusal::PanelChanged)), 0, 0)
                );
            }
        }

        #[test]
        fn the_writer_refusal_and_a_denied_admission_refuse_in_order() {
            assert_eq!(
                admit(&fence(single, true), &|| false, Err(Refused::Stale)),
                (Err(Refused::Stale), 1, 0)
            );
            assert_eq!(
                admit(&fence(single, false), &|| false, Ok(())),
                (Err(Refused::Fence(FenceRefusal::AdmissionDenied)), 1, 1)
            );
        }

        #[test]
        fn cancellation_is_checked_before_the_fence_and_after_the_admission() {
            assert_eq!(
                admit(&fence(single, true), &|| true, Ok(())),
                (Err(Refused::Fence(FenceRefusal::Cancelled)), 0, 0)
            );
            // Cancelled while the admission waited.
            let checks = Cell::new(0);
            let later = || {
                checks.set(checks.get() + 1);
                checks.get() > 1
            };
            assert_eq!(
                admit(&fence(single, true), &later, Ok(())),
                (Err(Refused::Fence(FenceRefusal::Cancelled)), 1, 1)
            );
        }
    }
}
