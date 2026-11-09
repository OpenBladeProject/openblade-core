//! Standard battery policy for the isolated runtime. All evidence and effects
//! are supplied by the host. Temporary full charge is only an eligibility input.

use openblade_domain::recovery::{ScopedStatus, SubmissionCertainty};

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct Limit(i32);

impl Limit {
    pub fn new(percent: i32) -> Option<Self> {
        (50..=99).contains(&percent).then_some(Self(percent))
    }

    pub fn percent(self) -> i32 {
        self.0
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct Desired {
    pub limit: Limit,
    pub protection: bool,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct Confirmed {
    pub limit: Option<Limit>,
    pub temporary: Option<bool>,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Intent {
    Limit(Limit),
    Protection(bool),
    Profile,
}

impl Intent {
    pub fn stage(self, desired: Desired) -> Desired {
        match self {
            Self::Limit(limit) => Desired { limit, ..desired },
            Self::Protection(protection) => Desired {
                protection,
                ..desired
            },
            Self::Profile => desired,
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Command {
    SetLimit(Limit),
    ProtectionOff,
}

#[derive(Clone, Copy)]
pub struct Capabilities<'a> {
    pub limits: &'a [i32],
    pub protection_off: bool,
    pub temporary: bool,
}

impl Capabilities<'_> {
    fn admitted(self, limit: Limit) -> bool {
        self.limits.contains(&limit.percent())
    }

    fn policy_admitted(self, read: Confirmed) -> bool {
        read.temporary == Some(false) || (read.temporary.is_none() && !self.temporary)
    }
}

#[derive(Debug, Eq, PartialEq)]
pub enum Plan {
    Blocked,
    Unchanged,
    Write(Command),
}

pub fn plan(
    intent: Intent,
    staged: Desired,
    capabilities: Capabilities<'_>,
    observed: Option<Confirmed>,
) -> Plan {
    let Some(read) = observed.filter(|read| capabilities.policy_admitted(*read)) else {
        return Plan::Blocked;
    };
    let limit = staged.limit;
    let current = read.limit;
    let set_limit = || {
        if !capabilities.admitted(limit) || !current.is_some_and(|v| capabilities.admitted(v)) {
            Plan::Blocked
        } else if current == Some(limit) {
            Plan::Unchanged
        } else {
            Plan::Write(Command::SetLimit(limit))
        }
    };
    let protection = |enabled| match (enabled, current) {
        (false, None) => Plan::Unchanged,
        (false, Some(previous))
            if capabilities.protection_off && capabilities.admitted(previous) =>
        {
            Plan::Write(Command::ProtectionOff)
        }
        (true, None) if capabilities.admitted(limit) => Plan::Write(Command::SetLimit(limit)),
        (true, Some(previous)) if previous == limit && capabilities.admitted(limit) => {
            Plan::Unchanged
        }
        _ => Plan::Blocked,
    };
    match intent {
        Intent::Limit(_) => set_limit(),
        Intent::Protection(enabled) => protection(enabled),
        Intent::Profile if staged.protection != current.is_some() => protection(staged.protection),
        Intent::Profile if staged.protection && current != Some(limit) => set_limit(),
        Intent::Profile => Plan::Unchanged,
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Confirmation {
    Unavailable,
    Mismatch,
    Match,
}

/// Only policy fields are the target of these two setters. A missing temporary
/// flag on a query-capable model prevents live success but can be ignored in
/// later read-only confirmation of the recorded policy scope.
pub fn confirm(
    command: Command,
    read: Option<Confirmed>,
    capabilities: Capabilities<'_>,
) -> Confirmation {
    let Some(read) = read else {
        return Confirmation::Unavailable;
    };
    if capabilities.temporary && read.temporary.is_none() {
        return Confirmation::Unavailable;
    }
    let expected = match command {
        Command::SetLimit(limit) => Some(limit),
        Command::ProtectionOff => None,
    };
    if read.limit == expected && read.temporary != Some(true) {
        Confirmation::Match
    } else {
        Confirmation::Mismatch
    }
}

#[derive(Debug, Eq, PartialEq)]
pub enum Submission {
    Refused,
    CanceledBeforeSet,
    Failed(SubmissionCertainty),
    Acknowledged,
}

#[derive(Debug, Eq, PartialEq)]
pub struct Outcome {
    pub status: ScopedStatus,
    pub needs_confirmation: bool,
}

pub fn complete(submission: Submission, readback: Confirmation) -> Outcome {
    let (status, needs_confirmation) = match (submission, readback) {
        (Submission::Refused, _) => (ScopedStatus::Blocked, false),
        (Submission::CanceledBeforeSet, _) => (ScopedStatus::Failed, false),
        (Submission::Failed(SubmissionCertainty::KnownNotApplied), _) => {
            (ScopedStatus::Failed, false)
        }
        (Submission::Failed(SubmissionCertainty::Indeterminate), _) => (ScopedStatus::Failed, true),
        (Submission::Acknowledged, Confirmation::Match) => (ScopedStatus::Applied, false),
        (Submission::Acknowledged, Confirmation::Unavailable) => (ScopedStatus::Failed, true),
        (Submission::Acknowledged, Confirmation::Mismatch) => (ScopedStatus::Failed, false),
    };
    Outcome {
        status,
        needs_confirmation,
    }
}
