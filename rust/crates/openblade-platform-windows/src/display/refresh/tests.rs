use super::*;

const FIXTURE: &str = include_str!("../../../../../../tests/Fixtures/Parity/display-refresh.psv");

fn parse_rate(text: &str) -> Option<RefreshRate> {
    let value: f64 = text.parse().ok()?;
    RefreshRate::observed(DISPLAYCONFIG_RATIONAL {
        Numerator: (value * 1000.0).round() as u32,
        Denominator: 1000,
    })
}

fn numbers<T: std::str::FromStr>(text: &str) -> impl Iterator<Item = T> + '_
where
    T::Err: std::fmt::Debug,
{
    text.split(',')
        .filter(|&value| value != "-")
        .map(|value| value.parse().unwrap())
}

fn entries(text: &str) -> impl Iterator<Item = &str> {
    text.split(';').filter(|&entry| entry != "-")
}

fn display(
    virtual_mode: bool,
    boost: bool,
    desktop: &str,
    physical: &str,
    width: u32,
    height: u32,
) -> ActiveInternalDisplay {
    let target = EndpointId {
        adapter_low: 0,
        adapter_high: 0,
        id: 1,
    };
    let physical_rate = parse_rate(physical);
    ActiveInternalDisplay {
        advanced_color: Ok(AdvancedColorState {
            hdr_supported: false,
            hdr_user_enabled: false,
            bits_per_color_channel: None,
        }),
        source: target,
        target,
        source_name: "fixture".into(),
        monitor_name: Ok(MonitorName {
            friendly_name: None,
            device_path: None,
        }),
        desktop_rate: parse_rate(desktop),
        physical_rate,
        supports_virtual_mode: virtual_mode,
        boost_refresh_rate: boost,
        active_signal: SignalTiming {
            width,
            height,
            scanline_ordering: 1,
            rate: physical_rate,
        },
    }
}

fn assert_options(options: &[RefreshOption], expected: &str, case: &str) {
    let actual: Vec<_> = options
        .iter()
        .map(|option| {
            format!(
                "{}~{}~{}~{}~{}",
                option.key,
                option.minimum_hertz,
                option.maximum_hertz,
                option.desktop_hertz,
                u8::from(option.is_dynamic)
            )
        })
        .collect();
    assert_eq!(actual, entries(expected).collect::<Vec<_>>(), "{case}");
}

fn assert_key(
    display: &ActiveInternalDisplay,
    options: &[RefreshOption],
    expected: &str,
    case: &str,
) {
    let actual = classify(display, options).map(|option| option.key.as_str());
    assert_eq!(actual, (expected != "-").then_some(expected), "{case}");
}

pub(in crate::display) fn write_timing(
    bytes: &mut [u8],
    width: u64,
    height: u64,
    pixel_rate: u64,
    total_width: u64,
    total_height: u64,
) {
    let clock = (pixel_rate / 1000 - 1).to_le_bytes();
    bytes[..3].copy_from_slice(&clock[..3]);
    for (offset, value) in [
        (4, width),
        (6, total_width - width),
        (12, height),
        (14, total_height - height),
    ] {
        bytes[offset..offset + 2].copy_from_slice(&u16::try_from(value - 1).unwrap().to_le_bytes());
    }
}

pub(in crate::display) fn set_checksum(block: &mut [u8]) {
    block[127] = 0_u8.wrapping_sub(block[..127].iter().copied().fold(0_u8, u8::wrapping_add));
}

pub(in crate::display) fn type7_edid(count: usize) -> Vec<u8> {
    assert!(count <= 5);
    let mut bytes = vec![0; 256];
    bytes[..8].copy_from_slice(&[0, 255, 255, 255, 255, 255, 255, 0]);
    bytes[126] = 1;
    bytes[128] = 0x70;
    bytes[130] = (3 + 20 * count) as u8;
    bytes[133] = 0x22;
    bytes[135] = (20 * count) as u8;
    bytes
}

fn timings(spec: &str) -> Vec<edid::DetailedTiming> {
    entries(spec)
        .map(|row| {
            let fields: Vec<_> = row.split(',').collect();
            let [width, height, hertz, pixel_rate, total_width, total_height] = fields[..] else {
                panic!("malformed timing fixture: {row}");
            };
            edid::DetailedTiming {
                width: width.parse().unwrap(),
                height: height.parse().unwrap(),
                hertz: hertz.parse().unwrap(),
                pixel_rate: pixel_rate.parse().unwrap(),
                total_width: total_width.parse().unwrap(),
                total_height: total_height.parse().unwrap(),
            }
        })
        .collect()
}

#[test]
fn managed_refresh_fixture_matches_rust() {
    for row in FIXTURE
        .lines()
        .filter(|line| !line.is_empty() && !line.starts_with('#'))
    {
        let fields: Vec<_> = row.split('|').collect();
        match fields.as_slice() {
            [
                "refresh",
                case,
                drivers,
                current,
                desktop,
                active,
                width,
                height,
                edid,
                preferred,
                virtual_mode,
                boost,
                range,
                fixed,
                expected_options,
                expected_key,
            ] => {
                let (width, height) = (width.parse().unwrap(), height.parse().unwrap());
                let current_mode = DriverMode {
                    width,
                    height,
                    hertz: current.parse().unwrap(),
                    interlaced: false,
                };
                let (active_hertz, scanline) = active.split_once(',').unwrap_or((active, "1"));
                let mut display = display(
                    *virtual_mode == "1",
                    *boost == "1",
                    if *desktop == "-" { current } else { desktop },
                    active_hertz,
                    width,
                    height,
                );
                display.active_signal.scanline_ordering = scanline.parse().unwrap();
                let preferred = entries(preferred).next().and_then(|timing| {
                    let mut fields = timing.split(',');
                    let timing = SignalTiming {
                        width: fields.next().unwrap().parse().unwrap(),
                        height: fields.next().unwrap().parse().unwrap(),
                        rate: parse_rate(fields.next().unwrap()),
                        scanline_ordering: fields.next().unwrap_or("1").parse().unwrap(),
                    };
                    display
                        .supports_virtual_mode
                        .then(|| accept_preferred(display.active_signal, timing))
                        .flatten()
                });
                let mut range = numbers(range);
                let range = range.next().map(|minimum_hertz| VerticalRange {
                    minimum_hertz,
                    maximum_hertz: range.next().unwrap(),
                });
                let options = build_options(
                    &display,
                    current_mode,
                    &numbers(drivers).collect(),
                    preferred,
                    &timings(edid),
                    range,
                    None,
                );
                let fixed_rates: Vec<_> = options
                    .iter()
                    .filter(|option| !option.is_dynamic)
                    .map(|option| option.maximum_hertz)
                    .collect();
                assert_eq!(
                    fixed_rates,
                    numbers::<i32>(fixed).collect::<Vec<_>>(),
                    "{case}"
                );
                assert_options(&options, expected_options, case);
                assert_key(&display, &options, expected_key, case);
            }
            [
                "fixed",
                case,
                drivers,
                advertised,
                current,
                physical,
                expected,
            ] => {
                let mut advertised: BTreeSet<_> = numbers(advertised).collect();
                advertised.extend([current.parse::<i32>().unwrap(), physical.parse().unwrap()]);
                let fixed = select_fixed_rates(&numbers(drivers).collect(), &advertised);
                assert_eq!(
                    fixed,
                    numbers::<i32>(expected).collect::<Vec<_>>(),
                    "{case}"
                );
            }
            [
                "classify",
                case,
                boost,
                desktop,
                physical,
                options,
                expected,
            ] => {
                let display = display(true, *boost == "1", desktop, physical, 2560, 1600);
                let options: Vec<_> = entries(options)
                    .map(|spec| {
                        let fields: Vec<_> = spec.split('~').collect();
                        RefreshOption {
                            key: fields[0].into(),
                            name: String::new(),
                            minimum_hertz: fields[1].parse().unwrap(),
                            maximum_hertz: fields[2].parse().unwrap(),
                            desktop_hertz: fields[3].parse().unwrap(),
                            is_dynamic: fields[4] == "1",
                        }
                    })
                    .collect();
                assert_key(&display, &options, expected, case);
            }
            [
                "edid",
                case,
                width,
                height,
                pixel_rate,
                total_width,
                total_height,
                interlaced,
                valid_checksum,
                expected,
            ] => {
                let mut bytes = type7_edid(1);
                write_timing(
                    &mut bytes[136..156],
                    width.parse().unwrap(),
                    height.parse().unwrap(),
                    pixel_rate.parse().unwrap(),
                    total_width.parse().unwrap(),
                    total_height.parse().unwrap(),
                );
                if *interlaced == "1" {
                    bytes[139] = 0x10;
                }
                set_checksum(&mut bytes[..128]);
                set_checksum(&mut bytes[128..]);
                if *valid_checksum == "0" {
                    bytes[255] ^= 1;
                }
                let actual = edid::detailed_timings(&bytes);
                if *expected == "overflow" {
                    assert_eq!(actual, Err(DisplayReadError::RefreshRateOverflow), "{case}");
                } else if *expected == "-" {
                    assert_eq!(actual.unwrap(), [], "{case}");
                } else {
                    assert_eq!(
                        actual.unwrap(),
                        [edid::DetailedTiming {
                            width: width.parse().unwrap(),
                            height: height.parse().unwrap(),
                            hertz: expected.parse().unwrap(),
                            pixel_rate: pixel_rate.parse().unwrap(),
                            total_width: total_width.parse().unwrap(),
                            total_height: total_height.parse().unwrap(),
                        }],
                        "{case}"
                    );
                }
            }
            _ => panic!("unknown fixture row: {row}"),
        }
    }
}

#[test]
fn range_requires_a_valid_edid_header_checksum_and_bounds() {
    let mut edid = type7_edid(0);
    edid[54..61].copy_from_slice(&[0, 0, 0, 0xfd, 0, 48, 240]);
    set_checksum(&mut edid[..128]);
    assert_eq!(
        edid::vertical_range(&edid),
        Some(VerticalRange {
            minimum_hertz: 48,
            maximum_hertz: 240
        })
    );
    assert_eq!(edid::vertical_range(&edid[..127]), None);
    edid[59] = 19;
    assert_eq!(edid::vertical_range(&edid), None);
    set_checksum(&mut edid[..128]);
    assert_eq!(edid::vertical_range(&edid), None);
}

#[test]
fn rational_keeps_representable_terms_and_reduces_only_on_overflow() {
    let terms = |ratio: Option<DISPLAYCONFIG_RATIONAL>| {
        ratio.map(|ratio| (ratio.Numerator, ratio.Denominator))
    };
    // Issue #884 panel timing: both ratios fit, so neither is reduced.
    assert_eq!(
        terms(edid::rational(268_620_000, 2720 * 1646)),
        Some((268_620_000, 4_477_120))
    );
    assert_eq!(
        terms(edid::rational(268_620_000, 2720)),
        Some((268_620_000, 2720))
    );
    // A term above u32::MAX is reduced by the greatest common divisor first.
    assert_eq!(
        terms(edid::rational(10_000_000_000, 4_000)),
        Some((2_500_000, 1))
    );
    assert_eq!(
        terms(edid::rational(8_589_934_590, 4)),
        Some((4_294_967_295, 2))
    );
    // Still too large after reduction, or a zero term.
    assert_eq!(terms(edid::rational(4_294_967_297, 2)), None);
    assert_eq!(terms(edid::rational(0, 1)), None);
    assert_eq!(terms(edid::rational(1, 0)), None);
}
