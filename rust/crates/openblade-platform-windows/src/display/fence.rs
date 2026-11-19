//! Host authority re-checked at a native display submission.

use super::{DisplayReadError, InternalDisplayTopology};

/// Host authority re-checked at the one possible native display submission,
/// after all fallible preparation. A fresh internal-panel topology must still
/// identify the admitted target, and the host's current lifetime/session/power
/// admission must still hold. Either refusal precedes the submission, so it
/// can never be a possible submission. Brightness (WmiSetBrightness), HDR
/// (SET_HDR_STATE) and refresh (SetDisplayConfig APPLY, after its
/// non-submitting VALIDATE) share this contract.
pub trait SubmissionFence: Send + Sync {
    fn topology(&self) -> Result<InternalDisplayTopology, DisplayReadError>;
    fn admit(&self) -> bool;
}

/// The internal panel admitted for one HDR or refresh write: the same CCD
/// endpoints and the same monitor device path, compared ASCII case-
/// insensitively like managed `PrepareBrightness` (`OrdinalIgnoreCase`), or
/// both unreadable. Endpoint IDs alone can be reused by another panel.
#[cfg(any(feature = "display-hdr-write", feature = "display-refresh-write"))]
#[derive(Debug)]
pub(super) struct PanelIdentity {
    source: super::EndpointId,
    target: super::EndpointId,
    path: Option<std::ffi::OsString>,
}

#[cfg(any(feature = "display-hdr-write", feature = "display-refresh-write"))]
impl PanelIdentity {
    pub(super) fn of(display: &super::ActiveInternalDisplay) -> Self {
        Self {
            source: display.source,
            target: display.target,
            path: device_path(display).cloned(),
        }
    }

    #[cfg(feature = "display-hdr-write")]
    pub(super) fn target(&self) -> super::EndpointId {
        self.target
    }

    pub(super) fn same_as(&self, display: &super::ActiveInternalDisplay) -> bool {
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

#[cfg(any(feature = "display-hdr-write", feature = "display-refresh-write"))]
fn device_path(display: &super::ActiveInternalDisplay) -> Option<&std::ffi::OsString> {
    display
        .monitor_name
        .as_ref()
        .ok()
        .and_then(|name| name.device_path.as_ref())
}

#[cfg(all(
    test,
    any(feature = "display-hdr-write", feature = "display-refresh-write")
))]
mod tests {
    use super::*;
    use crate::display::{ActiveInternalDisplay, MonitorName};

    const PATH: &str = r"\?\DISPLAY#SYN0001#FIXTURE#{guid}";

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
}
