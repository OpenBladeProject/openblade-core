//! Exact0581 linked state/target codec. No transport authority.
use crate::RazerReport;
use openblade_types::ProtocolValidationFailure as Failure;
#[derive(Clone, Debug)]
pub struct FanPair {
    commands: [RazerReport; 2],
}
impl FanPair {
    pub fn state(ids: [u8; 2], selector: u8, manual: bool) -> Option<Self> {
        if !matches!(selector, 0 | 2) {
            return None;
        }
        Self::new(ids, 2, |i| ([1, i + 1, selector, u8::from(manual)], 4))
    }
    pub fn target(ids: [u8; 2], rpm: u16) -> Option<Self> {
        if !(2000..=5400).contains(&rpm) || !rpm.is_multiple_of(100) {
            return None;
        }
        Self::new(ids, 1, |i| ([1, i + 1, (rpm / 100) as u8, 0], 3))
    }
    fn new(ids: [u8; 2], command: u8, payload: impl Fn(u8) -> ([u8; 4], usize)) -> Option<Self> {
        if ids.contains(&0) || ids[0] == ids[1] {
            return None;
        }
        Some(Self {
            commands: core::array::from_fn(|i| {
                let (p, n) = payload(i as u8);
                RazerReport::create(ids[i], 13, command, &p[..n])
                    .expect("reviewed bounded fan pair")
            }),
        })
    }
    pub fn commands(&self) -> &[RazerReport; 2] {
        &self.commands
    }
    pub fn acknowledge(&self, index: usize, reply: &[u8]) -> Result<(), Failure> {
        let c = self.commands.get(index).ok_or(Failure::PayloadValidator)?;
        let b = c.encode();
        let n = b[5] as usize;
        if c.validate_response(reply, Some(n as i32))?.as_slice() != &b[8..8 + n] {
            return Err(Failure::PayloadValidator);
        }
        Ok(())
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn exact_fan_pairs_validate_envelope_and_each_channel() {
        for pair in [
            FanPair::state([3, 4], 2, true).unwrap(),
            FanPair::state([5, 6], 0, false).unwrap(),
            FanPair::target([7, 8], 3700).unwrap(),
        ] {
            for i in 0..2 {
                let mut b = pair.commands()[i].encode();
                b[0] = 2;
                assert!(pair.acknowledge(i, &b).is_ok());
                assert!(pair.acknowledge(1 - i, &b).is_err());
                b[88] ^= 1;
                assert!(pair.acknowledge(i, &b).is_err());
            }
        }
        assert!(FanPair::target([1, 2], 3701).is_none());
        assert!(FanPair::state([1, 1], 0, true).is_none());
    }
}
