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

_Static_assert(sizeof(ObOptionalI32V1) == 8, "optional scalar size");
_Static_assert(_Alignof(ObOptionalI32V1) == 4, "optional scalar alignment");
_Static_assert(offsetof(ObOptionalI32V1, value) == 0, "scalar value offset");
_Static_assert(offsetof(ObOptionalI32V1, present) == 4, "scalar presence offset");
_Static_assert(offsetof(ObOptionalI32V1, reserved) == 5, "scalar reserved offset");
_Static_assert(sizeof(ObOptionalUtf8V1) == 12, "UTF-8 reference size");
_Static_assert(_Alignof(ObOptionalUtf8V1) == 4, "UTF-8 reference alignment");
_Static_assert(offsetof(ObOptionalUtf8V1, offset) == 0, "UTF-8 offset offset");
_Static_assert(offsetof(ObOptionalUtf8V1, length) == 4, "UTF-8 length offset");
_Static_assert(offsetof(ObOptionalUtf8V1, present) == 8, "UTF-8 presence offset");
_Static_assert(offsetof(ObOptionalUtf8V1, reserved) == 9, "UTF-8 reserved offset");
_Static_assert(sizeof(ObDisplayProjectionV1) == 44, "display size");
_Static_assert(_Alignof(ObDisplayProjectionV1) == 4, "display alignment");
_Static_assert(offsetof(ObDisplayProjectionV1, struct_size) == 0, "display size offset");
_Static_assert(offsetof(ObDisplayProjectionV1, refresh_rate_key) == 4, "refresh offset");
_Static_assert(offsetof(ObDisplayProjectionV1, hdr_enabled) == 16, "HDR offset");
_Static_assert(offsetof(ObDisplayProjectionV1, color_profile_key) == 24, "color offset");
_Static_assert(offsetof(ObDisplayProjectionV1, brightness_percent) == 36, "brightness offset");
_Static_assert(sizeof(ObProfileProjectionV1) == 88, "profile size");
_Static_assert(_Alignof(ObProfileProjectionV1) == 4, "profile alignment");
_Static_assert(offsetof(ObProfileProjectionV1, struct_size) == 0, "profile size offset");
_Static_assert(offsetof(ObProfileProjectionV1, performance) == 4, "performance offset");
_Static_assert(offsetof(ObProfileProjectionV1, fans) == 12, "fans offset");
_Static_assert(offsetof(ObProfileProjectionV1, fan_mode) == 20, "fan mode offset");
_Static_assert(offsetof(ObProfileProjectionV1, fixed_speed_rpm) == 24, "fan RPM offset");
_Static_assert(offsetof(ObProfileProjectionV1, has_fan_control) == 28, "fan presence offset");
_Static_assert(offsetof(ObProfileProjectionV1, has_display) == 29, "display presence offset");
_Static_assert(offsetof(ObProfileProjectionV1, reserved) == 30, "profile reserved offset");
_Static_assert(offsetof(ObProfileProjectionV1, gaming_mode_enabled) == 32, "gaming offset");
_Static_assert(offsetof(ObProfileProjectionV1, display) == 40, "nested display offset");
_Static_assert(offsetof(ObProfileProjectionV1, fan_control_source) == 84, "curve source offset");

_Static_assert(sizeof(ObAutomationRequestV1) == 16, "automation request size");
_Static_assert(_Alignof(ObAutomationRequestV1) == 4, "automation request alignment");
_Static_assert(offsetof(ObAutomationRequestV1, struct_size) == 0, "automation size offset");
_Static_assert(offsetof(ObAutomationRequestV1, operation) == 4, "automation operation offset");
_Static_assert(offsetof(ObAutomationRequestV1, has_profile) == 8, "automation profile offset");
_Static_assert(offsetof(ObAutomationRequestV1, has_confirmed) == 9, "automation confirmed offset");
_Static_assert(offsetof(ObAutomationRequestV1, has_temporary) == 10, "automation temporary offset");
_Static_assert(offsetof(ObAutomationRequestV1, has_persistent) == 11, "automation persistent offset");
_Static_assert(offsetof(ObAutomationRequestV1, external_unknown) == 12, "automation power offset");
_Static_assert(offsetof(ObAutomationRequestV1, from_application) == 13, "automation application offset");
_Static_assert(offsetof(ObAutomationRequestV1, reserved) == 14, "automation reserved offset");
_Static_assert(sizeof(ObAutomationSelectionV1) == 12, "automation selection size");
_Static_assert(_Alignof(ObAutomationSelectionV1) == 4, "automation selection alignment");
_Static_assert(offsetof(ObAutomationSelectionV1, struct_size) == 0, "selection size offset");
_Static_assert(offsetof(ObAutomationSelectionV1, origin) == 4, "selection origin offset");
_Static_assert(offsetof(ObAutomationSelectionV1, provenance) == 8, "selection provenance offset");

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
    {
        ObProfileProjectionV1 baseline = {0};
        ObProfileProjectionV1 policy = {0};
        ObProfileProjectionV1 merged = {0};
        ObDisplayProjectionV1 display_result = {0};
        const uint8_t key[] = {'x', 0xE8, 0x89, 0xB2};
        uint8_t key_output[8] = {0};
        baseline.struct_size = sizeof(baseline);
        baseline.performance.present = 1;
        baseline.performance.value = 1;
        baseline.fans.present = 1;
        baseline.has_display = 1;
        baseline.display.struct_size = sizeof(baseline.display);
        baseline.display.refresh_rate_key.present = 1;
        baseline.display.refresh_rate_key.length = sizeof(key);
        policy.struct_size = sizeof(policy);
        policy.fans.present = 1;
        policy.fans.value = 3;
        merged.struct_size = sizeof(merged);
        if (ob_policy_abi_version() != OB_POLICY_ABI_VERSION) return 5;
        if (ob_v1_apply_profile(OB_POLICY_ABI_VERSION, &baseline, &policy,
            key, sizeof(key), &merged, NULL, 0, &written, &error) != 3 ||
            written != sizeof(key) || merged.has_display != 0) return 6;
        if (ob_v1_apply_profile(OB_POLICY_ABI_VERSION, &baseline, &policy,
            key, sizeof(key), &merged, key_output, sizeof(key_output), &written, &error) != 0 ||
            written != sizeof(key) || merged.fans.value != 3 || merged.has_fan_control != 1 ||
            merged.fan_mode != 3 || merged.fixed_speed_rpm != 5400 ||
            merged.fan_control_source != 3 || merged.display.refresh_rate_key.length != sizeof(key) ||
            key_output[1] != 0xE8 || key_output[3] != 0xB2 || key_output[4] != 0) return 7;
        display_result.struct_size = sizeof(display_result);
        if (ob_v1_apply_display(OB_POLICY_ABI_VERSION, &baseline.display, NULL,
            key, sizeof(key), &display_result, key_output, sizeof(key_output), &written, &error) != 0 ||
            written != sizeof(key) || display_result.refresh_rate_key.present != 1) return 8;
    }
    const ObAutomationRequestV1 selection = {16, 1, 1, 1, 1, 1, 1, 1, {0}};
    ObAutomationSelectionV1 selected = {12, 99, 98};
    if (ob_automation_abi_version() != OB_AUTOMATION_ABI_VERSION) return 20;
    if (ob_v1_select_automation(OB_AUTOMATION_ABI_VERSION, &selection,
        &selected, &written, &error) != 0 || written != 0 ||
        selected.origin != 1 || selected.provenance != 0) return 21;
    if (ob_v1_select_automation(2, &selection, &selected, &written, &error) != 1 ||
        selected.origin != 1 || selected.provenance != 0 || written != 0) return 22;
    uint32_t eligible = 99;
    if (ob_v1_performance_source_eligible(2, 1, 2, &eligible, &written, &error) != 1 ||
        eligible != 99 || written != 0) return 23;
    if (ob_v1_performance_source_eligible(OB_AUTOMATION_ABI_VERSION, 1, 2,
        &eligible, &written, &error) != 0 || eligible != 1 || written != 0) return 24;
    if (ob_v1_performance_source_eligible(OB_AUTOMATION_ABI_VERSION, -1, 0,
        &eligible, &written, NULL) != 0 || eligible != 0 || written != 0) return 25;
    if (ob_v1_performance_source_eligible(OB_AUTOMATION_ABI_VERSION, 5, 2,
        &eligible, &written, &error) != 0 || eligible != 1 || written != 0) return 26;
    return 0;
}
