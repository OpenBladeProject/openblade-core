use super::{
    owner::{HidLeaseOwner, Retirement, ServiceLeaseSource},
    *,
};
use crate::discovery::{
    HostObservation, Interface, Platform, RegistrySource, RegistryText, ScanState,
};
use std::{
    cell::{Cell, RefCell},
    collections::VecDeque,
    rc::Rc,
    sync::atomic::Ordering,
    task::Poll,
};

#[cfg(windows)]
#[allow(dead_code)] // Consumed by the service host's test harness.
pub(crate) mod service_fixture;

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
    retired: RefCell<Vec<usize>>,
    retirements: RefCell<VecDeque<Poll<io::Result<()>>>>,
    descriptors: RefCell<VecDeque<io::Result<Descriptor>>>,
    removal_observed: Cell<bool>,
    open_fails: Cell<bool>,
    #[cfg(windows)]
    on_open: RefCell<Option<Box<dyn FnOnce()>>>,
    cancel_on_scan: Cell<bool>,
}

struct FakeHandle(Rc<Cell<usize>>, usize);

impl Drop for FakeHandle {
    fn drop(&mut self) {
        self.0.set(self.0.get() + 1);
    }
}

impl DiscoverySource for Rc<Fake> {
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

impl LeaseSource for Rc<Fake> {
    type Handle = FakeHandle;

    fn open_metadata(&self, path: &str) -> io::Result<FakeHandle> {
        self.opened.borrow_mut().push(path.to_owned());
        #[cfg(windows)]
        if let Some(on_open) = self.on_open.borrow_mut().take() {
            on_open();
        }
        if self.open_fails.get() {
            return Err(io::ErrorKind::NotFound.into());
        }
        Ok(FakeHandle(
            Rc::clone(&self.drops),
            self.opened.borrow().len(),
        ))
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

impl ServiceLeaseSource for Rc<Fake> {
    fn poll_retirement(&self, handle: &mut FakeHandle) -> Poll<io::Result<()>> {
        self.retired.borrow_mut().push(handle.1);
        self.retirements
            .borrow_mut()
            .pop_front()
            .unwrap_or(Poll::Ready(Ok(())))
    }
}
fn fake() -> Rc<Fake> {
    fake_host("Blade 16 - RZ09-0581")
}

/// The fake source on a Razer host with this product name.
fn fake_host(product: &str) -> Rc<Fake> {
    let source = RegistrySource {
        manufacturer: RegistryText::Present("Razer".into()),
        product_name: RegistryText::Present(product.into()),
    };
    Rc::new(Fake {
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
        retired: RefCell::new(Vec::new()),
        retirements: RefCell::new(VecDeque::new()),
        descriptors: RefCell::new(VecDeque::new()),
        removal_observed: Cell::new(false),
        open_fails: Cell::new(false),
        #[cfg(windows)]
        on_open: RefCell::new(None),
        cancel_on_scan: Cell::new(false),
    })
}

fn scripted_owner(source: &Rc<Fake>) -> HidLeaseOwner<Rc<Fake>> {
    HidLeaseOwner::new(Rc::clone(source))
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
    let mut owner = scripted_owner(&fake);
    let epoch = owner.acquire(&cancelled).unwrap();
    assert_eq!(*fake.opened.borrow(), ["opaque-original"]);
    assert_eq!(fake.reads.get(), 2);

    // Ignore case-only path changes and diagnostic-only host fields.
    fake.rows
        .replace(vec![row("OPAQUE-ORIGINAL", 0x02e0, Some((1, 2)))]);
    fake.host.borrow_mut().bios_version = RegistryText::Present("other".into());
    owner.revalidate(&epoch, &cancelled).unwrap();
    assert_eq!(fake.reads.get(), 3);
    assert_eq!(fake.opened.borrow().len(), 1);
    assert_eq!(fake.drops.get(), 0);
    assert_eq!(owner.stop(), Retirement::Complete);
    assert_eq!(fake.drops.get(), 1);
}

#[test]
fn unknown_host_or_incomplete_ambiguous_or_duplicate_hints_never_open() {
    let cancelled = AtomicBool::new(false);
    let host = fake();
    host.host.borrow_mut().system_information.product_name =
        RegistryText::Present("different".into());
    assert!(scripted_owner(&host).acquire(&cancelled).is_err());
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
        assert!(scripted_owner(&fake).acquire(&cancelled).is_err());
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
        assert!(
            scripted_owner(&fake)
                .acquire(&AtomicBool::new(false))
                .is_err()
        );
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
    let mut owner = scripted_owner(&fake);
    let epoch = owner.acquire(&cancelled).unwrap();
    fake.descriptors.borrow_mut().push_back(Ok(Descriptor {
        usage: (0xff00, 2),
        ..initial
    }));
    assert!(owner.revalidate(&epoch, &cancelled).is_err());
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
        assert!(
            scripted_owner(&fake)
                .acquire(&AtomicBool::new(false))
                .is_err()
        );
        assert_eq!(*fake.opened.borrow(), ["opaque-original"]);
        assert_eq!(fake.drops.get(), 1);
    }
}

#[test]
fn same_path_same_descriptor_does_not_revive_a_removed_handle() {
    let fake = fake();
    let cancelled = AtomicBool::new(false);
    let mut owner = scripted_owner(&fake);
    let epoch = owner.acquire(&cancelled).unwrap();
    fake.removal_observed.set(true);
    assert!(owner.revalidate(&epoch, &cancelled).is_err());
    assert_eq!(fake.drops.get(), 1);
    fake.removal_observed.set(false);
    assert!(owner.revalidate(&epoch, &cancelled).is_err());
    assert_eq!(fake.opened.borrow().len(), 1);
    assert_eq!(owner.stop(), Retirement::Complete);
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
    assert!(
        scripted_owner(&fake)
            .acquire(&AtomicBool::new(false))
            .is_err()
    );
    assert_eq!(fake.drops.get(), 1);
}

#[test]
fn open_and_metadata_errors_propagate_and_never_retry() {
    let unavailable = fake();
    unavailable.open_fails.set(true);
    let cancelled = AtomicBool::new(false);
    assert_eq!(
        scripted_owner(&unavailable)
            .acquire(&cancelled)
            .err()
            .unwrap()
            .kind(),
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
        scripted_owner(&unreadable)
            .acquire(&cancelled)
            .err()
            .unwrap()
            .kind(),
        io::ErrorKind::InvalidData
    );
    assert_eq!(unreadable.opened.borrow().len(), 1);
    assert_eq!(unreadable.drops.get(), 1);
}

#[test]
fn cancellation_during_revalidation_closes_the_lease() {
    let fake = fake();
    let cancelled = AtomicBool::new(false);
    let mut owner = scripted_owner(&fake);
    let epoch = owner.acquire(&cancelled).unwrap();
    fake.cancel_on_scan.set(true);
    assert_eq!(
        owner.revalidate(&epoch, &cancelled).unwrap_err().kind(),
        io::ErrorKind::Interrupted
    );
    assert_eq!(fake.reads.get(), 2);
    assert_eq!(fake.drops.get(), 1);
    cancelled.store(false, Ordering::Relaxed);
    assert!(owner.revalidate(&epoch, &cancelled).is_err());
}

#[test]
fn pending_removal_fences_same_path_reacquisition_and_late_callbacks() {
    let source = fake();
    let cancelled = AtomicBool::new(false);
    let mut owner = scripted_owner(&source);
    let first = owner.acquire(&cancelled).unwrap();
    assert!(owner.acquire(&cancelled).is_err());
    source
        .retirements
        .borrow_mut()
        .extend([Poll::Pending, Poll::Ready(Ok(()))]);

    assert_eq!(owner.invalidate(&first), Some(Retirement::Pending));
    assert!(owner.revalidate(&first, &cancelled).is_err());
    assert!(owner.acquire(&cancelled).is_err());
    assert_eq!(source.drops.get(), 0);
    assert_eq!(*source.retired.borrow(), [1]);
    assert_eq!(owner.poll_retirement(), Retirement::Complete);
    assert_eq!(source.drops.get(), 1);
    let second = owner.acquire(&cancelled).unwrap();
    assert_eq!(
        *source.opened.borrow(),
        ["opaque-original", "opaque-original"]
    );

    let reads = source.reads.get();
    assert_eq!(owner.invalidate(&first), None);
    assert!(owner.revalidate(&first, &cancelled).is_err());
    assert_eq!(source.reads.get(), reads);
    owner.revalidate(&second, &cancelled).unwrap();
    assert_eq!(owner.invalidate(&second), Some(Retirement::Complete));
    assert_eq!(*source.retired.borrow(), [1, 1, 2]);
    assert_eq!(source.drops.get(), 2);
}

#[test]
fn replacement_during_acquisition_cannot_escape_uncertain_retirement() {
    let source = fake();
    let cancelled = AtomicBool::new(false);
    source.scans.borrow_mut().extend([
        source.rows.borrow().clone(),
        vec![row("opaque-replacement", 0x02e0, Some((1, 2)))],
    ]);
    source
        .retirements
        .borrow_mut()
        .push_back(Poll::Ready(Err(io::ErrorKind::Other.into())));
    let mut owner = scripted_owner(&source);

    assert!(owner.acquire(&cancelled).is_err());
    assert_eq!(owner.retirement(), Retirement::Uncertain);
    assert!(owner.acquire(&cancelled).is_err());
    assert_eq!(owner.stop(), Retirement::Uncertain);
    assert_eq!(owner.poll_retirement(), Retirement::Uncertain);
    assert_eq!(*source.retired.borrow(), [1]);
    assert_eq!(*source.opened.borrow(), ["opaque-original"]);
    assert_eq!(source.drops.get(), 0);
}

#[test]
fn observation_loss_retires_the_old_handle_and_stop_drains_it_once() {
    let source = fake();
    let cancelled = AtomicBool::new(false);
    let mut owner = scripted_owner(&source);
    let epoch = owner.acquire(&cancelled).unwrap();
    source.rows.replace(vec![None]);
    source
        .retirements
        .borrow_mut()
        .extend([Poll::Pending, Poll::Pending, Poll::Ready(Ok(()))]);

    assert!(owner.revalidate(&epoch, &cancelled).is_err());
    assert_eq!(owner.retirement(), Retirement::Pending);
    source
        .rows
        .replace(vec![row("opaque-replacement", 0x02e0, Some((1, 2)))]);
    assert!(owner.acquire(&cancelled).is_err());
    cancelled.store(true, Ordering::Relaxed);
    assert_eq!(owner.stop(), Retirement::Pending);
    assert_eq!(owner.poll_retirement(), Retirement::Pending);
    assert_eq!(owner.poll_retirement(), Retirement::Complete);
    assert_eq!(owner.stop(), Retirement::Complete);
    assert_eq!(*source.retired.borrow(), [1, 1, 1]);
    assert_eq!(source.drops.get(), 1);
    cancelled.store(false, Ordering::Relaxed);
    assert!(owner.acquire(&cancelled).is_err());
    assert_eq!(*source.opened.borrow(), ["opaque-original"]);
}

#[test]
fn stop_revokes_active_epoch_before_waiting_for_retirement() {
    let source = fake();
    let cancelled = AtomicBool::new(false);
    let mut owner = scripted_owner(&source);
    let epoch = owner.acquire(&cancelled).unwrap();
    source.retirements.borrow_mut().push_back(Poll::Pending);
    assert_eq!(owner.stop(), Retirement::Pending);
    let reads = source.reads.get();
    assert_eq!(owner.invalidate(&epoch), None);
    assert!(owner.revalidate(&epoch, &cancelled).is_err());
    assert_eq!(source.reads.get(), reads);
    assert!(owner.acquire(&cancelled).is_err());
    assert_eq!(owner.poll_retirement(), Retirement::Complete);
    assert!(owner.acquire(&cancelled).is_err());
    assert_eq!(*source.retired.borrow(), [1, 1]);
    assert_eq!(source.drops.get(), 1);
}

#[test]
fn epochs_from_another_owner_cannot_observe_or_invalidate_this_handle() {
    let source = fake();
    let other_source = fake();
    let cancelled = AtomicBool::new(false);
    let mut owner = scripted_owner(&source);
    let mut other_owner = scripted_owner(&other_source);
    let epoch = owner.acquire(&cancelled).unwrap();
    let foreign = other_owner.acquire(&cancelled).unwrap();
    assert_eq!(owner.invalidate(&foreign), None);
    assert!(owner.revalidate(&foreign, &cancelled).is_err());
    assert_eq!(source.reads.get(), 2);
    owner.revalidate(&epoch, &cancelled).unwrap();
    assert_eq!(owner.stop(), Retirement::Complete);
    assert_eq!(other_owner.stop(), Retirement::Complete);
}

const MATRIX_ROLE: Role = Role {
    usage: (0x0c, 0x01),
    feature_bytes: 375,
};

const MATRIX: Descriptor = Descriptor {
    usage: (0x0c, 0x01),
    feature_bytes: 375,
    ..EXACT
};

/// The control collection and two consumer collections, the second the
/// matrix by its opened metadata.
fn matrix_fake() -> Rc<Fake> {
    let fake = fake();
    fake.rows.replace(vec![
        row("opaque-original", 0x02e0, Some((1, 2))),
        row("opaque-consumer-a", 0x02e0, Some((0x0c, 0x01))),
        row("opaque-consumer-b", 0x02e0, Some((0x0c, 0x01))),
        row("opaque-other-device", 0x02c6, Some((0x0c, 0x01))),
    ]);
    fake
}

fn consumer(feature_bytes: u16) -> io::Result<Descriptor> {
    Ok(Descriptor {
        feature_bytes,
        ..MATRIX
    })
}

#[test]
fn the_one_consumer_collection_with_the_matrix_length_is_selected() {
    let fake = matrix_fake();
    fake.descriptors.replace([consumer(3), Ok(MATRIX)].into());
    let cancelled = AtomicBool::new(false);
    let identity = RoleIdentity::select(&fake, MATRIX_ROLE, &cancelled).unwrap();
    assert_eq!(identity.path(), "opaque-consumer-b");
    assert_eq!(identity.model(), "RZ09-0581");
    assert_eq!(identity.descriptor(), MATRIX);
    // Each candidate was described through its own handle, released at once.
    assert_eq!(
        *fake.opened.borrow(),
        ["opaque-consumer-a", "opaque-consumer-b"]
    );
    assert_eq!(fake.drops.get(), 2);
}

#[test]
fn no_exact_or_two_exact_matrix_collections_select_nothing() {
    let cancelled = AtomicBool::new(false);
    for descriptors in [
        vec![consumer(3), consumer(91)],
        vec![Ok(MATRIX), Ok(MATRIX)],
        vec![
            consumer(3),
            Ok(Descriptor {
                device: (0x1532, 0x02c6),
                ..MATRIX
            }),
        ],
        vec![
            consumer(3),
            Ok(Descriptor {
                usage: (0x0c, 0x02),
                ..MATRIX
            }),
        ],
    ] {
        let fake = matrix_fake();
        fake.descriptors.replace(descriptors.into());
        assert!(RoleIdentity::select(&fake, MATRIX_ROLE, &cancelled).is_err());
        assert_eq!(fake.drops.get(), 2);
    }
}

#[test]
fn an_unhinted_or_unreadable_interface_selects_nothing_and_opens_nothing() {
    let cancelled = AtomicBool::new(false);
    for additional in [row("opaque-unresolved", 0x02e0, None), None] {
        let fake = matrix_fake();
        fake.rows.borrow_mut().push(additional);
        assert!(RoleIdentity::select(&fake, MATRIX_ROLE, &cancelled).is_err());
        assert!(fake.opened.borrow().is_empty());
    }
}

#[test]
fn a_candidate_that_cannot_be_described_refuses_the_selection() {
    let cancelled = AtomicBool::new(false);
    let fake = matrix_fake();
    fake.open_fails.set(true);
    assert!(RoleIdentity::select(&fake, MATRIX_ROLE, &cancelled).is_err());
    let fake = matrix_fake();
    fake.descriptors
        .replace([Err(io::ErrorKind::NotFound.into()), Ok(MATRIX)].into());
    assert!(RoleIdentity::select(&fake, MATRIX_ROLE, &cancelled).is_err());
    assert_eq!(fake.drops.get(), 1);
}

#[test]
fn a_changed_role_descriptor_or_candidate_set_fails_verification() {
    let cancelled = AtomicBool::new(false);
    let select = |fake: &Rc<Fake>| {
        fake.descriptors.replace([consumer(3), Ok(MATRIX)].into());
        RoleIdentity::select(fake, MATRIX_ROLE, &cancelled).unwrap()
    };
    let fake = matrix_fake();
    let identity = select(&fake);
    let handle = fake.open_metadata("opaque-consumer-b").unwrap();
    fake.descriptors.replace([Ok(MATRIX)].into());
    identity.verify(&fake, &handle, &cancelled).unwrap();
    // Case-only path changes are the same candidate.
    fake.rows.borrow_mut()[2] = row("OPAQUE-CONSUMER-B", 0x02e0, Some((0x0c, 0x01)));
    fake.descriptors.replace([Ok(MATRIX)].into());
    identity.verify(&fake, &handle, &cancelled).unwrap();

    fake.descriptors.replace([consumer(374)].into());
    assert!(identity.verify(&fake, &handle, &cancelled).is_err());

    for change in [
        |rows: &mut Rows| rows.push(row("opaque-consumer-c", 0x02e0, Some((0x0c, 0x01)))),
        |rows: &mut Rows| drop(rows.remove(1)),
        |rows: &mut Rows| rows[2] = row("opaque-replaced", 0x02e0, Some((0x0c, 0x01))),
    ] {
        let fake = matrix_fake();
        let identity = select(&fake);
        let handle = fake.open_metadata("opaque-consumer-b").unwrap();
        change(&mut fake.rows.borrow_mut());
        fake.descriptors.replace([Ok(MATRIX)].into());
        assert!(identity.verify(&fake, &handle, &cancelled).is_err());
    }

    let fake = matrix_fake();
    let identity = select(&fake);
    let handle = fake.open_metadata("opaque-consumer-b").unwrap();
    fake.removal_observed.set(true);
    assert!(identity.verify(&fake, &handle, &cancelled).is_err());
    fake.removal_observed.set(false);
    cancelled.store(true, Ordering::Relaxed);
    assert!(identity.verify(&fake, &handle, &cancelled).is_err());
}

/// The production matrix role and its reviewed identity select the exact
/// RZ09-0581 matrix collection, and refuse another model's matrix
/// collection that the generic role selection admits.
#[cfg(feature = "service-lighting-v1")]
#[test]
fn the_reviewed_matrix_role_selects_the_exact_collection_only() {
    let cancelled = AtomicBool::new(false);
    let fake = matrix_fake();
    fake.descriptors.replace([consumer(3), Ok(MATRIX)].into());
    let identity = performance::select_matrix(&fake, &cancelled).unwrap();
    assert_eq!(identity.path(), "opaque-consumer-b");
    assert_eq!(identity.descriptor(), MATRIX);

    let other = fake_host("Blade 16 - RZ09-0528");
    other.rows.replace(vec![
        row("opaque-original", 0x02c6, Some((1, 2))),
        row("opaque-consumer", 0x02c6, Some((0x0c, 0x01))),
    ]);
    let descriptor = Descriptor {
        device: (0x1532, 0x02c6),
        ..MATRIX
    };
    other.descriptors.replace([Ok(descriptor)].into());
    let generic = RoleIdentity::select(&other, MATRIX_ROLE, &cancelled).unwrap();
    assert_eq!(generic.model(), "RZ09-0528");
    other.descriptors.replace([Ok(descriptor)].into());
    assert!(performance::select_matrix(&other, &cancelled).is_err());
}

/// The reviewed matrix identity needs this module's model and its device
/// each. The selection binds the device to the host model's catalog entry,
/// so no selection differs in one alone; each clause is shown on the exact
/// identity with only that field changed.
#[cfg(feature = "service-lighting-v1")]
#[test]
fn the_reviewed_matrix_identity_needs_the_model_and_the_device() {
    let cancelled = AtomicBool::new(false);
    let fake = matrix_fake();
    let select = || {
        fake.descriptors.replace([consumer(3), Ok(MATRIX)].into());
        RoleIdentity::select(&fake, MATRIX_ROLE, &cancelled).unwrap()
    };
    assert!(performance::reviewed_matrix(&select()));
    let mut model = select();
    model.candidates.model = "RZ09-0528".into();
    assert!(!performance::reviewed_matrix(&model), "another model");
    let mut device = select();
    device.descriptor.device = (0x1532, 0x02c6);
    assert!(!performance::reviewed_matrix(&device), "another device");
}
