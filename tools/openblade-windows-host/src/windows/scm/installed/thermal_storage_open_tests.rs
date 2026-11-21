//! The order `Storage::open_with` answers in when it writes no migration, on
//! every lane: the aggregate, then the override document, then the journal.

use super::super::thermal_mutation::{fatal_admission, is_fatal_admission};
use super::{Files, Storage};
use std::{
    collections::BTreeMap,
    io,
    sync::{
        Arc, Mutex,
        atomic::{AtomicUsize, Ordering},
    },
};

const SID: &str = "S-1-5-21-111-222-333-1001";
const JOURNAL: &str = "thermal-mutation.journal";
const REFUSED: &str = "Uncommitted or non-current profile aggregate is preserved; managed recovery or migration is required.";

/// In-memory files that fail the journal read as an owner-binding failure
/// does (fatally), or count the journal reads, which authenticate rows and
/// may publish a roll.
#[derive(Default)]
struct Memory {
    bytes: Mutex<BTreeMap<String, Vec<u8>>>,
    fatal_journal: bool,
    journal_reads: Arc<AtomicUsize>,
}

impl Memory {
    /// A lone profile that declares a schema: incomplete to every lane.
    fn refused() -> Self {
        let files = Self::default();
        files.put("profiles.json", br#"{"schemaVersion":24}"#);
        files
    }
    fn put(&self, name: &str, bytes: &[u8]) {
        self.bytes
            .lock()
            .unwrap()
            .insert(name.into(), bytes.to_vec());
    }
}

impl Files for Memory {
    fn read(&self, name: &str) -> io::Result<Option<Vec<u8>>> {
        if name == JOURNAL {
            self.journal_reads.fetch_add(1, Ordering::AcqRel);
            if self.fatal_journal {
                return Err(fatal_admission("journal owner binding failed"));
            }
        }
        Ok(self.bytes.lock().unwrap().get(name).cloned())
    }
    fn replace(&self, name: &str, _expected: Option<&[u8]>, bytes: &[u8]) -> io::Result<()> {
        self.put(name, bytes);
        Ok(())
    }
}

/// A refused aggregate beside a journal that would fail fatally is a
/// non-fatal refusal, and the journal is never read.
#[test]
fn a_refused_aggregate_is_answered_before_the_override_or_the_journal() {
    let reads = Arc::new(AtomicUsize::new(0));
    let files = Memory {
        fatal_journal: true,
        journal_reads: reads.clone(),
        ..Memory::refused()
    };
    let error = Storage::open(Box::new(files), SID).err().unwrap();
    assert!(!is_fatal_admission(&error));
    assert_eq!(error.to_string(), REFUSED);
    assert_eq!(reads.load(Ordering::Acquire), 0);

    for (name, bytes) in [
        (JOURNAL, &b"not a journal\n"[..]),
        (
            "manual-overrides.json",
            &br#"{"schemaVersion":10,"overrides":{"performance":null,"fans":null,"lighting":{"effect":0}}}"#[..],
        ),
    ] {
        let reads = Arc::new(AtomicUsize::new(0));
        let files = Memory {
            journal_reads: reads.clone(),
            ..Memory::refused()
        };
        files.put(name, bytes);
        let error = Storage::open(Box::new(files), SID).err().unwrap();
        assert_eq!(error.to_string(), REFUSED, "{name}");
        assert_eq!(reads.load(Ordering::Acquire), 0, "{name}");
    }
}
