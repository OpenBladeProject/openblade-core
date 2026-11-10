//! Windows platform adapters for the current-user desktop.
#![cfg(windows)]

pub mod application;
pub mod brightness;
pub mod color_profile;
pub mod display;
pub mod function_row_hook;
pub mod gpu_apps;
pub mod snap_tap;
