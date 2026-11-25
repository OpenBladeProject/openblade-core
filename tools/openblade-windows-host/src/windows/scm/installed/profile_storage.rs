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
//! managed's load does: see [`plan_migration`].
use super::*;
#[cfg(feature = "service-fan-stop-v1")]
use openblade_domain::profile_set::preferences::FanStopSettings;
use openblade_domain::profile_set::{
    self as profile_set, Capabilities, ProfileSet, ValidationPolicy, Validator, encode,
    migrate::{self, Migration, Stored},
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
/// The staged preferences are valid, but this service's own reader would
/// refuse the aggregate once stored.
#[cfg(any(
    feature = "service-profile-triggers-v1",
    feature = "service-fan-stop-v1"
))]
const PREFERENCES_UNREADABLE: &str = "The requested intent was rejected: This service cannot read the configuration back once it is stored. The saved configuration is unchanged.";
/// A migration at open that left the stored aggregate as it was. The next
/// open tries again, and a failed open is not kept, so that is the next
/// request. A stage that cannot be removed, or another file under one of the
/// change's backup names, fails every attempt alike until it is removed.
const MIGRATION_UNCHANGED: &str =
    "The saved configuration could not be migrated to the current schema. It is unchanged.";
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
        // A schema-less legacy profile reaches a save only when the open
        // left it unmigrated; it and every preserved state stay preserved.
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
/// This is the read-only half: it decides whether the open writes a
/// migration, and reads nothing but the configuration documents and the
/// record. Everything else is left exactly as it is for the store's own
/// reader to admit or refuse: an absent aggregate (the first save
/// materialises it), a recorded or preserved state, an aggregate managed
/// would quarantine, and one this service could not read back once migrated.
/// `Storage::open_with` keeps its original order for those, and admits the
/// override document and the journal before [`Planned::write`] only when
/// there is a migration to write.
///
/// The caller has already refused a recorded transaction and pending
/// override or journal evidence. The record guard below repeats that check
/// for this function's own sake; at its one call site it cannot change the
/// outcome, which holds only while the call stays after those checks.
pub(super) fn plan_migration(
    files: &dyn Files,
    user_sid: &str,
    same_path: fn(&str, &str) -> bool,
) -> io::Result<Option<Planned>> {
    if files.read(configuration::RECOVERY_REQUIRED)?.is_some() {
        return Ok(None);
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
        _ => return Ok(None),
    };
    let (_, validator) = validator();
    let Ok(Some(migration)) = migrate::migrate(stored, &validator) else {
        return Ok(None);
    };
    let intended = migration.documents(sha256);
    if !reopens(&intended, same_path) {
        return Ok(None);
    }
    // Managed quarantines an aggregate whose migration does not read back:
    // `VerifyIntendedAggregateAsync` throws `InvalidDataException`, which
    // `LoadCoreAsync` turns into a quarantine (`BatteryConfigurationStore.cs`
    // `:914-932`, `:403-438`, `IsRecoverable` `:1344-1349`). It lasts while
    // that user's configuration stays active. The same change of the same
    // user's store is refused the same way without writing again.
    if held(user_sid, &change_id(&prior, &intended)) {
        return Err(io::Error::other(QUARANTINE_BLOCKED));
    }
    Ok(Some(Planned {
        prior,
        intended,
        migration,
        user_sid: user_sid.to_owned(),
    }))
}

/// A migration the open writes once it has admitted everything else.
pub(super) struct Planned {
    prior: [Option<Vec<u8>>; 3],
    intended: [Vec<u8>; 3],
    migration: Migration,
    user_sid: String,
}

impl Planned {
    /// The save's recorded transaction: the same backups, record,
    /// verification and restore, so the original bytes stay on disk until
    /// the migrated ones are verified.
    ///
    /// The open admits the override document and the journal between the
    /// plan and this write. Documents another writer changed in that window
    /// are not this plan's prior: the transaction would back up and, on a
    /// failure, restore the planned bytes over the newer ones. So they are
    /// left as they are and the open fails unchanged; the next open plans
    /// from what is stored then.
    pub(super) fn write(self, files: &dyn Files) -> io::Result<()> {
        if !matches!(read_documents(files), Ok(documents) if documents == self.prior) {
            return Err(io::Error::other(MIGRATION_UNCHANGED));
        }
        let (_, validator) = validator();
        let transaction = Transaction::new(
            files,
            &self.prior,
            self.intended,
            &self.migration.profiles,
            &self.migration.preferences,
            &validator,
        );
        transaction.run().map_err(|failure| {
            io::Error::other(match failure {
                Failure::Mismatched => {
                    hold(&self.user_sid, &transaction.id);
                    QUARANTINE_BLOCKED
                }
                Failure::RecoveryRequired => MIGRATION_RECOVERY_REQUIRED,
                Failure::StageBlocked | Failure::Unchanged | Failure::Restored => {
                    MIGRATION_UNCHANGED
                }
            })
        })
    }
}

thread_local! {
    /// The user whose migration this thread wrote, read back as something
    /// else and restored, and that change: the digest of the stored and the
    /// migrated bytes. Managed holds such a quarantine while that user's
    /// configuration stays active and loads it again when the user activates
    /// after another activates successfully (`ServiceControlRuntime.cs`
    /// `:1456-1487`); here another user's successful open forgets it
    /// ([`opened`]). The store's owner is the service runtime, which stays
    /// on one thread (`Rc`). One entry at most.
    static HELD: std::cell::RefCell<Option<(String, String)>> =
        const { std::cell::RefCell::new(None) };
}

/// A restart: this thread forgets the held change.
#[cfg(test)]
fn forget_mismatches() {
    HELD.set(None);
}

/// Whether `user_sid`'s store holds `id`.
fn held(user_sid: &str, id: &str) -> bool {
    HELD.with_borrow(|entry| {
        entry
            .as_ref()
            .is_some_and(|(sid, held)| sid == user_sid && held == id)
    })
}

/// `user_sid`'s store opened: another user's hold is forgotten, as managed
/// loads that user's configuration again once this user has activated. A
/// refused open, and this user's own, leave it.
pub(super) fn opened(user_sid: &str) {
    HELD.with_borrow_mut(|entry| {
        if entry.as_ref().is_some_and(|(sid, _)| sid != user_sid) {
            *entry = None;
        }
    });
}

/// Holds `id` for `user_sid`'s store, in place of anything held before.
fn hold(user_sid: &str, id: &str) {
    HELD.set(Some((user_sid.to_owned(), id.to_owned())));
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

/// The digest of a change from `prior` to `intended`, as the transaction
/// names its artifacts with it.
fn change_id(prior: &[Option<Vec<u8>>; 3], intended: &[Vec<u8>; 3]) -> String {
    let mut change = Sha256::new();
    for bytes in prior.iter().flatten().chain(intended) {
        change.update((bytes.len() as u64).to_le_bytes());
        change.update(bytes);
    }
    format!("{:x}", change.finalize())[..32].to_owned()
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
        Self {
            files,
            prior,
            id: change_id(prior, &intended),
            intended,
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

    /// Managed `VerifyIntendedAggregateAsync`: whether the store holds exactly
    /// the intended documents and they read back as the admitted aggregate.
    /// A read that fails is an error, as managed's I/O exception is.
    fn verify(&self) -> io::Result<bool> {
        Ok(
            read_committed(self.files, self.validator)?.is_some_and(|stored| {
                stored.profiles == *self.admitted
                    && stored.preferences == *self.preferences
                    && !stored.migrated
                    && stored
                        .documents
                        .iter()
                        .zip(&self.intended)
                        .all(|(stored, intended)| stored.as_ref() == Some(intended))
            }),
        )
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
        let replaced = match self.replace().and_then(|()| self.verify()) {
            Ok(true) => Ok(()),
            Ok(false) => Err(Failure::Mismatched),
            Err(_) => Err(Failure::Restored),
        };
        let settled = match replaced {
            Ok(()) => Ok(()),
            Err(failure) => self
                .restore()
                .map_err(|_| Failure::RecoveryRequired)
                .and(Err(failure)),
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
    /// As `Restored`, but the replacement was read back as other than the
    /// intended aggregate: managed's verification failure.
    Mismatched,
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
        Ok(
            match self.commit(&mut state, &saved, &admitted, None, &validator, UNREADABLE) {
                Ok(()) => ProfileSave::Stored,
                Err(message) => ProfileSave::Rejected(message.into()),
            },
        )
    }

    /// Commits `profiles` with `preferences` as one aggregate over `saved`,
    /// as managed `SaveProfilesAsync` and `SaveConfigurationAsync` write it,
    /// and reads it back before it counts. Without `preferences` managed keeps
    /// the committed preference bytes, unless loading them migrated
    /// something; then it writes them again. `unreadable` is the refusal when
    /// this service's own reader would refuse the aggregate once stored.
    fn commit(
        &self,
        state: &mut State,
        saved: &Committed,
        profiles: &ProfileSet,
        preferences: Option<&Preferences>,
        validator: &Validator,
        unreadable: &'static str,
    ) -> Result<(), &'static str> {
        let profile = encode::profile_document(profile_set::CURRENT_SCHEMA, profiles);
        let preference_bytes = match (preferences, &saved.documents[1]) {
            (Some(staged), _) => preferences_document(profile_set::CURRENT_SCHEMA, staged),
            (None, Some(bytes)) if !saved.migrated => bytes.clone(),
            (None, _) => preferences_document(profile_set::CURRENT_SCHEMA, &saved.preferences),
        };
        let marker = commit_marker(
            profile_set::CURRENT_SCHEMA,
            &sha256(&profile),
            &sha256(&preference_bytes),
        );
        let intended = [profile, preference_bytes, marker];
        // Managed `ValidatePreferences` and this service's own reader: the
        // staged aggregate must read back before anything is written.
        if !reopens(&intended, self.same_path) {
            return Err(unreadable);
        }
        Transaction::new(
            self.files.as_ref(),
            &saved.documents,
            intended,
            profiles,
            preferences.unwrap_or(&saved.preferences),
            validator,
        )
        .run()
        .map_err(|failure| settle_failure(state, failure))
    }

    /// The saved thermal control policy and disclosure state, as managed
    /// `PublishThermalConvergenceStatus` reports them. A preserved aggregate,
    /// or a configuration this service failed to replace or saw change, is
    /// quarantined, as managed quarantines its configuration; an absent one
    /// is managed's safe defaults. A journal or battery fence leaves the
    /// saved policy standing: it is pending reconciliation (`pending`), as
    /// managed's pending write confirmations are.
    #[cfg(feature = "service-profile-triggers-v1")]
    pub(in super::super) fn thermal_policy(&self) -> io::Result<SavedPolicy> {
        Ok(match self.admitted_preferences()? {
            None => SavedPolicy::Quarantined,
            Some(preferences) => SavedPolicy::Admitted {
                policy: preferences.thermal_control_policy,
                disclosure: preferences.thermal_policy_upgrade_disclosure,
            },
        })
    }

    /// Managed `SetThermalControlPolicyAsync` and
    /// `AcknowledgeThermalPolicyUpgradeDisclosureAsync` from the stored
    /// preferences to the store (`save_preferences`).
    #[cfg(feature = "service-profile-triggers-v1")]
    pub(in super::super) fn save_thermal_policy(
        &self,
        change: PolicyChange,
    ) -> io::Result<PolicySave> {
        use crate::service_status::thermal_convergence::{
            DISCLOSURE_ACKNOWLEDGED, DISCLOSURE_PENDING,
        };
        self.save_preferences(|staged| match change {
            PolicyChange::Set(policy) if staged.thermal_control_policy == policy => false,
            PolicyChange::Set(policy) => {
                staged.thermal_control_policy = policy;
                true
            }
            PolicyChange::Acknowledge
                if staged.thermal_policy_upgrade_disclosure != DISCLOSURE_PENDING =>
            {
                false
            }
            PolicyChange::Acknowledge => {
                staged.thermal_policy_upgrade_disclosure = DISCLOSURE_ACKNOWLEDGED;
                true
            }
        })
    }

    /// The saved Fan Stop settings, as managed projects
    /// `preferences.FanStopSettings`: managed's defaults where the aggregate
    /// is quarantined (`BatteryConfigurationStore` quarantine preferences).
    #[cfg(feature = "service-fan-stop-v1")]
    pub(in super::super) fn fan_stop_settings(&self) -> io::Result<FanStopSettings> {
        Ok(self
            .admitted_preferences()?
            .and_then(|preferences| preferences.fan_stop_settings)
            .unwrap_or_default())
    }

    /// Managed `ConfigurationIsAdmitted`: the aggregate is committed and this
    /// service neither failed to replace it nor saw it change.
    #[cfg(feature = "service-fan-stop-v1")]
    pub(in super::super) fn configuration_admitted(&self) -> io::Result<bool> {
        Ok(self.admitted_preferences()?.is_some())
    }

    /// Managed `SetFanStopSettingsAsync` from the stored preferences to the
    /// store (`save_preferences`). Settings equal to the saved ones are not
    /// written: managed's "already saved" reply.
    #[cfg(feature = "service-fan-stop-v1")]
    pub(in super::super) fn save_fan_stop_settings(
        &self,
        settings: FanStopSettings,
    ) -> io::Result<PreferencesSave> {
        self.save_preferences(|staged| {
            if staged.fan_stop_settings == Some(settings) {
                return false;
            }
            staged.fan_stop_settings = Some(settings);
            true
        })
    }

    /// Managed `SetBatterySaverFanStopAsync`'s store: the Battery profile's
    /// `stopFansInBatterySaver` saved with the committed preference bytes, as
    /// managed `SaveProfilesAsync` writes it, only when it changes. Managed
    /// refuses the request with an inadmissible configuration first, so a
    /// journal or battery fence does not refuse it (`configuration_quarantined`).
    #[cfg(feature = "service-fan-stop-v1")]
    pub(in super::super) fn save_battery_saver_fan_stop(
        &self,
        enabled: bool,
    ) -> io::Result<PreferencesSave> {
        let mut state = self.state.lock().map_err(|_| denied())?;
        if configuration_quarantined(&state) {
            return Ok(PreferencesSave::Quarantined);
        }
        let (_, validator) = validator();
        let Some(saved) = committed(self.files.as_ref(), &validator)? else {
            return Ok(PreferencesSave::Quarantined);
        };
        if saved.profiles.battery.stop_fans_in_battery_saver == enabled {
            return Ok(PreferencesSave::Unchanged(saved.preferences));
        }
        let mut profiles = saved.profiles.clone();
        profiles.battery.stop_fans_in_battery_saver = enabled;
        Ok(
            match self.commit(&mut state, &saved, &profiles, None, &validator, UNREADABLE) {
                Ok(()) => PreferencesSave::Stored(saved.preferences),
                Err(message) => PreferencesSave::Rejected(message.into()),
            },
        )
    }

    /// The committed preferences, `None` where the aggregate is preserved or
    /// this service failed to replace it or saw it change.
    #[cfg(any(
        feature = "service-profile-triggers-v1",
        feature = "service-fan-stop-v1"
    ))]
    fn admitted_preferences(&self) -> io::Result<Option<Preferences>> {
        let state = self.state.lock().map_err(|_| denied())?;
        if configuration_quarantined(&state) {
            return Ok(None);
        }
        let (_, validator) = validator();
        Ok(committed(self.files.as_ref(), &validator)?.map(|saved| saved.preferences))
    }

    /// One managed preference request from the stored preferences to the
    /// store: `edit` stages the change on a copy and says whether it changed
    /// anything; a change that leaves them as they are is not written.
    /// Otherwise the staged preferences and the saved profiles are committed
    /// as one aggregate, as managed `SaveConfigurationAsync` writes them
    /// (`commit`). Managed admits the change with an admitted configuration
    /// only, so a journal or battery fence does not refuse it
    /// (`configuration_quarantined`).
    #[cfg(any(
        feature = "service-profile-triggers-v1",
        feature = "service-fan-stop-v1"
    ))]
    fn save_preferences(
        &self,
        edit: impl FnOnce(&mut Preferences) -> bool,
    ) -> io::Result<PreferencesSave> {
        let mut state = self.state.lock().map_err(|_| denied())?;
        if configuration_quarantined(&state) {
            return Ok(PreferencesSave::Quarantined);
        }
        let (_, validator) = validator();
        let Some(saved) = committed(self.files.as_ref(), &validator)? else {
            return Ok(PreferencesSave::Quarantined);
        };
        let mut staged = saved.preferences.clone();
        if !edit(&mut staged) {
            return Ok(PreferencesSave::Unchanged(staged));
        }
        Ok(
            match self.commit(
                &mut state,
                &saved,
                &saved.profiles,
                Some(&staged),
                &validator,
                PREFERENCES_UNREADABLE,
            ) {
                Ok(()) => PreferencesSave::Stored(staged),
                Err(message) => PreferencesSave::Rejected(message.into()),
            },
        )
    }
}

/// Whether this service failed to replace the configuration or saw it
/// change: managed's configuration quarantine. A journal or battery fence
/// fences thermal writes only; the configuration itself stays admitted.
#[cfg(any(
    feature = "service-profile-triggers-v1",
    feature = "service-fan-stop-v1"
))]
fn configuration_quarantined(state: &State) -> bool {
    state.configuration_failed || state.configuration_changed
}

/// The reply for a failed transaction. One whose restore could not be
/// confirmed quarantines the aggregate, as managed's does.
fn settle_failure(state: &mut State, failure: Failure) -> &'static str {
    match failure {
        Failure::StageBlocked => STAGE_BLOCKED,
        Failure::Unchanged | Failure::Restored | Failure::Mismatched => RESTORED,
        Failure::RecoveryRequired => {
            state.quarantined = true;
            state.configuration_failed = true;
            RECOVERY_REQUIRED
        }
    }
}

/// `Storage::thermal_policy`.
#[cfg(feature = "service-profile-triggers-v1")]
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(in super::super) enum SavedPolicy {
    /// The aggregate is preserved: managed reports Strict with no
    /// disclosure (`BatteryConfigurationStore` quarantine preferences).
    Quarantined,
    /// Managed `ThermalControlPolicy` and
    /// `ThermalPolicyUpgradeDisclosureState` values.
    Admitted { policy: i32, disclosure: i32 },
}

/// What a thermal policy request changes.
#[cfg(feature = "service-profile-triggers-v1")]
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(in super::super) enum PolicyChange {
    /// Managed `SetThermalControlPolicyAsync` with a defined policy.
    Set(i32),
    /// Managed `AcknowledgeThermalPolicyUpgradeDisclosureAsync`.
    Acknowledge,
}

/// What a preference save (`Storage::save_preferences`) did.
#[cfg(any(
    feature = "service-profile-triggers-v1",
    feature = "service-fan-stop-v1"
))]
#[derive(Debug, PartialEq)]
pub(in super::super) enum PreferencesSave {
    /// The stored configuration already holds the change; nothing was written.
    Unchanged(Preferences),
    /// The change was committed and read back.
    Stored(Preferences),
    /// The store refused it; the message says whether anything is owed.
    Rejected(String),
    /// The aggregate is preserved, so nothing may change it.
    Quarantined,
}

/// What `Storage::save_thermal_policy` did.
#[cfg(feature = "service-profile-triggers-v1")]
pub(in super::super) type PolicySave = PreferencesSave;

#[cfg(test)]
#[path = "profile_storage_tests.rs"]
mod tests;
