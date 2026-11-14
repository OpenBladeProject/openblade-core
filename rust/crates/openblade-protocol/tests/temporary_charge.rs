use openblade_protocol::{RazerReport, battery};
use openblade_types::ProtocolValidationFailure as Failure;
fn reply(command: u8, payload: &[u8]) -> [u8; 90] {
    let mut r = RazerReport::create(3, 7, command, payload)
        .unwrap()
        .encode();
    r[0] = 2;
    r
}
#[test]
fn temporary_full_charge_preserves_full_flags_and_refuses_every_other_bit() {
    let query = battery::temporary_query(3);
    for flags in 0..=255u8 {
        let decoded = battery::power_flags(&query, &reply(0x8f, &[flags]));
        if flags <= 15 {
            assert_eq!(decoded, Ok(flags));
            assert_eq!(
                battery::temporary(&query, &reply(0x8f, &[flags])),
                Ok(flags & 4 != 0)
            );
        } else {
            assert_eq!(decoded, Err(Failure::PayloadValidator));
        }
        assert_eq!(
            battery::temporary_write_admitted(flags),
            flags == 0 || flags == 4
        );
    }
}
#[test]
fn temporary_full_charge_setter_has_exact_payload_and_echo_ack_is_not_readback() {
    for enabled in [false, true] {
        let target = if enabled { 4 } else { 0 };
        let setter = battery::temporary_setter(3, enabled);
        let bytes = setter.encode();
        assert_eq!(&bytes[5..9], &[1, 7, 0x0f, target]);
        assert!(battery::acknowledge(&setter, &reply(0x0f, &[target]), target).is_ok());
        assert!(battery::acknowledge(&setter, &reply(0x0f, &[target ^ 4]), target).is_err());
        assert!(battery::acknowledge(&setter, &reply(0x0f, &[target, 0]), target).is_err());
        assert!(
            battery::power_flags(&battery::temporary_query(3), &reply(0x0f, &[target])).is_err()
        );
    }
}
