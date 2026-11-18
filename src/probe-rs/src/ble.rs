//! BLE scan via btleplug (WinRT backend on Windows).
//!
//! Read-only: start_scan / peripherals / properties / stop_scan.
//! We never call connect() or discover_services().

use btleplug::api::{bleuuid::uuid_from_u16, Central, Manager as _, Peripheral as _, ScanFilter};
use btleplug::platform::Manager;
use std::error::Error;
use std::time::{Duration, Instant};
use uuid::Uuid;

/// Xiaomi MiBeacon / MiOT service UUID (0xFE95).
const MI_SERVICE: u16 = 0xFE95;

pub fn run_scan(secs: u64) -> Result<(), Box<dyn Error>> {
    let rt = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()?;
    rt.block_on(async move { scan_async(secs).await })
}

async fn scan_async(secs: u64) -> Result<(), Box<dyn Error>> {
    let mi_uuid = uuid_from_u16(MI_SERVICE);
    let manager = Manager::new().await?;
    let adapters = manager.adapters().await?;
    println!("[ble] adapters found: {}", adapters.len());
    let central = adapters
        .into_iter()
        .next()
        .ok_or("no Bluetooth LE adapter available")?;
    println!("[ble] adapter info: {:?}", Central::adapter_info(&central).await);

    central.start_scan(ScanFilter::default()).await?;
    println!("[ble] scanning for {secs}s (no connections will be made)...");
    let t0 = Instant::now();
    tokio::time::sleep(Duration::from_secs(secs)).await;
    let peripherals = central.peripherals().await?;
    central.stop_scan().await?;
    println!(
        "[ble] scan finished in {:.1}s, {} peripheral(s) seen\n",
        t0.elapsed().as_secs_f64(),
        peripherals.len()
    );

    // addr, name, rssi, fe95, services, svc_data
    let mut rows: Vec<(String, String, String, bool, String, String)> = Vec::new();
    for p in &peripherals {
        let addr = p.address().to_string();
        let props = p.properties().await?;
        match props {
            Some(props) => {
                let name = props
                    .local_name
                    .clone()
                    .filter(|n| !n.is_empty())
                    .unwrap_or_else(|| "<no name>".to_string());
                let has_mi = props.services.contains(&mi_uuid)
                    || props.service_data.contains_key(&mi_uuid);
                let services = props
                    .services
                    .iter()
                    .map(|u| short_uuid(u))
                    .collect::<Vec<_>>()
                    .join(",");
                let svc_data = props
                    .service_data
                    .iter()
                    .map(|(u, d)| format!("{}[{}B]", short_uuid(u), d.len()))
                    .collect::<Vec<_>>()
                    .join(",");
                let rssi = props
                    .rssi
                    .map(|v| v.to_string())
                    .unwrap_or_else(|| "-".to_string());
                rows.push((addr, name, rssi, has_mi, services, svc_data));
            }
            None => rows.push((addr, "<no adv data>".into(), "-".into(), false, String::new(), String::new())),
        }
    }
    // MiBeacon devices first, then by address for a stable listing.
    rows.sort_by(|a, b| b.3.cmp(&a.3).then_with(|| a.0.cmp(&b.0)));

    println!("{:<3} {:<20} {:<24} {:<5} {:<9} {:<28} {:<4}", "#", "address", "name", "rssi", "0xFE95?", "services", "svc_data");
    println!("{}", "-".repeat(120));
    let mut mi_count = 0;
    for (i, (addr, name, rssi, has_mi, services, svc_data)) in rows.iter().enumerate() {
        if *has_mi {
            mi_count += 1;
        }
        println!(
            "{:<3} {:<20} {:<24} {:<5} {:<9} {:<28} {:<4}",
            i + 1,
            addr,
            truncate(name, 24),
            rssi,
            if *has_mi { "YES" } else { "no" },
            truncate(services, 28),
            truncate(svc_data, 12)
        );
    }
    println!("{}", "-".repeat(120));
    println!(
        "[ble] total={} with_0xFE95={} (MiBeacon/MiOT candidates)",
        rows.len(),
        mi_count
    );
    Ok(())
}

fn short_uuid(u: &Uuid) -> String {
    let s = u.to_string();
    // 0000fe95-0000-1000-8000-00805f9b34fb -> fe95
    if s.starts_with("0000") && s.ends_with("-0000-1000-8000-00805f9b34fb") {
        s[4..8].to_string()
    } else {
        s
    }
}

fn truncate(s: &str, n: usize) -> String {
    if s.chars().count() <= n {
        s.to_string()
    } else {
        s.chars().take(n.saturating_sub(1)).collect::<String>() + "~"
    }
}
