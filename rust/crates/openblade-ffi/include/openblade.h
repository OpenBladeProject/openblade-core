#ifndef OPENBLADE_H
#define OPENBLADE_H
#include <stdint.h>
#ifdef __cplusplus
extern "C" {
#endif
#if defined(_WIN32)
#define OB_CALL __cdecl
#else
#define OB_CALL
#endif
#define OB_ABI_VERSION UINT32_C(0x00010000)
/* Optional additive policy contract; codec 1.0 negotiation is unchanged. */
#define OB_POLICY_ABI_VERSION UINT32_C(0x00010000)
/* Natural C layout, never packed. Sizes/alignment: request 8/4, options 12/4,
 * error 16/4. All struct_size fields must equal these exact sizes. */
typedef struct ObReportRequestV1 {
    uint32_t struct_size; /* offset 0 */
    uint8_t transaction_id; /* 4 */
    uint8_t command_class; /* 5 */
    uint8_t command_id; /* 6 */
    uint8_t reserved; /* 7, must be zero */
} ObReportRequestV1;
typedef struct ObValidationOptionsV1 {
    uint32_t struct_size; /* 0 */
    int32_t expected_payload_length; /* 4, signed, ignored when absent */
    uint8_t has_expected_payload_length; /* 8, exactly 0 or 1 */
    uint8_t reserved[3]; /* 9, all zero */
} ObValidationOptionsV1;
typedef struct ObErrorV1 {
    uint32_t struct_size; /* 0 */
    uint32_t category; /* 4 */
    uint32_t detail; /* 8 */
    uint32_t required_length; /* 12 */
} ObErrorV1;
/* Return/category: success=0, ABI mismatch=1, invalid argument=2,
 * buffer too small=3, protocol validation=4, internal failure=5.
 * Invalid-argument detail: payload too large=1, invalid length=2,
 * invalid flag/reserved=3, invalid UTF-8=4 (unused by these binary calls),
 * required pointer absent=5, invalid record size=6.
 * Protocol detail: envelope length=1, checksum=2, transaction=3, class=4,
 * ID=5, status=6, payload length=7. Other category details are zero.
 * Error required_length is nonzero only for insufficient capacity.
 * Success clears detail/required_length; invalid error size leaves it untouched.
 *
 * Exact ABI version is checked before reading operation inputs. All calls are
 * synchronous/stateless; no memory is retained, no I/O or hardware work occurs.
 * Caller guarantees aligned, valid, accessible ranges
 * for the entire call. Writable ranges must be mutually disjoint and disjoint
 * from all input ranges; immutable inputs may alias or be shared across calls
 * if no caller mutates them during calls. Even malformed records require a
 * readable u32 size prefix; only a recognized exact size requires the complete
 * record. Dangling pointers cannot be validated by this API.
 * Input null is allowed only for zero-length byte slices; output null only for
 * capacity zero. request/options/written_or_required are required; error optional.
 * A zero-capacity output is a query: buffer-too-small with exact required count,
 * except empty validated payload succeeds. Success writes only result bytes;
 * insufficient capacity changes no result bytes and sets written to required;
 * every other failure changes no result bytes and sets written to zero when
 * supplied. Caller memory ownership never transfers. Panic unwinds are caught;
 * caught panic payloads are deliberately forgotten to prevent destructor panics.
 * The process panic hook is unchanged and may emit diagnostics. Aborts,
 * allocation failure and process faults are not recoverable errors.
 * Concurrent calls require disjoint writable buffers. No exported test hooks.
 * Admission precedence: error record size, ABI version, written pointer,
 * request size/reserved, payload bound/pointer, options size/flags/reserved,
 * response pointer, protocol checks, output pointer/capacity. Options/response
 * checks apply only to validation. An absent expected length ignores its value.
 */
uint32_t OB_CALL ob_abi_version(void);
/* Writes 90 bytes excluding the OS report-ID prefix; payload is at most 80. */
uint32_t OB_CALL ob_v1_encode_report(uint32_t abi_version,
    const ObReportRequestV1 *request, const uint8_t *payload,
    uint32_t payload_length, uint8_t *output, uint32_t capacity,
    uint32_t *written_or_required, ObErrorV1 *error);
/* Validates before capacity handling, preserving codec error precedence.
 * Copies up to 80 payload bytes; negative expected lengths remain protocol errors. */
uint32_t OB_CALL ob_v1_validate_response(uint32_t abi_version,
    const ObReportRequestV1 *request, const uint8_t *payload,
    uint32_t payload_length, const uint8_t *response, uint32_t response_length,
    const ObValidationOptionsV1 *options, uint8_t *output, uint32_t capacity,
    uint32_t *written_or_required, ObErrorV1 *error);
/* Policy records use natural alignment 4, no native pointers. Optional scalar
 * records have size 8; UTF-8 references size 12; display size 44; profile size 88.
 * Every present flag is exactly 0 or 1; all reserved fields are zero. Absent
 * scalar/reference payloads are ignored. Absent nested objects are ignored,
 * including their nested size prefixes. Present nested records require exact size.
 * UTF-8 offsets/lengths refer to the shared keys input or the output_keys result.
 * Null and empty keys differ; keys are opaque strict UTF-8, not OS lookups. */
typedef struct ObOptionalI32V1 {
    int32_t value; /* 0; present booleans require 0 or 1 */
    uint8_t present; /* 4 */
    uint8_t reserved[3]; /* 5 */
} ObOptionalI32V1;
typedef struct ObOptionalUtf8V1 {
    uint32_t offset; /* 0 */
    uint32_t length; /* 4, bytes, no terminator */
    uint8_t present; /* 8 */
    uint8_t reserved[3]; /* 9 */
} ObOptionalUtf8V1;
typedef struct ObDisplayProjectionV1 {
    uint32_t struct_size; /* 0, exactly 44 */
    ObOptionalUtf8V1 refresh_rate_key; /* 4 */
    ObOptionalI32V1 hdr_enabled; /* 16 */
    ObOptionalUtf8V1 color_profile_key; /* 24 */
    ObOptionalI32V1 brightness_percent; /* 36 */
} ObDisplayProjectionV1;
typedef struct ObProfileProjectionV1 {
    uint32_t struct_size; /* 0, exactly 88 */
    ObOptionalI32V1 performance; /* 4; baseline required */
    ObOptionalI32V1 fans; /* 12; baseline required */
    int32_t fan_mode; /* 20, ignored unless has_fan_control */
    int32_t fixed_speed_rpm; /* 24, ignored unless has_fan_control */
    uint8_t has_fan_control; /* 28 */
    uint8_t has_display; /* 29 */
    uint8_t reserved[2]; /* 30 */
    ObOptionalI32V1 gaming_mode_enabled; /* 32 */
    ObDisplayProjectionV1 display; /* 40 */
    uint32_t fan_control_source; /* 84; input zero; result: none 0,
                                   baseline 1, override 2, legacy default 3 */
} ObProfileProjectionV1;
/* Additive pure policy exports have independent negotiation. Existing codec
 * clients need neither resolve these symbols nor change their version checks.
 * Scalar performance/fan mode/RPM/brightness values are merge data, not native
 * enums or a whole-profile validation boundary. Unknown values pass through,
 * except legacy fan synthesis admits only modes 0..3 (invalid-argument detail 3).
 * Host validation order, custom curves and all fields outside this projection
 * remain host-owned. Contradictory fan fields are deliberately preserved.
 *
 * The shared memory, disjointness, lifetime and panic rules above also apply.
 * baseline/result/written_or_required are required; override_value and error
 * are optional. result requires an accessible size prefix and an exact-size
 * writable record on success; set result.struct_size before calling.
 * Admission precedence: error size, policy ABI version, written pointer, result
 * size, baseline input, override input, then output pointer/capacity. All input
 * validation precedes publication. Invalid UTF-8 is detail 4; out-of-bounds or
 * overflowing UTF-8 references are detail 2. A missing baseline is detail 5.
 * No result record or output bytes change on any failure, including a capacity
 * query. written is exact required bytes on insufficient capacity, otherwise
 * zero on failure. Success publishes the result and only its output key bytes;
 * refresh key bytes precede color key bytes, without terminators. Empty results
 * succeed with null output_keys/capacity zero. Calls retain no memory or state.
 */
uint32_t OB_CALL ob_policy_abi_version(void);
uint32_t OB_CALL ob_v1_apply_profile(uint32_t abi_version,
    const ObProfileProjectionV1 *baseline,
    const ObProfileProjectionV1 *override_value,
    const uint8_t *keys, uint32_t keys_length,
    ObProfileProjectionV1 *result, uint8_t *output_keys, uint32_t capacity,
    uint32_t *written_or_required, ObErrorV1 *error);
uint32_t OB_CALL ob_v1_apply_display(uint32_t abi_version,
    const ObDisplayProjectionV1 *baseline,
    const ObDisplayProjectionV1 *override_value,
    const uint8_t *keys, uint32_t keys_length,
    ObDisplayProjectionV1 *result, uint8_t *output_keys, uint32_t capacity,
    uint32_t *written_or_required, ObErrorV1 *error);
/* Independent additive automation v1. Codec/profile policy negotiation is unchanged.
 * Fixed natural layouts: request 16/4, result 12/4. All flags are 0/1, reserved zero.
 * operation: performance 0, lighting 1. Presence never validates a host value.
 * Performance consumes temporary/persistent/from_application only (profile is a scalar).
 * Lighting consumes all flags; external_unknown ignores overrides/application provenance.
 * origin: profile 0, confirmed 1, temporary 2, persistent 3.
 * provenance: confirmed firmware 0, power profile 1, application 2, temporary 3, persistent 4.
 * Same borrowed-memory, panic/error and disjointness rules as codec v1.
 * request/result/written are required; error optional. Both record size prefixes must
 * be readable; exact sizes require full accessible records. Result needs struct_size=12.
 * Admission: error size, automation version, written, required record pointers,
 * result/request sizes, flags/reserved, operation. No result publication on failure.
 * written is always zero; no variable buffer or capacity negotiation is needed.
 * No values/strings/objects cross the ABI; host maps returned tokens to original values.
 */
#define OB_AUTOMATION_ABI_VERSION UINT32_C(0x00010000)
typedef struct ObAutomationRequestV1 {
    uint32_t struct_size; /* 0, exactly 16 */
    uint32_t operation; /* 4 */
    uint8_t has_profile; /* 8 */
    uint8_t has_confirmed; /* 9 */
    uint8_t has_temporary; /* 10 */
    uint8_t has_persistent; /* 11 */
    uint8_t external_unknown; /* 12 */
    uint8_t from_application; /* 13 */
    uint8_t reserved[2]; /* 14 */
} ObAutomationRequestV1;
typedef struct ObAutomationSelectionV1 {
    uint32_t struct_size; /* 0, exactly 12 */
    uint32_t origin; /* 4 */
    uint32_t provenance; /* 8 */
} ObAutomationSelectionV1;
uint32_t OB_CALL ob_automation_abi_version(void);
uint32_t OB_CALL ob_v1_select_automation(uint32_t abi_version,
    const ObAutomationRequestV1 *request, ObAutomationSelectionV1 *result,
    uint32_t *written, ObErrorV1 *error);
#ifdef __cplusplus
}
#endif
#endif
