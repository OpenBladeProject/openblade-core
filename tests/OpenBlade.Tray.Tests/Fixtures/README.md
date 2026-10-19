# Reviewed driver catalog snapshots

These metadata-only fixtures are copied from
[blade-driver-catalog commit f4dec7d](https://github.com/OpenBladeProject/blade-driver-catalog/tree/f4dec7de7d8b3ece8fb3012428310746e37000f8):

- `DriverCatalogApplicability.json`: `catalog/applicability.json`
- `ThxNativeComponentCatalog.json`: `components/thx-v4-native/1.1.1.0/component.json`
- `ThxRznb11StackCatalog.json`: `components/thx-rznb11/4.2.29.1159/stack.json`
- `ThxRznb12StackCatalog.json`: `components/thx-rznb12/4.2.41.2499/stack.json`
- `ThxRznb12CachedStackCatalog.json`: `components/thx-rznb12/4.2.37.1621/stack.json`
- `ThxLegacyStackCatalog.json`: `components/thx-legacy/2.6.5.0/stack.json`

When adding a model to `RazerBladeModelCatalog`, review every catalog entry
against exact-model vendor evidence and update the catalog ledger first.
Refresh the snapshots from the reviewed catalog commit and update this link.
`NotEstablished` records missing evidence, not a runtime incompatibility claim.
The legacy snapshot records both Realtek OEM codec tuning and the embedded
model-specific THX SCU tuning, separately from the unverified component/endpoint
binding that keeps fresh installation blocked. The cached rznb12 snapshot pins
the published original carrier and protected inner execution recipe; it remains
an experimental whole-stack candidate with both-endpoint read admission.
The fixtures contain no vendor binaries and tests make no network requests.
