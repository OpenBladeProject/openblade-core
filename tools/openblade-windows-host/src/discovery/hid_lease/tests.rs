use super::*;
use crate::discovery::{
    HostObservation, Interface, Platform, RegistrySource, RegistryText, ScanState,
};
use std::{
    cell::{Cell, RefCell},
    collections::VecDeque,
    rc::Rc,
    sync::atomic::Ordering,
};

const EXACT: Descriptor = Descriptor {
    device: (0x1532, 0x02e0),
    usage: (1, 2),
    feature_bytes: 91,
    version: 0x1234,
};

type Rows = Vec<Option<Interface>>;

struct Fake {
    host: RefCell<HostObservation>,
    rows: RefCell<Rows>,
    scans: RefCell<VecDeque<Rows>>,
    opened: RefCell<Vec<String>>,
    reads: Cell<usize>,
    drops: Rc<Cell<usize>>,
    descriptors: RefCell<VecDeque<io::Result<Descriptor>>>,
    removal_observed: Cell<bool>,
    open_fails: bool,
    cancel_on_scan: Cell<bool>,
}

struct FakeHandle(Rc<Cell<usize>>);

impl Drop for FakeHandle {
    fn drop(&mut self) {
        self.0.set(self.0.get() + 1);
    }
}

impl DiscoverySource for Fake {
    fn read_host(&self, _: &AtomicBool) -> io::Result<HostObservation> {
        Ok(self.host.borrow().clone())
    }

    fn scan_hid(
        &self,
        cancelled: &AtomicBool,
        mut visit: impl FnMut(Option<Interface>) -> io::Result<()>,
    ) -> io::Result<ScanState> {
        let rows = self
            .scans
            .borrow_mut()
            .pop_front()
            .unwrap_or_else(|| self.rows.borrow().clone());
        for row in rows {
            visit(row)?;
        }
        if self.cancel_on_scan.get() {
            cancelled.store(true, Ordering::Relaxed);
        }
        Ok(ScanState::Complete)
    }
}

impl LeaseSource for Fake {
    type Handle = FakeHandle;

    fn open_metadata(&self, path: &str) -> io::Result<FakeHandle> {
        self.opened.borrow_mut().push(path.to_owned());
        if self.open_fails {
            return Err(io::ErrorKind::NotFound.into());
        }
        Ok(FakeHandle(Rc::clone(&self.drops)))
    }

    fn describe(&self, _handle: &FakeHandle) -> io::Result<Descriptor> {
        self.reads.set(self.reads.get() + 1);
        if self.removal_observed.get() {
            return Err(io::ErrorKind::NotFound.into());
        }
        self.descriptors
            .borrow_mut()
            .pop_front()
            .unwrap_or(Ok(EXACT))
    }
}

fn fake() -> Fake {
    let source = RegistrySource {
        manufacturer: RegistryText::Present("Razer".into()),
        product_name: RegistryText::Present("Blade 16 - RZ09-0581".into()),
    };
    Fake {
        host: RefCell::new(HostObservation {
            platform: Platform::Supported,
            bios: source.clone(),
            system_information: source,
            sku: RegistryText::Present("SYNTHETIC-VARIANT".into()),
            bios_version: RegistryText::Unavailable,
        }),
        rows: RefCell::new(vec![row("opaque-original", 0x02e0, Some((1, 2)))]),
        scans: RefCell::new(VecDeque::new()),
        opened: RefCell::new(Vec::new()),
        reads: Cell::new(0),
        drops: Rc::new(Cell::new(0)),
        descriptors: RefCell::new(VecDeque::new()),
        removal_observed: Cell::new(false),
        open_fails: false,
        cancel_on_scan: Cell::new(false),
    }
}

fn row(path: &str, pid: u16, usage: Option<(u16, u16)>) -> Option<Interface> {
    let mut ids = vec![format!(r"HID\VID_1532&PID_{pid:04X}&MI_00&COL01")];
    if let Some((page, usage)) = usage {
        ids.push(format!("HID_DEVICE_UP:{page:04X}_U:{usage:04X}"));
    }
    Some(Interface {
        path: path.into(),
        hardware_ids: Some(ids),
    })
}

#[test]
fn exact_handle_is_read_twice_then_retained_and_closed_once() {
    let fake = fake();
    let cancelled = AtomicBool::new(false);
    let mut lease = Lease::open(&fake, &cancelled).unwrap();
    assert_eq!(*fake.opened.borrow(), ["opaque-original"]);
    assert_eq!(fake.reads.get(), 2);

    // Ignore case-only path changes and diagnostic-only host fields.
    fake.rows
        .replace(vec![row("OPAQUE-ORIGINAL", 0x02e0, Some((1, 2)))]);
    fake.host.borrow_mut().bios_version = RegistryText::Present("other".into());
    lease.revalidate(&cancelled).unwrap();
    assert_eq!(fake.reads.get(), 3);
    assert_eq!(fake.opened.borrow().len(), 1);
    assert_eq!(fake.drops.get(), 0);
    drop(lease);
    assert_eq!(fake.drops.get(), 1);
}

#[test]
fn unknown_host_or_incomplete_ambiguous_or_duplicate_hints_never_open() {
    let cancelled = AtomicBool::new(false);
    let host = fake();
    host.host.borrow_mut().system_information.product_name =
        RegistryText::Present("different".into());
    assert!(Lease::open(&host, &cancelled).is_err());
    assert!(host.opened.borrow().is_empty());

    let original = row("opaque-original", 0x02e0, Some((1, 2)));
    for additional in [
        None,
        row("OPAQUE-ORIGINAL", 0x02e0, Some((1, 2))),
        row("opaque-second", 0x02e0, Some((1, 2))),
        row("opaque-unresolved", 0x02e0, None),
    ] {
        let fake = fake();
        fake.rows.replace(vec![original.clone(), additional]);
        assert!(Lease::open(&fake, &cancelled).is_err());
        assert!(fake.opened.borrow().is_empty());
    }
}

#[test]
fn opened_handle_must_independently_match_exact_vid_pid_usage_and_length() {
    for wrong in [
        Descriptor {
            device: (0x1234, 0x02e0),
            ..EXACT
        },
        Descriptor {
            device: (0x1532, 0x02c6),
            ..EXACT
        },
        Descriptor {
            usage: (2, 2),
            ..EXACT
        },
        Descriptor {
            usage: (1, 6),
            ..EXACT
        },
        Descriptor {
            feature_bytes: 90,
            ..EXACT
        },
    ] {
        let fake = fake();
        fake.descriptors.borrow_mut().push_back(Ok(wrong));
        assert!(Lease::open(&fake, &AtomicBool::new(false)).is_err());
        assert_eq!(fake.opened.borrow().len(), 1);
        assert_eq!(fake.drops.get(), 1);
    }
}

#[test]
fn source_reviewed_handle_uses_its_own_usage_and_pins_full_observation() {
    let fake = fake();
    let cancelled = AtomicBool::new(false);
    let mut host = fake.host.borrow_mut();
    host.bios.product_name = RegistryText::Present("Blade 18 - RZ09-0484".into());
    host.system_information.product_name = host.bios.product_name.clone();
    drop(host);
    fake.rows
        .replace(vec![row("opaque-experimental", 0x02a0, None)]);
    let initial = Descriptor {
        device: (0x1532, 0x02a0),
        usage: (0xff00, 1),
        ..EXACT
    };
    fake.descriptors
        .borrow_mut()
        .extend([Ok(initial), Ok(initial)]);
    let mut lease = Lease::open(&fake, &cancelled).unwrap();
    fake.descriptors.borrow_mut().push_back(Ok(Descriptor {
        usage: (0xff00, 2),
        ..initial
    }));
    assert!(lease.revalidate(&cancelled).is_err());
    assert_eq!(fake.drops.get(), 1);
}

#[test]
fn post_open_rescan_rejects_removal_replacement_or_new_ambiguity() {
    let original = row("opaque-original", 0x02e0, Some((1, 2)));
    let replacement = row("opaque-replacement", 0x02e0, Some((1, 2)));
    for changed in [
        vec![],
        vec![replacement.clone()],
        vec![original.clone(), replacement],
    ] {
        let fake = fake();
        fake.scans
            .borrow_mut()
            .extend([vec![original.clone()], changed]);
        assert!(Lease::open(&fake, &AtomicBool::new(false)).is_err());
        assert_eq!(*fake.opened.borrow(), ["opaque-original"]);
        assert_eq!(fake.drops.get(), 1);
    }
}

#[test]
fn same_path_same_descriptor_does_not_revive_a_removed_handle() {
    let fake = fake();
    let cancelled = AtomicBool::new(false);
    let mut lease = Lease::open(&fake, &cancelled).unwrap();
    fake.removal_observed.set(true);
    assert!(lease.revalidate(&cancelled).is_err());
    assert_eq!(fake.drops.get(), 1);
    fake.removal_observed.set(false);
    assert!(lease.revalidate(&cancelled).is_err());
    assert_eq!(fake.opened.borrow().len(), 1);
    drop(lease);
    assert_eq!(fake.drops.get(), 1);
}

#[test]
fn same_handle_descriptor_change_during_open_fails_closed() {
    let fake = fake();
    fake.descriptors.borrow_mut().extend([
        Ok(EXACT),
        Ok(Descriptor {
            version: EXACT.version + 1,
            ..EXACT
        }),
    ]);
    assert!(Lease::open(&fake, &AtomicBool::new(false)).is_err());
    assert_eq!(fake.drops.get(), 1);
}

#[test]
fn open_and_metadata_errors_propagate_and_never_retry() {
    let mut unavailable = fake();
    unavailable.open_fails = true;
    let cancelled = AtomicBool::new(false);
    assert_eq!(
        Lease::open(&unavailable, &cancelled).err().unwrap().kind(),
        io::ErrorKind::NotFound
    );
    assert_eq!(unavailable.opened.borrow().len(), 1);
    assert_eq!(unavailable.reads.get(), 0);

    let unreadable = fake();
    unreadable
        .descriptors
        .borrow_mut()
        .push_back(Err(io::ErrorKind::InvalidData.into()));
    assert_eq!(
        Lease::open(&unreadable, &cancelled).err().unwrap().kind(),
        io::ErrorKind::InvalidData
    );
    assert_eq!(unreadable.opened.borrow().len(), 1);
    assert_eq!(unreadable.drops.get(), 1);
}

#[test]
fn cancellation_during_revalidation_closes_the_lease() {
    let fake = fake();
    let cancelled = AtomicBool::new(false);
    let mut lease = Lease::open(&fake, &cancelled).unwrap();
    fake.cancel_on_scan.set(true);
    assert_eq!(
        lease.revalidate(&cancelled).unwrap_err().kind(),
        io::ErrorKind::Interrupted
    );
    assert_eq!(fake.reads.get(), 2);
    assert_eq!(fake.drops.get(), 1);
    cancelled.store(false, Ordering::Relaxed);
    assert!(lease.revalidate(&cancelled).is_err());
}
