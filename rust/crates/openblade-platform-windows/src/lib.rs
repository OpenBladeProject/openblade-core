//! Windows platform adapters for the current-user desktop.
#![cfg(windows)]

pub mod agent_diagnostics;
pub mod application;
pub mod battery_report;
pub mod brightness;
pub mod color_profile;
pub mod controller_presence;
pub mod display;
pub mod function_row_hook;
pub mod gpu_apps;
mod process_query;
mod process_snapshot;
pub mod snap_tap;
