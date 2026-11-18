//! Feasibility spike for the CUKTECH Ultra tray app.
//!
//! Modes:
//!   scan  - list nearby BLE devices (name / address / does it advertise 0xFE95)
//!   tray  - show a notification-area icon with a menu, auto-exit after N seconds
//!   all   - scan, then tray (default)
//!
//! NEVER connects to any peripheral: scanning only. Connecting would steal the
//! exclusive BLE link that the C# host console needs.

mod ble;
mod tray;

use std::error::Error;

fn main() -> Result<(), Box<dyn Error>> {
    let mut args = std::env::args().skip(1);
    let mode = args.next().unwrap_or_else(|| "all".to_string());
    let secs: u64 = args
        .next()
        .and_then(|s| s.parse().ok())
        .unwrap_or(match mode.as_str() {
            "tray" => 5,
            _ => 8,
        });

    match mode.as_str() {
        "scan" => ble::run_scan(secs)?,
        "tray" => tray::run_tray(secs)?,
        "all" => {
            ble::run_scan(secs)?;
            tray::run_tray(5)?;
        }
        other => {
            eprintln!("unknown mode '{other}' (expected: scan | tray | all)");
            std::process::exit(2);
        }
    }
    Ok(())
}
