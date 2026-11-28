//! Protected adapter for the portable thermal grammar. Never resolves a new root.
use super::*;
use crate::thermal_history::{self as history, ACTIVE, Backend, Entry, SEGMENT, STAGING, View};
use windows_sys::{
    Wdk::Storage::FileSystem::FILE_DIRECTORY_FILE,
    Win32::Storage::FileSystem::{DELETE, FILE_SHARE_READ, FILE_SHARE_WRITE},
};
pub(super) struct History<'a>(pub(super) &'a Store);
// Inspection cannot publish recovered metadata or mutate a journal. Ordinary
// read/recovery and write paths retain their existing error semantics.
struct ReleaseHistory<'a>(History<'a>);
impl Backend for ReleaseHistory<'_> {
    fn read(&self, name: &str, max: usize) -> io::Result<Option<Vec<u8>>> {
        self.0.read_checked(name, max, true)
    }
    fn once(&self, _: &str, _: &[u8]) -> io::Result<()> {
        Err(deny())
    }
    fn publish_active(&self, _: Option<&[u8]>, _: &[u8]) -> io::Result<()> {
        Err(deny())
    }
    fn namespace(&self, sequence: u64) -> io::Result<()> {
        self.0.namespace_checked(sequence, true)
    }
}
impl History<'_> {
    fn boundary(&self, name: &str, stage: &str) -> io::Result<()> {
        self.0
            .boundary(name.split('-').next().unwrap_or_default(), stage)
    }
    fn directory(&self, create: bool) -> io::Result<Option<File>> {
        let file = match paths::open_directory(&self.0.root, OsStr::new("thermal-history")) {
            Ok(f) => f,
            Err(e) if paths::missing_leaf(&e) && !create => return Ok(None),
            Err(e) if paths::missing_leaf(&e) => {
                let security = paths::retain_descriptor(&self.0.root)?;
                paths::open_relative_with_security(
                    &self.0.root,
                    OsStr::new("thermal-history"),
                    FILE_GENERIC_READ | FILE_GENERIC_WRITE,
                    FILE_SHARE_READ | FILE_SHARE_WRITE,
                    FILE_CREATE,
                    FILE_DIRECTORY_FILE | FILE_OPEN_REPARSE_POINT,
                    Some(&security),
                )?
            }
            Err(e) => return Err(e),
        };
        paths::directory_info(&file)?;
        self.0.check(&file, Policy::ROOT)?;
        Ok(Some(file))
    }
    fn open(&self, parent: &File, name: &str, create: bool, delete: bool) -> io::Result<File> {
        self.open_checked(parent, name, create, delete, false)
    }
    fn open_checked(
        &self,
        parent: &File,
        name: &str,
        create: bool,
        delete: bool,
        release: bool,
    ) -> io::Result<File> {
        let f = paths::open_relative(
            parent,
            OsStr::new(name),
            FILE_GENERIC_READ | FILE_GENERIC_WRITE | if delete { DELETE } else { 0 },
            0,
            if create { FILE_CREATE } else { FILE_OPEN },
            FILE_NON_DIRECTORY_FILE | FILE_OPEN_REPARSE_POINT,
        )
        .map_err(|e| {
            if release {
                crate::installed::readiness::thermal_storage::release_open_error(e)
            } else {
                e
            }
        })?;
        paths::check_regular_file(&f)?;
        self.0.check(&f, Policy::Record)?;
        Ok(f)
    }
    fn read_checked(&self, name: &str, max: usize, release: bool) -> io::Result<Option<Vec<u8>>> {
        self.0.verify_journal_custody()?;
        let dir;
        let parent = if matches!(name, ACTIVE | STAGING | "thermal-mutation.journal.pending") {
            &self.0.root
        } else {
            dir = match self.directory(false)? {
                Some(d) => d,
                None => return Ok(None),
            };
            &dir
        };
        let mut f = match self.open_checked(parent, name, false, false, release) {
            Ok(f) => f,
            Err(e) if paths::missing_leaf(&e) => return Ok(None),
            Err(e) => return Err(e),
        };
        let n = usize::try_from(f.metadata()?.len()).map_err(|_| deny())?;
        if n > max {
            return Err(deny());
        }
        let mut b = vec![0; n];
        f.read_exact(&mut b).map_err(|e| {
            if release {
                crate::installed::readiness::thermal_storage::release_bytes_error(e)
            } else {
                e
            }
        })?;
        if f.metadata()?.len() != n as u64 {
            return Err(deny());
        }
        Ok(Some(b))
    }
    fn namespace_checked(&self, sequence: u64, release: bool) -> io::Result<()> {
        if self
            .read_checked(STAGING, SEGMENT + 2048, release)?
            .is_some()
        {
            return Err(deny());
        }
        let Some(_dir) = self.directory(false)? else {
            return if sequence == 0 { Ok(()) } else { Err(deny()) };
        };
        let mut count = 0u64;
        for entry in std::fs::read_dir(self.0.path.join("thermal-history"))? {
            let name = entry?.file_name();
            let name = name.to_str().ok_or_else(deny)?;
            let (kind, rest) = name.split_once('-').ok_or_else(deny)?;
            if !matches!(kind, "segment" | "checkpoint" | "roll")
                || rest.len() != 24
                || !rest.ends_with(".bin")
            {
                return Err(deny());
            }
            let n: u64 = rest[..20].parse().map_err(|_| deny())?;
            if n == 0 || n > sequence || name != history::name(kind, n) {
                return Err(deny());
            }
            count = count.checked_add(1).ok_or_else(deny)?;
        }
        if count != sequence.checked_mul(3).ok_or_else(deny)?
            || self
                .read_checked(STAGING, SEGMENT + 2048, release)?
                .is_some()
        {
            return Err(deny());
        }
        Ok(())
    }
}
impl Backend for History<'_> {
    fn read(&self, name: &str, max: usize) -> io::Result<Option<Vec<u8>>> {
        self.read_checked(name, max, false)
    }
    fn once(&self, name: &str, bytes: &[u8]) -> io::Result<()> {
        self.0.verify_journal_custody()?;
        let dir = self.directory(true)?.ok_or_else(deny)?;
        let mut f = self.open(&dir, name, true, false)?;
        self.boundary(name, "created")?;
        f.write_all(bytes)?;
        self.boundary(name, "written")?;
        f.sync_all()?;
        self.boundary(name, "flushed")?;
        drop(f);
        if self.read(name, bytes.len())?.as_deref() != Some(bytes) {
            return Err(deny());
        }
        self.boundary(name, "readback")?;
        Ok(())
    }
    fn publish_active(&self, expected: Option<&[u8]>, bytes: &[u8]) -> io::Result<()> {
        self.0.verify_journal_custody()?;
        if self
            .read("thermal-mutation.journal.pending", SEGMENT + 2048)?
            .is_some()
            || self.read(ACTIVE, SEGMENT + 2048)?.as_deref() != expected
        {
            return Err(deny());
        }
        match self.read(STAGING, SEGMENT + 2048)? {
            Some(old) if old == bytes => (),
            Some(_) => return Err(deny()),
            None => {
                let mut f = self.open(&self.0.root, STAGING, true, false)?;
                self.boundary("active", "created")?;
                f.write_all(bytes)?;
                self.boundary("active", "written")?;
                f.sync_all()?;
                self.boundary("active", "flushed")?;
            }
        }
        if self.read(STAGING, SEGMENT + 2048)?.as_deref() != Some(bytes)
            || self.read(ACTIVE, SEGMENT + 2048)?.as_deref() != expected
        {
            return Err(deny());
        }
        self.boundary("active", "readback")?;
        let f = self.open(&self.0.root, STAGING, false, true)?;
        paths::rename_same_directory(&f, ACTIVE, true)?;
        self.boundary("active", "renamed")?;
        f.sync_all()?;
        drop(f);
        if self.read(ACTIVE, SEGMENT + 2048)?.as_deref() != Some(bytes) {
            return Err(deny());
        }
        self.boundary("active", "verified")?;
        Ok(())
    }
    fn namespace(&self, sequence: u64) -> io::Result<()> {
        self.namespace_checked(sequence, false)
    }
}
impl Store {
    pub(in crate::installed) fn journal_read_release(
        &self,
        pending: bool,
    ) -> io::Result<Option<Vec<u8>>> {
        let b = ReleaseHistory(History(self));
        if pending {
            return b.read("thermal-mutation.journal.pending", SEGMENT + 2048);
        }
        let view = history::active(b.read(ACTIVE, SEGMENT + 2048)?)?;
        // Authenticate all existing evidence without invoking recover/publish.
        history::inspect(&b, &view, |e, r| self.join_row(e, r))?;
        Ok(view.physical.map(|_| view.tail))
    }
    pub(super) fn join_row(&self, entry: &Entry, _raw: &[u8]) -> io::Result<()> {
        if !self.active_records.contains_key(&entry.owner) {
            return Err(
                crate::installed::readiness::thermal_mutation::fatal_admission(
                    "Thermal journal original owner binding changed.",
                ),
            );
        }
        if entry.tfc.is_some()
            || entry.battery.is_some()
            || entry
                .fan
                .as_ref()
                .is_some_and(|f| f.contract == openblade_domain::fan_history::CONTRACT)
        {
            let original = self.active_records.get(&entry.owner).ok_or_else(deny)?;
            #[cfg(test)]
            if self.verifier.is_some() {
                return self.battery_producer_fixture.as_ref().ok_or_else(deny)?(
                    original,
                    entry.tfc.is_some(),
                );
            }
            let mut cache = self.battery_producers.lock().map_err(|_| deny())?;
            if !cache.contains_key(&original.epoch) {
                let proof = crate::native_package::historical_battery_producer(
                    self.path.parent().ok_or_else(deny)?,
                    &original.installation,
                    &original.epoch,
                    &original.artifact,
                )?;
                cache.insert(original.epoch.clone(), proof);
            }
            if entry.tfc.is_some() {
                cache
                    .get(&original.epoch)
                    .ok_or_else(deny)?
                    .verify_temporary()?;
            }
            cache.get(&original.epoch).ok_or_else(deny)?.verify(
                &original.installation,
                &original.epoch,
                &original.artifact,
            )?;
        }
        Ok(())
    }
    pub(super) fn journal_view(&self) -> io::Result<View> {
        let b = History(self);
        let view = history::active(b.read(ACTIVE, SEGMENT + 2048)?)?;
        // Authenticate all previous immutable evidence before metadata recovery.
        history::inspect_prefix(&b, &view, |e, r| self.join_row(e, r))?;
        let view = history::recover(&b, view, |e, r| self.join_row(e, r))?;
        history::inspect(&b, &view, |e, r| self.join_row(e, r))?;
        Ok(view)
    }
    pub(in crate::installed) fn journal_reserve(
        &self,
        expected: Option<&[u8]>,
        needed: usize,
    ) -> io::Result<Option<Vec<u8>>> {
        self.journal_view()?;
        let tail = history::reserve(&History(self), expected, needed, |e, r| self.join_row(e, r))?;
        Ok(if self.journal_view()?.physical.is_some() {
            Some(tail)
        } else {
            None
        })
    }
}
