#include <stddef.h>
#include <stdint.h>
#include "openblade.h"

_Static_assert(sizeof(ObReportRequestV1) == 8, "request size");
_Static_assert(_Alignof(ObReportRequestV1) == 4, "request alignment");
_Static_assert(offsetof(ObReportRequestV1, struct_size) == 0, "request size offset");
_Static_assert(offsetof(ObReportRequestV1, transaction_id) == 4, "transaction offset");
_Static_assert(offsetof(ObReportRequestV1, command_class) == 5, "class offset");
_Static_assert(offsetof(ObReportRequestV1, command_id) == 6, "ID offset");
_Static_assert(offsetof(ObReportRequestV1, reserved) == 7, "request reserved offset");
_Static_assert(sizeof(ObValidationOptionsV1) == 12, "options size");
_Static_assert(_Alignof(ObValidationOptionsV1) == 4, "options alignment");
_Static_assert(offsetof(ObValidationOptionsV1, struct_size) == 0, "options size offset");
_Static_assert(offsetof(ObValidationOptionsV1, expected_payload_length) == 4, "length offset");
_Static_assert(offsetof(ObValidationOptionsV1, has_expected_payload_length) == 8, "presence offset");
_Static_assert(offsetof(ObValidationOptionsV1, reserved) == 9, "options reserved offset");
_Static_assert(sizeof(ObErrorV1) == 16, "error size");
_Static_assert(_Alignof(ObErrorV1) == 4, "error alignment");
_Static_assert(offsetof(ObErrorV1, struct_size) == 0, "error size offset");
_Static_assert(offsetof(ObErrorV1, category) == 4, "category offset");
_Static_assert(offsetof(ObErrorV1, detail) == 8, "detail offset");
_Static_assert(offsetof(ObErrorV1, required_length) == 12, "required offset");

int main(void)
{
    const ObReportRequestV1 request = {sizeof(ObReportRequestV1), 1, 2, 3, 0};
    const ObValidationOptionsV1 options = {sizeof(ObValidationOptionsV1), 0, 0, {0}};
    ObErrorV1 error = {sizeof(ObErrorV1), 0, 0, 0};
    uint32_t written = 0;
    uint8_t output[90] = {0};
    if (ob_abi_version() != OB_ABI_VERSION) return 1;
    if (ob_v1_encode_report(OB_ABI_VERSION, &request, NULL, 0, NULL, 0, &written, &error) != 3 ||
        written != sizeof(output) || error.category != 3 || error.required_length != sizeof(output)) return 2;
    if (ob_v1_encode_report(OB_ABI_VERSION, &request, NULL, 0, output, sizeof(output), &written, &error) != 0 ||
        written != sizeof(output) || error.category != 0 || error.detail != 0 || error.required_length != 0) return 3;
    /* Exercise validation linkage without creating another golden report oracle. */
    if (ob_v1_validate_response(OB_ABI_VERSION, &request, NULL, 0, NULL, 0, &options,
        output, sizeof(output), &written, &error) != 4 || written != 0 || error.detail != 1) return 4;
    return 0;
}
