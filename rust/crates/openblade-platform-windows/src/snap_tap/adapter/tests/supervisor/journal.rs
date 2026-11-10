use std::fs::{File, OpenOptions};
use std::io::{self, Read, Write};
use std::os::windows::fs::OpenOptionsExt;
use std::path::Path;

const HEADER: &str = "openblade-inert-snaptap-v1\n";
const MAX_BYTES: usize = 64 * 1024;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) struct Helper {
    pub generation: u64,
    pub pid: u32,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) enum Phase {
    Reserved,
    Gated,
    Armed,
    NoWrite,
    Retired,
}

impl Phase {
    fn label(self) -> &'static str {
        match self {
            Self::Reserved => "reserved",
            Self::Gated => "gated",
            Self::Armed => "armed",
            Self::NoWrite => "no-write+exit-ok+published",
            Self::Retired => "off+unhooked+timer-retired+exit-ok+published",
        }
    }

    fn released(self) -> bool {
        matches!(self, Self::NoWrite | Self::Retired)
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
struct Record {
    helper: Helper,
    phase: Phase,
}

impl Record {
    fn encode(self) -> String {
        format!(
            "{} {} {}\n",
            self.helper.generation,
            self.helper.pid,
            self.phase.label()
        )
    }

    fn decode(line: &str) -> Option<Self> {
        let mut fields = line.split(' ');
        let generation = fields.next()?.parse().ok()?;
        let pid = fields.next()?.parse().ok()?;
        let label = fields.next()?;
        let phase = [
            Phase::Reserved,
            Phase::Gated,
            Phase::Armed,
            Phase::NoWrite,
            Phase::Retired,
        ]
        .into_iter()
        .find(|phase| phase.label() == label)?;
        let record = Self {
            helper: Helper { generation, pid },
            phase,
        };
        (record.encode().strip_suffix('\n') == Some(line)).then_some(record)
    }

    fn follows(self, prior: Option<Self>) -> bool {
        match self.phase {
            Phase::Reserved => {
                self.helper.pid == 0
                    && self.helper.generation != 0
                    && prior.is_none_or(|prior| {
                        prior.phase.released() && self.helper.generation > prior.helper.generation
                    })
            }
            Phase::Gated => {
                self.helper.pid != 0
                    && prior.is_some_and(|prior| {
                        prior.phase == Phase::Reserved
                            && prior.helper.generation == self.helper.generation
                    })
            }
            Phase::Armed | Phase::NoWrite => prior
                .is_some_and(|prior| prior.phase == Phase::Gated && prior.helper == self.helper),
            Phase::Retired => prior
                .is_some_and(|prior| prior.phase == Phase::Armed && prior.helper == self.helper),
        }
    }
}

// One exclusive append-only file in a trusted scratch directory. Never replace
// or delete it to release ownership. A short/torn record cannot become a proof.
pub(super) struct Journal {
    file: File,
    current: Option<Record>,
    length: usize,
}

impl Journal {
    pub fn create(path: &Path) -> io::Result<Self> {
        let mut file = Self::options().create_new(true).open(path)?;
        file.write_all(HEADER.as_bytes())?;
        file.sync_all()?;
        Ok(Self {
            file,
            current: None,
            length: HEADER.len(),
        })
    }

    pub fn reopen(path: &Path) -> io::Result<Self> {
        let mut file = Self::options().open(path)?;
        let mut contents = String::new();
        let length = (&mut file)
            .take((MAX_BYTES + 1) as u64)
            .read_to_string(&mut contents)?;
        let body = contents
            .strip_prefix(HEADER)
            .filter(|_| length > HEADER.len() && length <= MAX_BYTES && contents.ends_with('\n'))
            .ok_or_else(invalid)?;
        let mut current = None;
        for line in body.split_terminator('\n') {
            let record = Record::decode(line)
                .filter(|record| record.follows(current))
                .ok_or_else(invalid)?;
            current = Some(record);
        }
        // A complete line can survive a prior process's failed flush. Require
        // a fresh successful flush before using any parsed completion proof.
        file.sync_all()?;
        Ok(Self {
            file,
            current,
            length,
        })
    }

    pub fn unresolved(&self) -> bool {
        self.current.is_some_and(|record| !record.phase.released())
    }

    pub fn can_reserve(&self, generation: u64) -> bool {
        Record {
            helper: Helper { generation, pid: 0 },
            phase: Phase::Reserved,
        }
        .follows(self.current)
    }

    pub fn append(&mut self, helper: Helper, phase: Phase) -> io::Result<()> {
        let record = Record { helper, phase };
        let line = record.encode();
        if !record.follows(self.current) || self.length + line.len() > MAX_BYTES {
            return Err(invalid());
        }
        self.file.write_all(line.as_bytes())?;
        self.file.sync_all()?;
        self.current = Some(record);
        self.length += line.len();
        Ok(())
    }

    fn options() -> OpenOptions {
        let mut options = OpenOptions::new();
        options.read(true).write(true).share_mode(0);
        options
    }
}

fn invalid() -> io::Error {
    io::Error::new(
        io::ErrorKind::InvalidData,
        "invalid or conflicting SnapTap fixture journal",
    )
}
