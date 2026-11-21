//! Stores an accepted `ProfileSet` as managed `SaveProfilesAsync` does: the
//! whole aggregate is replaced in one recorded transaction, the preferences
//! document is carried over, and the result is read back before it counts.
//!
//! The transaction follows managed's protocol with this store's primitives:
//! exact backups, then the `recovery-required` record, then the three
//! documents in managed's order (profile, preferences, marker), managed's
//! verification of the stored aggregate, and only then the record and
//! backups are removed. Each document is staged by the store's own
//! compare-and-swap replace (`<name>.pending`) instead of a `.stage` file. A
//! failure after the record exists restores the exact prior documents from
//! memory, as managed's in-process rollback does; if that cannot be
//! confirmed the record and backups stay and the aggregate is quarantined.
//! Like managed, nothing recovers a recorded transaction after a crash: the
//! record alone preserves the aggregate until it is reviewed.
//!
//! A stage of this transaction is never evidence. Managed names its stages
//! per transaction and only ever deletes them; this store's stage names are
//! fixed and its replace leaves the stage where it fails, which the override
//! document relies on. So the transaction takes back the stage of each write
//! of its own that fails, and before it starts it removes any stage an
//! earlier attempt left under a name it writes. With no record there is no
//! open transaction for such a stage to belong to.
//!
//! The same transaction migrates an older aggregate when the store opens, as
//! managed's load does: see [`migrate_on_open`].
use super::*;
use openblade_domain::profile_set::{
    self as profile_set, Capabilities, ProfileSet, ValidationPolicy, Validator, encode,
    migrate::{self, Stored},
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
/// A leftover stage is in the way and could not be removed.
const STAGE_BLOCKED: &str = "The requested intent was rejected: A staging file left by an earlier configuration save could not be removed. The saved configuration is unchanged.";
/// The set is valid, but this service's own reader would refuse it once stored.
const UNREADABLE: &str = "The requested intent was rejected: This service cannot read the profile set back once it is stored. The saved configuration is unchanged.";
/// A migration at open that left the stored aggregate as it was.
const MIGRATION_UNCHANGED: &str = "The saved configuration could not be migrated to the current schema. It is unchanged, and the next start migrates it again.";
/// A migration at open whose restore could not be confirmed.
const MIGRATION_RECOVERY_REQUIRED: &str = "Configuration transaction recovery is required. The recovery-required record and the original transaction backups are preserved.";

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

/// Exact RZ09-0581 only: this composition serves no other model. Duplicates
/// are found as managed finds them, not by the comparison the store was
/// opened with.
fn validator() -> (Capabilities, Validator) {
    let capabilities = Capabilities::rz09_0581();
    let policy = ValidationPolicy::for_capabilities(&capabilities, &capabilities);
    let validator = Validator::new(policy, profile_set::text::same_ordinal_ignoring_case);
    (capabilities, validator)
}

/// A committed aggregate as it is stored.
struct Committed {
    documents: [Option<Vec<u8>>; 3],
    profiles: ProfileSet,
    preferences: Preferences,
    /// The stored documents are not what managed would write for them.
    migrated: bool,
}

/// The committed aggregate a save builds on, or `None` where it is preserved:
/// a recorded transaction preserves whatever the documents hold.
fn committed(files: &dyn Files, validator: &Validator) -> io::Result<Option<Committed>> {
    if files.read(configuration::RECOVERY_REQUIRED)?.is_some() {
        return Ok(None);
    }
    read_committed(files, validator)
}

/// The three documents, when they are one committed aggregate this
/// composition reads.
fn read_committed(files: &dyn Files, validator: &Validator) -> io::Result<Option<Committed>> {
    let prior = read_documents(files)?;
    Ok(match inspect(&prior) {
        // Managed materialises its safe defaults on load; an absent aggregate
        // is that same starting point here.
        Inspection::Empty => Some(Committed {
            documents: prior,
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
                    documents: prior,
                })
        }
        // A schema-less legacy profile and every preserved state need managed.
        _ => None,
    })
}

/// The three documents as they are stored, in managed's order.
fn read_documents(files: &dyn Files) -> io::Result<[Option<Vec<u8>>; 3]> {
    let mut documents: [Option<Vec<u8>>; 3] = Default::default();
    for (bytes, name) in documents.iter_mut().zip(NAMES) {
        *bytes = files.read(name)?;
    }
    Ok(documents)
}

/// The envelope inspection of `documents`, for a caller that found no record.
fn inspect(documents: &[Option<Vec<u8>>; 3]) -> Inspection {
    let file = |index: usize| {
        documents[index]
            .as_deref()
            .map_or(ConfigFile::Missing, ConfigFile::Bytes)
    };
    configuration::inspect(configuration::Snapshot {
        profile: file(0),
        preferences: file(1),
        commit: file(2),
        recovery: RecoveryRecord::Missing,
    })
}

/// The uppercase hexadecimal SHA-256 the marker binds each document with.
fn sha256(bytes: &[u8]) -> String {
    format!("{:X}", Sha256::digest(bytes))
}

/// Managed `LoadCoreAsync` with `materializeAdmittedState`
/// (`BatteryConfigurationStore.cs:349-417`): when the store opens, a
/// committed aggregate whose reading migrated something, and the schema-less
/// legacy profile, are replaced by the documents managed writes for them.
/// Managed does this when it loads, not at the first save, and so does this.
///
/// The replacement is the save's recorded transaction: the same backups,
/// record, verification and restore, so the original bytes stay on disk
/// until the migrated ones are verified. Everything else is left exactly as
/// it is for the store's own reader to admit or refuse: an absent aggregate
/// (the first save materialises it), a recorded or preserved state, an
/// aggregate managed would quarantine, and one this service could not read
/// back once migrated.
pub(super) fn migrate_on_open(
    files: &dyn Files,
    same_path: fn(&str, &str) -> bool,
) -> io::Result<()> {
    if files.read(configuration::RECOVERY_REQUIRED)?.is_some() {
        return Ok(());
    }
    let prior = read_documents(files)?;
    let stored = match (inspect(&prior), &prior) {
        (Inspection::LegacyCandidate, [Some(profile), None, None]) => Stored::Legacy { profile },
        (Inspection::CommittedEnvelope { schema }, [Some(profile), Some(preferences), Some(_)]) => {
            Stored::Committed {
                schema,
                profile,
                preferences,
            }
        }
        _ => return Ok(()),
    };
    let (_, validator) = validator();
    let Ok(Some(migration)) = migrate::migrate(stored, &validator) else {
        return Ok(());
    };
    let intended = migration.documents(sha256);
    if !reopens(&intended, same_path) {
        return Ok(());
    }
    let transaction = Transaction::new(
        files,
        &prior,
        intended,
        &migration.profiles,
        &migration.preferences,
        &validator,
    );
    transaction.run().map_err(|failure| {
        io::Error::other(match failure {
            Failure::RecoveryRequired => MIGRATION_RECOVERY_REQUIRED,
            Failure::StageBlocked | Failure::Unchanged | Failure::Restored => MIGRATION_UNCHANGED,
        })
    })
}

/// Whether this service's own reader accepts `documents` as the stored
/// aggregate. It reads the aggregate again before every later request and
/// when it starts, so a set it refuses would close the store it was saved to.
fn reopens(documents: &[Vec<u8>; 3], same_path: fn(&str, &str) -> bool) -> bool {
    struct Intended<'a>(&'a [Vec<u8>; 3]);
    impl Files for Intended<'_> {
        fn read(&self, name: &str) -> io::Result<Option<Vec<u8>>> {
            let index = NAMES.iter().position(|known| *known == name);
            Ok(index.map(|index| self.0[index].clone()))
        }
        fn replace(&self, _: &str, _: Option<&[u8]>, _: &[u8]) -> io::Result<()> {
            Err(denied())
        }
    }
    aggregate(&Intended(documents), same_path).is_ok()
}

/// The transaction's artifacts. The identifier is the digest of the change,
/// so a repeated attempt names the same artifacts and cannot overwrite the
/// evidence an earlier one left behind.
struct Transaction<'a> {
    files: &'a dyn Files,
    prior: &'a [Option<Vec<u8>>; 3],
    intended: [Vec<u8>; 3],
    id: String,
    /// What the intended documents must read back as.
    admitted: &'a ProfileSet,
    preferences: &'a Preferences,
    validator: &'a Validator,
}

impl<'a> Transaction<'a> {
    fn new(
        files: &'a dyn Files,
        prior: &'a [Option<Vec<u8>>; 3],
        intended: [Vec<u8>; 3],
        admitted: &'a ProfileSet,
        preferences: &'a Preferences,
        validator: &'a Validator,
    ) -> Self {
        let mut change = Sha256::new();
        for bytes in prior.iter().flatten().chain(&intended) {
            change.update((bytes.len() as u64).to_le_bytes());
            change.update(bytes);
        }
        Self {
            files,
            prior,
            intended,
            id: format!("{:x}", change.finalize())[..32].to_owned(),
            admitted,
            preferences,
            validator,
        }
    }

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

    /// Removes what an abandoned attempt left under a name this transaction
    /// stages: its backups, the record and the documents.
    fn discard_stages(&self) -> io::Result<()> {
        for (name, _) in self.backups() {
            self.files.discard_stage(&name)?;
        }
        for name in [configuration::RECOVERY_REQUIRED].into_iter().chain(NAMES) {
            self.files.discard_stage(name)?;
        }
        Ok(())
    }

    /// One staged write of this transaction. A failure takes its stage back,
    /// best effort: one left behind is removed before the next transaction.
    fn put(&self, name: &str, expected: Option<&[u8]>, bytes: &[u8]) -> io::Result<()> {
        self.files.replace(name, expected, bytes).inspect_err(|_| {
            let _ = self.files.discard_stage(name);
        })
    }

    /// Replaces the documents in managed's order.
    fn replace(&self) -> io::Result<()> {
        for (index, name) in NAMES.into_iter().enumerate() {
            self.put(name, self.prior[index].as_deref(), &self.intended[index])?;
        }
        Ok(())
    }

    /// Managed `VerifyIntendedAggregateAsync`: the store holds exactly the
    /// intended documents and they read back as the admitted aggregate.
    fn verify(&self) -> io::Result<()> {
        match read_committed(self.files, self.validator)? {
            Some(stored)
                if stored.profiles == *self.admitted
                    && stored.preferences == *self.preferences
                    && !stored.migrated
                    && stored
                        .documents
                        .iter()
                        .zip(&self.intended)
                        .all(|(stored, intended)| stored.as_ref() == Some(intended)) =>
            {
                Ok(())
            }
            _ => Err(denied()),
        }
    }

    /// Puts back whatever a failed replacement changed, then proves it.
    fn restore(&self) -> io::Result<()> {
        for (index, name) in NAMES.into_iter().enumerate() {
            let current = self.files.read(name)?;
            match (&self.prior[index], current) {
                (prior, current) if *prior == current => {}
                (Some(prior), current) => self.put(name, current.as_deref(), prior)?,
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

    /// The caller found no record, so no transaction is open.
    fn run(&self) -> Result<(), Failure> {
        if self.discard_stages().is_err() {
            return Err(Failure::StageBlocked);
        }
        for (name, bytes) in self.backups() {
            // An earlier attempt at this same change may have left its backup.
            let kept = matches!(self.files.read(&name), Ok(Some(existing)) if existing == bytes);
            if !kept && self.put(&name, None, bytes).is_err() {
                self.discard_backups();
                return Err(Failure::Unchanged);
            }
        }
        let record = self.record().map_err(|_| Failure::Unchanged)?;
        if self
            .put(configuration::RECOVERY_REQUIRED, None, &record)
            .is_err()
        {
            // No document was touched. As managed does, drop a record this
            // attempt may still have created.
            let _ = self.files.remove(configuration::RECOVERY_REQUIRED, &record);
            self.discard_backups();
            return Err(Failure::Unchanged);
        }
        // Verified while the record and the backups still stand, as managed
        // does: a stored aggregate that is not the intended one is restored.
        let replaced = self.replace().and_then(|()| self.verify());
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
    /// A leftover stage could not be removed. Nothing was written.
    StageBlocked,
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
        let (capabilities, validator) = validator();
        let Some(saved) = committed(self.files.as_ref(), &validator)? else {
            return Ok(ProfileSave::Blocked(QUARANTINE_BLOCKED));
        };
        let prior = &saved.documents;
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
        let preferences = match &prior[1] {
            Some(bytes) if !saved.migrated => bytes.clone(),
            _ => preferences_document(profile_set::CURRENT_SCHEMA, &saved.preferences),
        };
        let marker = commit_marker(
            profile_set::CURRENT_SCHEMA,
            &sha256(&profile),
            &sha256(&preferences),
        );
        let intended = [profile, preferences, marker];
        if !reopens(&intended, self.same_path) {
            return Ok(ProfileSave::Rejected(UNREADABLE.into()));
        }
        let transaction = Transaction::new(
            self.files.as_ref(),
            prior,
            intended,
            &admitted,
            &saved.preferences,
            &validator,
        );
        Ok(match transaction.run() {
            Ok(()) => ProfileSave::Stored,
            Err(Failure::StageBlocked) => ProfileSave::Rejected(STAGE_BLOCKED.into()),
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
