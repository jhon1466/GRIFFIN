use std::collections::HashMap;

use anyhow::Result;

use crate::interceptor::Interceptor;

use windows::{
    core::PCSTR,
    Win32::{
        Foundation::HMODULE,
        System::LibraryLoader::{GetModuleHandleA, GetProcAddress},
    },
};

mod network;
mod tls;

pub use network::Network;
pub use tls::Tls;

#[derive(Default)]
pub struct ModuleManager {
    modules: HashMap<ModuleType, Box<dyn Gf2Module>>,
}

unsafe impl Sync for ModuleManager {}
unsafe impl Send for ModuleManager {}

impl ModuleManager {
    pub unsafe fn enable(&mut self, module: impl Gf2Module + 'static) {
        let mut boxed_module = Box::new(module);
        boxed_module.init().unwrap();
        self.modules
            .insert(boxed_module.get_module_type(), boxed_module);
    }
}

#[derive(Copy, Clone, Hash, PartialEq, Eq)]
pub enum ModuleType {
    Network,
    Tls,
}

pub trait Gf2Module {
    unsafe fn init(&mut self) -> Result<()>;
    fn get_module_type(&self) -> ModuleType;
}

pub struct Gf2Context<T> {
    pub assembly_base: usize,
    pub interceptor: Interceptor,
    _phantom: std::marker::PhantomData<T>,
}

impl<T> Gf2Context<T> {
    pub const fn new(assembly_base: usize) -> Self {
        Self {
            assembly_base,
            interceptor: Interceptor::new(),
            _phantom: std::marker::PhantomData,
        }
    }

    pub fn get_export(&self, module: &str, name: &str) -> anyhow::Result<usize> {
        let module_cstr = std::ffi::CString::new(module)?;
        let name_cstr = std::ffi::CString::new(name)?;

        unsafe {
            let handle = GetModuleHandleA(PCSTR(module_cstr.as_ptr() as _))
                .ok()
                .ok_or_else(|| anyhow::anyhow!("Module `{module}` not found"))?;

            let addr = GetProcAddress::<HMODULE, PCSTR>(handle, PCSTR(name_cstr.as_ptr() as _))
                .ok_or_else(|| anyhow::anyhow!("Symbol `{name}` not found"))?;

            Ok(addr as usize)
        }
    }
}
