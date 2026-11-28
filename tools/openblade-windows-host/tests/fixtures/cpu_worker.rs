use std::{
    io::{self, Read, Write},
    process::ExitCode,
    thread,
    time::Duration,
};

use openblade_windows_host::cpu_worker::protocol::{MilliCelsius, REQUEST_LENGTH, Reply, Sequence};

fn main() -> ExitCode {
    if serve().is_ok() {
        ExitCode::SUCCESS
    } else {
        ExitCode::FAILURE
    }
}

fn serve() -> io::Result<()> {
    let mut input = io::stdin().lock();
    let mut output = io::stdout().lock();
    let mut frame = [0; REQUEST_LENGTH];
    let mut previous = 0;
    for reply in [
        Reply::Ready,
        Reply::Value(MilliCelsius::new(41_001).unwrap()),
    ] {
        input.read_exact(&mut frame)?;
        let sequence = Sequence::decode(&frame).ok_or(io::ErrorKind::InvalidData)?;
        let raw = i64::from_le_bytes(frame);
        if raw <= previous {
            return Err(io::ErrorKind::InvalidData.into());
        }
        previous = raw;
        output.write_all(&reply.encode(sequence))?;
        output.flush()?;
    }
    // The success path stays alive past the parent's retirement deadline, even
    // after stdin closes. Only the test job should retire it during the test.
    thread::sleep(Duration::from_secs(30));
    Ok(())
}
