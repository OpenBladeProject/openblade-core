//! Additive, stateless policy projection ABI. Codec ABI v1 remains independently usable.
use crate::{ABSENT, FLAG, Failure, ObErrorV1, RECORD_SIZE, boundary, bytes};
use openblade_domain::{
    DisplayProjection, FanControlProjection, ProfileOverride, ProfileProjection,
};
use std::{mem, ptr};

pub const OB_POLICY_ABI_VERSION: u32 = 0x0001_0000;

#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct ObOptionalI32V1 {
    pub value: i32,
    pub present: u8,
    pub reserved: [u8; 3],
}
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct ObOptionalUtf8V1 {
    pub offset: u32,
    pub length: u32,
    pub present: u8,
    pub reserved: [u8; 3],
}
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct ObDisplayProjectionV1 {
    pub struct_size: u32,
    pub refresh_rate_key: ObOptionalUtf8V1,
    pub hdr_enabled: ObOptionalI32V1,
    pub color_profile_key: ObOptionalUtf8V1,
    pub brightness_percent: ObOptionalI32V1,
}
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct ObProfileProjectionV1 {
    pub struct_size: u32,
    pub performance: ObOptionalI32V1,
    pub fans: ObOptionalI32V1,
    pub fan_mode: i32,
    pub fixed_speed_rpm: i32,
    pub has_fan_control: u8,
    pub has_display: u8,
    pub reserved: [u8; 2],
    pub gaming_mode_enabled: ObOptionalI32V1,
    pub display: ObDisplayProjectionV1,
    pub fan_control_source: u32,
}

#[unsafe(no_mangle)]
pub extern "C" fn ob_policy_abi_version() -> u32 {
    OB_POLICY_ABI_VERSION
}

// All pointer helpers are private; the exported contract supplies aligned,
// accessible ranges and disjoint writes. Never inspect a full unknown record.
unsafe fn admit_record<T>(pointer: *const T) -> Result<(), Failure> {
    if pointer.is_null() {
        return Err(ABSENT);
    }
    // SAFETY: callers supply a readable aligned size prefix.
    if unsafe { pointer.cast::<u32>().read() } != mem::size_of::<T>() as u32 {
        return Err(RECORD_SIZE);
    }
    Ok(())
}

fn optional(value: ObOptionalI32V1) -> Result<Option<i32>, Failure> {
    if value.present > 1 || value.reserved != [0; 3] {
        return Err(FLAG);
    }
    Ok((value.present == 1).then_some(value.value))
}
fn boolean(value: ObOptionalI32V1) -> Result<Option<bool>, Failure> {
    optional(value)?
        .map(|v| match v {
            0 => Ok(false),
            1 => Ok(true),
            _ => Err(FLAG),
        })
        .transpose()
}
fn key(value: ObOptionalUtf8V1, keys: &[u8]) -> Result<Option<&str>, Failure> {
    if value.present > 1 || value.reserved != [0; 3] {
        return Err(FLAG);
    }
    if value.present == 0 {
        return Ok(None);
    }
    let end = value
        .offset
        .checked_add(value.length)
        .ok_or(Failure(2, 2, 0))?;
    let selected = keys
        .get(value.offset as usize..end as usize)
        .ok_or(Failure(2, 2, 0))?;
    std::str::from_utf8(selected)
        .map(Some)
        .map_err(|_| Failure(2, 4, 0))
}
fn display(value: ObDisplayProjectionV1, keys: &[u8]) -> Result<DisplayProjection<'_>, Failure> {
    if value.struct_size != 44 {
        return Err(RECORD_SIZE);
    }
    Ok(DisplayProjection {
        refresh_rate_key: key(value.refresh_rate_key, keys)?,
        hdr_enabled: boolean(value.hdr_enabled)?,
        color_profile_key: key(value.color_profile_key, keys)?,
        brightness_percent: optional(value.brightness_percent)?,
    })
}
fn profile(value: ObProfileProjectionV1, keys: &[u8]) -> Result<ProfileOverride<'_>, Failure> {
    if value.has_fan_control > 1
        || value.has_display > 1
        || value.reserved != [0; 2]
        || value.fan_control_source != 0
    {
        return Err(FLAG);
    }
    Ok(ProfileOverride {
        performance: optional(value.performance)?,
        fans: optional(value.fans)?,
        fan_control: (value.has_fan_control == 1).then_some(FanControlProjection {
            mode: value.fan_mode,
            fixed_speed_rpm: value.fixed_speed_rpm,
        }),
        display: if value.has_display == 1 {
            Some(display(value.display, keys)?)
        } else {
            None
        },
        gaming_mode_enabled: boolean(value.gaming_mode_enabled)?,
    })
}
fn encode_optional(value: Option<i32>) -> ObOptionalI32V1 {
    ObOptionalI32V1 {
        value: value.unwrap_or(0),
        present: u8::from(value.is_some()),
        reserved: [0; 3],
    }
}
fn encode_bool(value: Option<bool>) -> ObOptionalI32V1 {
    encode_optional(value.map(i32::from))
}
fn encode_key(value: Option<&str>, offset: u32) -> Result<ObOptionalUtf8V1, Failure> {
    Ok(ObOptionalUtf8V1 {
        offset: if value.is_some() { offset } else { 0 },
        length: u32::try_from(value.map_or(0, str::len)).map_err(|_| Failure(2, 2, 0))?,
        present: u8::from(value.is_some()),
        reserved: [0; 3],
    })
}
fn encode_display(value: DisplayProjection<'_>) -> Result<ObDisplayProjectionV1, Failure> {
    let refresh = encode_key(value.refresh_rate_key, 0)?;
    let color = encode_key(value.color_profile_key, refresh.length)?;
    refresh
        .length
        .checked_add(color.length)
        .ok_or(Failure(2, 2, 0))?;
    Ok(ObDisplayProjectionV1 {
        struct_size: 44,
        refresh_rate_key: refresh,
        hdr_enabled: encode_bool(value.hdr_enabled),
        color_profile_key: color,
        brightness_percent: encode_optional(value.brightness_percent),
    })
}
// Validate all results/capacities before publishing either record or key bytes.
// There are no allocations, callbacks or fallible computations after publication starts.
unsafe fn publish<T: Copy>(
    record: T,
    result: *mut T,
    display: Option<DisplayProjection<'_>>,
    output: *mut u8,
    capacity: u32,
) -> Result<u32, Failure> {
    let refresh = display
        .and_then(|v| v.refresh_rate_key)
        .unwrap_or("")
        .as_bytes();
    let color = display
        .and_then(|v| v.color_profile_key)
        .unwrap_or("")
        .as_bytes();
    let required = refresh
        .len()
        .checked_add(color.len())
        .and_then(|v| u32::try_from(v).ok())
        .ok_or(Failure(2, 2, 0))?;
    if output.is_null() && capacity != 0 {
        return Err(ABSENT);
    }
    if capacity < required {
        return Err(Failure(3, 0, required));
    }
    #[cfg(test)]
    crate::panic_injection();
    // SAFETY: caller supplies admitted, accessible, disjoint writable ranges.
    unsafe {
        if !refresh.is_empty() {
            ptr::copy_nonoverlapping(refresh.as_ptr(), output, refresh.len());
        }
        if !color.is_empty() {
            ptr::copy_nonoverlapping(color.as_ptr(), output.add(refresh.len()), color.len());
        }
        result.write(record);
    }
    Ok(required)
}

/// Merge only the bounded profile slice; caller retains whole-profile validation.
/// # Safety
/// Same accessible/aligned, nonoverlapping pointer contract as codec v1. All records
/// need a readable u32 prefix; admitted sizes require the full accessible record.
/// Baseline/result/written are required; override/error optional. Key slices may be
/// null only when their length/capacity is zero. No input/output is retained.
#[allow(clippy::too_many_arguments)]
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ob_v1_apply_profile(
    abi_version: u32,
    baseline: *const ObProfileProjectionV1,
    override_: *const ObProfileProjectionV1,
    keys: *const u8,
    keys_length: u32,
    result: *mut ObProfileProjectionV1,
    output_keys: *mut u8,
    capacity: u32,
    written_or_required: *mut u32,
    error: *mut ObErrorV1,
) -> u32 {
    // SAFETY: forwarded exported contract. Policy currently negotiates the same
    // version value as codec v1, without changing or requiring codec exports.
    unsafe {
        boundary(abi_version, written_or_required, error, || {
            admit_record(result)?;
            admit_record(baseline)?;
            let keys = bytes(keys, keys_length)?;
            let baseline = profile(baseline.read(), keys)?;
            let baseline = ProfileProjection {
                performance: baseline.performance.ok_or(ABSENT)?,
                fans: baseline.fans.ok_or(ABSENT)?,
                fan_control: baseline.fan_control,
                display: baseline.display,
                gaming_mode_enabled: baseline.gaming_mode_enabled,
            };
            let override_ = if override_.is_null() {
                None
            } else {
                admit_record(override_)?;
                Some(profile(override_.read(), keys)?)
            };
            let resolved = openblade_domain::apply(Some(baseline), override_).map_err(|_| FLAG)?;
            let value = resolved.profile;
            let fan = value.fan_control;
            let record = ObProfileProjectionV1 {
                struct_size: 88,
                performance: encode_optional(Some(value.performance)),
                fans: encode_optional(Some(value.fans)),
                fan_mode: fan.map_or(0, |v| v.mode),
                fixed_speed_rpm: fan.map_or(0, |v| v.fixed_speed_rpm),
                has_fan_control: u8::from(fan.is_some()),
                has_display: u8::from(value.display.is_some()),
                reserved: [0; 2],
                gaming_mode_enabled: encode_bool(value.gaming_mode_enabled),
                display: encode_display(value.display.unwrap_or_default())?,
                fan_control_source: resolved.fan_control_source as u32,
            };
            publish(record, result, value.display, output_keys, capacity)
        })
    }
}

/// Merge display scalar fields and opaque keys only.
/// # Safety
/// Same pointer contract as [`ob_v1_apply_profile`], with display records.
#[allow(clippy::too_many_arguments)]
#[unsafe(no_mangle)]
pub unsafe extern "C" fn ob_v1_apply_display(
    abi_version: u32,
    baseline: *const ObDisplayProjectionV1,
    override_: *const ObDisplayProjectionV1,
    keys: *const u8,
    keys_length: u32,
    result: *mut ObDisplayProjectionV1,
    output_keys: *mut u8,
    capacity: u32,
    written_or_required: *mut u32,
    error: *mut ObErrorV1,
) -> u32 {
    // SAFETY: forwarded exported contract.
    unsafe {
        boundary(abi_version, written_or_required, error, || {
            admit_record(result)?;
            admit_record(baseline)?;
            let keys = bytes(keys, keys_length)?;
            let baseline = display(baseline.read(), keys)?;
            let override_ = if override_.is_null() {
                None
            } else {
                admit_record(override_)?;
                Some(display(override_.read(), keys)?)
            };
            let value = openblade_domain::apply_display(baseline, override_);
            publish(
                encode_display(value)?,
                result,
                Some(value),
                output_keys,
                capacity,
            )
        })
    }
}
