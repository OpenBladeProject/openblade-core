//! Host authority re-checked at a native display submission.

use super::{DisplayReadError, InternalDisplayTopology};

/// Host authority re-checked at the one possible native display submission,
/// after all fallible preparation. A fresh internal-panel topology must still
/// identify the admitted target, and the host's current lifetime/session/power
/// admission must still hold. Either refusal precedes the submission, so it
/// can never be a possible submission. Brightness (WmiSetBrightness) and HDR
/// (SET_HDR_STATE) share this contract.
pub trait SubmissionFence: Send + Sync {
    fn topology(&self) -> Result<InternalDisplayTopology, DisplayReadError>;
    fn admit(&self) -> bool;
}
