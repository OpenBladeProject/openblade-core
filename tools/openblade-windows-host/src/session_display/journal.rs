//! Recovery state for one private, scripted fixture. No product store is opened.

use std::{
    fs::{self, File, OpenOptions},
    io::{self, Read, Write},
    os::windows::fs::MetadataExt,
    path::{Component, Path, PathBuf, Prefix},
    sync::{Arc, Mutex},
};

use openblade_platform_windows::display::{ActiveInternalDisplay, EndpointId, RefreshSnapshot};
use openblade_windows_host::pipe::Identity;
use serde::{Deserialize, Serialize};
use windows_sys::Win32::Storage::FileSystem::FILE_ATTRIBUTE_REPARSE_POINT;

use crate::session_windows::valid_request_id;

use super::color::ColorChange;

const VERSION: u8 = 1;
const MAX_BYTES: usize = 4096;

pub(crate) type SharedJournal = Arc<Mutex<Journal>>;

#[derive(Clone, Copy, PartialEq, Eq)]
pub(crate) enum StartMode {
    New,
    Resume,
    Reconcile,
}

#[derive(Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
struct Record {
    version: u8,
    fixture: String,
    owner: String,
    // Required even when clean. A missing record must never mean clean.
    state: State,
}

#[derive(Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(tag = "status", rename_all = "snake_case", deny_unknown_fields)]
enum State {
    Clean,
    Pending { attempt: Pending },
}

#[derive(Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub(super) struct Pending {
    pub(super) id: String,
    pub(super) panel: Panel,
    pub(super) change: Change,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub(super) struct Panel {
    source: (u32, i32, u32),
    target: (u32, i32, u32),
    monitor_path: String,
}

impl Panel {
    pub(super) fn target(&self) -> EndpointId {
        let (adapter_low, adapter_high, id) = self.target;
        EndpointId {
            adapter_low,
            adapter_high,
            id,
        }
    }

    pub(super) fn observe(display: &ActiveInternalDisplay) -> Option<Self> {
        let endpoint = |id: EndpointId| (id.adapter_low, id.adapter_high, id.id);
        Some(Self {
            source: endpoint(display.source),
            target: endpoint(display.target),
            monitor_path: display
                .monitor_name
                .as_ref()
                .ok()?
                .device_path
                .as_ref()?
                .to_str()?
                .into(),
        })
    }
}

#[derive(Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub(super) struct RefreshBaseline {
    desktop: (u32, u32),
    physical: (u32, u32),
    boost: bool,
    width: u32,
    height: u32,
    virtual_mode: bool,
}

impl RefreshBaseline {
    pub(super) fn observe(snapshot: &RefreshSnapshot) -> Option<Self> {
        let desktop = snapshot.display.desktop_rate?;
        let physical = snapshot.display.physical_rate?;
        Some(Self {
            desktop: (desktop.numerator, desktop.denominator),
            physical: (physical.numerator, physical.denominator),
            boost: snapshot.display.boost_refresh_rate,
            width: snapshot.width,
            height: snapshot.height,
            virtual_mode: snapshot.display.supports_virtual_mode,
        })
    }

    pub(super) fn classifies(&self, fresh: &RefreshSnapshot, requested: &str) -> bool {
        Self::observe(fresh).is_some_and(|observed| {
            observed == *self
                || ((observed.width, observed.height, observed.virtual_mode)
                    == (self.width, self.height, self.virtual_mode)
                    && fresh.current_refresh_key.as_deref() == Some(requested))
        })
    }
}

#[derive(Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(tag = "operation", rename_all = "snake_case", deny_unknown_fields)]
pub(super) enum Change {
    Refresh {
        before: RefreshBaseline,
        requested: String,
    },
    Hdr {
        before: bool,
        requested: bool,
    },
    Brightness {
        before: u8,
        requested: u8,
    },
    Icc {
        evidence: Box<ColorChange>,
    },
}

impl Change {
    fn valid(&self) -> bool {
        match self {
            Self::Refresh { before, requested } => {
                let (desktop, desktop_divisor) = before.desktop;
                let (physical, physical_divisor) = before.physical;
                [
                    desktop,
                    desktop_divisor,
                    physical,
                    physical_divisor,
                    before.width,
                    before.height,
                ]
                .into_iter()
                .all(|value| value != 0)
                    && matches!(
                        requested.as_str(),
                        "fixed-60" | "fixed-240" | "dynamic-60-240"
                    )
            }
            Self::Hdr { before, requested } => before != requested,
            Self::Icc { evidence } => evidence.valid(),
            Self::Brightness { before, requested } => {
                before != requested && *before <= 100 && *requested <= 100
            }
        }
    }
}

pub(crate) struct Journal {
    directory: PathBuf,
    record: Option<Record>,
    reconcile: bool,
    brightness_request: Option<String>,
}

impl Journal {
    pub(crate) fn open(id: &str, owner: &Identity, mode: StartMode) -> SharedJournal {
        let root = fs::canonicalize(std::env::temp_dir()).ok().filter(|path| {
            matches!(path.components().next(), Some(Component::Prefix(prefix))
                if matches!(prefix.kind(), Prefix::Disk(_) | Prefix::VerbatimDisk(_)))
        });
        let directory = root
            .as_deref()
            .unwrap_or(Path::new(""))
            .join(format!("openblade-display-{id}"));
        let mut journal = Self {
            directory,
            record: None,
            reconcile: false,
            brightness_request: None,
        };
        let clean = Record {
            version: VERSION,
            fixture: id.into(),
            owner: owner.fixture_owner(),
            state: State::Clean,
        };
        if root.as_deref().is_some_and(ordinary_directory) {
            journal.record = if mode == StartMode::New {
                (fs::create_dir(&journal.directory).is_ok() && journal.write(&clean).is_ok())
                    .then_some(clean)
            } else {
                journal.read().ok().filter(|record| {
                    record.version == clean.version
                        && record.fixture == clean.fixture
                        && record.owner == clean.owner
                        && match &record.state {
                            State::Clean => true,
                            State::Pending { attempt } => {
                                valid_request_id(&attempt.id)
                                    && !attempt.panel.monitor_path.is_empty()
                                    && attempt.change.valid()
                            }
                        }
                })
            };
        }
        // Recovery permission belongs only to the attempt found at startup.
        // A new submission in this process must never inherit that permission.
        journal.reconcile = mode == StartMode::Reconcile
            && matches!(
                journal.record.as_ref().map(|r| &r.state),
                Some(State::Pending { .. })
            );
        Arc::new(Mutex::new(journal))
    }

    fn check(&mut self) -> bool {
        if self.record.is_some() && self.read().ok() == self.record {
            return true;
        }
        self.record = None;
        false
    }

    pub(super) fn can_write(&mut self) -> bool {
        self.check() && matches!(self.record.as_ref().map(|r| &r.state), Some(State::Clean))
    }

    pub(super) fn arm_brightness(&mut self, id: &str) {
        self.brightness_request = Some(id.into());
    }

    pub(crate) fn begin_brightness(
        &mut self,
        display: &ActiveInternalDisplay,
        before: u8,
        requested: u8,
    ) -> bool {
        let Some(id) = self.brightness_request.take() else {
            return false;
        };
        let Some(panel) = Panel::observe(display) else {
            return false;
        };
        self.begin(&id, panel, Change::Brightness { before, requested })
    }

    pub(super) fn begin(&mut self, id: &str, panel: Panel, change: Change) -> bool {
        if !change.valid() || !self.can_write() {
            return false;
        }
        self.replace(State::Pending {
            attempt: Pending {
                id: id.into(),
                panel,
                change,
            },
        })
    }

    pub(super) fn reconciliation(&mut self) -> Option<&Pending> {
        if !self.reconcile || !self.check() {
            return None;
        }
        match &self.record.as_ref()?.state {
            State::Pending { attempt } => Some(attempt),
            State::Clean => None,
        }
    }

    pub(super) fn finish(&mut self, id: &str) -> bool {
        if !self.check()
            || !matches!(&self.record.as_ref().unwrap().state,
                State::Pending { attempt } if attempt.id == id)
        {
            return false;
        }
        self.reconcile = false;
        self.replace(State::Clean)
    }

    fn replace(&mut self, state: State) -> bool {
        let mut record = self.record.take().expect("checked fixture record");
        record.state = state;
        if self.write(&record).is_err() {
            return false;
        }
        self.record = Some(record);
        true
    }

    fn read(&self) -> io::Result<Record> {
        if !ordinary_directory(&self.directory) {
            return Err(io::Error::other("invalid display fixture directory"));
        }
        match fs::symlink_metadata(self.directory.join("state.next")) {
            Err(error) if error.kind() == io::ErrorKind::NotFound => {}
            _ => {
                return Err(io::Error::other(
                    "unfinished display fixture record replacement",
                ));
            }
        }
        let path = self.directory.join("state.json");
        let metadata = fs::symlink_metadata(&path)?;
        if !metadata.is_file() || is_reparse_point(&metadata) {
            return Err(io::Error::other("invalid display fixture record"));
        }
        let mut bytes = Vec::new();
        File::open(path)?
            .take((MAX_BYTES + 1) as u64)
            .read_to_end(&mut bytes)?;
        if bytes.len() > MAX_BYTES {
            return Err(io::Error::other("oversize display fixture record"));
        }
        serde_json::from_slice(&bytes).map_err(io::Error::other)
    }

    fn write(&self, record: &Record) -> io::Result<()> {
        let bytes = serde_json::to_vec(record).map_err(io::Error::other)?;
        if bytes.len() > MAX_BYTES {
            return Err(io::Error::other("oversize display fixture record"));
        }
        // A complete prior record survives a process crash before replacement.
        // A leftover staging file grants no authority and is never overwritten.
        let temporary = self.directory.join("state.next");
        let mut file = OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&temporary)?;
        file.write_all(&bytes)?;
        file.sync_all()?;
        drop(file);
        fs::rename(temporary, self.directory.join("state.json"))
    }
}

fn ordinary_directory(path: &Path) -> bool {
    fs::symlink_metadata(path).is_ok_and(|info| info.is_dir() && !is_reparse_point(&info))
}

fn is_reparse_point(info: &fs::Metadata) -> bool {
    info.file_attributes() & FILE_ATTRIBUTE_REPARSE_POINT != 0
}
