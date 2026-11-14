//! Narrow current-user Root\\WMI COM boundary; no automation dispatch or arbitrary methods.
//!
//! `windows-sys` 0.61.2 supplies functions, constants and Automation layouts, but
//! not these COM interfaces. The private prefixes below follow Windows SDK
//! 10.0.26100.0 um/WbemCli.h, including IUnknown's three leading slots.
//! See Microsoft IWbemServices::ExecMethod, IEnumWbemClassObject::Next,
//! IWbemCallResult::GetCallStatus and WmiMonitorBrightnessMethods::WmiSetBrightness.
#![allow(unsafe_code)]

use super::{Acknowledgement, NativeFailure, Next, Submission};
use std::{
    ffi::c_void,
    marker::PhantomData,
    ptr::{null, null_mut},
    rc::Rc,
};
use windows_sys::{
    Win32::{
        Foundation::{
            E_OUTOFMEMORY, S_FALSE, S_OK, SysAllocStringLen, SysFreeString, SysStringLen,
        },
        Globalization::{CSTR_EQUAL, CompareStringOrdinal},
        System::{
            Com::{
                CLSCTX_INPROC_SERVER, COINIT_MULTITHREADED, CoCreateInstance, CoInitializeEx,
                CoInitializeSecurity, CoSetProxyBlanket, CoUninitialize, EOAC_NONE,
                RPC_C_AUTHN_LEVEL_CALL, RPC_C_IMP_LEVEL_IMPERSONATE,
            },
            Ole::{
                SafeArrayGetDim, SafeArrayGetElement, SafeArrayGetElemsize, SafeArrayGetLBound,
                SafeArrayGetUBound, SafeArrayGetVartype,
            },
            Rpc::{RPC_C_AUTHN_WINNT, RPC_C_AUTHZ_NONE},
            Variant::{
                VARIANT, VT_ARRAY, VT_BOOL, VT_BSTR, VT_EMPTY, VT_I4, VT_NULL, VT_UI1, VariantClear,
            },
            Wmi::{
                CIM_BOOLEAN, CIM_FLAG_ARRAY, CIM_STRING, CIM_UINT8, CIM_UINT32, WBEM_E_NOT_FOUND,
                WBEM_FLAG_CONNECT_USE_MAX_WAIT, WBEM_FLAG_ENSURE_LOCATABLE, WBEM_FLAG_FORWARD_ONLY,
                WBEM_FLAG_RETURN_IMMEDIATELY, WBEM_S_FALSE, WBEM_S_TIMEDOUT, WbemLocator,
            },
        },
    },
    core::{BSTR, GUID, PCWSTR, w},
};

type Raw = *mut c_void;
type Slot = *const c_void;
const IID_WBEM_LOCATOR: GUID = GUID::from_u128(0xdc12a687_737f_11cf_884d_00aa004b2e24);

#[repr(C)]
struct UnknownVtable {
    query_interface: Slot,
    add_ref: Slot,
    release: unsafe extern "system" fn(Raw) -> u32,
}
#[repr(C)]
struct LocatorVtable {
    unknown: UnknownVtable,
    connect:
        unsafe extern "system" fn(Raw, BSTR, BSTR, BSTR, BSTR, i32, BSTR, Raw, *mut Raw) -> i32,
}
#[repr(C)]
struct ServicesVtable {
    unknown: UnknownVtable,
    slots_3_to_5: [Slot; 3],
    get_object: unsafe extern "system" fn(Raw, BSTR, i32, Raw, *mut Raw, *mut Raw) -> i32,
    slots_7_to_19: [Slot; 13],
    exec_query: unsafe extern "system" fn(Raw, BSTR, BSTR, i32, Raw, *mut Raw) -> i32,
    slots_21_to_23: [Slot; 3],
    exec_method:
        unsafe extern "system" fn(Raw, BSTR, BSTR, i32, Raw, Raw, *mut Raw, *mut Raw) -> i32,
}
#[repr(C)]
struct ObjectVtable {
    unknown: UnknownVtable,
    qualifier_set: Slot,
    get: unsafe extern "system" fn(Raw, PCWSTR, i32, *mut VARIANT, *mut i32, *mut i32) -> i32,
    put: unsafe extern "system" fn(Raw, PCWSTR, i32, *mut VARIANT, i32) -> i32,
    slots_6_to_14: [Slot; 9],
    spawn_instance: unsafe extern "system" fn(Raw, i32, *mut Raw) -> i32,
    slots_16_to_18: [Slot; 3],
    get_method: unsafe extern "system" fn(Raw, PCWSTR, i32, *mut Raw, *mut Raw) -> i32,
}
#[repr(C)]
struct EnumeratorVtable {
    unknown: UnknownVtable,
    reset: Slot,
    next: unsafe extern "system" fn(Raw, i32, u32, *mut Raw, *mut u32) -> i32,
}
#[repr(C)]
struct PendingVtable {
    unknown: UnknownVtable,
    result: unsafe extern "system" fn(Raw, i32, *mut Raw) -> i32,
    slots_4_to_5: [Slot; 2],
    status: unsafe extern "system" fn(Raw, i32, *mut i32) -> i32,
}

fn ok(status: i32) -> Result<(), NativeFailure> {
    if status == S_OK {
        Ok(())
    } else {
        Err(NativeFailure::Status(status))
    }
}

pub(in crate::brightness) fn ordinal_equal(left: &str, right: &str) -> bool {
    if left.is_ascii() && right.is_ascii() {
        return left.eq_ignore_ascii_case(right);
    }
    let left: Vec<_> = left.encode_utf16().collect();
    let right: Vec<_> = right.encode_utf16().collect();
    let (Ok(left_len), Ok(right_len)) = (i32::try_from(left.len()), i32::try_from(right.len()))
    else {
        return false;
    };
    // SAFETY: both slices remain live and their explicit lengths fit i32.
    unsafe {
        CompareStringOrdinal(left.as_ptr(), left_len, right.as_ptr(), right_len, 1) == CSTR_EQUAL
    }
}

static POLICY: std::sync::OnceLock<Result<(), NativeFailure>> = std::sync::OnceLock::new();

/// Owns the entry thread's MTA reference until all installed Agent features drain.
#[must_use]
pub struct ProcessSecurityApartment {
    _apartment: Rc<Apartment>,
}

/// Called by the installed Agent owner before its feature workers start. Keep
/// the first outcome, including RPC_E_TOO_LATE for an unknown prior policy.
pub fn initialize_process_security() -> Result<ProcessSecurityApartment, NativeFailure> {
    let apartment = Apartment::initialize();
    initialize_policy(&POLICY, || {
        apartment.as_ref().map_err(|&failure| failure)?;
        // SAFETY: outbound-only client with zero incoming authentication
        // services, no server security descriptor, and no custom credentials.
        // Impersonation is requested only on the worker's own WMI proxies.
        ok(unsafe {
            CoInitializeSecurity(
                null_mut(),
                0,
                null(),
                null(),
                RPC_C_AUTHN_LEVEL_CALL,
                windows_sys::Win32::System::Com::RPC_C_IMP_LEVEL_IDENTIFY,
                null(),
                EOAC_NONE as u32,
                null(),
            )
        })
    })?;
    Ok(ProcessSecurityApartment {
        _apartment: apartment?,
    })
}

/// Workers may read the host's established outcome. Only the entry thread
/// can initialize the policy before the first COM consumer starts.
pub fn process_security_status() -> Result<(), NativeFailure> {
    POLICY
        .get()
        .copied()
        .unwrap_or(Err(NativeFailure::MissingData))
}

fn initialize_policy(
    policy: &std::sync::OnceLock<Result<(), NativeFailure>>,
    initialize: impl FnOnce() -> Result<(), NativeFailure>,
) -> Result<(), NativeFailure> {
    *policy.get_or_init(initialize)
}

// Rc keeps every dependent object in the originating apartment and makes all
// wrappers !Send + !Sync. The final Release precedes the final CoUninitialize.
struct Apartment {
    initialized: bool,
}
impl Apartment {
    fn initialize() -> Result<Rc<Self>, NativeFailure> {
        // SAFETY: the caller owns this thread-local MTA reference and balances it on Drop.
        let status = unsafe { CoInitializeEx(null(), COINIT_MULTITHREADED as u32) };
        if status != S_OK && status != S_FALSE {
            return Err(NativeFailure::Status(status));
        }
        // The host owns process-wide COM security. This adapter sets explicit
        // current-user security only on its own WMI proxies.
        Ok(Rc::new(Self { initialized: true }))
    }
}
impl Drop for Apartment {
    fn drop(&mut self) {
        // SAFETY: one balancing call for our successful S_OK/S_FALSE initialization.
        if self.initialized {
            unsafe { CoUninitialize() };
        }
    }
}

struct Com<T> {
    raw: Raw,
    apartment: Rc<Apartment>,
    marker: PhantomData<T>,
}
impl<T> Com<T> {
    fn table(&self) -> &T {
        // SAFETY: only typed COM out-parameters construct Com<T>; all wrappers
        // retain an owned reference and the apartment for the entire borrow.
        unsafe { &**self.raw.cast::<*const T>() }
    }
    fn blanket(&self) -> Result<(), NativeFailure> {
        // SAFETY: a live proxy; null credentials select current user.
        ok(unsafe {
            CoSetProxyBlanket(
                self.raw,
                RPC_C_AUTHN_WINNT,
                RPC_C_AUTHZ_NONE,
                null(),
                RPC_C_AUTHN_LEVEL_CALL,
                RPC_C_IMP_LEVEL_IMPERSONATE,
                null(),
                EOAC_NONE as u32,
            )
        })
    }
}
impl<T> Drop for Com<T> {
    fn drop(&mut self) {
        // SAFETY: COM interface prefix is IUnknown; exactly one owned reference.
        unsafe { ((&**self.raw.cast::<*const UnknownVtable>()).release)(self.raw) };
    }
}

// Own an out parameter immediately, including unusual failure-plus-object
// responses, so every early return releases any reference actually returned.
struct Out<T> {
    raw: Raw,
    apartment: Rc<Apartment>,
    marker: PhantomData<T>,
}
impl<T> Out<T> {
    fn new(apartment: &Rc<Apartment>) -> Self {
        Self {
            raw: null_mut(),
            apartment: Rc::clone(apartment),
            marker: PhantomData,
        }
    }
    fn take(&mut self) -> Result<Com<T>, NativeFailure> {
        if self.raw.is_null() {
            return Err(NativeFailure::InvalidData);
        }
        Ok(Com {
            raw: std::mem::replace(&mut self.raw, null_mut()),
            apartment: Rc::clone(&self.apartment),
            marker: PhantomData,
        })
    }
}
impl<T> Drop for Out<T> {
    fn drop(&mut self) {
        if !self.raw.is_null() {
            drop(self.take());
        }
    }
}

struct Bstr(BSTR);
impl Bstr {
    fn new(value: &str) -> Result<Self, NativeFailure> {
        if value.contains('\0') {
            return Err(NativeFailure::InvalidData);
        }
        let units: Vec<u16> = value.encode_utf16().collect();
        let len = u32::try_from(units.len()).map_err(|_| NativeFailure::InvalidData)?;
        // SAFETY: source has exactly len UTF-16 units and remains alive during copy.
        let ptr = unsafe { SysAllocStringLen(units.as_ptr(), len) };
        if ptr.is_null() {
            return Err(NativeFailure::Status(E_OUTOFMEMORY));
        }
        Ok(Self(ptr))
    }
}
impl Drop for Bstr {
    fn drop(&mut self) {
        // SAFETY: uniquely owned allocation from SysAllocStringLen.
        unsafe { SysFreeString(self.0) };
    }
}

#[derive(Default)]
struct Value(VARIANT);
impl Value {
    fn kind(&self) -> u16 {
        // SAFETY: every VARIANT is initialized; vt is the common discriminant.
        unsafe { self.0.Anonymous.Anonymous.vt }
    }
    fn absent(&self) -> bool {
        matches!(self.kind(), VT_NULL | VT_EMPTY)
    }
    fn text(&self, max_units: usize) -> Result<Option<String>, NativeFailure> {
        if self.absent() {
            return Ok(None);
        }
        if self.kind() != VT_BSTR {
            return Err(NativeFailure::InvalidData);
        }
        // SAFETY: discriminator checked; SysStringLen supports a null BSTR.
        let ptr = unsafe { self.0.Anonymous.Anonymous.Anonymous.bstrVal };
        let len = unsafe { SysStringLen(ptr) } as usize;
        if len > max_units {
            return Err(NativeFailure::InvalidData);
        }
        let units = if len == 0 {
            &[]
        } else {
            if ptr.is_null() {
                return Err(NativeFailure::InvalidData);
            }
            // SAFETY: BSTR owns the reported length, bounded before allocation/read.
            unsafe { std::slice::from_raw_parts(ptr, len) }
        };
        if units.contains(&0) {
            return Err(NativeFailure::InvalidData);
        }
        String::from_utf16(units)
            .map(Some)
            .map_err(|_| NativeFailure::InvalidData)
    }
    fn boolean(&self) -> Result<Option<bool>, NativeFailure> {
        if self.absent() {
            return Ok(None);
        }
        if self.kind() != VT_BOOL {
            return Err(NativeFailure::InvalidData);
        }
        // SAFETY: VT_BOOL is checked; accept only canonical VARIANT_BOOL values.
        match unsafe { self.0.Anonymous.Anonymous.Anonymous.boolVal } {
            0 => Ok(Some(false)),
            -1 => Ok(Some(true)),
            _ => Err(NativeFailure::InvalidData),
        }
    }
    fn byte(&self) -> Result<Option<u8>, NativeFailure> {
        if self.absent() {
            return Ok(None);
        }
        if self.kind() != VT_UI1 {
            return Err(NativeFailure::InvalidData);
        }
        // SAFETY: VT_UI1 discriminator was checked.
        Ok(Some(unsafe { self.0.Anonymous.Anonymous.Anonymous.bVal }))
    }
    fn bytes(&self, max_len: usize) -> Result<Option<Vec<u8>>, NativeFailure> {
        if self.absent() {
            return Ok(None);
        }
        if self.kind() != VT_ARRAY | VT_UI1 {
            return Err(NativeFailure::InvalidData);
        }
        // SAFETY: exact array discriminator excludes BYREF/other element types.
        let array = unsafe { self.0.Anonymous.Anonymous.Anonymous.parray };
        if array.is_null() {
            return Err(NativeFailure::InvalidData);
        }
        // SAFETY: owned SAFEARRAY remains alive until Value's VariantClear.
        if unsafe { SafeArrayGetDim(array) } != 1 || unsafe { SafeArrayGetElemsize(array) } != 1 {
            return Err(NativeFailure::InvalidData);
        }
        let (mut lower, mut upper, mut kind) = (0, 0, VT_EMPTY);
        // SAFETY: valid array and writable outputs, dimension checked above.
        unsafe {
            ok(SafeArrayGetVartype(array, &mut kind))?;
            ok(SafeArrayGetLBound(array, 1, &mut lower))?;
            ok(SafeArrayGetUBound(array, 1, &mut upper))?;
        }
        if kind != VT_UI1 {
            return Err(NativeFailure::InvalidData);
        }
        let count = i64::from(upper) - i64::from(lower) + 1;
        if count < 0 || count as u64 > max_len as u64 {
            return Err(NativeFailure::InvalidData);
        }
        let mut bytes = Vec::with_capacity(count as usize);
        // GetElement avoids exposing/locking array storage; partial failure still
        // destroys the array through its owning VARIANT without leaked locks.
        for offset in 0..count {
            let index = (i64::from(lower) + offset) as i32;
            let mut value = 0_u8;
            // SAFETY: index is within verified bounds; byte element size checked.
            ok(unsafe { SafeArrayGetElement(array, &index, (&mut value as *mut u8).cast()) })?;
            bytes.push(value);
        }
        Ok(Some(bytes))
    }
}
impl Drop for Value {
    fn drop(&mut self) {
        // SAFETY: owned initialized VARIANT; no borrowed payload or array locks.
        let _ = unsafe { VariantClear(&mut self.0) };
    }
}

pub(super) struct Connection(Com<ServicesVtable>);
pub(super) struct Enumerator(Com<EnumeratorVtable>);
pub(super) struct Object(Com<ObjectVtable>);
pub(super) struct Pending(Com<PendingVtable>);
pub(super) struct Prepared {
    path: Bstr,
    method: Bstr,
    input: Object,
}

impl Connection {
    pub(super) fn connect() -> Result<Self, NativeFailure> {
        let apartment = Apartment::initialize()?;
        let mut locator = Out::<LocatorVtable>::new(&apartment);
        // SAFETY: fixed system CLSID/IID and initialized COM; output owns one reference.
        ok(unsafe {
            CoCreateInstance(
                &WbemLocator,
                null_mut(),
                CLSCTX_INPROC_SERVER,
                &IID_WBEM_LOCATOR,
                &mut locator.raw,
            )
        })?;
        let locator = locator.take()?;
        let namespace = Bstr::new("Root\\WMI")?;
        let mut services = Out::<ServicesVtable>::new(&apartment);
        // SAFETY: exact local namespace, current-user credentials, valid live locator.
        // MAX_WAIT is WMI's coarse connection bound; caller deadline still governs
        // whether a completed late connection may be used for any later operation.
        ok(unsafe {
            (locator.table().connect)(
                locator.raw,
                namespace.0,
                null(),
                null(),
                null(),
                WBEM_FLAG_CONNECT_USE_MAX_WAIT,
                null(),
                null_mut(),
                &mut services.raw,
            )
        })?;
        let services = services.take()?;
        services.blanket()?;
        Ok(Self(services))
    }
    pub(super) fn query(&self, query: &str) -> Result<Enumerator, NativeFailure> {
        let language = Bstr::new("WQL")?;
        let query = Bstr::new(query)?;
        let mut output = Out::<EnumeratorVtable>::new(&self.0.apartment);
        // SAFETY: live services and BSTRs; no sink/callback, semisynchronous forward-only.
        ok(unsafe {
            (self.0.table().exec_query)(
                self.0.raw,
                language.0,
                query.0,
                WBEM_FLAG_RETURN_IMMEDIATELY | WBEM_FLAG_FORWARD_ONLY | WBEM_FLAG_ENSURE_LOCATABLE,
                null_mut(),
                &mut output.raw,
            )
        })?;
        let enumerator = output.take()?;
        enumerator.blanket()?;
        Ok(Enumerator(enumerator))
    }
    pub(super) fn prepare(
        &self,
        path: &str,
        level: u8,
        submission: &Submission<'_>,
    ) -> Result<Prepared, NativeFailure> {
        submission.remaining()?;
        if level > 100 {
            return Err(NativeFailure::InvalidData);
        }
        let path = Bstr::new(path)?;
        let method = Bstr::new("WmiSetBrightness")?;
        let class = Bstr::new("WmiMonitorBrightnessMethods")?;
        let mut definition = Out::<ObjectVtable>::new(&self.0.apartment);
        // SAFETY: fixed class lookup only; does not submit a hardware method.
        ok(unsafe {
            (self.0.table().get_object)(
                self.0.raw,
                class.0,
                0,
                null_mut(),
                &mut definition.raw,
                null_mut(),
            )
        })?;
        submission.remaining()?;
        let definition = definition.take()?;
        let mut input = Out::<ObjectVtable>::new(&self.0.apartment);
        let mut output = Out::<ObjectVtable>::new(&self.0.apartment);
        // SAFETY: fixed method signature lookup; both returned references are owned.
        ok(unsafe {
            (definition.table().get_method)(
                definition.raw,
                w!("WmiSetBrightness"),
                0,
                &mut input.raw,
                &mut output.raw,
            )
        })?;
        submission.remaining()?;
        let input = Object(input.take()?);
        let output = Object(output.take()?);
        input.require_type(w!("Timeout"), CIM_UINT32, VT_I4)?;
        submission.remaining()?;
        input.require_type(w!("Brightness"), CIM_UINT8, VT_UI1)?;
        submission.remaining()?;
        output.require_type(w!("ReturnValue"), CIM_UINT32, VT_I4)?;
        submission.remaining()?;
        let mut instance = Out::<ObjectVtable>::new(&self.0.apartment);
        // SAFETY: spawn an in-memory parameter object, not a provider instance.
        ok(unsafe { (input.0.table().spawn_instance)(input.0.raw, 0, &mut instance.raw) })?;
        submission.remaining()?;
        let input = Object(instance.take()?);
        let mut timeout = Value::default();
        let mut brightness = Value::default();
        // Initialized owned variants; these scalar discriminants match payloads.
        timeout.0.Anonymous.Anonymous.vt = VT_I4;
        timeout.0.Anonymous.Anonymous.Anonymous.lVal = 0;
        brightness.0.Anonymous.Anonymous.vt = VT_UI1;
        brightness.0.Anonymous.Anonymous.Anonymous.bVal = level;
        input.put(w!("Timeout"), &mut timeout)?;
        submission.remaining()?;
        input.put(w!("Brightness"), &mut brightness)?;
        submission.remaining()?;
        Ok(Prepared {
            path,
            method,
            input,
        })
    }
    pub(super) fn submit(&self, prepared: Prepared) -> Result<Pending, NativeFailure> {
        let mut pending = Out::<PendingVtable>::new(&self.0.apartment);
        // SAFETY: caller has marked possible submission immediately before entering
        // this function. All input preparation is complete; this is the sole method
        // invocation, with exactly one current-user local relative instance path.
        ok(unsafe {
            (self.0.table().exec_method)(
                self.0.raw,
                prepared.path.0,
                prepared.method.0,
                WBEM_FLAG_RETURN_IMMEDIATELY,
                null_mut(),
                prepared.input.0.raw,
                null_mut(),
                &mut pending.raw,
            )
        })?;
        let pending = pending.take()?;
        pending.blanket()?;
        Ok(Pending(pending))
    }
}

impl Enumerator {
    pub(super) fn next(&mut self, timeout_ms: i32) -> Result<Next<Object>, NativeFailure> {
        if timeout_ms < 0 {
            return Err(NativeFailure::InvalidData);
        }
        let mut output = Out::<ObjectVtable>::new(&self.0.apartment);
        let mut count = 0;
        // SAFETY: one requested object and exactly one initialized output slot.
        let status = unsafe {
            (self.0.table().next)(self.0.raw, timeout_ms, 1, &mut output.raw, &mut count)
        };
        if !matches!(status, S_OK | WBEM_S_FALSE | WBEM_S_TIMEDOUT) {
            return Err(NativeFailure::Status(status));
        }
        match count {
            1 if status == S_OK => Ok(Next::Item(Object(output.take()?))),
            0 if !output.raw.is_null() => Err(NativeFailure::InvalidData),
            0 if status == WBEM_S_FALSE => Ok(Next::End),
            0 if status == WBEM_S_TIMEDOUT => Ok(Next::Pending),
            _ => Err(NativeFailure::InvalidData),
        }
    }
}

impl Object {
    fn get(&self, name: PCWSTR) -> Result<Option<(Value, i32)>, NativeFailure> {
        let mut value = Value::default();
        let mut cim_type = 0;
        // SAFETY: names are private fixed wide literals supplied by the worker;
        // owned initialized VARIANT and CIMTYPE outputs remain alive for the call.
        let status = unsafe {
            (self.0.table().get)(self.0.raw, name, 0, &mut value.0, &mut cim_type, null_mut())
        };
        if status == WBEM_E_NOT_FOUND {
            return Ok(None);
        }
        ok(status)?;
        Ok(Some((value, cim_type)))
    }
    fn typed(&self, name: PCWSTR, expected: i32) -> Result<Option<Value>, NativeFailure> {
        match self.get(name)? {
            None => Ok(None),
            Some((value, actual)) if actual == expected => Ok(Some(value)),
            Some(_) => Err(NativeFailure::InvalidData),
        }
    }
    fn require_type(&self, name: PCWSTR, cim: i32, variant: u16) -> Result<(), NativeFailure> {
        let value = self.typed(name, cim)?.ok_or(NativeFailure::InvalidData)?;
        if value.absent() || value.kind() == variant {
            Ok(())
        } else {
            Err(NativeFailure::InvalidData)
        }
    }
    fn put(&self, name: PCWSTR, value: &mut Value) -> Result<(), NativeFailure> {
        // SAFETY: class schema validated; Put modifies only this in-memory argument.
        // Type=0 preserves the existing CIM property definition on the spawned instance.
        ok(unsafe { (self.0.table().put)(self.0.raw, name, 0, &mut value.0, 0) })
    }
    pub(super) fn text(
        &self,
        name: PCWSTR,
        max_units: usize,
    ) -> Result<Option<String>, NativeFailure> {
        self.typed(name, CIM_STRING)?
            .map(|v| v.text(max_units))
            .unwrap_or(Ok(None))
    }
    pub(super) fn boolean(&self, name: PCWSTR) -> Result<Option<bool>, NativeFailure> {
        self.typed(name, CIM_BOOLEAN)?
            .map(|v| v.boolean())
            .unwrap_or(Ok(None))
    }
    pub(super) fn byte(&self, name: PCWSTR) -> Result<Option<u8>, NativeFailure> {
        self.typed(name, CIM_UINT8)?
            .map(|v| v.byte())
            .unwrap_or(Ok(None))
    }
    pub(super) fn bytes(
        &self,
        name: PCWSTR,
        max_len: usize,
    ) -> Result<Option<Vec<u8>>, NativeFailure> {
        self.typed(name, CIM_UINT8 | CIM_FLAG_ARRAY)?
            .map(|v| v.bytes(max_len))
            .unwrap_or(Ok(None))
    }
    pub(super) fn uint32(&self, name: PCWSTR) -> Result<Option<u32>, NativeFailure> {
        let Some(value) = self.typed(name, CIM_UINT32)? else {
            return Ok(None);
        };
        if value.absent() {
            return Ok(None);
        }
        // WMI represents CIM_UINT32 as VT_I4. Preserve all 32 bits, including
        // provider errors whose high bit would make the Automation LONG negative.
        if value.kind() != VT_I4 {
            return Err(NativeFailure::InvalidData);
        }
        // SAFETY: exact scalar discriminant checked; no Automation coercion.
        Ok(Some(
            unsafe { value.0.Anonymous.Anonymous.Anonymous.lVal } as u32
        ))
    }
    pub(super) fn ack(&self) -> Result<Acknowledgement, NativeFailure> {
        self.uint32(w!("ReturnValue"))?
            .map(Acknowledgement::Status)
            .ok_or(NativeFailure::MissingData)
    }
}

impl Pending {
    pub(super) fn status(&self, timeout_ms: i32) -> Result<Option<i32>, NativeFailure> {
        if timeout_ms < 0 {
            return Err(NativeFailure::InvalidData);
        }
        let mut result = 0;
        // SAFETY: live semisynchronous call result and writable LONG output.
        let status = unsafe { (self.0.table().status)(self.0.raw, timeout_ms, &mut result) };
        if status == WBEM_S_TIMEDOUT {
            return Ok(None);
        }
        ok(status)?;
        Ok(Some(result))
    }
    pub(super) fn result(&self, timeout_ms: i32) -> Result<Next<Object>, NativeFailure> {
        if timeout_ms < 0 {
            return Err(NativeFailure::InvalidData);
        }
        let mut output = Out::<ObjectVtable>::new(&self.0.apartment);
        // SAFETY: live call result and initialized owned output slot.
        let status = unsafe { (self.0.table().result)(self.0.raw, timeout_ms, &mut output.raw) };
        if status == WBEM_S_TIMEDOUT {
            if !output.raw.is_null() {
                return Err(NativeFailure::InvalidData);
            }
            return Ok(Next::Pending);
        }
        ok(status)?;
        if output.raw.is_null() {
            return Ok(Next::End);
        }
        Ok(Next::Item(Object(output.take()?)))
    }
}

#[cfg(test)]
mod tests;
