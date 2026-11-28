//! Process evidence for the candidate sign-in boundary.
#![allow(unsafe_code)]

use std::{io, mem::size_of_val, os::windows::io::AsRawHandle};

use windows_sys::Win32::{
    Security::{
        GetTokenInformation, TOKEN_ELEVATION, TOKEN_TYPE, TokenElevation, TokenPrimary, TokenType,
    },
    System::RemoteDesktop::ProcessIdToSessionId,
};

use crate::windows_token::{Identity, check, process_token};

pub(super) struct ProcessTokenEvidence {
    pub sid: String,
    pub token_session: u32,
    pub process_session: u32,
    #[cfg_attr(not(test), allow(dead_code))]
    // Read only by the sign-in binding check that tests exercise.
    pub logon: (i32, u32),
    pub primary: bool,
    pub elevated: bool,
}

pub(super) fn process_token_evidence() -> io::Result<ProcessTokenEvidence> {
    let token = process_token()?;
    let identity = Identity::read(&token)?;
    let mut process_session = 0;
    let mut token_type: TOKEN_TYPE = 0;
    let mut elevation = TOKEN_ELEVATION::default();
    let mut returned = 0;
    // SAFETY: the token and PID refer to this process. The output slots
    // are writable and have the layout and size for the requested class.
    unsafe {
        check(ProcessIdToSessionId(
            std::process::id(),
            &mut process_session,
        ))?;
        check(GetTokenInformation(
            token.as_raw_handle(),
            TokenType,
            (&mut token_type as *mut TOKEN_TYPE).cast(),
            size_of_val(&token_type) as u32,
            &mut returned,
        ))?;
        check(GetTokenInformation(
            token.as_raw_handle(),
            TokenElevation,
            (&mut elevation as *mut TOKEN_ELEVATION).cast(),
            size_of_val(&elevation) as u32,
            &mut returned,
        ))?;
    }

    Ok(ProcessTokenEvidence {
        sid: identity.sid,
        token_session: identity.session,
        process_session,
        logon: (
            identity.authentication.HighPart,
            identity.authentication.LowPart,
        ),
        primary: token_type == TokenPrimary,
        elevated: elevation.TokenIsElevated != 0,
    })
}
