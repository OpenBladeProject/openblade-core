#![allow(dead_code)]
use sha2::{Digest, Sha256};
use std::io;
#[derive(Clone, Debug, PartialEq, Eq)]
pub(crate) enum Phase {
    Reserved,
    Active,
    Retiring,
    Retired,
}
#[derive(Clone, Debug)]
pub(crate) struct Record {
    pub(crate) phase: Phase,
    pub(crate) sequence: u64,
    pub(crate) predecessor: String,
    pub(crate) installation: String,
    pub(crate) epoch: String,
    pub(crate) artifact: String,
    pub(crate) process: String,
    pub(crate) nonce: String,
}
impl Record {
    pub(crate) fn bytes(&self) -> Vec<u8> {
        format!(
            "OpenBlade.NativeOwner.v1\n{:?}\n{}\n{}\n{}\n{}\n{}\n{}\n{}\n",
            self.phase,
            self.sequence,
            self.predecessor,
            self.installation,
            self.epoch,
            self.artifact,
            self.process,
            self.nonce
        )
        .into_bytes()
    }
    pub(crate) fn parse(bytes: &[u8]) -> io::Result<Self> {
        if bytes.is_empty()
            || bytes.len() > 4096
            || bytes.iter().any(|b| *b != 10 && !(0x20..=0x7e).contains(b))
        {
            return Err(deny());
        }
        let text = std::str::from_utf8(bytes).map_err(|_| deny())?;
        let f: Vec<_> = text.split('\n').collect();
        if f.len() != 10 || f[0] != "OpenBlade.NativeOwner.v1" || !f[9].is_empty() {
            return Err(deny());
        }
        let phase = match f[1] {
            "Reserved" => Phase::Reserved,
            "Active" => Phase::Active,
            "Retiring" => Phase::Retiring,
            "Retired" => Phase::Retired,
            _ => return Err(deny()),
        };
        let record = Self {
            phase,
            sequence: f[2].parse().map_err(|_| deny())?,
            predecessor: f[3].into(),
            installation: f[4].into(),
            epoch: f[5].into(),
            artifact: f[6].into(),
            process: f[7].into(),
            nonce: f[8].into(),
        };
        if record.sequence == 0
            || record.bytes() != bytes
            || !hex(&record.predecessor, 64)
            || !hex(&record.installation, 32)
            || !hex(&record.epoch, 32)
            || !hex(&record.nonce, 64)
            || record.artifact.len() != 72
            || !hex(&record.artifact[..64], 64)
            || !record.artifact.ends_with("-service")
            || record.process.split(':').count() != 2
            || record.process.split(':').any(|v| {
                v.parse::<u64>().is_err()
                    || v.parse::<u64>().is_ok_and(|n| n == 0 || n.to_string() != v)
            })
        {
            return Err(deny());
        }
        Ok(record)
    }
}
pub(crate) fn require_transition(prior: &Record, next: &Record) -> io::Result<()> {
    if Some(next.sequence) != prior.sequence.checked_add(1)
        || next.predecessor != digest(&prior.bytes())
    {
        return Err(deny());
    }
    let same = prior.installation == next.installation
        && prior.epoch == next.epoch
        && prior.artifact == next.artifact
        && prior.process == next.process
        && prior.nonce == next.nonce;
    if !matches!(
        (&prior.phase, &next.phase),
        (Phase::Reserved, Phase::Active)
            | (Phase::Active, Phase::Retiring)
            | (Phase::Retiring, Phase::Retired)
    ) || !same
    {
        // Across generations a fresh reservation is allowed only after actual
        // drain published Retired. Identical bytes still require a new epoch.
        if prior.phase != Phase::Retired
            || next.phase != Phase::Reserved
            || prior.epoch == next.epoch
            || prior.installation != next.installation
        {
            return Err(deny());
        }
    }
    Ok(())
}
pub(crate) fn hex(s: &str, n: usize) -> bool {
    s.len() == n
        && s.bytes()
            .all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b))
}
pub(crate) fn digest(b: &[u8]) -> String {
    Sha256::digest(b)
        .iter()
        .map(|b| format!("{b:02x}"))
        .collect()
}
pub(crate) fn deny() -> io::Error {
    io::Error::new(
        io::ErrorKind::PermissionDenied,
        "native firmware ownership/admission remains unresolved",
    )
}

#[cfg(test)]
mod tests {
    use super::*;
    fn reserved() -> Record {
        Record {
            phase: Phase::Reserved,
            sequence: 1,
            predecessor: "0".repeat(64),
            installation: "1".repeat(32),
            epoch: "2".repeat(32),
            artifact: format!("{}-service", "3".repeat(64)),
            process: "17:1".into(),
            nonce: "4".repeat(64),
        }
    }
    fn next(prior: &Record, phase: Phase) -> Record {
        Record {
            phase,
            sequence: prior.sequence + 1,
            predecessor: digest(&prior.bytes()),
            ..prior.clone()
        }
    }
    #[test]
    fn durable_owner_requires_order_identity_and_exact_predecessor() {
        let reserved = reserved();
        assert_eq!(
            Record::parse(&reserved.bytes()).unwrap().bytes(),
            reserved.bytes()
        );
        let active = next(&reserved, Phase::Active);
        assert!(require_transition(&reserved, &active).is_ok());
        let retiring = next(&active, Phase::Retiring);
        let retired = next(&retiring, Phase::Retired);
        assert!(require_transition(&active, &retiring).is_ok());
        assert!(require_transition(&retiring, &retired).is_ok());
        for phase in [Phase::Reserved, Phase::Retiring, Phase::Retired] {
            assert!(require_transition(&reserved, &next(&reserved, phase)).is_err());
        }
        let mut replacement = next(&retired, Phase::Reserved);
        assert!(require_transition(&retired, &replacement).is_err());
        replacement.epoch = "5".repeat(32);
        assert!(require_transition(&retired, &replacement).is_ok());
        replacement.predecessor = "0".repeat(64);
        assert!(require_transition(&retired, &replacement).is_err());
        let mut changed = active.clone();
        changed.process = "17:2".into();
        assert!(require_transition(&reserved, &changed).is_err());
    }
    #[test]
    fn crashes_unknown_or_partial_records_never_certify_retirement() {
        let b = reserved().bytes();
        for n in 0..b.len() {
            assert!(Record::parse(&b[..n]).is_err());
        }
        let text = String::from_utf8(b).unwrap();
        assert!(Record::parse(text.replace("Reserved", "Exited").as_bytes()).is_err());
        assert!(Record::parse(text.replace("\n1\n", "\n01\n").as_bytes()).is_err());
    }
}
