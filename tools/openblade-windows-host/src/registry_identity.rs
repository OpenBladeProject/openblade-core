/// Manufacturer and product must agree ordinally across the two Windows
/// registry sources. SKU and BIOS version do not participate in this decision.
pub(crate) struct RegistryIdentity<'a> {
    pub(crate) bios: (Option<&'a str>, Option<&'a str>),
    pub(crate) system: (Option<&'a str>, Option<&'a str>),
}

pub(crate) enum RegistryMatch<'a> {
    Incomplete,
    Contradictory,
    UnknownManufacturer,
    RazerProduct(&'a str),
}

impl<'a> RegistryIdentity<'a> {
    pub(crate) fn resolve(self) -> RegistryMatch<'a> {
        match (self.bios, self.system) {
            (
                (Some(bios_maker), Some(bios_product)),
                (Some(system_maker), Some(system_product)),
            ) => {
                if (bios_maker, bios_product) != (system_maker, system_product) {
                    RegistryMatch::Contradictory
                } else if bios_maker == "Razer" {
                    RegistryMatch::RazerProduct(bios_product)
                } else {
                    RegistryMatch::UnknownManufacturer
                }
            }
            _ => RegistryMatch::Incomplete,
        }
    }
}
