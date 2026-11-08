/* Test-only loader fixture: policy v1 is available without any codec exports.
 * Work stubs fail without publishing results; parity tests always use Rust. */
#include "openblade.h"
uint32_t OB_CALL ob_policy_abi_version(void) { return OB_POLICY_ABI_VERSION; }
static uint32_t unavailable(uint32_t *written, ObErrorV1 *error)
{
    if (written) *written = 0;
    if (error && error->struct_size == sizeof(ObErrorV1)) {
        error->category = 5;
        error->detail = 0;
        error->required_length = 0;
    }
    return 5;
}
uint32_t OB_CALL ob_v1_apply_profile(uint32_t abi_version,
    const ObProfileProjectionV1 *baseline, const ObProfileProjectionV1 *override_value,
    const uint8_t *keys, uint32_t keys_length, ObProfileProjectionV1 *result,
    uint8_t *output_keys, uint32_t capacity, uint32_t *written, ObErrorV1 *error)
{
    (void)abi_version; (void)baseline; (void)override_value; (void)keys;
    (void)keys_length; (void)result; (void)output_keys; (void)capacity;
    return unavailable(written, error);
}
uint32_t OB_CALL ob_v1_apply_display(uint32_t abi_version,
    const ObDisplayProjectionV1 *baseline, const ObDisplayProjectionV1 *override_value,
    const uint8_t *keys, uint32_t keys_length, ObDisplayProjectionV1 *result,
    uint8_t *output_keys, uint32_t capacity, uint32_t *written, ObErrorV1 *error)
{
    (void)abi_version; (void)baseline; (void)override_value; (void)keys;
    (void)keys_length; (void)result; (void)output_keys; (void)capacity;
    return unavailable(written, error);
}
