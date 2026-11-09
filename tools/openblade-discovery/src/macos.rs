//! Only registry metadata. No user client, device open, or report functions.
use crate::snapshot::{
    Entry, Hid, HostMetadata, MAX_HOST_TEXT, Platform, RAZER_VID, Snapshot, host_text,
};
use std::{
    ffi::{CStr, c_char, c_int, c_uchar, c_uint, c_void},
    num::NonZeroU32,
    ptr,
};

type CfRef = *const c_void;
#[cfg(target_arch = "x86_64")]
type MachBoolean = c_uint;
#[cfg(target_arch = "aarch64")]
type MachBoolean = c_int;

#[link(name = "IOKit", kind = "framework")]
unsafe extern "C" {
    fn IOServiceMatching(name: *const c_char) -> CfRef;
    fn IOServiceGetMatchingServices(port: c_uint, matching: CfRef, iter: *mut u32) -> c_int;
    fn IOIteratorNext(iter: u32) -> u32;
    fn IOIteratorIsValid(iter: u32) -> MachBoolean;
    fn IOObjectRelease(object: u32) -> c_int;
    fn IORegistryEntryCreateCFProperty(
        entry: u32,
        key: CfRef,
        allocator: CfRef,
        options: u32,
    ) -> CfRef;
}

#[link(name = "CoreFoundation", kind = "framework")]
unsafe extern "C" {
    fn CFStringCreateWithCString(allocator: CfRef, text: *const c_char, encoding: u32) -> CfRef;
    fn CFGetTypeID(value: CfRef) -> usize;
    fn CFNumberGetTypeID() -> usize;
    fn CFStringGetTypeID() -> usize;
    fn CFNumberIsFloatType(value: CfRef) -> c_uchar;
    fn CFNumberGetValue(value: CfRef, kind: isize, result: *mut c_void) -> c_uchar;
    fn CFEqual(a: CfRef, b: CfRef) -> c_uchar;
    fn CFRelease(value: CfRef);
}

unsafe extern "C" {
    fn sysctlbyname(
        name: *const c_char,
        old: *mut c_void,
        old_len: *mut usize,
        new: *mut c_void,
        new_len: usize,
    ) -> c_int;
}

struct RegistryObject(NonZeroU32);

impl Drop for RegistryObject {
    fn drop(&mut self) {
        // SAFETY: This wrapper uniquely owns a nonzero matching iterator or entry.
        unsafe { IOObjectRelease(self.0.get()) };
    }
}

struct CfObject(ptr::NonNull<c_void>);

impl CfObject {
    fn from_owned(value: CfRef) -> Option<Self> {
        ptr::NonNull::new(value.cast_mut()).map(Self)
    }

    fn as_ref(&self) -> CfRef {
        self.0.as_ptr()
    }

    fn string(text: &CStr) -> Option<Self> {
        const UTF8: u32 = 0x0800_0100;
        // SAFETY: text is NUL-terminated; null selects the default allocator.
        Self::from_owned(unsafe { CFStringCreateWithCString(ptr::null(), text.as_ptr(), UTF8) })
    }

    fn property(entry: &RegistryObject, key: &Self) -> Option<Self> {
        // SAFETY: Both objects are live. The returned reference is owned, if non-null.
        Self::from_owned(unsafe {
            IORegistryEntryCreateCFProperty(entry.0.get(), key.as_ref(), ptr::null(), 0)
        })
    }

    fn usb_id(&self) -> Option<u16> {
        const SINT64: isize = 4;
        let mut value = 0_i64;
        // SAFETY: Check the CF type before using the number APIs; value has the
        // exact size and alignment selected by kCFNumberSInt64Type.
        unsafe {
            if CFGetTypeID(self.as_ref()) != CFNumberGetTypeID()
                || CFNumberIsFloatType(self.as_ref()) != 0
                || CFNumberGetValue(self.as_ref(), SINT64, (&raw mut value).cast()) == 0
            {
                return None;
            }
        }
        u16::try_from(value).ok()
    }

    fn is_transport(&self, expected: &Self) -> Option<bool> {
        // SAFETY: Both references are live CF objects.
        unsafe {
            (CFGetTypeID(self.as_ref()) == CFStringGetTypeID())
                .then(|| CFEqual(self.as_ref(), expected.as_ref()) != 0)
        }
    }
}

impl Drop for CfObject {
    fn drop(&mut self) {
        // SAFETY: This wrapper owns one retained reference from a Create call.
        unsafe { CFRelease(self.as_ref()) };
    }
}

pub fn discover() -> Snapshot {
    Snapshot::new(Platform::Macos, host_metadata(), enumerate())
}

fn host_metadata() -> HostMetadata {
    let mut bytes = [0; MAX_HOST_TEXT + 1];
    let mut size = bytes.len();
    // SAFETY: The fixed buffer is writable for the supplied size. A null new
    // buffer and zero new size request a read of the NUL-terminated selector.
    let result = unsafe {
        sysctlbyname(
            c"hw.model".as_ptr(),
            bytes.as_mut_ptr().cast(),
            &mut size,
            ptr::null_mut(),
            0,
        )
    };
    if result == 0
        && bytes
            .get(..size)
            .and_then(|bytes| bytes.strip_suffix(b"\0"))
            .and_then(host_text)
            .is_some()
    {
        // This may be an Apple SMBIOS identity on a Blade. Never resolve it to a model.
        HostMetadata::Unverified
    } else {
        HostMetadata::Unavailable
    }
}

fn enumerate() -> Hid {
    let (Some(transport), Some(vendor), Some(product), Some(usb)) = (
        CfObject::string(c"Transport"),
        CfObject::string(c"VendorID"),
        CfObject::string(c"ProductID"),
        CfObject::string(c"USB"),
    ) else {
        return Hid::unavailable();
    };
    let mut iterator = 0;
    // SAFETY: The class name is NUL-terminated. On a non-null result the
    // matching API consumes the dictionary, including on failure. Port 0 is
    // kIOMainPortDefault. iterator is writable and used only on success.
    let result = unsafe {
        let matching = IOServiceMatching(c"IOHIDDevice".as_ptr());
        if matching.is_null() {
            return Hid::unavailable();
        }
        IOServiceGetMatchingServices(0, matching, &mut iterator)
    };
    if result != 0 {
        return Hid::unavailable();
    }
    let Some(iterator) = NonZeroU32::new(iterator).map(RegistryObject) else {
        return Hid::from_entries([]);
    };
    let mut hid = Hid::from_entries(std::iter::from_fn(|| {
        // SAFETY: iterator is live; each nonzero returned entry is caller-owned.
        let entry = NonZeroU32::new(unsafe { IOIteratorNext(iterator.0.get()) })?;
        let entry = RegistryObject(entry);
        Some(match CfObject::property(&entry, &vendor) {
            // Some services do not publish a vendor at all. We only scan published IDs.
            None => Entry::Irrelevant,
            Some(value) => match value.usb_id() {
                Some(RAZER_VID) => match CfObject::property(&entry, &transport)
                    .and_then(|value| value.is_transport(&usb))
                {
                    Some(true) => CfObject::property(&entry, &product)
                        .and_then(|value| value.usb_id())
                        .map_or(Entry::Unreadable, Entry::Candidate),
                    Some(false) => Entry::Irrelevant,
                    None => Entry::Unreadable,
                },
                Some(_) => Entry::Irrelevant,
                None => Entry::Unreadable,
            },
        })
    }));
    // SAFETY: The iterator is still owned and live. A zero from Next can
    // indicate invalidation on a registry change rather than end of results.
    if unsafe { IOIteratorIsValid(iterator.0.get()) } == 0 {
        hid.invalidate();
    }
    hid
}
