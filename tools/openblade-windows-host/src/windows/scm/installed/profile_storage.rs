//! Stores an accepted `ProfileSet` as managed `SaveProfilesAsync` does: the
//! whole aggregate is replaced in one recorded transaction, the preferences
//! document is carried over, and the result is read back before it counts.
//!
//! The transaction follows managed's protocol with this store's primitives:
//! exact backups, then the `recovery-required` record, then the three
//! documents in managed's order (profile, preferences, marker), a readback,
//! and only then the record and backups are removed. Each document is staged
//! by the store's own compare-and-swap replace (`<name>.pending`) instead of
//! a `.stage` file. A failure after the record exists restores the exact
//! prior documents from memory, as managed's in-process rollback does; if
//! that cannot be confirmed the record and backups stay and the aggregate is
//! quarantined. Like managed, nothing recovers a recorded transaction after
//! a crash: the record alone preserves the aggregate until it is reviewed.
use super::*;
use openblade_domain::profile_set::{
    self as profile_set, Capabilities, ProfileSet, ValidationPolicy, Validator, encode,
    preferences::{Preferences, commit_marker, preferences_document, read_aggregate},
    update,
};
use sha2::{Digest, Sha256};

/// Managed `ConfigurationQuarantineBlockedMessage`.
const QUARANTINE_BLOCKED: &str = "OpenBlade preserved an unsupported or invalid configuration. Thermal settings are read-only until the service restarts with a valid configuration.";
/// A transaction that left the exact prior aggregate in place.
const RESTORED: &str = "The requested intent was rejected: The configuration aggregate could not be replaced. The saved configuration is unchanged.";
/// A transaction whose restore could not be confirmed.
const RECOVERY_REQUIRED: &str = "The requested intent was rejected: Configuration transaction recovery is required. The recovery-required record and the original transaction backups are preserved.";

const NAMES: [&str; 3] = [
    configuration::PROFILE,
    configuration::PREFERENCES,
    configuration::COMMIT,
];

/// What a profile save did, as the reply managed gives for it.
#[derive(Debug, Eq, PartialEq)]
pub(in super::super) enum ProfileSave {
    /// Managed `Stored`.
    Stored,
    /// Managed `Failed`: the set or the store refused it. Nothing changed,
    /// unless the message says recovery is required.
    Rejected(String),
    /// Managed `Blocked`: the saved aggregate is preserved in quarantine.
    Blocked(&'static str),
}

/// Exact RZ09-0581 only: this composition serves no other model.
fn validator(same_path: fn(&str, &str) -> bool) -> (Capabilities, Validator) {
    let capabilities = Capabilities::rz09_0581();
    let policy = ValidationPolicy::for_capabilities(&capabilities, &capabilities);
    (capabilities, Validator::new(policy, same_path))
}

/// The committed aggregate a save builds on, or why it is preserved.
struct Committed {
    prior: [Option<Vec<u8>>; 3],
    profiles: ProfileSet,
    preferences: Preferences,
    /// The stored documents are not what managed would write for them.
    migrated: bool,
}

fn committed(files: &dyn Files, validator: &Validator) -> io::Result<Option<Committed>> {
    if files.read(configuration::RECOVERY_REQUIRED)?.is_some() {
        return Ok(None);
    }
    let mut prior: [Option<Vec<u8>>; 3] = Default::default();
    for (bytes, name) in prior.iter_mut().zip(NAMES) {
        *bytes = files.read(name)?;
    }
    fn file(bytes: &Option<Vec<u8>>) -> ConfigFile<'_> {
        bytes
            .as_deref()
            .map_or(ConfigFile::Missing, ConfigFile::Bytes)
    }
    let inspection = configuration::inspect(configuration::Snapshot {
        profile: file(&prior[0]),
        preferences: file(&prior[1]),
        commit: file(&prior[2]),
        recovery: RecoveryRecord::Missing,
    });
    Ok(match inspection {
        // Managed materialises its safe defaults on load; an absent aggregate
        // is that same starting point here.
        Inspection::Empty => Some(Committed {
            prior,
            profiles: ProfileSet::safe_defaults(),
            preferences: Preferences::default(),
            migrated: true,
        }),
        Inspection::CommittedEnvelope { schema } => {
            let (Some(profile), Some(preferences)) = (&prior[0], &prior[1]) else {
                return Ok(None);
            };
            read_aggregate(schema, profile, preferences, validator)
                .ok()
                .map(|aggregate| Committed {
                    profiles: aggregate.profiles,
                    preferences: aggregate.preferences,
                    migrated: aggregate.migrated,
                    prior,
                })
        }
        // A schema-less legacy profile and every preserved state need managed.
        _ => None,
    })
}

/// The transaction's artifacts. The identifier is the digest of the change,
/// so a repeated attempt names the same artifacts and cannot overwrite the
/// evidence an earlier one left behind.
struct Transaction<'a> {
    files: &'a dyn Files,
    prior: &'a [Option<Vec<u8>>; 3],
    intended: [Vec<u8>; 3],
    id: String,
}

impl Transaction<'_> {
    fn backup(&self, index: usize) -> String {
        format!("{}.transaction-{}.backup", NAMES[index], self.id)
    }

    /// Managed's record: the transaction and, per document, whether a prior
    /// existed and where its backup is. Only its presence is ever read.
    fn record(&self) -> io::Result<Vec<u8>> {
        let targets: Vec<Value> = (0..3)
            .map(|index| {
                let backup = self.prior[index].as_ref().map(|_| self.backup(index));
                json!({"path": NAMES[index], "priorExists": backup.is_some(), "backupPath": backup, "restorePath": null})
            })
            .collect();
        serde_json::to_vec(
            &json!({"schemaVersion": 1, "transactionId": self.id, "targets": targets}),
        )
        .map_err(io::Error::other)
    }

    fn backups(&self) -> impl Iterator<Item = (String, &[u8])> {
        (0..3).filter_map(|index| Some((self.backup(index), self.prior[index].as_deref()?)))
    }

    /// Best effort, as managed's artifact cleanup is: a leftover backup
    /// changes nothing the aggregate means.
    fn discard_backups(&self) {
        for (name, bytes) in self.backups() {
            let _ = self.files.remove(&name, bytes);
        }
    }

    /// Whether the three documents hold exactly `expected`.
    fn holds(&self, expected: [Option<&[u8]>; 3]) -> io::Result<bool> {
        for (name, bytes) in NAMES.into_iter().zip(expected) {
            if self.files.read(name)?.as_deref() != bytes {
                return Ok(false);
            }
        }
        Ok(true)
    }

    /// Replaces the documents in managed's order and reads them back.
    fn replace(&self) -> io::Result<()> {
        for (index, name) in NAMES.into_iter().enumerate() {
            self.files
                .replace(name, self.prior[index].as_deref(), &self.intended[index])?;
        }
        if self.holds([0, 1, 2].map(|index| Some(self.intended[index].as_slice())))? {
            Ok(())
        } else {
            Err(denied())
        }
    }

    /// Puts back whatever a failed replacement changed, then proves it.
    fn restore(&self) -> io::Result<()> {
        for (index, name) in NAMES.into_iter().enumerate() {
            let current = self.files.read(name)?;
            match (&self.prior[index], current) {
                (prior, current) if *prior == current => {}
                (Some(prior), current) => self.files.replace(name, current.as_deref(), prior)?,
                (None, Some(current)) => self.files.remove(name, &current)?,
                (None, None) => {}
            }
        }
        if self.holds([0, 1, 2].map(|index| self.prior[index].as_deref()))? {
            Ok(())
        } else {
            Err(denied())
        }
    }

    fn run(&self) -> Result<(), Failure> {
        for (name, bytes) in self.backups() {
            // An earlier attempt at this same change may have left its backup.
            let kept = matches!(self.files.read(&name), Ok(Some(existing)) if existing == bytes);
            if !kept && self.files.replace(&name, None, bytes).is_err() {
                self.discard_backups();
                return Err(Failure::Unchanged);
            }
        }
        let record = self.record().map_err(|_| Failure::Unchanged)?;
        if self
            .files
            .replace(configuration::RECOVERY_REQUIRED, None, &record)
            .is_err()
        {
            // No document was touched. As managed does, drop a record this
            // attempt may still have created.
            let _ = self.files.remove(configuration::RECOVERY_REQUIRED, &record);
            self.discard_backups();
            return Err(Failure::Unchanged);
        }
        let replaced = self.replace();
        let settled = match replaced {
            Ok(()) => Ok(()),
            Err(_) => self
                .restore()
                .map_err(|_| Failure::RecoveryRequired)
                .and(Err(Failure::Restored)),
        };
        if matches!(settled, Err(Failure::RecoveryRequired)) {
            return settled;
        }
        // The aggregate is whole again either way, so the record can go.
        if self
            .files
            .remove(configuration::RECOVERY_REQUIRED, &record)
            .is_err()
        {
            return Err(Failure::RecoveryRequired);
        }
        self.discard_backups();
        settled
    }
}

#[derive(Debug, Eq, PartialEq)]
enum Failure {
    /// No document was touched.
    Unchanged,
    /// Documents were replaced and the exact prior aggregate is back.
    Restored,
    /// The record and backups remain; the aggregate needs review.
    RecoveryRequired,
}

impl Storage {
    /// Managed `UpdateProfilesAsync` up to and including the store: normalise
    /// and validate `requested` against the saved set, then commit it.
    pub(in super::super) fn save_profiles(
        &self,
        requested: &ProfileSet,
    ) -> io::Result<ProfileSave> {
        let mut state = self.state.lock().map_err(|_| denied())?;
        if state.quarantined {
            return Ok(ProfileSave::Blocked(QUARANTINE_BLOCKED));
        }
        let (capabilities, validator) = validator(self.same_path);
        let Some(saved) = committed(self.files.as_ref(), &validator)? else {
            return Ok(ProfileSave::Blocked(QUARANTINE_BLOCKED));
        };
        let admitted = match update::admit(
            requested.clone(),
            &saved.profiles,
            &validator,
            &validator,
            Some(&capabilities),
        ) {
            Ok(admitted) => admitted,
            Err(reason) => return Ok(ProfileSave::Rejected(update::rejection_message(&reason))),
        };
        let profile = encode::profile_document(profile_set::CURRENT_SCHEMA, &admitted);
        // Managed keeps the committed preference bytes unless loading them
        // migrated something; then it writes them again.
        let preferences = match &saved.prior[1] {
            Some(bytes) if !saved.migrated => bytes.clone(),
            _ => preferences_document(profile_set::CURRENT_SCHEMA, &saved.preferences),
        };
        let digest = |bytes: &[u8]| format!("{:X}", Sha256::digest(bytes));
        let marker = commit_marker(
            profile_set::CURRENT_SCHEMA,
            &digest(&profile),
            &digest(&preferences),
        );
        let mut change = Sha256::new();
        for bytes in saved
            .prior
            .iter()
            .flatten()
            .chain([&profile, &preferences, &marker])
        {
            change.update((bytes.len() as u64).to_le_bytes());
            change.update(bytes);
        }
        let transaction = Transaction {
            files: self.files.as_ref(),
            prior: &saved.prior,
            intended: [profile, preferences, marker],
            id: format!("{:x}", change.finalize())[..32].to_owned(),
        };
        Ok(match transaction.run() {
            // Managed verifies the committed aggregate reads back as intended.
            Ok(()) => match committed(self.files.as_ref(), &validator)? {
                Some(stored) if stored.profiles == admitted && !stored.migrated => {
                    ProfileSave::Stored
                }
                _ => {
                    state.quarantined = true;
                    state.configuration_changed = true;
                    ProfileSave::Blocked(QUARANTINE_BLOCKED)
                }
            },
            Err(Failure::Unchanged | Failure::Restored) => ProfileSave::Rejected(RESTORED.into()),
            Err(Failure::RecoveryRequired) => {
                state.quarantined = true;
                state.configuration_failed = true;
                ProfileSave::Rejected(RECOVERY_REQUIRED.into())
            }
        })
    }
}

#[cfg(test)]
#[path = "profile_storage_tests.rs"]
mod tests;
