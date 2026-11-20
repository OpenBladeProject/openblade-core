//! The application-profile selection fields of verified Service status, shared
//! with the reporter owner. Managed hands every connected status to
//! `ApplicationProfileReporter.Observe`; this feed does the same, except that a
//! snapshot with unchanged selection fields is not republished.
use std::sync::Arc;

use openblade_application_reporter::{ActivationMode, ApplicationProfile, Guid};
use serde::Deserialize;
use serde_json::Value;
use tokio::sync::watch;

use crate::service_frame::is_guid;
use crate::service_status::Snapshot;

/// Absent until the first verified status. Shared so a republish is a pointer
/// swap and an unchanged snapshot is observable as the same allocation.
pub(crate) type Profiles = Option<Arc<[ApplicationProfile]>>;

/// Owned by the installed monitor, which publishes each verified fresh status.
pub(crate) struct ProfileFeed(watch::Sender<Profiles>);

impl ProfileFeed {
    pub(crate) fn new() -> Self {
        Self(watch::Sender::new(None))
    }

    pub(crate) fn subscribe(&self) -> watch::Receiver<Profiles> {
        self.0.subscribe()
    }

    /// A status whose profile list cannot be read leaves the earlier list in
    /// place, as a managed status that fails to deserialize never reaches
    /// `Observe`.
    pub(crate) fn observe(&self, snapshot: &Snapshot) {
        let Some(next) = snapshot.application_profiles().and_then(decode) else {
            return;
        };
        self.0.send_if_modified(|current| {
            if current.as_deref() == Some(&next[..]) {
                return false;
            }
            *current = Some(next.into());
            true
        });
    }
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Entry {
    id: String,
    executable_path: String,
    activation_mode: i64,
    // Managed ApplicationProfileDefinition.Enabled defaults to true.
    #[serde(default = "enabled")]
    enabled: bool,
}

fn enabled() -> bool {
    true
}

/// The Service stores and publishes the normalized path (managed
/// `NormalizeExecutablePath`), so the reporter compares it as given.
pub(super) fn decode(list: &Value) -> Option<Vec<ApplicationProfile>> {
    Vec::<Entry>::deserialize(list)
        .ok()?
        .into_iter()
        .map(|entry| {
            Some(ApplicationProfile {
                id: guid(&entry.id)?,
                executable_path: entry.executable_path,
                mode: match entry.activation_mode {
                    0 => ActivationMode::Foreground,
                    1 => ActivationMode::WhileRunningAfterActivation,
                    _ => return None,
                },
                enabled: entry.enabled,
            })
        })
        .collect()
}

pub(super) fn guid(text: &str) -> Option<Guid> {
    if !is_guid(text) {
        return None;
    }
    let digits: String = text.chars().filter(|c| *c != '-').collect();
    Some(Guid(u128::from_str_radix(&digits, 16).ok()?.to_be_bytes()))
}
