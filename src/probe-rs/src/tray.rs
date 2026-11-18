//! Notification-area icon spike: tray-icon (+ winit event loop), auto-exit.

use std::error::Error;
use std::time::{Duration, Instant};
use tray_icon::menu::{Menu, MenuEvent, MenuItem};
use tray_icon::{Icon, TrayIcon, TrayIconBuilder, TrayIconEvent};
use winit::application::ApplicationHandler;
use winit::event::WindowEvent;
use winit::event_loop::{ActiveEventLoop, ControlFlow, EventLoop, EventLoopProxy};
use winit::window::WindowId;

#[derive(Debug)]
enum UserEvent {
    Tray(TrayIconEvent),
    Menu(MenuEvent),
}

struct App {
    proxy: EventLoopProxy<UserEvent>,
    quit_item: MenuItem,
    tray: Option<TrayIcon>,
    deadline: Instant,
}

impl ApplicationHandler<UserEvent> for App {
    fn resumed(&mut self, event_loop: &ActiveEventLoop) {
        if self.tray.is_some() {
            return;
        }
        let menu = Menu::new();
        let hello = MenuItem::new("CUKTECH Ultra tray spike", false, None);
        let _ = menu.append(&hello);
        let _ = menu.append(&tray_icon::menu::PredefinedMenuItem::separator());
        let _ = menu.append(&self.quit_item);

        let proxy_tray = self.proxy.clone();
        TrayIconEvent::set_event_handler(Some(move |e| {
            let _ = proxy_tray.send_event(UserEvent::Tray(e));
        }));
        let proxy_menu = self.proxy.clone();
        MenuEvent::set_event_handler(Some(move |e| {
            let _ = proxy_menu.send_event(UserEvent::Menu(e));
        }));

        let icon = make_icon();
        match TrayIconBuilder::new()
            .with_menu(Box::new(menu))
            .with_tooltip("CUKTECH Ultra - Rust spike")
            .with_icon(icon)
            .build()
        {
            Ok(t) => {
                println!("[tray] tray icon created OK (id={:?})", t.id());
                self.tray = Some(t);
            }
            Err(e) => {
                eprintln!("[tray] FAILED to create tray icon: {e}");
                event_loop.exit();
                return;
            }
        }
        event_loop.set_control_flow(ControlFlow::WaitUntil(self.deadline));
        println!(
            "[tray] icon visible; will auto-exit in {:.0?}",
            self.deadline - Instant::now()
        );
    }

    fn window_event(&mut self, _e: &ActiveEventLoop, _id: WindowId, _event: WindowEvent) {}

    fn user_event(&mut self, event_loop: &ActiveEventLoop, event: UserEvent) {
        match event {
            UserEvent::Tray(e) => println!("[tray] TrayIconEvent: {e:?}"),
            UserEvent::Menu(e) => {
                println!("[tray] MenuEvent: {:?}", e.id);
                if e.id == self.quit_item.id() {
                    println!("[tray] quit menu item clicked -> exit");
                    event_loop.exit();
                }
            }
        }
    }

    fn about_to_wait(&mut self, event_loop: &ActiveEventLoop) {
        if Instant::now() >= self.deadline {
            event_loop.exit();
        }
    }
}

pub fn run_tray(secs: u64) -> Result<(), Box<dyn Error>> {
    let event_loop = EventLoop::<UserEvent>::with_user_event().build()?;
    let proxy = event_loop.create_proxy();
    let mut app = App {
        proxy,
        quit_item: MenuItem::new("Quit", true, None),
        tray: None,
        deadline: Instant::now() + Duration::from_secs(secs),
    };
    println!("[tray] starting winit event loop, auto-exit after {secs}s");
    event_loop.run_app(&mut app)?;
    println!("[tray] event loop exited cleanly after {secs}s");
    Ok(())
}

/// 32x32 RGBA icon generated at runtime (no image assets / image crate needed).
fn make_icon() -> Icon {
    let (w, h) = (32u32, 32u32);
    let mut rgba = Vec::with_capacity((w * h * 4) as usize);
    for y in 0..h {
        for x in 0..w {
            let dx = x as i32 - 16;
            let dy = y as i32 - 16;
            let d2 = dx * dx + dy * dy;
            if d2 <= 169 {
                // solid teal disc with a lighter core ring
                let (r, g, b) = if d2 <= 49 { (255, 255, 255) } else { (0, 150, 136) };
                rgba.extend_from_slice(&[r, g, b, 255]);
            } else {
                rgba.extend_from_slice(&[0, 0, 0, 0]);
            }
        }
    }
    Icon::from_rgba(rgba, w, h).expect("32x32 RGBA icon")
}
