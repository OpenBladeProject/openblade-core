#[cfg(windows)]
mod windows;

#[cfg(windows)]
pub fn get_status() -> Result<String, crate::wire::Error> {
    windows::get_status()
}

#[cfg(not(windows))]
pub fn get_status() -> Result<String, crate::wire::Error> {
    Err(crate::wire::Error::UnsupportedTransport)
}
