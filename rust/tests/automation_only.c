/* Test-only export fixture: never supplies parity, never packaged. */
#include "openblade.h"
uint32_t OB_CALL ob_automation_abi_version(void) { return OB_AUTOMATION_ABI_VERSION; }
uint32_t OB_CALL ob_v1_select_automation(uint32_t version,
    const ObAutomationRequestV1 *request, ObAutomationSelectionV1 *result,
    uint32_t *written, ObErrorV1 *error)
{
    (void)version; (void)request; (void)result; (void)written; (void)error;
    return 2;
}
