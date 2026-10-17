use std::{sync::RwLock, time::Duration};

use lazy_static::lazy_static;
use windows::core::PCSTR;
use windows::Win32::System::LibraryLoader::GetModuleHandleA;
use windows::Win32::System::SystemServices::DLL_PROCESS_ATTACH;
use windows::Win32::{Foundation::HINSTANCE, System::Console};

mod interceptor;
mod modules;
mod rvas;
mod util;

use crate::modules::{Gf2Context, ModuleManager, Network, Tls};

unsafe fn thread_func() {
    while GetModuleHandleA(PCSTR(b"GameAssembly.dll\0".as_ptr())).is_err() {
        std::thread::sleep(Duration::from_millis(200));
    }

    let base = *util::GAME_ASSEMBLY_BASE;

    let _ = Console::AllocConsole();
    util::disable_memory_protection();

    println!("Griffin Patcher");
    println!("GameAssembly base: 0x{base:X}");
    println!("SDK target: https://127.0.0.1 (Griffin Kestrel :443)");
    println!("RVAs: client {} (MakeInitialUrl 0x12B9150)", rvas::CLIENT_VERSION);

    let mut module_manager = MODULE_MANAGER.write().unwrap();
    module_manager.enable(Gf2Context::<Tls>::new(base));
    module_manager.enable(Gf2Context::<Network>::new(base));

    println!("Hooks installed.");
}

lazy_static! {
    static ref MODULE_MANAGER: RwLock<ModuleManager> = RwLock::new(ModuleManager::default());
}

#[no_mangle]
#[allow(non_snake_case)]
unsafe extern "system" fn DllMain(_: HINSTANCE, call_reason: u32, _: *mut ()) -> bool {
    if call_reason == DLL_PROCESS_ATTACH {
        std::thread::spawn(|| thread_func());
    }

    true
}
