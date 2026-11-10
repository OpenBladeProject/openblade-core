//! Read-only Win32, PDH, and DXGI bindings for GPU app sampling.
#![allow(unsafe_code)]

use std::{
    cell::Cell,
    ffi::c_void,
    os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle},
    ptr::{NonNull, null, null_mut},
    thread,
    time::Instant,
};

use windows_sys::{
    Win32::{
        Foundation::{
            ERROR_INVALID_PARAMETER, FILETIME, GetLastError, HANDLE, INVALID_HANDLE_VALUE, LUID,
            STILL_ACTIVE,
        },
        Globalization::CompareStringOrdinal,
        Security::{
            GetTokenInformation, SECURITY_MAX_SID_SIZE, TOKEN_QUERY, TOKEN_USER, TokenSessionId,
            TokenUser,
        },
        System::{
            Diagnostics::ToolHelp::{
                CreateToolhelp32Snapshot, PROCESSENTRY32W, Process32FirstW, Process32NextW,
                TH32CS_SNAPPROCESS,
            },
            Performance::*,
            Threading::{
                GetCurrentProcess, GetExitCodeProcess, GetProcessTimes, OpenProcess,
                OpenProcessToken, PROCESS_QUERY_LIMITED_INFORMATION, QueryFullProcessImageNameW,
            },
        },
    },
    core::{GUID, w},
};

use super::*;

const PDH_FMT_NOCAP100: u32 = 0x8000;
const MAX_ENGINE_COUNTERS: usize = 65_536;
const MAX_INSTANCE_UNITS: usize = 1024;
const MAX_PATH_UNITS: usize = 32_768;
const MAX_PROCESS_ENTRIES: usize = 65_536;

#[derive(Default)]
pub(super) struct WindowsSampling {
    pub(super) comparison_failed: Cell<bool>,
}

impl SamplingApi for WindowsSampling {
    type Query = Query;

    fn current_user(&mut self) -> Option<(Vec<u8>, u32)> {
        // SAFETY: This is a borrowed pseudo-handle for the calling process.
        token_identity(unsafe { GetCurrentProcess() })
    }

    fn locate_adapter(&mut self) -> Result<NvidiaAdapterLuid, Unavailable> {
        locate_nvidia()
    }

    fn open_query(&mut self) -> Result<Query, Unavailable> {
        let mut handle = null_mut();
        // SAFETY: Null data source selects live counters. The output is writable.
        let status = unsafe { PdhOpenQueryW(null(), 0, &mut handle) };
        check_pdh(status)?;
        let mut query = Query {
            handle: NonNull::new(handle).ok_or(Unavailable::MalformedCounters)?,
            counter: null_mut(),
        };
        // SAFETY: The live query owns the output counter until Query drops.
        let status = unsafe {
            PdhAddEnglishCounterW(
                query.handle.as_ptr(),
                w!(r"\GPU Engine(*)\Utilization Percentage"),
                0,
                &mut query.counter,
            )
        };
        check_pdh(status)?;
        if query.counter.is_null() {
            return Err(Unavailable::MalformedCounters);
        }
        Ok(query)
    }

    fn collect(&mut self, query: &mut Query) -> Result<i64, Unavailable> {
        let mut timestamp = 0;
        // SAFETY: Query is live and the timestamp output is writable.
        check_pdh(unsafe { PdhCollectQueryDataWithTime(query.handle.as_ptr(), &mut timestamp) })?;
        Ok(timestamp)
    }

    fn counter_array(
        &mut self,
        query: &mut Query,
        bytes: u32,
    ) -> Result<CounterArray, Unavailable> {
        let mut buffer = vec![0_u64; (bytes as usize).div_ceil(size_of::<u64>())];
        let items = if bytes == 0 {
            null_mut()
        } else {
            buffer.as_mut_ptr().cast()
        };
        let mut used_bytes = bytes;
        let mut count = 0;
        // SAFETY: The query owns the counter. The aligned buffer holds at least
        // the advertised bytes or is null for the sizing call.
        let status = unsafe {
            PdhGetFormattedCounterArrayW(
                query.counter,
                PDH_FMT_DOUBLE | PDH_FMT_NOCAP100,
                &mut used_bytes,
                &mut count,
                items,
            )
        };
        match (bytes, status) {
            (0, PDH_MORE_DATA) => Ok(CounterArray::Size(used_bytes)),
            (0, 0) => Err(Unavailable::MalformedCounters),
            (_, PDH_MORE_DATA | PDH_INVALID_ARGUMENT) if bytes != 0 => {
                Ok(CounterArray::Resized(status))
            }
            (_, 0) if used_bytes <= bytes => {
                decode_counters(&buffer, used_bytes as usize, count as usize)
                    .map(CounterArray::Items)
            }
            (_, 0) => Err(Unavailable::MalformedCounters),
            (_, status) => Err(pdh_error(status)),
        }
    }

    fn inspect(&mut self, pid: i32) -> Inspection {
        inspect_process(pid)
    }

    fn read_parents(&mut self, cancelled: &AtomicBool) -> BTreeMap<i32, i32> {
        read_process_parents(cancelled)
    }

    fn pause(&mut self, interval: Duration, cancelled: &AtomicBool) {
        let start = Instant::now();
        while !cancelled.load(AtomicOrdering::Relaxed) {
            let remaining = interval.saturating_sub(start.elapsed());
            if remaining.is_zero() {
                break;
            }
            thread::sleep(remaining.min(Duration::from_millis(10)));
        }
    }

    fn compare_names(&self, left: &str, right: &str) -> Ordering {
        let left: Vec<_> = left.encode_utf16().collect();
        let right: Vec<_> = right.encode_utf16().collect();
        // SAFETY: Both inputs are length-delimited UTF-16. Projected names
        // and the built-in comparison literals are bounded to 260 units.
        match unsafe {
            CompareStringOrdinal(
                left.as_ptr(),
                left.len() as i32,
                right.as_ptr(),
                right.len() as i32,
                1,
            )
        } {
            1 => Ordering::Less,
            2 => Ordering::Equal,
            3 => Ordering::Greater,
            _ => {
                self.comparison_failed.set(true);
                left.cmp(&right)
            }
        }
    }
}

pub(super) struct Query {
    handle: NonNull<c_void>,
    counter: PDH_HCOUNTER,
}

impl Drop for Query {
    fn drop(&mut self) {
        // SAFETY: This instance uniquely owns the query and its counters.
        unsafe { PdhCloseQuery(self.handle.as_ptr()) };
    }
}

fn check_pdh(status: u32) -> Result<(), Unavailable> {
    if status == 0 {
        Ok(())
    } else {
        Err(pdh_error(status))
    }
}

fn pdh_error(status: u32) -> Unavailable {
    match status {
        PDH_NO_DATA | PDH_CSTATUS_NO_INSTANCE | PDH_INVALID_DATA | PDH_CSTATUS_INVALID_DATA => {
            Unavailable::MissingData
        }
        _ => Unavailable::NativeCounter(status),
    }
}

fn decode_counters(
    buffer: &[u64],
    used: usize,
    count: usize,
) -> Result<Vec<EngineCounter>, Unavailable> {
    if count > MAX_ENGINE_COUNTERS || used > size_of_val(buffer) {
        return Err(Unavailable::MalformedCounters);
    }
    let item_bytes = count
        .checked_mul(size_of::<PDH_FMT_COUNTERVALUE_ITEM_W>())
        .filter(|&end| end <= used)
        .ok_or(Unavailable::MalformedCounters)?;
    // SAFETY: The caller's zero-initialized u64 buffer backs both the validated
    // item region and the bounded UTF-16 region. The SDK struct has alignment
    // no stricter than u64, and all its field bit patterns are valid.
    let items = unsafe {
        std::slice::from_raw_parts(buffer.as_ptr().cast::<PDH_FMT_COUNTERVALUE_ITEM_W>(), count)
    };
    let mut counters = Vec::with_capacity(count);
    for item in items {
        if !matches!(
            item.FmtValue.CStatus,
            PDH_CSTATUS_VALID_DATA | PDH_CSTATUS_NEW_DATA
        ) {
            continue;
        }
        let offset = item
            .szName
            .addr()
            .checked_sub(buffer.as_ptr().addr())
            .filter(|&offset| offset >= item_bytes && offset < used && offset % 2 == 0)
            .ok_or(Unavailable::MalformedCounters)?;
        // SAFETY: Use the buffer pointer's provenance, an aligned offset, and
        // only the complete UTF-16 units within the initialized used region.
        let name = unsafe {
            std::slice::from_raw_parts(
                buffer.as_ptr().cast::<u8>().add(offset).cast::<u16>(),
                ((used - offset) / 2).min(MAX_INSTANCE_UNITS),
            )
        };
        let end = name
            .iter()
            .position(|&unit| unit == 0)
            .ok_or(Unavailable::MalformedCounters)?;
        let instance =
            String::from_utf16(&name[..end]).map_err(|_| Unavailable::MalformedCounters)?;
        // SAFETY: PDH_FMT_DOUBLE selects the double member. Invalid statuses
        // are discarded by the shared counter filter.
        let utilization_percent = unsafe { item.FmtValue.Anonymous.doubleValue };
        counters.push(EngineCounter {
            instance,
            status: item.FmtValue.CStatus,
            utilization_percent,
        });
    }
    Ok(counters)
}

fn read_process_parents(cancelled: &AtomicBool) -> BTreeMap<i32, i32> {
    let mut parents = BTreeMap::new();
    // SAFETY: TH32CS_SNAPPROCESS takes a read-only system process snapshot.
    let raw = unsafe { CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0) };
    if raw == INVALID_HANDLE_VALUE {
        return parents;
    }
    // SAFETY: A successful snapshot is a uniquely owned handle.
    let snapshot = unsafe { OwnedHandle::from_raw_handle(raw) };
    let mut entry = PROCESSENTRY32W {
        dwSize: size_of::<PROCESSENTRY32W>() as u32,
        ..Default::default()
    };
    // SAFETY: The snapshot is held and the entry has the required size.
    let mut found = unsafe { Process32FirstW(snapshot.as_raw_handle(), &mut entry) };
    for _ in 0..MAX_PROCESS_ENTRIES {
        if found == 0 || cancelled.load(AtomicOrdering::Relaxed) {
            break;
        }
        if let (Ok(pid), Ok(parent)) = (
            i32::try_from(entry.th32ProcessID),
            i32::try_from(entry.th32ParentProcessID),
        ) && pid > 0
            && parent > 0
        {
            parents.insert(pid, parent);
        }
        // SAFETY: The snapshot and correctly sized entry remain live.
        found = unsafe { Process32NextW(snapshot.as_raw_handle(), &mut entry) };
    }
    parents
}

fn inspect_process(pid: i32) -> Inspection {
    // SAFETY: Open a positive PID from counters or the parent snapshot with
    // non-inheritable query-limited access.
    let raw = unsafe { OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, pid as u32) };
    if raw.is_null() {
        // SAFETY: Read the thread's last error immediately after OpenProcess.
        return if unsafe { GetLastError() } == ERROR_INVALID_PARAMETER {
            Inspection::Stale
        } else {
            Inspection::Unavailable
        };
    }
    // SAFETY: A successful OpenProcess returns a uniquely owned real handle.
    let process = unsafe { OwnedHandle::from_raw_handle(raw) };
    let handle = process.as_raw_handle();

    let identity = image_name(handle).zip(token_identity(handle));
    let mut exit_code = 0;
    // SAFETY: The process is held through the last observation and the
    // output is writable.
    if unsafe { GetExitCodeProcess(handle, &mut exit_code) } == 0 {
        return Inspection::Unavailable;
    }
    if exit_code != STILL_ACTIVE as u32 {
        return Inspection::Stale;
    }

    let mut created = FILETIME::default();
    let mut exited = FILETIME::default();
    let mut kernel = FILETIME::default();
    let mut user = FILETIME::default();
    // SAFETY: All outputs are distinct writable FILETIMEs on a held handle.
    if unsafe { GetProcessTimes(handle, &mut created, &mut exited, &mut kernel, &mut user) } == 0 {
        return Inspection::Unavailable;
    }
    let created_filetime = filetime(created);
    if dotnet_ticks(created_filetime).is_none() {
        return Inspection::Unavailable;
    }
    // 259 is also a possible application exit code. Conservatively omit a
    // process with a plausible exit time. A live process's exit field is
    // unspecified, so this check can only reduce the observed inventory.
    if filetime(exited) >= created_filetime {
        return Inspection::Stale;
    }
    match identity {
        Some((name, (user_sid, session_id))) => Inspection::Identified(Identity {
            created_filetime,
            user_sid,
            session_id,
            name,
        }),
        None => Inspection::Unavailable,
    }
}

fn filetime(value: FILETIME) -> i64 {
    ((u64::from(value.dwHighDateTime) << 32) | u64::from(value.dwLowDateTime)) as i64
}

fn image_name(process: HANDLE) -> Option<String> {
    let mut path = vec![0; MAX_PATH_UNITS];
    let mut units = path.len() as u32;
    // SAFETY: The process handle is held and the path buffer has the advertised
    // capacity. Flag zero selects the ordinary Win32 executable path.
    if unsafe { QueryFullProcessImageNameW(process, 0, path.as_mut_ptr(), &mut units) } == 0 {
        return None;
    }
    process_name(path.get(..units as usize)?)
}

fn token_identity(process: HANDLE) -> Option<(Vec<u8>, u32)> {
    let mut raw = null_mut();
    // SAFETY: The caller holds the process handle and the output is writable.
    if unsafe { OpenProcessToken(process, TOKEN_QUERY, &mut raw) } == 0 || raw.is_null() {
        return None;
    }
    // SAFETY: OpenProcessToken returned a uniquely owned real token handle.
    let token = unsafe { OwnedHandle::from_raw_handle(raw) };
    let mut session = 0_u32;
    let mut used = 0;
    // SAFETY: TokenSessionId writes a DWORD and a separate length.
    if unsafe {
        GetTokenInformation(
            token.as_raw_handle(),
            TokenSessionId,
            (&mut session as *mut u32).cast(),
            4,
            &mut used,
        )
    } == 0
        || used != 4
    {
        return None;
    }

    const TOKEN_USER_BYTES: usize = size_of::<TOKEN_USER>();
    const BUFFER_WORDS: usize =
        (TOKEN_USER_BYTES + SECURITY_MAX_SID_SIZE as usize).div_ceil(size_of::<usize>());
    let mut buffer = [0_usize; BUFFER_WORDS];
    let base = buffer.as_mut_ptr().cast::<u8>();
    let capacity = size_of_val(&buffer);
    // SAFETY: The initialized buffer is aligned for TOKEN_USER and has space
    // for the maximum binary SID.
    if unsafe {
        GetTokenInformation(
            token.as_raw_handle(),
            TokenUser,
            base.cast(),
            capacity as u32,
            &mut used,
        )
    } == 0
        || !(TOKEN_USER_BYTES..=capacity).contains(&(used as usize))
    {
        return None;
    }
    // SAFETY: A complete aligned TOKEN_USER was returned inside the buffer.
    let sid = unsafe { (*base.cast::<TOKEN_USER>()).User.Sid };
    let offset = sid
        .addr()
        .checked_sub(base.addr())
        .filter(|&offset| offset >= TOKEN_USER_BYTES)?;
    // SAFETY: The validated output length fits in the initialized buffer.
    let payload = unsafe { std::slice::from_raw_parts(base, used as usize) }.get(offset..)?;
    let [1, subauthorities, ..] = payload else {
        return None;
    };
    let length = 8 + 4 * usize::from(*subauthorities);
    if length > SECURITY_MAX_SID_SIZE as usize {
        return None;
    }
    Some((payload.get(..length)?.to_vec(), session))
}

// windows-sys does not project DXGI COM interfaces. Use the DXGI 1.1 slots
// also used by NvidiaAdapterLocator. Creating a factory and reading adapter
// descriptions requires no graphics device or vendor GPU API.
#[link(name = "dxgi")]
unsafe extern "system" {
    fn CreateDXGIFactory1(iid: *const GUID, factory: *mut *mut c_void) -> i32;
}

const FACTORY1: GUID = GUID::from_u128(0x770aae78_f26f_4dba_a829_253c83d1b387);
const DXGI_ERROR_NOT_FOUND: i32 = 0x887a0002_u32 as i32;

#[repr(C)]
struct UnknownVtable {
    reserved: [*const c_void; 2],
    release: unsafe extern "system" fn(*mut c_void) -> u32,
}

#[repr(C)]
struct Factory1Vtable {
    inherited: [*const c_void; 12],
    enum_adapters1: unsafe extern "system" fn(*mut c_void, u32, *mut *mut c_void) -> i32,
}

#[repr(C)]
struct Adapter1Vtable {
    inherited: [*const c_void; 10],
    get_desc1: unsafe extern "system" fn(*mut c_void, *mut AdapterDesc1) -> i32,
}

#[repr(C)]
struct AdapterDesc1 {
    description: [u16; 128],
    vendor_id: u32,
    device_id: u32,
    subsystem_id: u32,
    revision: u32,
    dedicated_video_memory: usize,
    dedicated_system_memory: usize,
    shared_system_memory: usize,
    luid: LUID,
    flags: u32,
}

struct ComPtr(NonNull<c_void>);

impl ComPtr {
    fn from_result(status: i32, pointer: *mut c_void) -> Result<Self, Unavailable> {
        if status < 0 {
            return Err(Unavailable::AdapterDiscovery(status));
        }
        NonNull::new(pointer)
            .map(Self)
            .ok_or(Unavailable::AdapterDiscovery(0x80004003_u32 as i32))
    }

    unsafe fn vtable<T>(&self) -> &T {
        // SAFETY: The caller chooses the vtable corresponding to this live
        // interface. A COM interface starts with a pointer to that vtable.
        unsafe { &**self.0.cast::<*const T>().as_ptr() }
    }
}

impl Drop for ComPtr {
    fn drop(&mut self) {
        // SAFETY: All COM interfaces start with IUnknown. This owns one reference.
        unsafe { (self.vtable::<UnknownVtable>().release)(self.0.as_ptr()) };
    }
}

fn locate_nvidia() -> Result<NvidiaAdapterLuid, Unavailable> {
    let mut factory = null_mut();
    // SAFETY: The interface ID and output match CreateDXGIFactory1.
    let status = unsafe { CreateDXGIFactory1(&FACTORY1, &mut factory) };
    let factory = ComPtr::from_result(status, factory)?;
    let mut description_failure = None;
    for index in 0..64 {
        let mut adapter = null_mut();
        // SAFETY: The live IDXGIFactory1 has this EnumAdapters1 slot.
        let status = unsafe {
            (factory.vtable::<Factory1Vtable>().enum_adapters1)(
                factory.0.as_ptr(),
                index,
                &mut adapter,
            )
        };
        if status == DXGI_ERROR_NOT_FOUND {
            return Err(description_failure.unwrap_or(Unavailable::UnsupportedAdapter));
        }
        let adapter = ComPtr::from_result(status, adapter)?;
        let mut description = std::mem::MaybeUninit::<AdapterDesc1>::uninit();
        // SAFETY: The live IDXGIAdapter1 writes the SDK description on success.
        let status = unsafe {
            (adapter.vtable::<Adapter1Vtable>().get_desc1)(
                adapter.0.as_ptr(),
                description.as_mut_ptr(),
            )
        };
        if status < 0 {
            description_failure = Some(Unavailable::AdapterDiscovery(status));
            continue;
        }
        // SAFETY: GetDesc1 succeeded and initialized the complete description.
        let description = unsafe { description.assume_init() };
        if description.vendor_id == 0x10de {
            return Ok(NvidiaAdapterLuid {
                high: description.luid.HighPart,
                low: description.luid.LowPart,
            });
        }
    }
    Err(description_failure.unwrap_or(Unavailable::AdapterDiscovery(0x80004005_u32 as i32)))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn bounds_counter_array_and_instance_strings() {
        let item_bytes = size_of::<PDH_FMT_COUNTERVALUE_ITEM_W>();
        let mut buffer = [0_u64; 8];
        let base = buffer.as_mut_ptr().cast::<u8>();
        // SAFETY: The fixture writes one aligned SDK item and one UTF-16
        // string into the allocated buffer. No Windows API is called.
        unsafe {
            let name = base.add(item_bytes).cast::<u16>();
            name.write(u16::from(b'x'));
            name.add(1).write(0);
            let item = PDH_FMT_COUNTERVALUE_ITEM_W {
                szName: name,
                FmtValue: PDH_FMT_COUNTERVALUE {
                    CStatus: PDH_CSTATUS_NEW_DATA,
                    Anonymous: PDH_FMT_COUNTERVALUE_0 { doubleValue: 101.5 },
                },
            };
            base.cast::<PDH_FMT_COUNTERVALUE_ITEM_W>().write(item);
        }

        let used = item_bytes + 4;
        let counters = decode_counters(&buffer, used, 1).unwrap();
        assert_eq!(counters[0].instance, "x");
        assert_eq!(counters[0].utilization_percent, 101.5);
        for (length, count) in [
            (used, 2),
            (item_bytes + 2, 1),
            (item_bytes, 1),
            (size_of_val(&buffer) + 1, 1),
            (used, MAX_ENGINE_COUNTERS + 1),
        ] {
            assert_eq!(
                decode_counters(&buffer, length, count).err(),
                Some(Unavailable::MalformedCounters)
            );
        }
    }
}
