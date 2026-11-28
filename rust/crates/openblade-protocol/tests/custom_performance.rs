use openblade_protocol::{
    RazerReport, calculate_checksum,
    custom_performance::{
        CustomPerformanceComponent as Component, CustomPerformanceLevel as Level,
        CustomPerformanceQuery,
    },
};
use openblade_types::ProtocolValidationFailure as Failure;

fn reply(component: Component, payload: &[u8]) -> [u8; 90] {
    let _ = component;
    let mut report = RazerReport::create(3, 0x0d, 0x87, payload)
        .unwrap()
        .encode();
    report[0] = 2;
    report
}
#[test]
fn typed_custom_requests_and_all_exact_levels_decode_without_boost() {
    for component in [Component::Cpu, Component::Gpu] {
        let query = CustomPerformanceQuery::new(3, component);
        let encoded = query.request().encode();
        assert_eq!(&encoded[5..11], &[3, 0x0d, 0x87, 1, component as u8, 0]);
        for (byte, level) in [(0, Level::Low), (1, Level::Medium), (2, Level::High)] {
            assert_eq!(
                query.decode(&reply(component, &[1, component as u8, byte])),
                Ok(level)
            );
        }
        for payload in [
            vec![],
            vec![1, component as u8],
            vec![1, component as u8, 0, 0],
            vec![0, component as u8, 0],
            vec![1, 3 - component as u8, 0],
            vec![1, component as u8, 3],
        ] {
            let expected = if payload.len() != 3 {
                Failure::PayloadLength
            } else {
                Failure::PayloadValidator
            };
            assert_eq!(query.decode(&reply(component, &payload)), Err(expected));
        }
    }
}
#[test]
fn custom_decoder_preserves_envelope_fault_precedence_before_semantics() {
    let query = CustomPerformanceQuery::new(3, Component::Cpu);
    assert_eq!(query.decode(&[]), Err(Failure::EnvelopeLength));
    let mut response = reply(Component::Cpu, &[0, 2, 3]);
    response[0] = 3;
    response[1] = 4;
    response[6] = 0;
    response[7] = 0;
    response[5] = 4;
    response[88] ^= 1;
    assert_eq!(query.decode(&response), Err(Failure::Checksum));
    response[88] = calculate_checksum(&response).unwrap();
    assert_eq!(query.decode(&response), Err(Failure::TransactionMismatch));
    response[1] = 3;
    assert_eq!(query.decode(&response), Err(Failure::CommandClassMismatch));
    response[6] = 0x0d;
    response[88] = calculate_checksum(&response).unwrap();
    assert_eq!(query.decode(&response), Err(Failure::CommandIdMismatch));
    response[7] = 0x87;
    response[88] = calculate_checksum(&response).unwrap();
    assert_eq!(query.decode(&response), Err(Failure::Status));
    response[0] = 2;
    assert_eq!(query.decode(&response), Err(Failure::PayloadLength));
    response[5] = 3;
    response[88] = calculate_checksum(&response).unwrap();
    assert_eq!(query.decode(&response), Err(Failure::PayloadValidator));
}
