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

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum JournalEntry<'a> {
    Missing,
    Paused,
    /// One entry from a host-validated v1 document (at most four unique controls).
    Valid {
        identity: &'a str,
        revision: JournalRevision,
        entry: PendingEntry,
    },
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum LaptopObservation {
    NotRead,
    Unavailable,
    Read(LaptopValue),
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
pub enum LaptopRecovery {
    NoPendingEntry,
    Pause(PauseReason),
    ReadOnly(LaptopGetter),
    /// The host may publish the observation independently of the journal save.
    /// The entry is still pending until the proposed change is durably committed.
    Settle {
        expected_revision: JournalRevision,
        requested: LaptopValue,
        observed: Option<LaptopValue>,
        change: EntryChange,
    },
}

/// Evaluate one entry in restart or ordinary read-only reconciliation.
/// The observation must come from the freshly verified device for this operation.
/// The managed journal uses exact device identity, with machine name as provenance.
pub fn recover_laptop_entry(
    journal: JournalEntry<'_>,
    fresh_identity: Option<&str>,
    observation: LaptopObservation,
) -> LaptopRecovery {
    let (identity, revision, entry) = match journal {
        JournalEntry::Missing => return LaptopRecovery::NoPendingEntry,
        JournalEntry::Paused => return LaptopRecovery::Pause(PauseReason::JournalUnavailable),
        JournalEntry::Valid {
            identity,
            revision,
            entry,
        } => (identity, revision, entry),
    };
    if !entry.reconciliation_required {
        return LaptopRecovery::NoPendingEntry;
    }
    if fresh_identity != Some(identity) {
        return LaptopRecovery::Pause(PauseReason::ExactIdentityUnavailable);
    }
    resolve_readback(revision, entry.requested, observation)
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum LiveCompletion {
    Acknowledged(LaptopObservation),
    MayHaveSubmitted,
}

/// The acknowledgement must belong to the live executor's persisted intent.
/// A possible submission leaves the entry pending for a separate observation.
pub fn complete_laptop_intent(
    intent_revision: JournalRevision,
    requested: LaptopValue,
    completion: LiveCompletion,
) -> LaptopRecovery {
    match completion {
        LiveCompletion::MayHaveSubmitted => LaptopRecovery::Pause(PauseReason::PossibleSubmission),
        LiveCompletion::Acknowledged(_) if requested.getter().is_none() => LaptopRecovery::Settle {
            expected_revision: intent_revision,
            requested,
            observed: None,
            change: EntryChange::Remove,
        },
        LiveCompletion::Acknowledged(observation) => {
            resolve_readback(intent_revision, requested, observation)
        }
    }
}

fn resolve_readback(
    revision: JournalRevision,
    requested: LaptopValue,
    observation: LaptopObservation,
) -> LaptopRecovery {
    let Some(getter) = requested.getter() else {
        return LaptopRecovery::Pause(PauseReason::WriteOnly);
    };
    match observation {
        LaptopObservation::NotRead => LaptopRecovery::ReadOnly(getter),
        LaptopObservation::Read(observed) if observed.getter() == Some(getter) => {
            LaptopRecovery::Settle {
                expected_revision: revision,
                requested,
                observed: Some(observed),
                change: if requested == observed {
                    EntryChange::Remove
                } else {
                    EntryChange::KeepTombstone
                },
            }
        }
        _ => LaptopRecovery::Pause(PauseReason::ReadbackUnavailable),
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum JournalLoadRevision {
    Paused,
    Readable(Option<JournalRevision>),
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum NonSubmissionCleanup {
    KeepProof,
    ForgetProof,
    RestorePriorDocument { expected_revision: JournalRevision },
}

/// Only call with process-local proof that submission did not begin, including
/// cancellation before submission. Retain proof until restore succeeds or a fresh
/// load shows different bytes. The host retains the exact prior document.
pub fn clean_up_known_non_submission(
    intent_revision: JournalRevision,
    loaded: JournalLoadRevision,
) -> NonSubmissionCleanup {
    match loaded {
        JournalLoadRevision::Paused => NonSubmissionCleanup::KeepProof,
        JournalLoadRevision::Readable(Some(current)) if current == intent_revision => {
            NonSubmissionCleanup::RestorePriorDocument {
                expected_revision: current,
            }
        }
        JournalLoadRevision::Readable(_) => NonSubmissionCleanup::ForgetProof,
    }
}

/// A settled tombstone suppresses the same automatic request. A new explicit
/// request needs ordinary host admission and a new write-ahead save.
pub fn tombstone_blocks_automatic(entry: PendingEntry, requested: LaptopValue) -> bool {
    !entry.reconciliation_required && entry.requested == requested
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

/// Mirrors ThermalTransitionExecutor's scoped trace classification. Fresh
/// readback can resolve state while preserving possible-submission chronology.
pub fn classify_scoped_thermal(outcome: ScopedThermalOutcome) -> ThermalCompletion {
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
