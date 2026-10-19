# Reviewed driver catalog snapshots

These metadata-only fixtures are copied from
[blade-driver-catalog commit e65d43d](https://github.com/OpenBladeProject/blade-driver-catalog/tree/e65d43dcd5128291dfcc9b333d6cd3e93c2f9ad3):

- `DriverCatalogApplicability.json`: `catalog/applicability.json`
- `ThxNativeComponentCatalog.json`: `components/thx-v4-native/1.1.1.0/component.json`
- `ThxRznb11StackCatalog.json`: `components/thx-rznb11/4.2.29.1159/stack.json`
- `ThxRznb12StackCatalog.json`: `components/thx-rznb12/4.2.41.2499/stack.json`
- `ThxLegacyStackCatalog.json`: `components/thx-legacy/2.6.5.0/stack.json`

When adding a model to `RazerBladeModelCatalog`, review every catalog entry
against exact-model vendor evidence and update the catalog ledger first.
Refresh the snapshots from the reviewed catalog commit and update this link.
`NotEstablished` records missing evidence, not a runtime incompatibility claim.
The legacy snapshot records Realtek OEM codec tuning separately from the
unverified THX integration and keeps fresh installation blocked.
The fixtures contain no vendor binaries and tests make no network requests.
