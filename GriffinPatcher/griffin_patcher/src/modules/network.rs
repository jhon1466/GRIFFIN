use anyhow::Result;
use ilhook::x64::Registers;

use super::{Gf2Context, Gf2Module, ModuleType};
use crate::rvas;
use crate::util::{overwrite_csharp_string, read_csharp_string, redirect_url, should_redirect_url};

pub struct Network;

impl Gf2Module for Gf2Context<Network> {
    unsafe fn init(&mut self) -> Result<()> {
        self.interceptor.attach(
            self.assembly_base + rvas::UNITY_WEB_REQUEST_SET_URL,
            on_set_url,
        )?;

        self.interceptor.attach(
            self.assembly_base + rvas::UNITY_WEB_REQUEST_SET_REQUEST_HEADER,
            on_set_request_header,
        )?;

        println!("[network] hooks attached");
        Ok(())
    }

    fn get_module_type(&self) -> ModuleType {
        ModuleType::Network
    }
}

/// WebRequestUtils.MakeInitialUrl(string) — static, URL in RCX
unsafe extern "win64" fn on_set_url(reg: *mut Registers, _: usize) {
    let url_ptr = (*reg).rcx as usize;
    if url_ptr == 0 {
        return;
    }

    let url = read_csharp_string(url_ptr);
    if url.is_empty() {
        return;
    }

    println!("[url] {url}");

    let Some(new_url) = redirect_url(&url) else {
        return;
    };

    println!("[url] -> {new_url}");

    if !overwrite_csharp_string(url_ptr, &new_url) {
        println!("[url] rewrite failed (string too short or unreadable)");
    }
}

/// UnityWebRequest.SetRequestHeader(string name, string value) — RCX = this, RDX = name, R8 = value
unsafe extern "win64" fn on_set_request_header(reg: *mut Registers, _: usize) {
    if (*reg).rdx == 0 || (*reg).r8 == 0 {
        return;
    }

    let key = read_csharp_string((*reg).rdx as usize);
    let value = read_csharp_string((*reg).r8 as usize);

    if key.is_empty() || value.is_empty() {
        return;
    }

    if key.eq_ignore_ascii_case("host") && should_redirect_url(&value) {
        println!("[SetRequestHeader] Host: {value} -> 127.0.0.1");
        overwrite_csharp_string((*reg).r8 as usize, "127.0.0.1");
    }
}
