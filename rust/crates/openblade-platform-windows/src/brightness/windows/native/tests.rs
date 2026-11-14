//! Inert Automation allocations and local fake vtables only. Never initializes
//! COM, activates WMI, queries a provider or changes a monitor.
use super::*;
use std::{
    cell::{Cell, RefCell},
    mem::{offset_of, size_of},
};
use windows_sys::Win32::{
    Foundation::E_FAIL,
    System::{
        Ole::{SafeArrayCreateVector, SafeArrayPutElement},
        Variant::{VT_BYREF, VT_UI4},
        Wmi::WBEM_S_PARTIAL_RESULTS,
    },
};

#[test]
fn sdk_vtable_offsets_include_iunknown() {
    let slot = size_of::<Slot>();
    assert_eq!(offset_of!(UnknownVtable, release), 2 * slot);
    assert_eq!(offset_of!(LocatorVtable, connect), 3 * slot);
    assert_eq!(offset_of!(ServicesVtable, get_object), 6 * slot);
    assert_eq!(offset_of!(ServicesVtable, exec_query), 20 * slot);
    assert_eq!(offset_of!(ServicesVtable, exec_method), 24 * slot);
    assert_eq!(offset_of!(ObjectVtable, get), 4 * slot);
    assert_eq!(offset_of!(ObjectVtable, put), 5 * slot);
    assert_eq!(offset_of!(ObjectVtable, spawn_instance), 15 * slot);
    assert_eq!(offset_of!(ObjectVtable, get_method), 19 * slot);
    assert_eq!(offset_of!(EnumeratorVtable, next), 4 * slot);
    assert_eq!(offset_of!(PendingVtable, result), 3 * slot);
    assert_eq!(offset_of!(PendingVtable, status), 6 * slot);
}

fn scalar(kind: u16, bits: i32) -> Value {
    let mut value = Value::default();
    value.0.Anonymous.Anonymous.vt = kind;
    value.0.Anonymous.Anonymous.Anonymous.lVal = bits;
    value
}

fn text_value(units: &[u16]) -> Value {
    let mut value = Value::default();
    // SAFETY: exact readable unit count and ownership transfers to Value.
    unsafe {
        let ptr = SysAllocStringLen(units.as_ptr(), units.len() as u32);
        assert!(!ptr.is_null());
        value.0.Anonymous.Anonymous.vt = VT_BSTR;
        value.0.Anonymous.Anonymous.Anonymous.bstrVal = ptr;
    }
    value
}

#[test]
fn strings_preserve_absence_and_reject_unbounded_or_malformed_input() {
    assert!(Value::default().text(16).unwrap().is_none());
    assert!(scalar(VT_NULL, 0).text(16).unwrap().is_none());
    assert_eq!(
        text_value(&[65, 66]).text(2).unwrap().as_deref(),
        Some("AB")
    );
    assert_eq!(text_value(&[]).text(0).unwrap().as_deref(), Some(""));
    assert!(matches!(
        text_value(&[65, 66]).text(1),
        Err(NativeFailure::InvalidData)
    ));
    assert!(matches!(
        text_value(&[65, 0, 66]).text(3),
        Err(NativeFailure::InvalidData)
    ));
    assert!(matches!(
        text_value(&[0xd800]).text(1),
        Err(NativeFailure::InvalidData)
    ));
    assert!(matches!(
        scalar(VT_I4, 7).text(20),
        Err(NativeFailure::InvalidData)
    ));
    assert!(Bstr::new("before\0after").is_err());
}

#[test]
fn scalar_decoders_do_not_coerce_types_or_noncanonical_booleans() {
    assert_eq!(scalar(VT_BOOL, -1).boolean().unwrap(), Some(true));
    assert_eq!(scalar(VT_BOOL, 0).boolean().unwrap(), Some(false));
    assert!(matches!(
        scalar(VT_BOOL, 1).boolean(),
        Err(NativeFailure::InvalidData)
    ));
    assert!(matches!(
        scalar(VT_I4, 1).boolean(),
        Err(NativeFailure::InvalidData)
    ));
    assert_eq!(scalar(VT_UI1, 255).byte().unwrap(), Some(255));
    assert!(matches!(
        scalar(VT_I4, 40).byte(),
        Err(NativeFailure::InvalidData)
    ));
    // Null BYREF payload has no ownership and VariantClear does not dereference it.
    assert!(matches!(
        scalar(VT_BYREF | VT_UI1, 0).byte(),
        Err(NativeFailure::InvalidData)
    ));
}

#[test]
fn process_security_caches_success_and_unknown_prior_policy() {
    use windows_sys::Win32::Foundation::RPC_E_TOO_LATE;
    for status in [S_OK, RPC_E_TOO_LATE] {
        let policy = std::sync::OnceLock::new();
        let expected = ok(status);
        assert_eq!(initialize_policy(&policy, || ok(status)), expected);
        assert_eq!(
            initialize_policy(&policy, || panic!(
                "duplicate process policy initialization"
            )),
            expected
        );
    }
}

fn array_value(lower: i32, bytes: &[u8]) -> Value {
    let mut value = Value::default();
    // SAFETY: standalone Automation allocation, never connected to COM/WMI.
    let array = unsafe { SafeArrayCreateVector(VT_UI1, lower, bytes.len() as u32) };
    assert!(!array.is_null());
    // Exact owned array kind, transfers destruction to VariantClear.
    value.0.Anonymous.Anonymous.vt = VT_ARRAY | VT_UI1;
    value.0.Anonymous.Anonymous.Anonymous.parray = array;
    for (offset, byte) in bytes.iter().enumerate() {
        let index = lower + offset as i32;
        // SAFETY: index within allocated bounds and source points to one byte.
        assert_eq!(
            unsafe { SafeArrayPutElement(array, &index, (byte as *const u8).cast()) },
            S_OK
        );
    }
    value
}

#[test]
fn arrays_support_real_bounds_and_reject_wrong_shape_before_reading() {
    let value = array_value(-3, &[0, 50, 100]);
    assert_eq!(value.bytes(3).unwrap(), Some(vec![0, 50, 100]));
    assert!(matches!(value.bytes(2), Err(NativeFailure::InvalidData)));
    assert!(Value::default().bytes(256).unwrap().is_none());
    let mut null_array = Value::default();
    // Null owned array has no allocation for VariantClear to release.
    null_array.0.Anonymous.Anonymous.vt = VT_ARRAY | VT_UI1;
    assert!(matches!(
        null_array.bytes(256),
        Err(NativeFailure::InvalidData)
    ));
    let mut wrong_array = Value::default();
    // SAFETY: valid standalone uint32 array deliberately tagged as byte array;
    // Automation destroys through the SAFEARRAY descriptor, not this tag.
    unsafe {
        let array = SafeArrayCreateVector(VT_UI4, 0, 1);
        assert!(!array.is_null());
        wrong_array.0.Anonymous.Anonymous.vt = VT_ARRAY | VT_UI1;
        wrong_array.0.Anonymous.Anonymous.Anonymous.parray = array;
    }
    assert!(matches!(
        wrong_array.bytes(256),
        Err(NativeFailure::InvalidData)
    ));
}

#[repr(C)]
struct FakeObject {
    vtable: *const ObjectVtable,
    releases: Rc<Cell<u32>>,
    status: i32,
    cim: i32,
    kind: u16,
    bits: i32,
    writes: Rc<RefCell<Vec<PropertyWrite>>>,
}
#[derive(Debug, PartialEq, Eq)]
struct PropertyWrite {
    name: String,
    flags: i32,
    cim: i32,
    kind: u16,
    bits: i32,
}
unsafe extern "system" fn release_object(raw: Raw) -> u32 {
    // SAFETY: fake COM pointer owns exactly this Box and Release occurs once.
    let object = unsafe { Box::from_raw(raw.cast::<FakeObject>()) };
    object.releases.set(object.releases.get() + 1);
    0
}
unsafe extern "system" fn get_object(
    raw: Raw,
    _: PCWSTR,
    _: i32,
    value: *mut VARIANT,
    cim: *mut i32,
    _: *mut i32,
) -> i32 {
    // SAFETY: fixture pointer and outputs supplied by Object::get.
    let object = unsafe { &*raw.cast::<FakeObject>() };
    if object.status == S_OK {
        // SAFETY: writable initialized outputs; scalar has no resource payload.
        unsafe {
            *value = scalar(object.kind, object.bits).0;
            *cim = object.cim;
        }
    }
    object.status
}
unsafe extern "system" fn put_object(
    raw: Raw,
    name: PCWSTR,
    flags: i32,
    value: *mut VARIANT,
    cim: i32,
) -> i32 {
    // SAFETY: fixture pointer and live scalar VARIANT from Object::put.
    let object = unsafe { &*raw.cast::<FakeObject>() };
    let value = unsafe { &*value };
    let kind = unsafe { value.Anonymous.Anonymous.vt };
    let bits = match kind {
        VT_I4 => unsafe { value.Anonymous.Anonymous.Anonymous.lVal },
        VT_UI1 => i32::from(unsafe { value.Anonymous.Anonymous.Anonymous.bVal }),
        _ => return E_FAIL,
    };
    let mut units = Vec::new();
    for index in 0..32 {
        // SAFETY: test only supplies NUL-terminated fixed names, at most 10 units.
        let unit = unsafe { *name.add(index) };
        if unit == 0 {
            break;
        }
        units.push(unit);
    }
    let Ok(name) = String::from_utf16(&units) else {
        return E_FAIL;
    };
    object.writes.borrow_mut().push(PropertyWrite {
        name,
        flags,
        cim,
        kind,
        bits,
    });
    S_OK
}
unsafe extern "system" fn unused_spawn(_: Raw, _: i32, _: *mut Raw) -> i32 {
    E_FAIL
}
unsafe extern "system" fn unused_method(
    _: Raw,
    _: PCWSTR,
    _: i32,
    _: *mut Raw,
    _: *mut Raw,
) -> i32 {
    E_FAIL
}

struct ObjectFixture {
    object: Object,
    _vtable: Box<ObjectVtable>,
    releases: Rc<Cell<u32>>,
    writes: Rc<RefCell<Vec<PropertyWrite>>>,
}
impl ObjectFixture {
    fn new(status: i32, cim: i32, kind: u16, bits: i32) -> Self {
        let table = Box::new(ObjectVtable {
            unknown: UnknownVtable {
                query_interface: null(),
                add_ref: null(),
                release: release_object,
            },
            qualifier_set: null(),
            get: get_object,
            put: put_object,
            slots_6_to_14: [null(); 9],
            spawn_instance: unused_spawn,
            slots_16_to_18: [null(); 3],
            get_method: unused_method,
        });
        let releases = Rc::new(Cell::new(0));
        let writes = Rc::new(RefCell::new(Vec::new()));
        let raw = Box::into_raw(Box::new(FakeObject {
            vtable: &*table,
            releases: Rc::clone(&releases),
            status,
            cim,
            kind,
            bits,
            writes: Rc::clone(&writes),
        }))
        .cast();
        let object = Object(Com {
            raw,
            apartment: Rc::new(Apartment { initialized: false }),
            marker: PhantomData,
        });
        Self {
            object,
            _vtable: table,
            releases,
            writes,
        }
    }
}

#[test]
fn object_get_distinguishes_missing_null_bad_cim_and_bad_variant() {
    let missing = ObjectFixture::new(WBEM_E_NOT_FOUND, 0, VT_EMPTY, 0);
    assert_eq!(missing.object.byte(w!("CurrentBrightness")).unwrap(), None);
    let null = ObjectFixture::new(S_OK, CIM_UINT8, VT_NULL, 0);
    assert_eq!(null.object.byte(w!("CurrentBrightness")).unwrap(), None);
    let wrong_cim = ObjectFixture::new(S_OK, CIM_UINT32, VT_UI1, 50);
    assert!(matches!(
        wrong_cim.object.byte(w!("CurrentBrightness")),
        Err(NativeFailure::InvalidData)
    ));
    let wrong_variant = ObjectFixture::new(S_OK, CIM_UINT8, VT_I4, 50);
    assert!(matches!(
        wrong_variant.object.byte(w!("CurrentBrightness")),
        Err(NativeFailure::InvalidData)
    ));
    let partial = ObjectFixture::new(WBEM_S_PARTIAL_RESULTS, CIM_UINT8, VT_UI1, 50);
    assert!(matches!(
        partial.object.byte(w!("CurrentBrightness")),
        Err(NativeFailure::Status(WBEM_S_PARTIAL_RESULTS))
    ));
}

#[test]
fn method_acknowledgement_preserves_uint32_status_and_rejects_bool() {
    for bits in [0, 1, -1, i32::MIN] {
        let fixture = ObjectFixture::new(S_OK, CIM_UINT32, VT_I4, bits);
        assert!(
            matches!(fixture.object.ack().unwrap(), Acknowledgement::Status(status) if status == bits as u32)
        );
    }
    let boolean = ObjectFixture::new(S_OK, CIM_BOOLEAN, VT_BOOL, -1);
    assert!(matches!(
        boolean.object.ack(),
        Err(NativeFailure::InvalidData)
    ));
    let missing = ObjectFixture::new(WBEM_E_NOT_FOUND, 0, VT_EMPTY, 0);
    assert!(matches!(
        missing.object.ack(),
        Err(NativeFailure::MissingData)
    ));
    let null = ObjectFixture::new(S_OK, CIM_UINT32, VT_NULL, 0);
    assert!(matches!(null.object.ack(), Err(NativeFailure::MissingData)));
    let empty = ObjectFixture::new(S_OK, CIM_UINT32, VT_EMPTY, 0);
    assert!(matches!(
        empty.object.ack(),
        Err(NativeFailure::MissingData)
    ));
}

#[test]
fn uint32_preserves_unsigned_bits_without_coercion() {
    let value = ObjectFixture::new(S_OK, CIM_UINT32, VT_I4, -1);
    assert_eq!(value.object.uint32(w!("Levels")).unwrap(), Some(u32::MAX));
    let missing = ObjectFixture::new(WBEM_E_NOT_FOUND, 0, VT_EMPTY, 0);
    assert_eq!(missing.object.uint32(w!("Levels")).unwrap(), None);
    let wrong = ObjectFixture::new(S_OK, CIM_UINT32, VT_UI1, 10);
    assert!(matches!(
        wrong.object.uint32(w!("Levels")),
        Err(NativeFailure::InvalidData)
    ));
}

#[test]
fn fixed_input_parameters_use_typed_scalars_and_preserve_schema() {
    let timeout = ObjectFixture::new(S_OK, CIM_UINT32, VT_NULL, 0);
    timeout
        .object
        .require_type(w!("Timeout"), CIM_UINT32, VT_I4)
        .unwrap();
    let mut timeout_value = scalar(VT_I4, 0);
    timeout
        .object
        .put(w!("Timeout"), &mut timeout_value)
        .unwrap();
    assert_eq!(
        &*timeout.writes.borrow(),
        &[PropertyWrite {
            name: "Timeout".into(),
            flags: 0,
            cim: 0,
            kind: VT_I4,
            bits: 0,
        }]
    );
    let brightness = ObjectFixture::new(S_OK, CIM_UINT8, VT_NULL, 0);
    brightness
        .object
        .require_type(w!("Brightness"), CIM_UINT8, VT_UI1)
        .unwrap();
    let mut brightness_value = scalar(VT_UI1, 77);
    brightness
        .object
        .put(w!("Brightness"), &mut brightness_value)
        .unwrap();
    assert_eq!(
        &*brightness.writes.borrow(),
        &[PropertyWrite {
            name: "Brightness".into(),
            flags: 0,
            cim: 0,
            kind: VT_UI1,
            bits: 77,
        }]
    );
    assert_eq!(brightness_value.byte().unwrap(), Some(77));
}

#[test]
fn com_owner_and_failed_out_parameter_release_exactly_once() {
    let fixture = ObjectFixture::new(S_OK, CIM_UINT8, VT_UI1, 50);
    let releases = Rc::clone(&fixture.releases);
    drop(fixture);
    assert_eq!(releases.get(), 1);
    let fixture = ObjectFixture::new(S_OK, CIM_UINT8, VT_UI1, 50);
    let releases = Rc::clone(&fixture.releases);
    let ObjectFixture {
        object, _vtable, ..
    } = fixture;
    let object = std::mem::ManuallyDrop::new(object);
    let out = Out::<ObjectVtable> {
        raw: object.0.raw,
        apartment: Rc::clone(&object.0.apartment),
        marker: PhantomData,
    };
    // Hand the one owned reference to the failure cleanup guard. This models
    // a provider that supplied a reference but then returned a failing HRESULT.
    drop(out);
    assert_eq!(releases.get(), 1);
    // The manually dropped wrapper's apartment Rc is fixture-only; release it.
    // SAFETY: raw reference already released; only its separate Rc field is read.
    unsafe {
        drop(std::ptr::read(&object.0.apartment));
    }
    drop(_vtable);
}

#[repr(C)]
struct FakeEnumerator {
    vtable: *const EnumeratorVtable,
    status: i32,
    count: u32,
}
unsafe extern "system" fn release_enumerator(raw: Raw) -> u32 {
    // SAFETY: fixture uniquely owns this Box.
    drop(unsafe { Box::from_raw(raw.cast::<FakeEnumerator>()) });
    0
}
unsafe extern "system" fn next_object(
    raw: Raw,
    _: i32,
    requested: u32,
    output: *mut Raw,
    count: *mut u32,
) -> i32 {
    assert_eq!(requested, 1);
    // SAFETY: fixture pointer and writable output slots from Enumerator::next.
    let fixture = unsafe { &*raw.cast::<FakeEnumerator>() };
    unsafe {
        *output = null_mut();
        *count = fixture.count;
    }
    fixture.status
}

#[test]
fn enumeration_requires_exact_completion_status_and_count() {
    let table = Box::new(EnumeratorVtable {
        unknown: UnknownVtable {
            query_interface: null(),
            add_ref: null(),
            release: release_enumerator,
        },
        reset: null(),
        next: next_object,
    });
    for (status, count) in [
        (WBEM_S_FALSE, 0),
        (WBEM_S_TIMEDOUT, 0),
        (S_OK, 0),
        (WBEM_S_FALSE, 1),
        (WBEM_S_TIMEDOUT, 1),
        (S_OK, 1),
        (S_OK, 2),
        (WBEM_S_PARTIAL_RESULTS, 0),
        (E_FAIL, 0),
    ] {
        let raw = Box::into_raw(Box::new(FakeEnumerator {
            vtable: &*table,
            status,
            count,
        }))
        .cast();
        let mut enumerator = Enumerator(Com {
            raw,
            apartment: Rc::new(Apartment { initialized: false }),
            marker: PhantomData,
        });
        let result = enumerator.next(0);
        match (status, count) {
            (WBEM_S_FALSE, 0) => assert!(matches!(result, Ok(Next::End))),
            (WBEM_S_TIMEDOUT, 0) => assert!(matches!(result, Ok(Next::Pending))),
            (WBEM_S_PARTIAL_RESULTS | E_FAIL, _) => {
                assert!(matches!(result, Err(NativeFailure::Status(s)) if s == status))
            }
            // All pointers are null; even S_OK/1 must fail instead of wrapping null.
            _ => assert!(matches!(result, Err(NativeFailure::InvalidData))),
        }
        assert!(matches!(
            enumerator.next(-1),
            Err(NativeFailure::InvalidData)
        ));
    }
}

#[repr(C)]
struct FakePending {
    vtable: *const PendingVtable,
    status: i32,
    call_status: i32,
}
unsafe extern "system" fn release_pending(raw: Raw) -> u32 {
    // SAFETY: fixture uniquely owns this Box.
    drop(unsafe { Box::from_raw(raw.cast::<FakePending>()) });
    0
}
unsafe extern "system" fn get_status(raw: Raw, _: i32, output: *mut i32) -> i32 {
    // SAFETY: fixture pointer and writable status output from Pending::status.
    let fixture = unsafe { &*raw.cast::<FakePending>() };
    unsafe {
        *output = fixture.call_status;
    }
    fixture.status
}
unsafe extern "system" fn get_result(raw: Raw, _: i32, output: *mut Raw) -> i32 {
    // SAFETY: fixture pointer and writable object output from Pending::result.
    let fixture = unsafe { &*raw.cast::<FakePending>() };
    unsafe {
        *output = null_mut();
    }
    fixture.status
}

#[test]
fn pending_distinguishes_timeout_hresult_provider_status_and_missing_output() {
    let table = Box::new(PendingVtable {
        unknown: UnknownVtable {
            query_interface: null(),
            add_ref: null(),
            release: release_pending,
        },
        result: get_result,
        slots_4_to_5: [null(); 2],
        status: get_status,
    });
    for status in [S_OK, WBEM_S_TIMEDOUT, WBEM_S_PARTIAL_RESULTS, E_FAIL] {
        let raw = Box::into_raw(Box::new(FakePending {
            vtable: &*table,
            status,
            call_status: E_FAIL,
        }))
        .cast();
        let pending = Pending(Com {
            raw,
            apartment: Rc::new(Apartment { initialized: false }),
            marker: PhantomData,
        });
        match status {
            S_OK => {
                assert!(matches!(pending.status(0), Ok(Some(E_FAIL))));
                assert!(matches!(pending.result(0), Ok(Next::End)));
            }
            WBEM_S_TIMEDOUT => {
                assert!(matches!(pending.status(0), Ok(None)));
                assert!(matches!(pending.result(0), Ok(Next::Pending)));
            }
            _ => {
                assert!(matches!(pending.status(0), Err(NativeFailure::Status(s)) if s == status));
                assert!(matches!(pending.result(0), Err(NativeFailure::Status(s)) if s == status));
            }
        }
        assert!(matches!(
            pending.status(-1),
            Err(NativeFailure::InvalidData)
        ));
        assert!(matches!(
            pending.result(-1),
            Err(NativeFailure::InvalidData)
        ));
    }
}
