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
 * aborts, allocation failure and process faults are not recoverable errors.
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
#ifdef __cplusplus
}
#endif
#endif
