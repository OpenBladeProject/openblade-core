//! Pure recovery decisions after journal loading or a scoped thermal attempt.
//! Serialization, admission, readback freshness and durable commit belong to the host.

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum FunctionKeyMode {
    FunctionKeys,
    MultimediaKeys,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum LaptopValue {
    StartupAnimation(bool),
    FunctionKeyPrimary(FunctionKeyMode),
    GamingMode(bool),
    MicrophoneMuteIndicator(bool),
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum LaptopGetter {
    StartupAnimation,
    FunctionKeyPrimary,
    GamingMode,
}

impl LaptopValue {
    fn getter(self) -> Option<LaptopGetter> {
        match self {
            Self::StartupAnimation(_) => Some(LaptopGetter::StartupAnimation),
            Self::FunctionKeyPrimary(_) => Some(LaptopGetter::FunctionKeyPrimary),
            Self::GamingMode(_) => Some(LaptopGetter::GamingMode),
            Self::MicrophoneMuteIndicator(_) => None,
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct PendingEntry {
    pub requested: LaptopValue,
    pub reconciliation_required: bool,
}

/// SHA-256 of the exact loaded bytes, compared again by the host at commit.
pub type JournalRevision = [u8; 32];

const MAX_ENTRIES: usize = 4;

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum LaptopJournal<'a> {
    Missing,
    Paused,
    /// The host validates the v1 document, including exact identity and unique controls.
    Valid {
        identity: &'a str,
        revision: JournalRevision,
        entries: &'a [PendingEntry],
    },
}

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct LaptopReadback {
    pub startup_animation: Option<bool>,
    pub function_key_primary: Option<FunctionKeyMode>,
    pub gaming_mode: Option<bool>,
}

impl LaptopReadback {
    fn value(self, getter: LaptopGetter) -> Option<LaptopValue> {
        match getter {
            LaptopGetter::StartupAnimation => {
                self.startup_animation.map(LaptopValue::StartupAnimation)
            }
            LaptopGetter::FunctionKeyPrimary => self
                .function_key_primary
                .map(LaptopValue::FunctionKeyPrimary),
            LaptopGetter::GamingMode => self.gaming_mode.map(LaptopValue::GamingMode),
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum PauseReason {
    JournalUnavailable,
    ExactIdentityUnavailable,
    ReadbackUnavailable,
    WriteOnly,
    PossibleSubmission,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum EntryChange {
    Remove,
    KeepTombstone,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct EntrySettlement {
    pub requested: LaptopValue,
    pub observed: Option<LaptopValue>,
    pub change: EntryChange,
}

impl EntrySettlement {
    fn observed(requested: LaptopValue, observed: LaptopValue) -> Self {
        Self {
            requested,
            observed: Some(observed),
            change: if requested == observed {
                EntryChange::Remove
            } else {
                EntryChange::KeepTombstone
            },
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct LaptopSettlement {
    pub expected_revision: JournalRevision,
    /// Save all changes together. The observations may be published independently.
    /// No entry is settled until the compare-and-save succeeds.
    pub changes: [Option<EntrySettlement>; MAX_ENTRIES],
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum NonSubmissionCleanup {
    KeepProof,
    ForgetProof,
    RestorePriorDocument { expected_revision: JournalRevision },
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum LaptopRecoveryPlan<'a> {
    NoPendingEntries,
    Pause(PauseReason),
    StorageOnly(NonSubmissionCleanup),
    ReadOnly(LaptopReadPlan<'a>),
}

/// One read-only pass over a single validated journal revision.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct LaptopReadPlan<'a> {
    revision: JournalRevision,
    entries: &'a [PendingEntry],
}

impl LaptopReadPlan<'_> {
    fn readable_entries(&self) -> impl Iterator<Item = (LaptopValue, LaptopGetter)> + '_ {
        self.entries.iter().filter_map(|entry| {
            if !entry.reconciliation_required {
                return None;
            }
            Some((entry.requested, entry.requested.getter()?))
        })
    }

    pub fn getters(&self) -> impl Iterator<Item = LaptopGetter> + '_ {
        self.readable_entries().map(|(_, getter)| getter)
    }

    /// Use only readings produced by this plan on the freshly verified device.
    /// Unavailable values and write-only entries remain pending.
    pub fn finish(self, readback: LaptopReadback) -> Option<LaptopSettlement> {
        let mut changes = [None; MAX_ENTRIES];
        let confirmed = self.readable_entries().filter_map(|(request, getter)| {
            Some(EntrySettlement::observed(request, readback.value(getter)?))
        });
        for (slot, settlement) in changes.iter_mut().zip(confirmed) {
            *slot = Some(settlement);
        }
        changes[0].is_some().then_some(LaptopSettlement {
            expected_revision: self.revision,
            changes,
        })
    }
}

/// Cleanup takes the entire pass when live non-submission proof is present.
/// The host retains the exact prior document and keeps proof until its restore
/// commits or a fresh load shows different bytes. Restart has no such proof.
pub fn plan_laptop_recovery<'a>(
    journal: LaptopJournal<'a>,
    fresh_identity: Option<&str>,
    known_unsubmitted_revision: Option<JournalRevision>,
) -> LaptopRecoveryPlan<'a> {
    if let Some(expected) = known_unsubmitted_revision {
        let cleanup = match journal {
            LaptopJournal::Paused => NonSubmissionCleanup::KeepProof,
            LaptopJournal::Valid { revision, .. } if revision == expected => {
                NonSubmissionCleanup::RestorePriorDocument {
                    expected_revision: revision,
                }
            }
            _ => NonSubmissionCleanup::ForgetProof,
        };
        return LaptopRecoveryPlan::StorageOnly(cleanup);
    }

    let (identity, revision, entries) = match journal {
        LaptopJournal::Missing => return LaptopRecoveryPlan::NoPendingEntries,
        LaptopJournal::Paused => return LaptopRecoveryPlan::Pause(PauseReason::JournalUnavailable),
        LaptopJournal::Valid {
            identity,
            revision,
            entries,
        } => (identity, revision, entries),
    };
    if entries.len() > MAX_ENTRIES {
        return LaptopRecoveryPlan::Pause(PauseReason::JournalUnavailable);
    }
    if !entries.iter().any(|entry| entry.reconciliation_required) {
        return LaptopRecoveryPlan::NoPendingEntries;
    }
    if fresh_identity != Some(identity) {
        return LaptopRecoveryPlan::Pause(PauseReason::ExactIdentityUnavailable);
    }
    let plan = LaptopReadPlan { revision, entries };
    if plan.getters().next().is_none() {
        return LaptopRecoveryPlan::Pause(PauseReason::WriteOnly);
    }
    LaptopRecoveryPlan::ReadOnly(plan)
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum JournalWriteBlock {
    JournalUnavailable,
    ReconciliationRequired,
    ExactIdentityUnavailable,
    AutomaticReplay,
}

/// Evaluate the journal part of write admission. Successful evaluation still
/// requires protected storage, ordinary host checks and a new write-ahead save.
pub fn check_laptop_journal_for_write(
    journal: LaptopJournal<'_>,
    fresh_identity: Option<&str>,
    requested: LaptopValue,
    explicit_request: bool,
) -> Result<(), JournalWriteBlock> {
    let fresh_identity = fresh_identity
        .filter(|identity| !identity.trim().is_empty())
        .ok_or(JournalWriteBlock::ExactIdentityUnavailable)?;
    let (identity, entries) = match journal {
        LaptopJournal::Missing => return Ok(()),
        LaptopJournal::Paused => return Err(JournalWriteBlock::JournalUnavailable),
        LaptopJournal::Valid {
            identity, entries, ..
        } => (identity, entries),
    };
    if entries.len() > MAX_ENTRIES {
        return Err(JournalWriteBlock::JournalUnavailable);
    }
    if entries.iter().any(|entry| entry.reconciliation_required) {
        return Err(JournalWriteBlock::ReconciliationRequired);
    }
    if !entries.is_empty() && identity != fresh_identity {
        return Err(JournalWriteBlock::ExactIdentityUnavailable);
    }
    if !explicit_request && entries.iter().any(|entry| entry.requested == requested) {
        return Err(JournalWriteBlock::AutomaticReplay);
    }
    Ok(())
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum LiveCompletion {
    Acknowledged(LaptopReadback),
    MayHaveSubmitted,
}

/// The acknowledgement and any readback must belong to the live executor's
/// persisted intent. A possible submission remains pending for another pass.
pub fn complete_laptop_intent(
    intent_revision: JournalRevision,
    requested: LaptopValue,
    completion: LiveCompletion,
) -> Result<LaptopSettlement, PauseReason> {
    let LiveCompletion::Acknowledged(readback) = completion else {
        return Err(PauseReason::PossibleSubmission);
    };
    let settlement = match requested.getter() {
        None => EntrySettlement {
            requested,
            observed: None,
            change: EntryChange::Remove,
        },
        Some(getter) => {
            let observed = readback
                .value(getter)
                .ok_or(PauseReason::ReadbackUnavailable)?;
            EntrySettlement::observed(requested, observed)
        }
    };
    Ok(LaptopSettlement {
        expected_revision: intent_revision,
        changes: [Some(settlement), None, None, None],
    })
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ScopedStatus {
    Applied,
    Unchanged,
    Blocked,
    Failed,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum SubmissionCertainty {
    KnownNotApplied,
    Indeterminate,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum SubmissionProgress {
    NotSubmitted,
    Submitted,
    Acknowledged,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ThermalStage {
    Preflight,
    Submission,
    Acknowledgement,
    Confirmation,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ThermalDisposition {
    Blocked,
    Failed,
    Confirmed,
    Acknowledged,
    ReconciliationUnavailable,
    External,
}

/// Projection of an evaluated scoped result. The host retains the actual
/// baseline and observed values, including partial readings. A baseline in
/// the readback field can contribute to acknowledgement without confirmation.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct ScopedThermalOutcome {
    pub status: ScopedStatus,
    pub certainty: Option<SubmissionCertainty>,
    pub write_attempted: bool,
    pub has_readback: bool,
    pub readback_matched: bool,
    pub confirmation_unavailable: bool,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct ThermalCompletion {
    pub progress: SubmissionProgress,
    pub certainty: Option<SubmissionCertainty>,
    pub stage: ThermalStage,
    pub disposition: ThermalDisposition,
}

/// Mirrors the three base classification methods in ThermalTransitionExecutor.
/// The runner's retry-admission and continuation overrides are separate.
/// Fresh readback can resolve state while preserving submission chronology.
pub fn classify_scoped_thermal_outcome(outcome: ScopedThermalOutcome) -> ThermalCompletion {
    use ScopedStatus::{Applied, Blocked, Unchanged};
    use SubmissionCertainty::{Indeterminate, KnownNotApplied};
    let known_not_applied = outcome.certainty == Some(KnownNotApplied);
    let indeterminate = outcome.certainty == Some(Indeterminate);
    let progress = match outcome.certainty {
        Some(KnownNotApplied) => SubmissionProgress::NotSubmitted,
        Some(Indeterminate) => SubmissionProgress::Submitted,
        None if !outcome.write_attempted => SubmissionProgress::NotSubmitted,
        None if outcome.has_readback || outcome.status == Applied => {
            SubmissionProgress::Acknowledged
        }
        None => SubmissionProgress::Submitted,
    };
    let stage = if known_not_applied || (!outcome.write_attempted && outcome.status == Blocked) {
        ThermalStage::Preflight
    } else if outcome.readback_matched || (indeterminate && outcome.has_readback) {
        ThermalStage::Confirmation
    } else {
        match progress {
            SubmissionProgress::Acknowledged => ThermalStage::Acknowledgement,
            SubmissionProgress::Submitted => ThermalStage::Submission,
            SubmissionProgress::NotSubmitted => ThermalStage::Preflight,
        }
    };
    let disposition = if known_not_applied {
        if outcome.status == Blocked {
            ThermalDisposition::Blocked
        } else {
            ThermalDisposition::Failed
        }
    } else if outcome.readback_matched && matches!(outcome.status, Applied | Unchanged) {
        ThermalDisposition::Confirmed
    } else if outcome.status == Applied && outcome.write_attempted {
        ThermalDisposition::Acknowledged
    } else if outcome.confirmation_unavailable {
        ThermalDisposition::ReconciliationUnavailable
    } else if outcome.has_readback && (outcome.write_attempted || indeterminate) {
        ThermalDisposition::External
    } else if outcome.status == Blocked {
        ThermalDisposition::Blocked
    } else {
        ThermalDisposition::Failed
    };
    ThermalCompletion {
        progress,
        certainty: outcome.certainty,
        stage,
        disposition,
    }
}
