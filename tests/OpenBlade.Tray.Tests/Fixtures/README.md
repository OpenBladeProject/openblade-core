# Reviewed driver catalog snapshots

These metadata-only fixtures are copied from
[blade-driver-catalog commit 89c12ae](https://github.com/OpenBladeProject/blade-driver-catalog/tree/89c12ae4d805a8102776f75b8e27d46084a137b9):

- `DriverCatalogApplicability.json`: `catalog/applicability.json`
- `ThxNativeComponentCatalog.json`: `components/thx-v4-native/1.1.1.0/component.json`

When adding a model to `RazerBladeModelCatalog`, review every catalog entry
against exact-model vendor evidence and update the catalog ledger first.
Refresh both snapshots from the reviewed catalog commit and update this link.
`NotEstablished` records missing evidence, not a runtime incompatibility claim.
The fixtures contain no vendor binaries and tests make no network requests.
